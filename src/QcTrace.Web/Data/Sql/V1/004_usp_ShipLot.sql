/*
  출하 실적

  규칙
  - R7  출하 수량은 승인 잔량(예약) 이내. 출하하면 예약 → 출하로 옮긴다
  - R6  같은 요청 키는 한 번만 반영
  - R8  LOT 행 UPDLOCK
  - R10 원장에 작업자·시각·사유(출하처) 기록

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_ShipLot
    @RequestKey  nvarchar(100),
    @LotId       int,
    @ShipperId   nvarchar(450),
    @Qty         int,
    @Destination nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'ShipLot';
    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Qty IS NULL OR @Qty <= 0
        THROW 50400, N'출하 수량은 1 이상이어야 합니다.', 1;
    IF @Destination IS NULL
        THROW 50400, N'출하처가 필요합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @ShipperId, @Qty, @Destination)), 2);

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
                   WHERE ur.UserId = @ShipperId AND r.Name = N'Shipper')
        THROW 50403, N'출하 담당 역할이 있는 사용자만 출하할 수 있습니다.', 1;

    DECLARE @LotExists bit = 0;
    SELECT @LotExists = 1 FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @LotExists = 0
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;

    DECLARE @Reserved int;
    SELECT @Reserved = Reserved FROM dbo.LotQuantities WITH (UPDLOCK, ROWLOCK) WHERE LotId = @LotId;

    -- R7
    IF @Qty > @Reserved
        THROW 52007, N'출하 수량이 승인된 잔량을 넘습니다. (R7)', 1;

    DECLARE @Now datetime2 = SYSUTCDATETIME(), @ShipmentId bigint;
    DECLARE @LedgerReason nvarchar(500) = N'출하: ' + @Destination;

    INSERT INTO dbo.Shipments (LotId, ShipperId, Qty, Destination, CreatedAt)
    VALUES (@LotId, @ShipperId, @Qty, @Destination, @Now);
    SET @ShipmentId = SCOPE_IDENTITY();

    INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
    VALUES (@LotId, N'Ship', N'Reserved', -@Qty, @ShipperId, @LedgerReason, N'Shipments', @ShipmentId, @Now),
           (@LotId, N'Ship', N'Shipped',   @Qty, @ShipperId, @LedgerReason, N'Shipments', @ShipmentId, @Now);

    UPDATE dbo.LotQuantities
    SET Reserved = Reserved - @Qty, Shipped = Shipped + @Qty, UpdatedAt = @Now
    WHERE LotId = @LotId;

    EXEC dbo.usp_RefreshLotStatus @LotId;

    DECLARE @Response nvarchar(max) = (
        SELECT @LotId AS lotId, @ShipmentId AS shipmentId, @Qty AS shippedQty, l.Status AS status,
               q.Passed AS [quantities.passed], q.Shipped AS [quantities.shipped], q.Reserved AS [quantities.reserved],
               q.Passed - q.Shipped - q.Reserved AS [quantities.available]
        FROM dbo.Lots l JOIN dbo.LotQuantities q ON q.LotId = l.Id
        WHERE l.Id = @LotId
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT INTO dbo.QcRequests (RequestKey, Operation, BodyHash, ResponseJson, CreatedAt)
    VALUES (@RequestKey, @Operation, @BodyHash, @Response, @Now);

    COMMIT TRANSACTION;

    SELECT @Response AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
