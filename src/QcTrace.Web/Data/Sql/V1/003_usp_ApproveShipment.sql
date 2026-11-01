/*
  출하 승인

  규칙
  - R1  승인 수량은 출하 가능 수량(합격 − 출하 − 예약) 이내. 미검사·보류·불합격은 승인할 수 없다
  - R5  이 LOT을 한 번이라도 검사한 사람은 승인할 수 없다
  - R6  같은 요청 키는 한 번만 반영
  - R8  LOT 행 UPDLOCK
  - R10 반려는 사유 필수. 승인은 원장 Reserved +수량으로 기록

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_ApproveShipment
    @RequestKey nvarchar(100),
    @LotId      int,
    @ApproverId nvarchar(450),
    @Qty        int,
    @Decision   nvarchar(10) = N'Approved',
    @Reason     nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'ApproveShipment';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');

    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Decision NOT IN (N'Approved', N'Rejected') OR @Decision IS NULL
        THROW 50400, N'결정은 Approved 또는 Rejected여야 합니다.', 1;
    IF @Decision = N'Approved' AND (@Qty IS NULL OR @Qty <= 0)
        THROW 50400, N'승인 수량은 1 이상이어야 합니다.', 1;
    IF @Decision = N'Rejected' AND @Reason IS NULL
        THROW 52004, N'반려는 사유가 필요합니다. (R10)', 1;
    IF @Decision = N'Rejected'
        SET @Qty = 0;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @ApproverId, @Qty, @Decision, ISNULL(@Reason, N''))), 2);

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
                   WHERE ur.UserId = @ApproverId AND r.Name = N'Approver')
        THROW 50403, N'승인권자 역할이 있는 사용자만 출하를 승인할 수 있습니다.', 1;

    DECLARE @LotExists bit = 0;
    SELECT @LotExists = 1 FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @LotExists = 0
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;

    -- R5: 독립 승인
    IF EXISTS (SELECT 1 FROM dbo.Inspections WHERE LotId = @LotId AND InspectorId = @ApproverId)
        THROW 52005, N'이 LOT을 검사한 사람은 출하를 승인할 수 없습니다. (R5)', 1;

    DECLARE @Available int;
    SELECT @Available = Passed - Shipped - Reserved
    FROM dbo.LotQuantities WITH (UPDLOCK, ROWLOCK)
    WHERE LotId = @LotId;

    -- R1
    IF @Decision = N'Approved' AND @Qty > @Available
        THROW 52001, N'승인 수량이 출하 가능 수량(합격 − 출하 − 예약)을 넘습니다. 검사 전 수량은 승인할 수 없습니다. (R1)', 1;

    DECLARE @Now datetime2 = SYSUTCDATETIME(), @ApprovalId bigint;

    INSERT INTO dbo.ShipmentApprovals (LotId, ApproverId, ApprovedQty, Decision, Reason, CreatedAt)
    VALUES (@LotId, @ApproverId, @Qty, @Decision, @Reason, @Now);
    SET @ApprovalId = SCOPE_IDENTITY();

    IF @Decision = N'Approved'
    BEGIN
        INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
        VALUES (@LotId, N'Approve', N'Reserved', @Qty, @ApproverId, COALESCE(@Reason, N'출하 승인'), N'ShipmentApprovals', @ApprovalId, @Now);

        UPDATE dbo.LotQuantities SET Reserved = Reserved + @Qty, UpdatedAt = @Now WHERE LotId = @LotId;
    END;

    DECLARE @Response nvarchar(max) = (
        SELECT @LotId AS lotId, @ApprovalId AS approvalId, @Decision AS decision, @Qty AS approvedQty,
               l.Status AS status,
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
