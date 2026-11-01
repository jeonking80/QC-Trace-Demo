/*
  불합격 처리: 불합격 수량 중 일부(또는 전부)를
  - Reinspect: 미검사로 되돌려 다시 검사받게 한다 (Failed → Uninspected)
  - Scrap:     폐기로 확정한다 (Failed → Scrapped)
  규칙: 사유 필수(R4·R10), 처리 수량 ≤ 현재 불합격 수량(52013), 검사자 역할, LOT UPDLOCK(R8), 멱등(R6)

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_DisposeFailed
    @RequestKey  nvarchar(100),
    @LotId       int,
    @InspectorId nvarchar(450),
    @Qty         int,
    @Action      nvarchar(10),
    @Reason      nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'DisposeFailed';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Qty IS NULL OR @Qty <= 0
        THROW 50400, N'처리 수량은 1 이상이어야 합니다.', 1;
    IF @Action NOT IN (N'Reinspect', N'Scrap') OR @Action IS NULL
        THROW 50400, N'처리 방법은 Reinspect 또는 Scrap이어야 합니다.', 1;
    IF @Reason IS NULL
        THROW 52004, N'불합격 처리는 사유가 필요합니다. (R4)', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @InspectorId, @Qty, @Action, @Reason)), 2);

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
                   WHERE ur.UserId = @InspectorId AND r.Name = N'Inspector')
        THROW 50403, N'검사자 역할이 있는 사용자만 불합격을 처리할 수 있습니다.', 1;

    DECLARE @LotExists bit = 0;
    SELECT @LotExists = 1 FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @LotExists = 0
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;

    DECLARE @Failed int;
    SELECT @Failed = Failed FROM dbo.LotQuantities WITH (UPDLOCK, ROWLOCK) WHERE LotId = @LotId;
    IF @Qty > @Failed
        THROW 52013, N'처리 수량이 현재 불합격 수량을 넘습니다.', 1;

    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @ToField nvarchar(20) = IIF(@Action = N'Reinspect', N'Uninspected', N'Scrapped');
    DECLARE @Kind nvarchar(20) = IIF(@Action = N'Reinspect', N'Reinspect', N'Scrap');

    INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
    VALUES (@LotId, @Kind, N'Failed', -@Qty, @InspectorId, @Reason, N'Lots', @LotId, @Now),
           (@LotId, @Kind, @ToField,  @Qty, @InspectorId, @Reason, N'Lots', @LotId, @Now);

    UPDATE dbo.LotQuantities
    SET Failed = Failed - @Qty,
        Uninspected = Uninspected + IIF(@Action = N'Reinspect', @Qty, 0),
        Scrapped = Scrapped + IIF(@Action = N'Scrap', @Qty, 0),
        UpdatedAt = @Now
    WHERE LotId = @LotId;

    EXEC dbo.usp_RefreshLotStatus @LotId;

    DECLARE @Response nvarchar(max) = (
        SELECT @LotId AS lotId, @Action AS action, @Qty AS qty, l.Status AS status,
               q.Uninspected AS [quantities.uninspected], q.Passed AS [quantities.passed],
               q.Failed AS [quantities.failed], q.Held AS [quantities.held], q.Scrapped AS [quantities.scrapped],
               q.Shipped AS [quantities.shipped], q.Reserved AS [quantities.reserved],
               q.Passed - q.Shipped - q.Reserved AS [quantities.available]
        FROM dbo.Lots l JOIN dbo.LotQuantities q ON q.LotId = l.Id
        WHERE l.Id = @LotId
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT INTO dbo.QcRequests (RequestKey, Operation, BodyHash, ResponseJson, CreatedAt)
    VALUES (@RequestKey, @Operation, @BodyHash, @Response, @Now);

    COMMIT TRANSACTION;

    SELECT @Response AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
