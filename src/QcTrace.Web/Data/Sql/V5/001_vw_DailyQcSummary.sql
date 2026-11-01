/*
  일일 검사·출하 집계 (한국 날짜 기준). 대시보드 불합격률·당일 출하와 일일 보고서(단위 9)가 같이 쓴다.
  - 한국은 서머타임이 없어 UTC + 9시간으로 날짜를 정한다.
  - 검사 수량은 정정이 아닌 검사 행의 Qty 합(R9 초과분 포함). 정정은 건수만 따로 센다.
  - 불합격률 = FailedQty / InspectedQty (InspectedQty가 0이면 NULL). 처음 판정 기준이며, 나중 정정은 반영하지 않는다.
*/
CREATE OR ALTER VIEW dbo.vw_DailyQcSummary
AS
WITH E AS (
    SELECT CAST(DATEADD(HOUR, 9, CreatedAt) AS date) AS SummaryDate,
           Qty AS InspectedQty,
           IIF(Verdict = N'Pass', Qty, 0) AS PassedQty,
           IIF(Verdict = N'Fail', Qty, 0) AS FailedQty,
           IIF(Verdict = N'Hold', Qty, 0) AS HeldQty,
           0 AS CorrectionCount, 0 AS ApprovedQty, 0 AS RejectedCount, 0 AS ShippedQty, 0 AS ShipmentCount
    FROM dbo.Inspections WHERE CorrectsInspectionId IS NULL
    UNION ALL
    SELECT CAST(DATEADD(HOUR, 9, CreatedAt) AS date), 0, 0, 0, 0, 1, 0, 0, 0, 0
    FROM dbo.Inspections WHERE CorrectsInspectionId IS NOT NULL
    UNION ALL
    SELECT CAST(DATEADD(HOUR, 9, CreatedAt) AS date), 0, 0, 0, 0, 0,
           IIF(Decision = N'Approved', ApprovedQty, 0), IIF(Decision = N'Rejected', 1, 0), 0, 0
    FROM dbo.ShipmentApprovals
    UNION ALL
    SELECT CAST(DATEADD(HOUR, 9, CreatedAt) AS date), 0, 0, 0, 0, 0, 0, 0, Qty, 1
    FROM dbo.Shipments
)
SELECT SummaryDate,
       SUM(InspectedQty) AS InspectedQty, SUM(PassedQty) AS PassedQty, SUM(FailedQty) AS FailedQty, SUM(HeldQty) AS HeldQty,
       SUM(CorrectionCount) AS CorrectionCount, SUM(ApprovedQty) AS ApprovedQty, SUM(RejectedCount) AS RejectedCount,
       SUM(ShippedQty) AS ShippedQty, SUM(ShipmentCount) AS ShipmentCount,
       CAST(SUM(FailedQty) AS decimal(9, 4)) / NULLIF(SUM(InspectedQty), 0) AS FailRate
FROM E
GROUP BY SummaryDate;
