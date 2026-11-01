/*
  V9: 불량 유형(@DefectCodeId, 2026-11-01 사용자 결정 "목록에서 고르고 기타로 입력")
      - 불합격·보류에 쓸 수 있다. 합격에 쓰면 50400, 없는·사용 중지 코드는 50400
      - "기타"(ETC)는 사유 직접 입력 필수(52004). 다른 코드는 사유를 비우면 코드 이름이 사유가 되고,
        사유를 쓰면 "코드 이름 - 사유"로 남는다(원장 사유도 같음)
      - 코드 없이 사유만 보내는 기존 호출(API)도 그대로 받는다
  V3: 측정값이 규격(InspectionItems.Lower~Upper)을 벗어나면 합격 판정 거절(52014, R11),
      합격은 전 검사 항목 측정 필수(52015, R11)
  V2: 검사 의뢰 전(Registered) LOT 거절(52012), 응답에 scrapped 추가
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
  - R11 측정값 중 하나라도 규격을 벗어나면 합격(Pass)으로 저장하지 않는다. 불합격·보류는 받는다.
        측정값 JSON: [{"inspectionItemId": 1, "name": "외경", "value": 140.02, ...}, ...]
        항목은 inspectionItemId로, 없으면 name으로 그 LOT 품목의 검사 항목과 맞춘다.
        합격은 그 품목의 검사 항목 전부에 숫자 측정값이 있어야 한다(52015). 생략·null·빈 값·빈 배열·일부 누락은 거절.
        정정으로 합격으로 바꿀 때도 새 측정값 전체가 필요하다(원 검사 측정값을 쓰지 않는다).
        불합격·보류는 미측정을 허용한다. 검사 항목이 없는 품목은 측정값 없이 합격할 수 있다.
        값이 숫자가 아니거나, 값은 있는데 그 품목의 검사 항목이 아니면 50400.
        측정값은 소수 10자리까지 비교한다(그보다 작은 자리는 반올림).

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
    @CorrectsInspectionId bigint = NULL,
    @DefectCodeId         int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Operation nvarchar(50) = N'ApplyInspection';
    SET @Reason = NULLIF(LTRIM(RTRIM(@Reason)), N'');
    DECLARE @RawReason nvarchar(500) = @Reason;   -- 요청 해시는 받은 그대로의 사유로 만든다

    IF @DefectCodeId IS NOT NULL AND @Verdict = N'Pass'
        THROW 50400, N'합격에는 불량 유형을 쓰지 않습니다.', 1;

    -- 입력 검사 (트랜잭션 전에 끝낼 수 있는 것)
    IF @RequestKey IS NULL OR LEN(@RequestKey) = 0
        THROW 50400, N'요청 키가 필요합니다.', 1;
    IF @Qty IS NULL OR @Qty <= 0
        THROW 50400, N'검사 수량은 1 이상이어야 합니다.', 1;
    IF @Verdict NOT IN (N'Pass', N'Fail', N'Hold') OR @Verdict IS NULL
        THROW 50400, N'판정은 Pass, Fail, Hold 중 하나여야 합니다.', 1;
    IF @Verdict IN (N'Fail', N'Hold') AND @Reason IS NULL AND @DefectCodeId IS NULL
        THROW 52004, N'불합격·보류 판정은 사유가 필요합니다. (R4)', 1;
    IF @CorrectsInspectionId IS NOT NULL AND @Reason IS NULL AND @DefectCodeId IS NULL
        THROW 52004, N'정정은 사유가 필요합니다. (R10)', 1;
    SET @MeasuredJson = NULLIF(LTRIM(RTRIM(@MeasuredJson)), N'');
    IF @MeasuredJson IS NOT NULL AND (ISJSON(@MeasuredJson) = 0 OR LEFT(@MeasuredJson, 1) <> N'[')
        THROW 50400, N'측정값은 JSON 배열이어야 합니다.', 1;

    DECLARE @BodyHash char(64) = CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT_WS(N'|',
        @Operation, @LotId, @InspectorId, @Qty, @Verdict, ISNULL(@MeasuredJson, N''), ISNULL(@RawReason, N''),
        ISNULL(CONVERT(nvarchar(20), @CorrectsInspectionId), N''),
        CONVERT(nvarchar(20), @DefectCodeId))), 2);   -- 코드가 없으면 CONCAT_WS가 건너뛰어 V3과 같은 해시

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

    -- 불량 유형 (멱등 재전송 뒤에 확인한다: 나중에 중지된 유형이어도 같은 키는 저장된 응답을 돌려준다)
    IF @DefectCodeId IS NOT NULL
    BEGIN
        DECLARE @DefectCode nvarchar(20), @DefectName nvarchar(100);
        SELECT @DefectCode = Code, @DefectName = Name FROM dbo.DefectCodes WHERE Id = @DefectCodeId AND IsActive = 1;
        IF @DefectCode IS NULL
            THROW 50400, N'없거나 사용 중지된 불량 유형입니다.', 1;
        IF @DefectCode = N'ETC'
        BEGIN
            IF @Reason IS NULL
                THROW 52004, N'불량 유형이 기타이면 사유를 직접 입력해야 합니다. (R4)', 1;
        END
        ELSE
            SET @Reason = LEFT(IIF(@Reason IS NULL, @DefectName, @DefectName + N' - ' + @Reason), 500);
    END;

    -- 작업자 역할
    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
                   WHERE ur.UserId = @InspectorId AND r.Name = N'Inspector')
        THROW 50403, N'검사자 역할이 있는 사용자만 검사 실적을 입력할 수 있습니다.', 1;

    -- R8: LOT 단위 잠금
    DECLARE @LotStatus nvarchar(20), @ItemId int;
    SELECT @LotStatus = Status, @ItemId = ItemId FROM dbo.Lots WITH (UPDLOCK, ROWLOCK) WHERE Id = @LotId;
    IF @LotStatus IS NULL
        THROW 50404, N'LOT을 찾을 수 없습니다.', 1;
    IF @LotStatus = N'Registered'
        THROW 52012, N'검사 의뢰 전 LOT은 검사할 수 없습니다. 생산 담당이 먼저 검사를 의뢰해야 합니다.', 1;

    -- R11: 측정값 규격 검사. 규격은 클라이언트가 보낸 lower·upper가 아니라 InspectionItems 값을 쓴다.
    DECLARE @Measured TABLE (InspectionItemId int NULL, Name nvarchar(200) NULL, RawValue nvarchar(100) NULL,
                             Value decimal(38, 10) NULL, MatchedId int NULL);
    IF @MeasuredJson IS NOT NULL
    BEGIN

        INSERT INTO @Measured (InspectionItemId, Name, RawValue)
        SELECT TRY_CAST(JSON_VALUE(j.value, '$.inspectionItemId') AS int),
               JSON_VALUE(j.value, '$.name'),
               NULLIF(LTRIM(RTRIM(JSON_VALUE(j.value, '$.value'))), N'')
        FROM OPENJSON(@MeasuredJson) j
        WHERE j.type = 5;

        UPDATE @Measured SET Value = TRY_CAST(RawValue AS decimal(38, 10));
        IF EXISTS (SELECT 1 FROM @Measured WHERE RawValue IS NOT NULL AND Value IS NULL)
            THROW 50400, N'측정값은 숫자여야 합니다.', 1;

        UPDATE m SET MatchedId = ii.Id
        FROM @Measured m
        JOIN dbo.InspectionItems ii ON ii.ItemId = @ItemId
            AND (ii.Id = m.InspectionItemId OR (m.InspectionItemId IS NULL AND ii.Name = m.Name));
        IF EXISTS (SELECT 1 FROM @Measured WHERE Value IS NOT NULL AND MatchedId IS NULL)
            THROW 50400, N'측정값에 이 LOT 품목의 검사 항목이 아닌 항목이 있습니다.', 1;

        IF @Verdict = N'Pass'
        BEGIN
            DECLARE @OutOfSpec nvarchar(1000) = (
                SELECT STRING_AGG(CONCAT(ii.Name, N' ', FORMAT(m.Value, N'0.##########'), ii.Unit,
                                         N'(규격 ', FORMAT(ii.Lower, N'0.####'), N'~',
                                         FORMAT(ii.Upper, N'0.####'), N')'), N', ')
                       WITHIN GROUP (ORDER BY ii.SortOrder)
                FROM @Measured m JOIN dbo.InspectionItems ii ON ii.Id = m.MatchedId
                WHERE m.Value < ii.Lower OR m.Value > ii.Upper);
            IF @OutOfSpec IS NOT NULL
            BEGIN
                DECLARE @SpecMessage nvarchar(2048) = CONCAT(N'규격을 벗어난 측정값이 있어 합격으로 저장할 수 없습니다: ',
                                                             @OutOfSpec, N'. 불합격 또는 보류로 입력하세요. (R11)');
                THROW 52014, @SpecMessage, 1;
            END;
        END;
    END;

    -- R11: 합격은 검사 항목 전부를 측정해야 한다
    IF @Verdict = N'Pass'
    BEGIN
        DECLARE @Missing nvarchar(1000) = (
            SELECT STRING_AGG(ii.Name, N', ') WITHIN GROUP (ORDER BY ii.SortOrder)
            FROM dbo.InspectionItems ii
            WHERE ii.ItemId = @ItemId
              AND NOT EXISTS (SELECT 1 FROM @Measured m WHERE m.MatchedId = ii.Id AND m.Value IS NOT NULL));
        IF @Missing IS NOT NULL
        BEGIN
            DECLARE @MissingMessage nvarchar(2048) = CONCAT(N'합격은 모든 검사 항목의 측정값이 필요합니다. 빠진 항목: ',
                                                            @Missing, N'. (R11)');
            THROW 52015, @MissingMessage, 1;
        END;
    END;

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

        INSERT INTO dbo.Inspections (LotId, InspectorId, Qty, Verdict, MeasuredJson, Reason, DefectCodeId, CreatedAt)
        VALUES (@LotId, @InspectorId, @Qty, @Verdict, @MeasuredJson, @Reason, @DefectCodeId, @Now);
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

        INSERT INTO dbo.Inspections (LotId, InspectorId, Qty, Verdict, MeasuredJson, Reason, DefectCodeId, CorrectsInspectionId, CreatedAt)
        VALUES (@LotId, @InspectorId, @Qty, @Verdict, @MeasuredJson, @Reason, @DefectCodeId, @CorrectsInspectionId, @Now);
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
               q.Scrapped AS [quantities.scrapped],
               q.Passed - q.Shipped - q.Reserved AS [quantities.available]
        FROM dbo.Lots l JOIN dbo.LotQuantities q ON q.LotId = l.Id
        WHERE l.Id = @LotId
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT INTO dbo.QcRequests (RequestKey, Operation, BodyHash, ResponseJson, CreatedAt)
    VALUES (@RequestKey, @Operation, @BodyHash, @Response, @Now);

    COMMIT TRANSACTION;

    SELECT @Response AS ResponseJson, CAST(0 AS bit) AS Replayed;
END;
