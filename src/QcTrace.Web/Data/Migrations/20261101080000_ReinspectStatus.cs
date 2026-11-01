using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <summary>전량 재검사 후 LOT 상태가 검사 완료로 남던 문제 수정(V6). 이미 그렇게 남은 LOT은 부분 검사로 바로잡는다. 스키마 변경 없음.</summary>
    public partial class ReinspectStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("V6/001_usp_RefreshLotStatus.sql"));
            migrationBuilder.Sql("""
                UPDATE l SET Status = N'Inspecting'
                FROM dbo.Lots l JOIN dbo.LotQuantities q ON q.LotId = l.Id
                WHERE l.Status IN (N'Inspected', N'Shipped') AND q.Uninspected > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("V2/001_usp_RefreshLotStatus.sql"));
        }
    }
}
