/*
  생산 담당의 일정 회신 확인: 수락(Accept)하면 그 시각이 검사 기한이 되고,
  거절(Reject, 사유 필수)하면 결정권자(관리자·승인권자)에게 넘어간다(Escalated).
  - 확인 대기(Proposed) 상태에서만 가능(52012)
  - 결정권자가 한 번 최종 기한을 정한 LOT은, 생산 담당이 수락해도 기한을 바로 바꾸지 않고
    결정권자 확인 대기(Escalated)로 보낸다(2026-11-01 사용자 결정: "바꾸려면 결정권자 다시 거침")
  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_RespondSchedule
    @RequestKey nvarchar(100),
    @LotId      int,
    @ProducerId nvarchar(450),
    @Decision   nvarchar(10),
    @Reason     nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'RespondSchedule';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');
    DECLARE @Now datetime2 = SYSUTCDATETIME();

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Decision IS NULL OR @Decision NOT IN (N'Accept', N'Reject')
        THROW 50400, N'확인 결과는 Accept 또는 Reject여야 합니다.', 1;
    IF @Decision = N'Reject' AND @Reason IS NULL
        THROW 52020, N'거절은 사유가 필요합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @ProducerId, @Decision, @Reason)), 2);

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

    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @ProducerId AND r.Name IN (N'Producer'))
        THROW 50403, N'생산 담당 역할이 있는 사용자만 일정 회신을 확인할 수 있습니다.', 1;

    DECLARE @Found bit = 0, @State nvarchar(20), @Proposed datetime2;
    SELECT @Found = 1, @State = ScheduleState, @Proposed = ProposedDueAt
    FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @Found = 0
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;
    IF @State IS NULL OR @State <> N'Proposed'
        THROW 52012, N'확인을 기다리는 일정 회신이 없습니다.', 1;

    IF @Decision = N'Accept' AND NOT EXISTS (SELECT 1 FROM dbo.ScheduleEvents WHERE LotId = @LotId AND Kind = N'Decide')
        UPDATE dbo.Lots SET ScheduleState = N'Accepted', DueAt = @Proposed, ProposedDueAt = NULL WHERE Id = @LotId;
    ELSE
        UPDATE dbo.Lots SET ScheduleState = N'Escalated' WHERE Id = @LotId;

    INSERT INTO dbo.ScheduleEvents (LotId, Kind, DueAt, Reason, ActorId, CreatedAt)
    VALUES (@LotId, @Decision, @Proposed, @Reason, @ProducerId, @Now);

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
