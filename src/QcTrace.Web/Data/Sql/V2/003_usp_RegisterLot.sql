/*
  생산 LOT 등록

  - LOT 번호: LOT-yyyyMMdd-NNN (생산일 기준, 같은 날 안에서 일련번호). 번호 결정 구간을 잠가 동시 등록에도 겹치지 않는다
  - Lots + LotQuantities(미검사 = 생산 수량) + Ledger Produce 행을 한 트랜잭션에
  - R6 같은 요청 키는 한 번만 반영 / R10 원장에 작업자·시각·사유

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_RegisterLot
    @RequestKey  nvarchar(100),
    @ProducerId  nvarchar(450),
    @ItemId      int,
    @ProducedQty int,
    @ProducedOn  date,
    @Shift       nvarchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'RegisterLot';
    SET @Shift = NULLIF(LTRIM(RTRIM(@Shift)), N'');

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @ProducedQty IS NULL OR @ProducedQty <= 0
        THROW 50400, N'생산 수량은 1 이상이어야 합니다.', 1;
    IF @ProducedOn IS NULL
        THROW 50400, N'생산일이 필요합니다.', 1;
    IF @Shift IS NULL
        THROW 50400, N'작업반이 필요합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @ProducerId, @ItemId, @ProducedQty, CONVERT(nvarchar(10), @ProducedOn, 23), @Shift)), 2);

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
        THROW 50403, N'생산 담당 역할이 있는 사용자만 LOT을 등록할 수 있습니다.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Items WHERE Id = @ItemId AND IsActive = 1)
        THROW 50404, N'사용 중인 품목을 찾을 수 없습니다.', 1;

    DECLARE @Prefix nvarchar(20) = CONCAT(N'LOT-', FORMAT(@ProducedOn, 'yyyyMMdd'), N'-');
    DECLARE @Seq int = 1 + COALESCE((
        SELECT MAX(TRY_CAST(SUBSTRING(LotNo, LEN(@Prefix) + 1, 10) AS int))
        FROM dbo.Lots WITH (UPDLOCK, HOLDLOCK)
        WHERE LotNo LIKE @Prefix + N'%'), 0);
    DECLARE @LotNo nvarchar(30) = CONCAT(@Prefix, FORMAT(@Seq, '000'));

    DECLARE @Now datetime2 = SYSUTCDATETIME(), @LotId int;

    INSERT INTO dbo.Lots (LotNo, ItemId, ProducedQty, ProducedOn, Shift, Status, CreatedById, CreatedAt)
    VALUES (@LotNo, @ItemId, @ProducedQty, @ProducedOn, @Shift, N'Registered', @ProducerId, @Now);
    SET @LotId = SCOPE_IDENTITY();

    INSERT INTO dbo.LotQuantities (LotId, Uninspected, Passed, Failed, Held, Shipped, Reserved, Scrapped, UpdatedAt)
    VALUES (@LotId, @ProducedQty, 0, 0, 0, 0, 0, 0, @Now);

    INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
    VALUES (@LotId, N'Produce', N'Uninspected', @ProducedQty, @ProducerId, N'생산 LOT 등록', N'Lots', @LotId, @Now);

    DECLARE @Response nvarchar(max) = (
        SELECT @LotId AS lotId, @LotNo AS lotNo, N'Registered' AS status, @ProducedQty AS producedQty
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT INTO dbo.QcRequests (RequestKey, Operation, BodyHash, ResponseJson, CreatedAt)
    VALUES (@RequestKey, @Operation, @BodyHash, @Response, @Now);

    COMMIT TRANSACTION;

    SELECT @Response AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
