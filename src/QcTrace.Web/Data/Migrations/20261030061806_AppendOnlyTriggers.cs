using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppendOnlyTriggers : Migration
    {
        // 마이그레이션은 작성 시점 스키마를 고정해야 하므로 목록을 여기에 직접 둔다
        private static readonly string[] Tables = ["Inspections", "ShipmentApprovals", "Shipments", "Ledger"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R3: 판정·수량 이력은 덮어쓰거나 지우지 않는다. 정정은 새 행으로 추가한다.
            foreach (var table in Tables)
            {
                var trigger = $"TR_{table}_AppendOnly";
                migrationBuilder.Sql($@"
CREATE TRIGGER [dbo].[{trigger}] ON [dbo].[{table}]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51000, N'{table} 테이블은 추가만 허용합니다. UPDATE·DELETE는 거부됩니다. 정정은 새 행으로 기록하세요.', 1;
END;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [dbo].[TR_{table}_AppendOnly];");
            }
        }
    }
}
