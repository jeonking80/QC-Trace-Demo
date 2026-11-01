using System;
using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminTools : Migration
    {
        // 역할 변경 감사(RoleChanges), 역할 변경 SP, 재고 대사 뷰, 앱 계정 권한
        private static readonly string[] Scripts =
        [
            "V4/001_usp_SetUserRoles.sql",
            "V4/002_vw_LotReconciliation.sql",
            "V4/003_app_role.sql",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoleChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RoleName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Change = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleChanges", x => x.Id);
                    table.CheckConstraint("CK_RoleChanges_Change", "[Change] IN ('Add', 'Remove')");
                    table.CheckConstraint("CK_RoleChanges_Reason", "LEN(LTRIM(RTRIM([Reason]))) > 0");
                    table.ForeignKey(
                        name: "FK_RoleChanges_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleChanges_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleChanges_ActorId",
                table: "RoleChanges",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleChanges_CreatedAt",
                table: "RoleChanges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RoleChanges_UserId_CreatedAt",
                table: "RoleChanges",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_RoleChanges_AppendOnly] ON [dbo].[RoleChanges]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51000, N'RoleChanges 테이블은 추가만 허용합니다. UPDATE·DELETE는 거부됩니다.', 1;
END;");

            foreach (var script in Scripts)
            {
                migrationBuilder.Sql(SqlScripts.Read(script));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE INSERT, UPDATE, DELETE ON dbo.UserRoles TO qctrace_app_role;");
            migrationBuilder.Sql("GRANT INSERT, UPDATE, DELETE ON dbo.UserRoles TO qctrace_app_role;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_LotReconciliation;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_SetUserRoles;");
            // RoleChanges의 DENY와 트리거는 테이블과 함께 지워진다
            migrationBuilder.DropTable(
                name: "RoleChanges");
        }
    }
}
