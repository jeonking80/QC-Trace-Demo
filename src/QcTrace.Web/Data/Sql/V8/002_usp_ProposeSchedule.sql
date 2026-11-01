/*
  검사자 일정 회신: 미검사가 남은 LOT(부분 검사 포함)에 "나머지는 언제까지 하겠다"는 예정 시각과 사유를 보낸다.
  - 검사자 역할, 사유 필수, 예정 시각은 지금 이후(52020)
  - 의뢰된 LOT(Waiting·Inspecting)에 미검사가 남아 있어야 한다(52012)
  - 확인 대기(Proposed)나 결정 대기(Escalated) 중이면 새로 보낼 수 없다(52012)
  - 상태: ScheduleState = Proposed, ProposedDueAt = 예정 시각 → 생산 담당이 수락/거절
  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_ProposeSchedule
    @RequestKey  nvarchar(100),
    @LotId       int,
    @InspectorId nvarchar(450),
    @DueAt       datetime2,
    @Reason      nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'ProposeSchedule';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');
    DECLARE @Now datetime2 = SYSUTCDATETIME();

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @DueAt IS NULL
        THROW 50400, N'완료 예정 시각이 필요합니다.', 1;
    IF @Reason IS NULL
        THROW 52020, N'일정 회신은 사유가 필요합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @InspectorId, CONVERT(nvarchar(30), @DueAt, 126), @Reason)), 2);

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
        THROW 52020, N'완료 예정 시각은 지금 이후여야 합니다.', 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @InspectorId AND r.Name IN (N'Inspector'))
        THROW 50403, N'검사자 역할이 있는 사용자만 일정을 회신할 수 있습니다.', 1;

    DECLARE @Status nvarchar(20), @State nvarchar(20), @Uninspected int;
    SELECT @Status = l.Status, @State = l.ScheduleState, @Uninspected = q.Uninspected
    FROM dbo.Lots l WITH (UPDLOCK, ROWLOCK) JOIN dbo.LotQuantities q ON q.LotId = l.Id
    WHERE l.Id = @LotId;
    IF @Status IS NULL
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;
    IF @Status NOT IN (N'Waiting', N'Inspecting') OR @Uninspected = 0
        THROW 52012, N'미검사가 남은 의뢰 LOT만 일정을 회신할 수 있습니다.', 1;
    IF @State IN (N'Proposed', N'Escalated')
        THROW 52012, N'이미 확인·결정을 기다리는 일정 회신이 있습니다.', 1;

    UPDATE dbo.Lots SET ScheduleState = N'Proposed', ProposedDueAt = @DueAt WHERE Id = @LotId;

    INSERT INTO dbo.ScheduleEvents (LotId, Kind, DueAt, Reason, ActorId, CreatedAt)
    VALUES (@LotId, N'Propose', @DueAt, @Reason, @InspectorId, @Now);

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
