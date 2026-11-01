using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DelayAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DelayAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    InspectionRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UninspectedAtDetection = table.Column<int>(type: "int", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelayAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DelayAlerts_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DelayAlerts_DetectedAt",
                table: "DelayAlerts",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DelayAlerts_LotId",
                table: "DelayAlerts",
                column: "LotId",
                unique: true);

            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_DelayAlerts_AppendOnly] ON [dbo].[DelayAlerts]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51000, N'DelayAlerts 테이블은 추가만 허용합니다.', 1;
END;");
            // 지연 알림은 앱(백그라운드 작업)이 직접 추가만 한다
            migrationBuilder.Sql("GRANT INSERT ON dbo.DelayAlerts TO qctrace_app_role;");
            migrationBuilder.Sql("DENY UPDATE, DELETE ON dbo.DelayAlerts TO qctrace_app_role;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DelayAlerts");
        }
    }
}
