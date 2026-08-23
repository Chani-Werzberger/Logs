using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogsPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisEngineEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Baselines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    EnvironmentId = table.Column<int>(type: "int", nullable: false),
                    ScopeType = table.Column<int>(type: "int", nullable: false),
                    ScopeId = table.Column<long>(type: "bigint", nullable: false),
                    MetricType = table.Column<int>(type: "int", nullable: false),
                    BucketHourOfDay = table.Column<byte>(type: "tinyint", nullable: false),
                    MeanValue = table.Column<double>(type: "float", nullable: false),
                    StdDevValue = table.Column<double>(type: "float", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Baselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Baselines_AppEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "AppEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Baselines_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    EnvironmentId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ScopeType = table.Column<int>(type: "int", nullable: false),
                    ScopeId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    ConfidenceLevel = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Findings_AppEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "AppEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Findings_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Evidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FindingId = table.Column<long>(type: "bigint", nullable: false),
                    EvidenceType = table.Column<int>(type: "int", nullable: false),
                    ReferenceId = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Evidence_Findings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "Findings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FindingStatements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FindingId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    OrderIndex = table.Column<int>(type: "int", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FindingStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FindingStatements_Findings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "Findings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Baselines_ApplicationId_EnvironmentId_ScopeType_ScopeId_MetricType_BucketHourOfDay",
                table: "Baselines",
                columns: new[] { "ApplicationId", "EnvironmentId", "ScopeType", "ScopeId", "MetricType", "BucketHourOfDay" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Baselines_EnvironmentId",
                table: "Baselines",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_FindingId",
                table: "Evidence",
                column: "FindingId");

            migrationBuilder.CreateIndex(
                name: "IX_Findings_ApplicationId_EnvironmentId_ScopeType_ScopeId_Type_Status",
                table: "Findings",
                columns: new[] { "ApplicationId", "EnvironmentId", "ScopeType", "ScopeId", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Findings_EnvironmentId",
                table: "Findings",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FindingStatements_FindingId_OrderIndex",
                table: "FindingStatements",
                columns: new[] { "FindingId", "OrderIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Baselines");

            migrationBuilder.DropTable(
                name: "Evidence");

            migrationBuilder.DropTable(
                name: "FindingStatements");

            migrationBuilder.DropTable(
                name: "Findings");
        }
    }
}
