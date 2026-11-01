/*
  QC Trace 시드 데이터 (가상 데이터)
  - 품목 5종, 품목당 검사 항목 3~4개, LOT 30건
  - LOT 구성: 출하 완료 4 / 승인·예약 4 / 검사 완료(불합격·보류 포함) 4 / 부분 검사 6 / 검사 대기 6 / 등록 6
  - 수량은 Ledger에 먼저 기록하고, LotQuantities는 Ledger 합으로 계산한다 (두 값이 처음부터 일치)
  - 사용자(Users)는 C# 시더가 먼저 만든다. 이 스크립트는 UserName으로 Id를 찾는다.
  - 시각은 UTC. 실행 시점 기준 상대 시각이라 언제 실행해도 지연 LOT이 섞여 나온다.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM dbo.Lots)
BEGIN
    PRINT N'LOT 데이터가 이미 있어 시드를 건너뜁니다.';
    RETURN;
END;

DECLARE @producer   nvarchar(450) = (SELECT Id FROM dbo.Users WHERE UserName = N'producer');
DECLARE @inspector1 nvarchar(450) = (SELECT Id FROM dbo.Users WHERE UserName = N'inspector1');
DECLARE @inspector2 nvarchar(450) = (SELECT Id FROM dbo.Users WHERE UserName = N'inspector2');
DECLARE @approver   nvarchar(450) = (SELECT Id FROM dbo.Users WHERE UserName = N'approver');
DECLARE @shipper    nvarchar(450) = (SELECT Id FROM dbo.Users WHERE UserName = N'shipper');

IF @producer IS NULL OR @inspector1 IS NULL OR @inspector2 IS NULL OR @approver IS NULL OR @shipper IS NULL
    THROW 50001, N'시드 사용자(producer, inspector1, inspector2, approver, shipper)가 없습니다. C# 시더를 먼저 실행하세요.', 1;

DECLARE @now datetime2 = SYSUTCDATETIME();

BEGIN TRANSACTION;

/* 1. 품목 */
INSERT INTO dbo.Items (ItemCode, Name, Spec, IsActive) VALUES
    (N'PV-010', N'소형 압력 용기 10L', N'설계압력 15 MPa, 강철제 (가상 규격)', 1),
    (N'PV-040', N'중형 압력 용기 40L', N'설계압력 15 MPa, 강철제 (가상 규격)', 1),
    (N'VL-200', N'용기 밸브 어셈블리', N'황동 단조 (가상 규격)', 1),
    (N'FN-050', N'플랜지 노즐 50A', N'탄소강 (가상 규격)', 1),
    (N'HD-100', N'용기 보호캡', N'강판 프레스 (가상 규격)', 1);

/* 2. 검사 항목 (품목당 3~4개) */
INSERT INTO dbo.InspectionItems (ItemId, Name, Lower, Upper, Unit, SortOrder)
SELECT i.Id, v.Name, v.Lower, v.Upper, v.Unit, v.SortOrder
FROM (VALUES
    (N'PV-010', N'외경',         139.0,  141.0, N'mm',     1),
    (N'PV-010', N'두께',           5.0,    5.6, N'mm',     2),
    (N'PV-010', N'내압 시험 압력', 22.5,   23.0, N'MPa',    3),
    (N'PV-010', N'중량',          13.0,   14.0, N'kg',     4),
    (N'PV-040', N'외경',         228.0,  232.0, N'mm',     1),
    (N'PV-040', N'두께',           5.6,    6.2, N'mm',     2),
    (N'PV-040', N'내압 시험 압력', 22.5,   23.0, N'MPa',    3),
    (N'PV-040', N'중량',          47.0,   50.0, N'kg',     4),
    (N'VL-200', N'나사 유효경',    27.6,   27.9, N'mm',     1),
    (N'VL-200', N'기밀 누설량',     0.0,    0.5, N'cc/min', 2),
    (N'VL-200', N'체결 토크',     120.0,  150.0, N'N·m',    3),
    (N'FN-050', N'내경',          52.4,   53.0, N'mm',     1),
    (N'FN-050', N'플랜지 두께',    18.0,   19.0, N'mm',     2),
    (N'FN-050', N'볼트 구멍 피치', 125.0,  125.5, N'mm',     3),
    (N'HD-100', N'내경',         150.0,  151.0, N'mm',     1),
    (N'HD-100', N'두께',           2.8,    3.2, N'mm',     2),
    (N'HD-100', N'중량',           0.9,    1.1, N'kg',     3)
) AS v (ItemCode, Name, Lower, Upper, Unit, SortOrder)
JOIN dbo.Items i ON i.ItemCode = v.ItemCode;

