using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <summary>
    /// 불량 유형 기준정보(2026-11-01 사용자 결정: 목록에서 고르고 "기타"면 직접 입력).
    /// 기본 유형을 넣고, 검사 SP(V9)가 @DefectCodeId를 받는다. 이전 검사 기록은 추가 전용이라 고치지 않는다(유형 없음 = 이전 자유 입력).
    /// </summary>
    public partial class DefectCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefectCodeId",
                table: "Inspections",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DefectCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DefectCodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_DefectCodeId",
                table: "Inspections",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_DefectCodes_Code",
                table: "DefectCodes",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Inspections_DefectCodes_DefectCodeId",
                table: "Inspections",
                column: "DefectCodeId",
                principalTable: "DefectCodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                INSERT INTO dbo.DefectCodes (Code, Name, IsActive, SortOrder) VALUES
                (N'DIM-OVER',  N'치수 상한 초과', 1, 10),
                (N'DIM-UNDER', N'치수 하한 미달', 1, 20),
                (N'SURFACE',   N'외관 흠집·찍힘', 1, 30),
                (N'WELD',      N'용접 불량', 1, 40),
                (N'LEAK',      N'누설·내압 불량', 1, 50),
                (N'WEIGHT',    N'중량 규격 밖', 1, 60),
                (N'RECHECK',   N'측정 재확인 필요(보류)', 1, 70),
                (N'ETC',       N'기타 (직접 입력)', 1, 999);
                """);
            migrationBuilder.Sql("GRANT INSERT, UPDATE ON dbo.DefectCodes TO qctrace_app_role;");
            migrationBuilder.Sql("DENY DELETE ON dbo.DefectCodes TO qctrace_app_role;");
            migrationBuilder.Sql(SqlScripts.Read("V9/001_usp_ApplyInspection.sql"));
            // 폐기는 관리자만(2026-11-01 사용자 결정). 재검사 요청은 검사자
            migrationBuilder.Sql(SqlScripts.Read("V9/002_usp_DisposeFailed.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("V3/001_usp_ApplyInspection.sql"));
            migrationBuilder.Sql(SqlScripts.Read("V2/005_usp_DisposeFailed.sql"));
            migrationBuilder.DropForeignKey(
                name: "FK_Inspections_DefectCodes_DefectCodeId",
                table: "Inspections");

            migrationBuilder.DropTable(
                name: "DefectCodes");

            migrationBuilder.DropIndex(
                name: "IX_Inspections_DefectCodeId",
                table: "Inspections");

            migrationBuilder.DropColumn(
                name: "DefectCodeId",
                table: "Inspections");
        }
    }
}
