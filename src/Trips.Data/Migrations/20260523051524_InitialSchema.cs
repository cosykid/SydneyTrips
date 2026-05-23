using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Trips.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Belt-and-braces explicit extension creation. The Npgsql model annotation
            // below also creates the extension, but raw SQL here makes the intent obvious
            // and ensures the schema works even if someone disables the model annotation.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS postgis;");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS postgis_topology;");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "trip_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Location = table.Column<Point>(type: "geometry(Point, 4326)", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "trips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DestinationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DestinationLocation = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    DepartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArrivalWindowEarliest = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArrivalWindowLatest = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedSolutionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "optimisation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Solver = table.Column<int>(type: "integer", nullable: false),
                    WeightDriveTime = table.Column<double>(type: "double precision", nullable: false),
                    WeightStopCount = table.Column<double>(type: "double precision", nullable: false),
                    WeightWalkAndPt = table.Column<double>(type: "double precision", nullable: false),
                    WeightArrivalSpread = table.Column<double>(type: "double precision", nullable: false),
                    WeightFairness = table.Column<double>(type: "double precision", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    WallClock = table.Column<TimeSpan>(type: "interval", nullable: true),
                    IterationsOrNodes = table.Column<int>(type: "integer", nullable: true),
                    BestObjective = table.Column<double>(type: "double precision", nullable: true),
                    LpRelaxation = table.Column<double>(type: "double precision", nullable: true),
                    BestSolutionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_optimisation_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_optimisation_runs_trips_TripId",
                        column: x => x.TripId,
                        principalTable: "trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Home = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    HasCar = table.Column<bool>(type: "boolean", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    WalkBudgetMins = table.Column<int>(type: "integer", nullable: false),
                    DetourToleranceMins = table.Column<int>(type: "integer", nullable: false),
                    FairnessWeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_participants_trips_TripId",
                        column: x => x.TripId,
                        principalTable: "trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "solutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OptimisationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Objective = table.Column<double>(type: "double precision", nullable: false),
                    ObjectiveTerms = table.Column<double[]>(type: "double precision[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_solutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_solutions_optimisation_runs_OptimisationRunId",
                        column: x => x.OptimisationRunId,
                        principalTable: "optimisation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "candidate_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Location = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    WalkMins = table.Column<int>(type: "integer", nullable: false),
                    PtMins = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_candidate_nodes_participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "driver_routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SolutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelMins = table.Column<double>(type: "double precision", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_driver_routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_driver_routes_solutions_SolutionId",
                        column: x => x.SolutionId,
                        principalTable: "solutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverRouteId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    Location = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    CandidateNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstimatedArrival = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Pickups = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_stops_driver_routes_DriverRouteId",
                        column: x => x.DriverRouteId,
                        principalTable: "driver_routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_nodes_Location",
                table: "candidate_nodes",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_candidate_nodes_ParticipantId",
                table: "candidate_nodes",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_driver_routes_SolutionId",
                table: "driver_routes",
                column: "SolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_driver_routes_SolutionId_OrderIndex",
                table: "driver_routes",
                columns: new[] { "SolutionId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_optimisation_runs_TripId",
                table: "optimisation_runs",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_optimisation_runs_TripId_StartedAt",
                table: "optimisation_runs",
                columns: new[] { "TripId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_participants_Home",
                table: "participants",
                column: "Home")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_participants_TripId",
                table: "participants",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_participants_UserId",
                table: "participants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_solutions_OptimisationRunId",
                table: "solutions",
                column: "OptimisationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_stops_DriverRouteId",
                table: "stops",
                column: "DriverRouteId");

            migrationBuilder.CreateIndex(
                name: "IX_stops_DriverRouteId_OrderIndex",
                table: "stops",
                columns: new[] { "DriverRouteId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_trip_events_TripId",
                table: "trip_events",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_trip_events_TripId_Timestamp",
                table: "trip_events",
                columns: new[] { "TripId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_trips_DestinationLocation",
                table: "trips",
                column: "DestinationLocation")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_trips_OwnerId",
                table: "trips",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_nodes");

            migrationBuilder.DropTable(
                name: "stops");

            migrationBuilder.DropTable(
                name: "trip_events");

            migrationBuilder.DropTable(
                name: "participants");

            migrationBuilder.DropTable(
                name: "driver_routes");

            migrationBuilder.DropTable(
                name: "solutions");

            migrationBuilder.DropTable(
                name: "optimisation_runs");

            migrationBuilder.DropTable(
                name: "trips");
        }
    }
}
