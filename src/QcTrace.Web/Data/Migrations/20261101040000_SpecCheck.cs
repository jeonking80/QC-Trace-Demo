using Microsoft.EntityFrameworkCore.Migrations;
using QcTrace.Web.Data.Sql;

#nullable disable

namespace QcTrace.Web.Data.Migrations
{
    /// <summary>R11: 규격을 벗어난 측정값의 합격 판정 거절. 스키마 변경 없이 검사 SP만 다시 만든다.</summary>
    public partial class SpecCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("V3/001_usp_ApplyInspection.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Read("V2/002_usp_ApplyInspection.sql"));
        }
    }
}
