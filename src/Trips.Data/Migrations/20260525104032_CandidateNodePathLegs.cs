using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trips.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateNodePathLegs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "path_legs",
                table: "candidate_nodes",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "path_legs",
                table: "candidate_nodes");
        }
    }
}
