/*
  검사 실적 반영 (부분 검사·정정 포함)

  규칙
  - R2  부분 검사: 검사 수량만큼 미검사 → 합격/불합격/보류로 옮긴다
  - R3  정정은 원 행을 고치지 않고 새 행(CorrectsInspectionId)과 원장 Correct 행으로 기록
  - R4  불합격·보류·정정은 사유 필수
  - R6  같은 요청 키는 한 번만 반영, 재전송 시 저장된 응답 반환, 본문이 다르면 50409
  - R8  LOT 행을 UPDLOCK으로 잠가 같은 LOT 요청을 직렬화
  - R9  미검사 잔량보다 많이 검사하면 초과분은 보류로 기록하고 버리지 않는다
        (생산 수량 등식에서 초과분만큼 차이가 나므로 대사 화면에서 확인 대상이 된다)
  - R10 원장에 작업자·시각·사유 기록

  결과: 한 행 (ResponseJson, Replayed)
*/
CREATE OR ALTER PROCEDURE dbo.usp_ApplyInspection
    @RequestKey           nvarchar(100),
    @LotId                int,
    @InspectorId          nvarchar(450),
    @Qty                  int,
    @Verdict              nvarchar(10),
    @MeasuredJson         nvarchar(max) = NULL,
    @Reason               nvarchar(500) = NULL,
    @CorrectsInspectionId bigint = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'ApplyInspection';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');

    -- 입력 검사 (트랜잭션 전에 끝낼 수 있는 것)
    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Qty IS NULL OR @Qty <= 0
        THROW 50400, N'검사 수량은 1 이상이어야 합니다.', 1;
    IF @Verdict NOT IN (N'Pass', N'Fail', N'Hold') OR @Verdict IS NULL
        THROW 50400, N'판정은 Pass, Fail, Hold 중 하나여야 합니다.', 1;
    IF @Verdict IN (N'Fail', N'Hold') AND @Reason IS NULL
        THROW 52004, N'불합격·보류 판정은 사유가 필요합니다. (R4)', 1;
    IF @CorrectsInspectionId IS NOT NULL AND @Reason IS NULL
        THROW 52004, N'정정은 사유가 필요합니다. (R10)', 1;
    IF @MeasuredJson IS NOT NULL AND ISJSON(@MeasuredJson) = 0
        THROW 50400, N'측정값은 JSON 형식이어야 합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @InspectorId, @Qty, @Verdict, ISNULL(@MeasuredJson, N''), ISNULL(@Reason, N''),
        ISNULL(CONVERT(nvarchar(20), @CorrectsInspectionId), N''))), 2);

    BEGIN TRANSACTION;

    -- R6: 같은 키가 이미 있으면 저장된 응답을 돌려준다. HOLDLOCK으로 같은 키 동시 요청도 직렬화한다.
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

    -- 작업자 역할
    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @InspectorId AND r.Name = N'Inspector')
        THROW 50403, N'검사자 역할이 있는 사용자만 검사 실적을 입력할 수 있습니다.', 1;

    -- R8: LOT 단위 잠금
    DECLARE @LotExists bit = 0;
    SELECT @LotExists = 1 FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @LotExists = 0
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;

    DECLARE @Uninspected int, @Passed int, @Failed int, @Held int, @Shipped int, @Reserved int;
    SELECT @Uninspected = Uninspected, @Passed = Passed, @Failed = Failed, @Held = Held,
           @Shipped = Shipped, @Reserved = Reserved
    FROM dbo.LotQuantities WITH (UPDLOCK, ROWLOCK)
    WHERE LotId = @LotId;

    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @ToField nvarchar(20) = CASE @Verdict WHEN N'Pass' THEN N'Passed' WHEN N'Fail' THEN N'Failed' ELSE N'Held' END;
    DECLARE @LedgerReason nvarchar(500) = COALESCE(@Reason, N'검사 합격');
    DECLARE @InspectionId bigint, @Applied int = 0, @Overage int = 0;
    DECLARE @Ledger TABLE (Kind nvarchar(20), DeltaField nvarchar(20), Delta int, Reason nvarchar(500));

    IF @CorrectsInspectionId IS NULL
    BEGIN
        -- R2 / R9
        SET @Applied = IIF(@Qty <= @Uninspected, @Qty, @Uninspected);
        SET @Overage = @Qty - @Applied;

        INSERT INTO dbo.Inspections (LotId, InspectorId, Qty, Verdict, MeasuredJson, Reason, CreatedAt)
        VALUES (@LotId, @InspectorId, @Qty, @Verdict, @MeasuredJson, @Reason, @Now);
        SET @InspectionId = SCOPE_IDENTITY();

        IF @Applied > 0
            INSERT INTO @Ledger VALUES
                (N'Inspect', N'Uninspected', -@Applied, @LedgerReason),
                (N'Inspect', @ToField,        @Applied, @LedgerReason);

        IF @Overage > 0
            INSERT INTO @Ledger VALUES
                (N'Inspect', N'Held', @Overage,
                 CONCAT(N'생산 수량 초과 검사분 ', @Overage, N'개 보류 (R9)', IIF(@Reason IS NULL, N'', N': ' + @Reason)));
    END
    ELSE
    BEGIN
        -- R3: 정정. 원 검사 판정의 수량 일부(또는 전부)를 새 판정으로 옮긴다.
        DECLARE @OrigVerdict nvarchar(10), @OrigQty int, @AlreadyCorrected int;
        SELECT @OrigVerdict = Verdict, @OrigQty = Qty
        FROM dbo.Inspections
        WHERE Id = @CorrectsInspectionId AND LotId = @LotId;

        IF @OrigVerdict IS NULL
            THROW 50404, N'정정할 원 검사 행이 이 LOT에 없습니다.', 1;
        IF @OrigVerdict = @Verdict
            THROW 50400, N'정정 판정이 원 판정과 같습니다.', 1;

        SELECT @AlreadyCorrected = COALESCE(SUM(Qty), 0)
        FROM dbo.Inspections
        WHERE CorrectsInspectionId = @CorrectsInspectionId;

        IF @Qty > @OrigQty - @AlreadyCorrected
            THROW 52011, N'정정 수량이 원 검사의 남은 수량을 넘습니다. (R3)', 1;

        DECLARE @FromField nvarchar(20) = CASE @OrigVerdict WHEN N'Pass' THEN N'Passed' WHEN N'Fail' THEN N'Failed' ELSE N'Held' END;
        DECLARE @FromQty int = CASE @FromField WHEN N'Passed' THEN @Passed - @Shipped - @Reserved
                                               WHEN N'Failed' THEN @Failed ELSE @Held END;
        IF @Qty > @FromQty
            THROW 52011, N'정정할 수량이 현재 남은 수량(합격은 출하·예약분 제외)을 넘습니다. (R3)', 1;

        SET @Applied = @Qty;

        INSERT INTO dbo.Inspections (LotId, InspectorId, Qty, Verdict, MeasuredJson, Reason, CorrectsInspectionId, CreatedAt)
        VALUES (@LotId, @InspectorId, @Qty, @Verdict, @MeasuredJson, @Reason, @CorrectsInspectionId, @Now);
        SET @InspectionId = SCOPE_IDENTITY();

        INSERT INTO @Ledger VALUES
            (N'Correct', @FromField, -@Qty, @Reason),
            (N'Correct', @ToField,    @Qty, @Reason);
    END;

    INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
    SELECT @LotId, Kind, DeltaField, Delta, @InspectorId, Reason, N'Inspections', @InspectionId, @Now
    FROM @Ledger;

    UPDATE q SET
        Uninspected = Uninspected + COALESCE((SELECT SUM(Delta) FROM @Ledger WHERE DeltaField = N'Uninspected'), 0),
        Passed      = Passed      + COALESCE((SELECT SUM(Delta) FROM @Ledger WHERE DeltaField = N'Passed'), 0),
        Failed      = Failed      + COALESCE((SELECT SUM(Delta) FROM @Ledger WHERE DeltaField = N'Failed'), 0),
        Held        = Held        + COALESCE((SELECT SUM(Delta) FROM @Ledger WHERE DeltaField = N'Held'), 0),
        UpdatedAt   = @Now
    FROM dbo.LotQuantities q
    WHERE q.LotId = @LotId;

    EXEC dbo.usp_RefreshLotStatus @LotId;

    DECLARE @Response nvarchar(max) = (
        SELECT @LotId AS lotId, @InspectionId AS inspectionId, @Verdict AS verdict,
               @Applied AS appliedQty, @Overage AS overageQty, l.Status AS status,
               q.Uninspected AS [quantities.uninspected], q.Passed AS [quantities.passed],
               q.Failed AS [quantities.failed], q.Held AS [quantities.held],
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
