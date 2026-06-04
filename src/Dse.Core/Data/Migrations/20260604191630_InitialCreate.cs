using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dse.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Source",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AssemblyQualifiedName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Source", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActiveSourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestRun_Source_SourceKey",
                        column: x => x.SourceKey,
                        principalTable: "Source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IngestProgress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Checkpoint = table.Column<string>(type: "TEXT", nullable: false),
                    Metadata = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TotalToProduce = table.Column<long>(type: "INTEGER", nullable: false),
                    Elapsed = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    PercentComplete = table.Column<double>(type: "REAL", nullable: false),
                    DocsPerSecond = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedRemaining = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    Produced = table.Column<long>(type: "INTEGER", nullable: false),
                    ManagedMemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkingMemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestProgress_IngestRun_RunId",
                        column: x => x.RunId,
                        principalTable: "IngestRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestProgress_RunId",
                table: "IngestProgress",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestRun_ActiveSourceKey",
                table: "IngestRun",
                column: "ActiveSourceKey",
                unique: true,
                filter: "\"ActiveSourceKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IngestRun_SourceKey",
                table: "IngestRun",
                column: "SourceKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestProgress");

            migrationBuilder.DropTable(
                name: "IngestRun");

            migrationBuilder.DropTable(
                name: "Source");
        }
    }
}
