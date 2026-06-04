using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace core_service.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Intent = table.Column<string>(type: "TEXT", nullable: true),
                    Sentiment = table.Column<string>(type: "TEXT", nullable: true),
                    IsSpam = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpamReason = table.Column<string>(type: "TEXT", nullable: true),
                    ActionTaken = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_EventId",
                table: "EventRecords",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventRecords");
        }
    }
}
