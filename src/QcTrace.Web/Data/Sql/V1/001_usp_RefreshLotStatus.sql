/*
  LOT 상태를 현재 수량으로 다시 정한다. 다른 SP의 트랜잭션 안에서만 호출한다.
  - 출하 완료: 미검사 0, 보류 0, 합격 > 0, 합격 전량 출하
  - 검사 완료: 미검사 0
  - 부분 검사: 일부라도 검사됨
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
            WHEN q.Passed + q.Failed + q.Held > 0 THEN N'Inspecting'
            ELSE l.Status
        END
    FROM dbo.Lots l
    JOIN dbo.LotQuantities q ON q.LotId = l.Id
    WHERE l.Id = @LotId;
END;
