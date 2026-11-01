/*
  검사 의뢰: Registered → Waiting, 의뢰 시각 기록 (48시간 지연 판단 기준)
  - 수량 변화는 없지만 R10 감사 추적을 위해 원장에 Request 행(Delta 0)을 남긴다
  - R6 같은 요청 키는 한 번만 반영

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_RequestInspection
    @RequestKey nvarchar(100),
    @LotId      int,
    @ProducerId nvarchar(450)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'RequestInspection';

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @ProducerId)), 2);

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
                   WHERE ur.UserId = @ProducerId AND r.Name = N'Producer')
        THROW 50403, N'생산 담당 역할이 있는 사용자만 검사를 의뢰할 수 있습니다.', 1;

    DECLARE @Status nvarchar(20);
    SELECT @Status = Status FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @Status IS NULL
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;
    IF @Status <> N'Registered'
        THROW 52012, N'등록 상태인 LOT만 검사를 의뢰할 수 있습니다.', 1;

    DECLARE @Now datetime2 = SYSUTCDATETIME();

    UPDATE dbo.Lots SET Status = N'Waiting', InspectionRequestedAt = @Now WHERE Id = @LotId;

    INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
    VALUES (@LotId, N'Request', N'Uninspected', 0, @ProducerId, N'검사 의뢰', N'Lots', @LotId, @Now);

    DECLARE @Response nvarchar(max) = (
        SELECT @LotId AS lotId, N'Waiting' AS status, @Now AS inspectionRequestedAt
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT INTO dbo.QcRequests (RequestKey, Operation, BodyHash, ResponseJson, CreatedAt)
    VALUES (@RequestKey, @Operation, @BodyHash, @Response, @Now);

    COMMIT TRANSACTION;

    SELECT @Response AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