/* 3. LOT 30건 계획: n이 작을수록 오래된 LOT이고 업무 흐름이 많이 진행돼 있다 */
CREATE TABLE #Lot (
    N           int          NOT NULL PRIMARY KEY,
    LotId       int          NULL,
    ItemId      int          NOT NULL,
    Qty         int          NOT NULL,
    CreatedAt   datetime2    NOT NULL,
    Status      nvarchar(20) NOT NULL
);

WITH Numbers AS (
    SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects
),
ItemOrder AS (
    SELECT Id, ROW_NUMBER() OVER (ORDER BY ItemCode) AS Seq FROM dbo.Items
)
INSERT INTO #Lot (N, ItemId, Qty, CreatedAt, Status)
SELECT
    n.N,
    io.Id,
    50 * (1 + n.N % 4),                                   -- 50, 100, 150, 200
    DATEADD(HOUR, -(31 - n.N) * 12, @now),                -- 12시간 간격, LOT 1이 가장 오래됨
    CASE
        WHEN n.N <= 4  THEN N'Shipped'
        WHEN n.N <= 12 THEN N'Inspected'
        WHEN n.N <= 18 THEN N'Inspecting'
        WHEN n.N <= 24 THEN N'Waiting'
        ELSE N'Registered'
    END
FROM Numbers n
JOIN ItemOrder io ON io.Seq = (n.N - 1) % 5 + 1;

INSERT INTO dbo.Lots (LotNo, ItemId, ProducedQty, ProducedOn, Shift, Status, CreatedById, CreatedAt, InspectionRequestedAt)
SELECT
    CONCAT(N'LOT-', FORMAT(DATEADD(HOUR, 9, l.CreatedAt), 'yyyyMMdd'), N'-', FORMAT(l.N, '000')),
    l.ItemId,
    l.Qty,
    CAST(DATEADD(HOUR, 9, l.CreatedAt) AS date),          -- 생산일은 한국 날짜
    CASE l.N % 3 WHEN 0 THEN N'A조' WHEN 1 THEN N'B조' ELSE N'C조' END,
    l.Status,
    @producer,
    l.CreatedAt,
    CASE
        WHEN l.N BETWEEN 22 AND 24 THEN DATEADD(HOUR, -(25 - l.N) * 10, @now)   -- 최근 의뢰 (10·20·30시간 전)
        WHEN l.N <= 21 THEN DATEADD(HOUR, 1, l.CreatedAt)                      -- 등록 1시간 뒤 의뢰 (19~21은 48시간 초과 지연)
    END
FROM #Lot l;

UPDATE l SET LotId = lt.Id
FROM #Lot l
JOIN dbo.Lots lt ON lt.LotNo = CONCAT(N'LOT-', FORMAT(DATEADD(HOUR, 9, l.CreatedAt), 'yyyyMMdd'), N'-', FORMAT(l.N, '000'));

/* 4. 이벤트 계획 (생산 → 검사 → 승인 → 출하) */
CREATE TABLE #Event (
    Seq         int IDENTITY PRIMARY KEY,
    Ord         int           NULL,       -- 시간 순 처리 순서
    N           int           NOT NULL,
    EventType   nvarchar(20)  NOT NULL,   -- Produce / Inspect / Approve / Ship
    Verdict     nvarchar(10)  NULL,
    Qty         int           NOT NULL,
    ActorId     nvarchar(450) NOT NULL,
    Reason      nvarchar(500) NULL,
    Destination nvarchar(100) NULL,
    At          datetime2     NOT NULL
);

-- 생산 등록 (전 LOT)
INSERT INTO #Event (N, EventType, Qty, ActorId, Reason, At)
SELECT N, N'Produce', Qty, @producer, N'생산 LOT 등록', CreatedAt FROM #Lot ORDER BY N;

-- LOT 1~8: 전량 검사. 2·6번은 5% 불합격
INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, At)
SELECT N, N'Inspect', N'Pass', Qty - CASE WHEN N IN (2, 6) THEN Qty / 20 ELSE 0 END,
       CASE WHEN N % 2 = 1 THEN @inspector1 ELSE @inspector2 END, DATEADD(HOUR, 4, CreatedAt)
FROM #Lot WHERE N <= 8;

INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, At)
SELECT N, N'Inspect', N'Fail', Qty / 20, CASE WHEN N % 2 = 1 THEN @inspector1 ELSE @inspector2 END, DATEADD(HOUR, 5, CreatedAt)
FROM #Lot WHERE N IN (2, 6);

