using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoredProcedures : Migration
    {
        // Data/Sql/V1 파일을 순서대로 실행한다. 적용된 뒤에는 V1 파일을 고치지 않는다(Data/Sql/README.md).
        private static readonly string[] Scripts =
        [
            "V1/001_usp_RefreshLotStatus.sql",
            "V1/002_usp_ApplyInspection.sql",
            "V1/003_usp_ApproveShipment.sql",
            "V1/004_usp_ShipLot.sql",
            "V1/010_app_role.sql",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var script in Scripts)
            {
                migrationBuilder.Sql(SqlScripts.Read(script));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @stmt nvarchar(max) = N'';
                SELECT @stmt += N'ALTER ROLE qctrace_app_role DROP MEMBER ' + QUOTENAME(m.name) + N'; '
                FROM sys.database_role_members rm
                JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id AND r.name = N'qctrace_app_role'
                JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id;
                EXEC (@stmt);
                DROP ROLE IF EXISTS qctrace_app_role;
                """);
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_ShipLot;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_ApproveShipment;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_ApplyInspection;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_RefreshLotStatus;");
        }
    }
}
