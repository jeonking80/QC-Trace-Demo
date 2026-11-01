using System;
using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <summary>
    /// 검사 일정 협의(2026-11-01 사용자 요청): 의뢰 우선순위(보통/긴급)·희망 기한, 검사자 잔량 예정일 회신,
    /// 생산 담당 수락/거절, 거절 시 결정권자(관리자·승인권자) 최종 기한. 기록은 ScheduleEvents(추가만).
    /// </summary>
    public partial class InspectionSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DueAt",
                table: "Lots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "Lots",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<string>(
                name: "PriorityReason",
                table: "Lots",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProposedDueAt",
                table: "Lots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleState",
                table: "Lots",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduleEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleEvents", x => x.Id);
                    table.CheckConstraint("CK_ScheduleEvents_Kind", "[Kind] IN ('Request', 'Propose', 'Accept', 'Reject', 'Decide')");
                    table.CheckConstraint("CK_ScheduleEvents_Reason", "[Kind] NOT IN ('Propose', 'Reject', 'Decide') OR ([Reason] IS NOT NULL AND LEN(LTRIM(RTRIM([Reason]))) > 0)");
                    table.ForeignKey(
                        name: "FK_ScheduleEvents_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleEvents_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lots_Priority",
                table: "Lots",
                sql: "[Priority] IN ('Normal', 'Urgent')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lots_ScheduleState",
                table: "Lots",
                sql: "[ScheduleState] IS NULL OR [ScheduleState] IN ('Proposed', 'Accepted', 'Escalated', 'Decided')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lots_UrgentReason",
                table: "Lots",
                sql: "[Priority] <> 'Urgent' OR ([PriorityReason] IS NOT NULL AND LEN(LTRIM(RTRIM([PriorityReason]))) > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEvents_ActorId",
                table: "ScheduleEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEvents_CreatedAt",
                table: "ScheduleEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEvents_LotId_CreatedAt",
                table: "ScheduleEvents",
                columns: new[] { "LotId", "CreatedAt" });

            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_ScheduleEvents_AppendOnly] ON [dbo].[ScheduleEvents]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51000, N'ScheduleEvents 테이블은 추가만 허용합니다. UPDATE·DELETE는 거부됩니다.', 1;
END;");
            migrationBuilder.Sql(SqlScripts.Read("V8/001_usp_RequestInspection.sql"));
            migrationBuilder.Sql(SqlScripts.Read("V8/002_usp_ProposeSchedule.sql"));
            migrationBuilder.Sql(SqlScripts.Read("V8/003_usp_RespondSchedule.sql"));
            migrationBuilder.Sql(SqlScripts.Read("V8/004_usp_DecideSchedule.sql"));
            migrationBuilder.Sql(SqlScripts.Read("V8/005_app_role.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_DecideSchedule;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_RespondSchedule;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_ProposeSchedule;");
            migrationBuilder.Sql(SqlScripts.Read("V2/004_usp_RequestInspection.sql"));
            migrationBuilder.DropTable(
                name: "ScheduleEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lots_Priority",
                table: "Lots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lots_ScheduleState",
                table: "Lots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lots_UrgentReason",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "DueAt",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "PriorityReason",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "ProposedDueAt",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "ScheduleState",
                table: "Lots");
        }
    }
}
