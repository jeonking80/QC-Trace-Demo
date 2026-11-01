/*
  V7: 불합격 처리가 남은 LOT은 출하 완료로 두지 않는다
  V6까지는 합격 전량을 출하하면 불합격 수량이 남아 있어도 출하 완료(Shipped)가 되어,
  검사자 화면(불합격 처리 대기)에는 남고 대시보드 조치 목록(출하 완료 LOT 제외)에서는 빠졌다(집 PC 피드백 A2).
  - 출하 완료: 미검사 0, 보류 0, 불합격 0, 합격 > 0, 합격 전량 출하
  - 검사 완료: 미검사 0
  - 부분 검사: 일부라도 검사됨(폐기 포함), 또는 미검사가 다시 생긴 검사 완료·출하 완료 LOT
  - 그 외: 현재 상태(Registered / Waiting) 유지
*/
CREATE OR ALTER PROCEDURE dbo.usp_RefreshLotStatus
    @LotId int
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE l
    SET Status = CASE
            WHEN q.Uninspected = 0 AND q.Held = 0 AND q.Failed = 0 AND q.Passed > 0 AND q.Shipped = q.Passed THEN N'Shipped'
            WHEN q.Uninspected = 0 THEN N'Inspected'
            WHEN q.Passed + q.Failed + q.Held + q.Scrapped > 0 THEN N'Inspecting'
            WHEN l.Status IN (N'Inspected', N'Shipped') THEN N'Inspecting'
            ELSE l.Status
        END
    FROM dbo.Lots l
    JOIN dbo.LotQuantities q ON q.LotId = l.Id
    WHERE l.Id = @LotId;
END;
