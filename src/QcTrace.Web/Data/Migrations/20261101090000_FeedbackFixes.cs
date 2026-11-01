using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <summary>
    /// 집 PC 피드백 결함 수정(스키마 변경 없음).
    /// A2: 불합격이 남은 LOT은 출하 완료가 되지 않게 상태 규칙 수정(V7), 이미 그렇게 된 LOT은 검사 완료로 되돌림.
    /// A1: 검사 의뢰 시각은 있는데 원장에 검사 의뢰 행이 없는 LOT(이전 시드)에 Request 행 보충. 원장은 추가만 하므로 지우지 않는다.
    /// </summary>
    public partial class FeedbackFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("V7/001_usp_RefreshLotStatus.sql"));
            migrationBuilder.Sql("""
                UPDATE l SET Status = N'Inspected'
                FROM dbo.Lots l JOIN dbo.LotQuantities q ON q.LotId = l.Id
                WHERE l.Status = N'Shipped' AND q.Failed > 0;
                """);
            migrationBuilder.Sql("""
                INSERT INTO dbo.Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt)
                SELECT l.Id, N'Request', N'Uninspected', 0, l.CreatedById, N'검사 의뢰 (원장 보충: 이전 시드에 빠진 기록)', N'Lots', l.Id, l.InspectionRequestedAt
                FROM dbo.Lots l
                WHERE l.InspectionRequestedAt IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM dbo.Ledger g WHERE g.LotId = l.Id AND g.Kind = N'Request');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 보충한 원장 행은 추가 전용이라 지우지 않는다. 상태 규칙만 V6으로 되돌린다.
            migrationBuilder.Sql(SqlScripts.Read("V6/001_usp_RefreshLotStatus.sql"));
        }
    }
}
