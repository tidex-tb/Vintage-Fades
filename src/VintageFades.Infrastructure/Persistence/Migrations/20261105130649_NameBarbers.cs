using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VintageFades.Infrastructure.Persistence.Migrations
{
    /// <summary>Gives the four seeded placeholder barbers ("Barber 1".."Barber 4") the shop's real names.
    /// Only rows that still carry the placeholder name are changed, so names edited later are kept.</summary>
    public partial class NameBarbers : Migration
    {
        private static readonly (int Station, string Name)[] Names =
            [(1, "Kapza"), (2, "Luther"), (3, "Godwin"), (4, "Tevohn")];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (station, name) in Names)
                migrationBuilder.Sql(
                    $"""
                    UPDATE "Barbers" SET "FirstName" = '{name}', "LastName" = NULL, "DisplayName" = '{name}', "UpdatedAtUtc" = now()
                    WHERE "StationNumber" = {station} AND "DisplayName" = 'Barber {station}';
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (station, name) in Names)
                migrationBuilder.Sql(
                    $"""
                    UPDATE "Barbers" SET "FirstName" = 'Barber', "LastName" = '{station}', "DisplayName" = 'Barber {station}', "UpdatedAtUtc" = now()
                    WHERE "StationNumber" = {station} AND "DisplayName" = '{name}';
                    """);
        }
    }
}
