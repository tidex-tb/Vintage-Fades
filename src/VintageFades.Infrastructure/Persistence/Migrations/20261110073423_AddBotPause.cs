using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VintageFades.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBotPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BotPausedAtUtc",
                table: "ShopSettings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BotPausedAtUtc",
                table: "ShopSettings");
        }
    }
}
