/*
  결정권자(관리자 또는 승인권자)의 최종 기한 결정: 결정권자 확인 대기(Escalated) 일정에 대해서만.
  Escalated는 생산 담당이 회신을 거절했거나, 이미 결정권자가 정한 기한을 바꾸는 회신을 생산 담당이 수락한 경우다.
  - 사유 필수, 기한은 지금 이후(52020)
  - 상태: ScheduleState = Decided, DueAt = 결정한 기한
  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_DecideSchedule
    @RequestKey nvarchar(100),
    @LotId      int,
    @DeciderId  nvarchar(450),
    @DueAt      datetime2,
    @Reason     nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'DecideSchedule';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');
    DECLARE @Now datetime2 = SYSUTCDATETIME();

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @DueAt IS NULL
        THROW 50400, N'최종 기한이 필요합니다.', 1;
    IF @Reason IS NULL
        THROW 52020, N'최종 기한 결정은 사유가 필요합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @DeciderId, CONVERT(nvarchar(30), @DueAt, 126), @Reason)), 2);

    BEGIN TRANSACTION;

    DECLARE @SavedHash char(64), @SavedOperation nvarchar(50), @SavedResponse nvarchar(max);
    SELECT @SavedHash = BodyHash, @SavedOperation = Operation, @SavedResponse = ResponseJson
    FROM dbo.QcRequests WITH (UPDLOCK, HOLDLOCK)
    WHERE RequestKey = @RequestKey;

    IF @SavedHash IS NOT NULL
    BEGIN
        IF @SavedHash <> @BodyHash OR @SavedOperation <> @Operation
        BEGIN
            ROLLBACK TRANSACTION;
            THROW 50409, N'같은 요청 키로 다른 내용의 요청이 이미 처리되었습니다. (R6)', 1;
        END;
        COMMIT TRANSACTION;
        SELECT @SavedResponse AS ResponseJson, CAST(1 AS bit) AS Replayed;
        RETURN;
    END;

    IF @DueAt <= @Now
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 52020, N'최종 기한은 지금 이후여야 합니다.', 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @DeciderId AND r.Name IN (N'Admin', N'Approver'))
        THROW 50403, N'관리자 또는 승인권자만 최종 기한을 정할 수 있습니다.', 1;

    DECLARE @Found bit = 0, @State nvarchar(20);
    SELECT @Found = 1, @State = ScheduleState FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @Found = 0
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;
    IF @State IS NULL OR @State <> N'Escalated'
        THROW 52012, N'결정을 기다리는 일정이 없습니다(생산 담당이 거절했거나, 결정된 기한을 바꾸는 회신만 결정합니다).', 1;

    UPDATE dbo.Lots SET ScheduleState = N'Decided', DueAt = @DueAt, ProposedDueAt = NULL WHERE Id = @LotId;

    INSERT INTO dbo.ScheduleEvents (LotId, Kind, DueAt, Reason, ActorId, CreatedAt)
    VALUES (@LotId, N'Decide', @DueAt, @Reason, @DeciderId, @Now);

    DECLARE @Response nvarchar(max) = (
        SELECT l.Id AS lotId, l.Status AS status, l.Priority AS priority, l.DueAt AS dueAt,
               l.ScheduleState AS scheduleState, l.ProposedDueAt AS proposedDueAt
        FROM dbo.Lots l WHERE l.Id = @LotId
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT INTO dbo.QcRequests (RequestKey, Operation, BodyHash, ResponseJson, CreatedAt)
    VALUES (@RequestKey, @Operation, @BodyHash, @Response, @Now);

    COMMIT TRANSACTION;

    SELECT @Response AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