-- LOT 9~12: 검사 완료, 10% 불합격. 12번은 5% 보류
INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, At)
SELECT N, N'Inspect', N'Pass', Qty - Qty / 10 - CASE WHEN N = 12 THEN Qty / 20 ELSE 0 END,
       CASE WHEN N % 2 = 1 THEN @inspector1 ELSE @inspector2 END, DATEADD(HOUR, 4, CreatedAt)
FROM #Lot WHERE N BETWEEN 9 AND 12;

INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, At)
SELECT N, N'Inspect', N'Fail', Qty / 10, CASE WHEN N % 2 = 1 THEN @inspector1 ELSE @inspector2 END, DATEADD(HOUR, 5, CreatedAt)
FROM #Lot WHERE N BETWEEN 9 AND 12;

INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, Reason, At)
SELECT N, N'Inspect', N'Hold', Qty / 20, @inspector2, N'측정값 경계, 재측정 대기', DATEADD(HOUR, 6, CreatedAt)
FROM #Lot WHERE N = 12;

-- LOT 13~18: 부분 검사. 13~15는 60%, 16~18은 40% + 20% 두 번에 나눠 검사
INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, At)
SELECT N, N'Inspect', N'Pass', CASE WHEN N <= 15 THEN Qty * 6 / 10 ELSE Qty * 4 / 10 END, @inspector1, DATEADD(HOUR, 4, CreatedAt)
FROM #Lot WHERE N BETWEEN 13 AND 18;

INSERT INTO #Event (N, EventType, Verdict, Qty, ActorId, At)
SELECT N, N'Inspect', N'Pass', Qty * 2 / 10, @inspector2, DATEADD(HOUR, 6, CreatedAt)
FROM #Lot WHERE N BETWEEN 16 AND 18;

-- LOT 1~8: 합격 수량 전량 출하 승인 (승인권자는 검사자와 다른 사람)
INSERT INTO #Event (N, EventType, Qty, ActorId, Reason, At)
SELECT l.N, N'Approve', l.Qty - CASE WHEN l.N IN (2, 6) THEN l.Qty / 20 ELSE 0 END, @approver, N'검사 합격분 출하 승인', DATEADD(HOUR, 10, l.CreatedAt)
FROM #Lot l WHERE l.N <= 8;

-- LOT 1~4: 승인 수량 전량 출하
INSERT INTO #Event (N, EventType, Qty, ActorId, Destination, At)
SELECT l.N, N'Ship', l.Qty - CASE WHEN l.N = 2 THEN l.Qty / 20 ELSE 0 END, @shipper,
       CASE l.N WHEN 1 THEN N'부산 물류센터 (가상)' WHEN 2 THEN N'울산 고객사 A (가상)'
                WHEN 3 THEN N'거제 고객사 B (가상)' ELSE N'창원 대리점 C (가상)' END,
       DATEADD(HOUR, 20, l.CreatedAt)
FROM #Lot l WHERE l.N <= 4;

