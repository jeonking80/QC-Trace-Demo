/*
  LOT별 재고 대사. 앱 계산을 쓰지 않고 DB에서 바로 비교한다.
  - LedgerMatches: LotQuantities 각 열 = Ledger의 같은 DeltaField Delta 합
  - ProductionDiff = (미검사 + 합격 + 불합격 + 보류 + 폐기) − 생산 수량
  - OverageQty: R9 초과 보류 수량. 정정이 아닌 검사 행마다 (검사 수량 − 그 행이 미검사에서 실제로 뺀 수량)의 합.
    원장 사유 문구가 아니라 검사 행과 원장 행의 수량으로 계산한다.
  - ShipmentWithinPassed: 출하 + 예약 ≤ 합격
  - State
      'Mismatch'    원장 불일치, 생산 등식 차이가 R9 초과분과 다름, 또는 출하·예약이 합격을 넘음
      'OverageHeld' 위가 모두 맞고 R9 초과 보류가 있음 (생산 등식은 초과분만큼 어긋나는 것이 정상, PLAN 3장)
      'OK'          그 밖
*/
CREATE OR ALTER VIEW dbo.vw_LotReconciliation
AS
WITH L AS (
    SELECT LotId,
           SUM(CASE WHEN DeltaField = N'Uninspected' THEN Delta ELSE 0 END) AS Uninspected,
           SUM(CASE WHEN DeltaField = N'Passed'      THEN Delta ELSE 0 END) AS Passed,
           SUM(CASE WHEN DeltaField = N'Failed'      THEN Delta ELSE 0 END) AS Failed,
           SUM(CASE WHEN DeltaField = N'Held'        THEN Delta ELSE 0 END) AS Held,
           SUM(CASE WHEN DeltaField = N'Scrapped'    THEN Delta ELSE 0 END) AS Scrapped,
           SUM(CASE WHEN DeltaField = N'Shipped'     THEN Delta ELSE 0 END) AS Shipped,
           SUM(CASE WHEN DeltaField = N'Reserved'    THEN Delta ELSE 0 END) AS Reserved
    FROM dbo.Ledger
    GROUP BY LotId
), O AS (
    SELECT i.LotId, SUM(i.Qty - ISNULL(a.Applied, 0)) AS OverageQty
    FROM dbo.Inspections i
    OUTER APPLY (SELECT -SUM(g.Delta) AS Applied FROM dbo.Ledger g
                 WHERE g.SourceTable = N'Inspections' AND g.SourceId = i.Id
                   AND g.Kind = N'Inspect' AND g.DeltaField = N'Uninspected') a
    WHERE i.CorrectsInspectionId IS NULL
    GROUP BY i.LotId
), R AS (
    SELECT lot.Id AS LotId, lot.LotNo, lot.ItemId, lot.Status, lot.ProducedQty, lot.ProducedOn,
           q.Uninspected, q.Passed, q.Failed, q.Held, q.Scrapped, q.Shipped, q.Reserved,
           ISNULL(L.Uninspected, 0) AS LedgerUninspected, ISNULL(L.Passed, 0) AS LedgerPassed,
           ISNULL(L.Failed, 0) AS LedgerFailed, ISNULL(L.Held, 0) AS LedgerHeld,
           ISNULL(L.Scrapped, 0) AS LedgerScrapped, ISNULL(L.Shipped, 0) AS LedgerShipped,
           ISNULL(L.Reserved, 0) AS LedgerReserved,
           ISNULL(O.OverageQty, 0) AS OverageQty,
           q.Uninspected + q.Passed + q.Failed + q.Held + q.Scrapped - lot.ProducedQty AS ProductionDiff,
           CAST(CASE WHEN q.Uninspected = ISNULL(L.Uninspected, 0) AND q.Passed = ISNULL(L.Passed, 0)
                      AND q.Failed = ISNULL(L.Failed, 0) AND q.Held = ISNULL(L.Held, 0)
                      AND q.Scrapped = ISNULL(L.Scrapped, 0) AND q.Shipped = ISNULL(L.Shipped, 0)
                      AND q.Reserved = ISNULL(L.Reserved, 0) THEN 1 ELSE 0 END AS bit) AS LedgerMatches,
           CAST(CASE WHEN q.Shipped + q.Reserved <= q.Passed THEN 1 ELSE 0 END AS bit) AS ShipmentWithinPassed
    FROM dbo.Lots lot
    JOIN dbo.LotQuantities q ON q.LotId = lot.Id
    LEFT JOIN L ON L.LotId = lot.Id
    LEFT JOIN O ON O.LotId = lot.Id
)
SELECT R.*,
       CASE WHEN R.LedgerMatches = 0 OR R.ShipmentWithinPassed = 0 OR R.ProductionDiff <> R.OverageQty THEN N'Mismatch'
            WHEN R.OverageQty > 0 THEN N'OverageHeld'
            ELSE N'OK' END AS State
FROM R;
