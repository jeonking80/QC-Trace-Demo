/*
  검사 의뢰: Registered → Waiting, 의뢰 시각 기록
  V8(검사 일정, 2026-11-01 사용자 요청): 우선순위(보통/긴급)와 희망 완료 기한을 함께 받는다.
  - 긴급은 사유 필수(52020). 희망 기한은 선택, 넣으면 지금 이후여야 한다(52020).
  - 긴급이나 기한을 지정하면 ScheduleEvents에 Request 행을 남긴다(수량 원장과 별도).
  - 지연 판단: 기한이 있으면 그 기한, 없으면 의뢰 후 48시간
  - 수량 변화는 없지만 R10 감사 추적을 위해 원장에 Request 행(Delta 0)을 남긴다
  - R6 같은 요청 키는 한 번만 반영

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_RequestInspection
    @RequestKey     nvarchar(100),
    @LotId          int,
    @ProducerId     nvarchar(450),
    @Priority       nvarchar(10)  = N'Normal',
    @PriorityReason nvarchar(500) = NULL,
    @DueAt          datetime2     = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'RequestInspection';
    SET @Priority = ISNULL(NULLIF(LTRIM(RTRIM(@Priority)), N''), N'Normal');
    SET @PriorityReason = NULLIF(LTRIM(RTRIM(@PriorityReason)), N'');
    DECLARE @Now datetime2 = SYSUTCDATETIME();

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Priority NOT IN (N'Normal', N'Urgent')
        THROW 50400, N'우선순위는 Normal 또는 Urgent여야 합니다.', 1;
    IF @Priority = N'Urgent' AND @PriorityReason IS NULL
        THROW 52020, N'긴급 의뢰는 사유가 필요합니다.', 1;
    IF @Priority = N'Normal' SET @PriorityReason = NULL;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @ProducerId, @Priority, @PriorityReason, CONVERT(nvarchar(30), @DueAt, 126))), 2);
    -- V2(일정 기능 전) 해시: 업그레이드 전에 처리된 보통·기한 없는 의뢰를 같은 키로 다시 보내도 재전송으로 인정한다(R6)
    DECLARE @LegacyHash char(64) = IIF(@Priority = N'Normal' AND @DueAt IS NULL,
        CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|', @Operation, @LotId, @ProducerId)), 2), NULL);

    BEGIN TRANSACTION;

    DECLARE @SavedHash char(64), @SavedOperation nvarchar(50), @SavedResponse nvarchar(max);
    SELECT @SavedHash = BodyHash, @SavedOperation = Operation, @SavedResponse = ResponseJson
    FROM dbo.QcRequests WITH (UPDLOCK, HOLDLOCK)
    WHERE RequestKey = @RequestKey;

    IF @SavedHash IS NOT NULL
    BEGIN
        IF (@SavedHash <> @BodyHash AND (@LegacyHash IS NULL OR @SavedHash <> @LegacyHash)) OR @SavedOperation <> @Operation
        BEGIN
            ROLLBACK TRANSACTION;
            THROW 50409, N'같은 요청 키로 다른 내용의 요청이 이미 처리되었습니다. (R6)', 1;
        END;
        COMMIT TRANSACTION;
        SELECT @SavedResponse AS ResponseJson, CAST(1 AS bit) AS Replayed;
        RETURN;
    END;

    IF @DueAt IS NOT NULL AND @DueAt <= @Now
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 52020, N'희망 완료 기한은 지금 이후여야 합니다.', 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @ProducerId AND r.Name IN (N'Producer'))
        THROW 50403, N'생산 담당 역할이 있는 사용자만 검사를 의뢰할 수 있습니다.', 1;

    DECLARE @Status nvarchar(20);
    SELECT @Status = Status FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @Status IS NULL
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;
    IF @Status <> N'Registered'
        THROW 52012, N'등록 상태인 LOT만 검사를 의뢰할 수 있습니다.', 1;

    UPDATE dbo.Lots
    SET Status = N'Waiting', InspectionRequestedAt = @Now,
        Priority = @Priority, PriorityReason = @PriorityReason, DueAt = @DueAt
    WHERE Id = @LotId;

    INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
    VALUES (@LotId, N'Request', N'Uninspected', 0, @ProducerId,
            IIF(@Priority = N'Urgent', N'검사 의뢰 (긴급)', N'검사 의뢰'), N'Lots', @LotId, @Now);

    IF @Priority = N'Urgent' OR @DueAt IS NOT NULL
        INSERT INTO dbo.ScheduleEvents (LotId, Kind, Priority, DueAt, Reason, ActorId, CreatedAt)
        VALUES (@LotId, N'Request', @Priority, @DueAt, @PriorityReason, @ProducerId, @Now);

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