/* 5. 이벤트를 시간 순으로 원천 테이블 + 원장에 기록 */
WITH o AS (SELECT Ord, ROW_NUMBER() OVER (ORDER BY At, Seq) AS Rn FROM #Event)
UPDATE o SET Ord = Rn;

DECLARE @seq int = 1, @maxSeq int = (SELECT MAX(Ord) FROM #Event);
DECLARE @n int, @type nvarchar(20), @verdict nvarchar(10), @qty int, @actor nvarchar(450),
        @reason nvarchar(500), @dest nvarchar(100), @at datetime2,
        @lotId int, @itemId int, @sourceId bigint, @json nvarchar(max), @field nvarchar(20);

WHILE @seq <= @maxSeq
BEGIN
    SELECT @n = e.N, @type = e.EventType, @verdict = e.Verdict, @qty = e.Qty, @actor = e.ActorId,
           @reason = e.Reason, @dest = e.Destination, @at = e.At, @lotId = l.LotId, @itemId = l.ItemId
    FROM #Event e JOIN #Lot l ON l.N = e.N
    WHERE e.Ord = @seq;

    IF @type = N'Produce'
    BEGIN
        INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
        VALUES (@lotId, N'Produce', N'Uninspected', @qty, @actor, @reason, N'Lots', @lotId, @at);
    END
    ELSE IF @type = N'Inspect'
    BEGIN
        -- 측정값: 합격·보류는 규격 중앙값, 불합격은 첫 항목을 상한 10% 초과로 기록
        SET @json = (
            SELECT ii.Id AS inspectionItemId, ii.Name AS name,
                   CAST(CASE WHEN @verdict = N'Fail' AND ii.SortOrder = 1
                             THEN ii.Upper + (ii.Upper - ii.Lower) * 0.1
                             ELSE (ii.Lower + ii.Upper) / 2 END AS decimal(18, 3)) AS value,
                   ii.Unit AS unit
            FROM dbo.InspectionItems ii
            WHERE ii.ItemId = @itemId
            ORDER BY ii.SortOrder
            FOR JSON PATH);

        IF @verdict = N'Fail'
            SET @reason = (SELECT Name FROM dbo.InspectionItems WHERE ItemId = @itemId AND SortOrder = 1) + N' 상한 초과';

        INSERT INTO dbo.Inspections (LotId, InspectorId, Qty, Verdict, MeasuredJson, Reason, CreatedAt)
        VALUES (@lotId, @actor, @qty, @verdict, @json, @reason, @at);
        SET @sourceId = SCOPE_IDENTITY();

        SET @field = CASE @verdict WHEN N'Pass' THEN N'Passed' WHEN N'Fail' THEN N'Failed' ELSE N'Held' END;
        SET @reason = COALESCE(@reason, N'검사 합격');

        INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
        VALUES (@lotId, N'Inspect', N'Uninspected', -@qty, @actor, @reason, N'Inspections', @sourceId, @at),
               (@lotId, N'Inspect', @field,          @qty, @actor, @reason, N'Inspections', @sourceId, @at);
    END
    ELSE IF @type = N'Approve'
    BEGIN
        INSERT INTO dbo.ShipmentApprovals (LotId, ApproverId, ApprovedQty, Decision, Reason, CreatedAt)
        VALUES (@lotId, @actor, @qty, N'Approved', @reason, @at);
        SET @sourceId = SCOPE_IDENTITY();

        INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
        VALUES (@lotId, N'Approve', N'Reserved', @qty, @actor, @reason, N'ShipmentApprovals', @sourceId, @at);
    END
    ELSE IF @type = N'Ship'
    BEGIN
        INSERT INTO dbo.Shipments (LotId, ShipperId, Qty, Destination, CreatedAt)
        VALUES (@lotId, @actor, @qty, @dest, @at);
        SET @sourceId = SCOPE_IDENTITY();
        SET @reason = N'출하: ' + @dest;

        INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
        VALUES (@lotId, N'Ship', N'Reserved', -@qty, @actor, @reason, N'Shipments', @sourceId, @at),
               (@lotId, N'Ship', N'Shipped',   @qty, @actor, @reason, N'Shipments', @sourceId, @at);
    END;

    SET @seq += 1;
END;

/* 6. LotQuantities = Ledger 합 */
INSERT INTO dbo.LotQuantities (LotId, Uninspected, Passed, Failed, Held, Shipped, Reserved, UpdatedAt)
SELECT
    lt.Id,
    COALESCE(SUM(CASE WHEN g.DeltaField = N'Uninspected' THEN g.Delta END), 0),
    COALESCE(SUM(CASE WHEN g.DeltaField = N'Passed'      THEN g.Delta END), 0),
    COALESCE(SUM(CASE WHEN g.DeltaField = N'Failed'      THEN g.Delta END), 0),
    COALESCE(SUM(CASE WHEN g.DeltaField = N'Held'        THEN g.Delta END), 0),
    COALESCE(SUM(CASE WHEN g.DeltaField = N'Shipped'     THEN g.Delta END), 0),
    COALESCE(SUM(CASE WHEN g.DeltaField = N'Reserved'    THEN g.Delta END), 0),
    @now
FROM dbo.Lots lt
LEFT JOIN dbo.Ledger g ON g.LotId = lt.Id
GROUP BY lt.Id;

/* 7. 검사 의뢰 원장 (R10). 화면의 검사 의뢰(usp_RequestInspection)와 같은 모양: 수량 변화 없음(Delta 0) */
INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
SELECT lt.Id, N'Request', N'Uninspected', 0, lt.CreatedById, N'검사 의뢰', N'Lots', lt.Id, lt.InspectionRequestedAt
FROM dbo.Lots lt
WHERE lt.InspectionRequestedAt IS NOT NULL;

/* 8. LOT 상태를 수량에서 다시 정한다(앱의 상태 규칙 usp_RefreshLotStatus와 같게) */
DECLARE @refreshId int = (SELECT MIN(Id) FROM dbo.Lots);
WHILE @refreshId IS NOT NULL
BEGIN
    EXEC dbo.usp_RefreshLotStatus @refreshId;
    SET @refreshId = (SELECT MIN(Id) FROM dbo.Lots WHERE Id > @refreshId);
END;

COMMIT TRANSACTION;

DROP TABLE #Event;
DROP TABLE #Lot;
