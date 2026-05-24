using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Trips.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateNodePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LineString>(
                name: "Path",
                table: "candidate_nodes",
                type: "geometry(LineString, 4326)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Path",
                table: "candidate_nodes");
        }
    }
}
