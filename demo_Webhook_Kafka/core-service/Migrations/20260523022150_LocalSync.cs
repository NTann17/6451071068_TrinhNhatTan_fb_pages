using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace core_service.Migrations
{
    /// <inheritdoc />
    public partial class LocalSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventProcessingStatuses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventRecordId = table.Column<string>(type: "TEXT", nullable: false),
                    FromState = table.Column<string>(type: "TEXT", nullable: true),
                    ToState = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventProcessingStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventProcessingStatuses_EventRecordId",
                table: "EventProcessingStatuses",
                column: "EventRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventProcessingStatuses");
        }
    }
}
