using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogsPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationRetentionDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "Applications",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetentionDays",
                table: "Applications");
        }
    }
}
