using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace core_service.Migrations
{
    public partial class AddModerationControls : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModerationReviewItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RiskLevel = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationReviewItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SenderBlacklistEntries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RepeatedSpamCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BlacklistedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderBlacklistEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationReviewItems_EventId",
                table: "ModerationReviewItems",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationReviewItems_SenderId",
                table: "ModerationReviewItems",
                column: "SenderId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderBlacklistEntries_SenderId",
                table: "SenderBlacklistEntries",
                column: "SenderId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModerationReviewItems");

            migrationBuilder.DropTable(
                name: "SenderBlacklistEntries");
        }
    }
}