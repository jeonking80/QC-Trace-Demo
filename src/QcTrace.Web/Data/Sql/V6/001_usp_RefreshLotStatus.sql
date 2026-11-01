/*
  V6: 재검사로 미검사가 다시 생긴 LOT을 검사 목록에 되돌린다
  V2에서는 전량 불합격(검사 완료) 뒤 전량 재검사하면 합격·불합격·보류·폐기가 모두 0이 되어
  "그 외: 현재 상태 유지"로 떨어져 검사 완료(Inspected)인 채 미검사가 남았다. 검사 화면은 대기·부분 검사만 보여주므로
  그 LOT을 다시 검사할 수 없었다(단위 10 E2E 시나리오 2에서 발견).
  - 출하 완료: 미검사 0, 보류 0, 합격 > 0, 합격 전량 출하
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
            WHEN q.Uninspected = 0 AND q.Held = 0 AND q.Passed > 0 AND q.Shipped = q.Passed THEN N'Shipped'
            WHEN q.Uninspected = 0 THEN N'Inspected'
            WHEN q.Passed + q.Failed + q.Held + q.Scrapped > 0 THEN N'Inspecting'
            WHEN l.Status IN (N'Inspected', N'Shipped') THEN N'Inspecting'
            ELSE l.Status
        END
    FROM dbo.Lots l
    JOIN dbo.LotQuantities q ON q.LotId = l.Id
    WHERE l.Id = @LotId;
END;
