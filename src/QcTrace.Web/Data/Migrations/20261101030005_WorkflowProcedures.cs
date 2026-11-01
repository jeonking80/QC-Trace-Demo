using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowProcedures : Migration
    {
        // 열·제약 변경 뒤에 실행한다. 001·002는 V1 SP를 다시 만든다(폐기 수량, 의뢰 전 검사 거절).
        private static readonly string[] Scripts =
        [
            "V2/001_usp_RefreshLotStatus.sql",
            "V2/002_usp_ApplyInspection.sql",
            "V2/003_usp_RegisterLot.sql",
            "V2/004_usp_RequestInspection.sql",
            "V2/005_usp_DisposeFailed.sql",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LotQuantities_NonNegative",
                table: "LotQuantities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Ledger_DeltaField",
                table: "Ledger");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Ledger_Kind",
                table: "Ledger");

            migrationBuilder.AddColumn<int>(
                name: "Scrapped",
                table: "LotQuantities",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LotQuantities_NonNegative",
                table: "LotQuantities",
                sql: "[Uninspected] >= 0 AND [Passed] >= 0 AND [Failed] >= 0 AND [Held] >= 0 AND [Shipped] >= 0 AND [Reserved] >= 0 AND [Scrapped] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Ledger_DeltaField",
                table: "Ledger",
                sql: "[DeltaField] IN ('Uninspected', 'Passed', 'Failed', 'Held', 'Shipped', 'Reserved', 'Scrapped')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Ledger_Kind",
                table: "Ledger",
                sql: "[Kind] IN ('Produce', 'Request', 'Inspect', 'Correct', 'Reinspect', 'Scrap', 'Approve', 'Ship')");

            foreach (var script in Scripts)
            {
                migrationBuilder.Sql(SqlScripts.Read(script));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_DisposeFailed;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_RequestInspection;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_RegisterLot;");
            migrationBuilder.Sql(SqlScripts.Read("V1/001_usp_RefreshLotStatus.sql"));
            migrationBuilder.Sql(SqlScripts.Read("V1/002_usp_ApplyInspection.sql"));

            migrationBuilder.DropCheckConstraint(
                name: "CK_LotQuantities_NonNegative",
                table: "LotQuantities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Ledger_DeltaField",
                table: "Ledger");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Ledger_Kind",
                table: "Ledger");

            migrationBuilder.DropColumn(
                name: "Scrapped",
                table: "LotQuantities");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LotQuantities_NonNegative",
                table: "LotQuantities",
                sql: "[Uninspected] >= 0 AND [Passed] >= 0 AND [Failed] >= 0 AND [Held] >= 0 AND [Shipped] >= 0 AND [Reserved] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Ledger_DeltaField",
                table: "Ledger",
                sql: "[DeltaField] IN ('Uninspected', 'Passed', 'Failed', 'Held', 'Shipped', 'Reserved')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Ledger_Kind",
                table: "Ledger",
                sql: "[Kind] IN ('Produce', 'Inspect', 'Correct', 'Approve', 'Ship')");
        }
    }
}
