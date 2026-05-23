CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE EXTENSION IF NOT EXISTS postgis;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE EXTENSION IF NOT EXISTS postgis_topology;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE EXTENSION IF NOT EXISTS postgis;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE trip_events (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "Kind" integer NOT NULL,
        "ActorId" uuid,
        "Location" geometry(Point, 4326),
        "Timestamp" timestamp with time zone NOT NULL,
        "PayloadJson" jsonb,
        CONSTRAINT "PK_trip_events" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE trips (
        "Id" uuid NOT NULL,
        "Name" character varying(200) NOT NULL,
        "DestinationName" character varying(200) NOT NULL,
        "DestinationLocation" geometry(Point, 4326) NOT NULL,
        "DepartAt" timestamp with time zone NOT NULL,
        "ArrivalWindowEarliest" timestamp with time zone NOT NULL,
        "ArrivalWindowLatest" timestamp with time zone NOT NULL,
        "OwnerId" uuid NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "LockedSolutionId" uuid,
        CONSTRAINT "PK_trips" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE optimisation_runs (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "Status" integer NOT NULL,
        "Solver" integer NOT NULL,
        "WeightDriveTime" double precision NOT NULL,
        "WeightStopCount" double precision NOT NULL,
        "WeightWalkAndPt" double precision NOT NULL,
        "WeightArrivalSpread" double precision NOT NULL,
        "WeightFairness" double precision NOT NULL,
        "StartedAt" timestamp with time zone NOT NULL,
        "CompletedAt" timestamp with time zone,
        "FailureReason" character varying(2000),
        "WallClock" interval,
        "IterationsOrNodes" integer,
        "BestObjective" double precision,
        "LpRelaxation" double precision,
        "BestSolutionId" uuid,
        CONSTRAINT "PK_optimisation_runs" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_optimisation_runs_trips_TripId" FOREIGN KEY ("TripId") REFERENCES trips ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE participants (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "DisplayName" character varying(120) NOT NULL,
        "Home" geometry(Point, 4326) NOT NULL,
        "HasCar" boolean NOT NULL,
        "Seats" integer NOT NULL,
        "WalkBudgetMins" integer NOT NULL,
        "DetourToleranceMins" integer NOT NULL,
        "FairnessWeight" double precision NOT NULL,
        CONSTRAINT "PK_participants" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_participants_trips_TripId" FOREIGN KEY ("TripId") REFERENCES trips ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE solutions (
        "Id" uuid NOT NULL,
        "OptimisationRunId" uuid NOT NULL,
        "Label" character varying(120) NOT NULL,
        "Objective" double precision NOT NULL,
        "ObjectiveTerms" double precision[] NOT NULL,
        CONSTRAINT "PK_solutions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_solutions_optimisation_runs_OptimisationRunId" FOREIGN KEY ("OptimisationRunId") REFERENCES optimisation_runs ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE candidate_nodes (
        "Id" uuid NOT NULL,
        "ParticipantId" uuid NOT NULL,
        "Kind" integer NOT NULL,
        "Location" geometry(Point, 4326) NOT NULL,
        "WalkMins" integer NOT NULL,
        "PtMins" integer NOT NULL,
        "ExternalId" character varying(64),
        "DisplayName" character varying(200),
        CONSTRAINT "PK_candidate_nodes" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_candidate_nodes_participants_ParticipantId" FOREIGN KEY ("ParticipantId") REFERENCES participants ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE driver_routes (
        "Id" uuid NOT NULL,
        "SolutionId" uuid NOT NULL,
        "DriverId" uuid NOT NULL,
        "TravelMins" double precision NOT NULL,
        "OrderIndex" integer NOT NULL,
        CONSTRAINT "PK_driver_routes" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_driver_routes_solutions_SolutionId" FOREIGN KEY ("SolutionId") REFERENCES solutions ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE TABLE stops (
        "Id" uuid NOT NULL,
        "DriverRouteId" uuid NOT NULL,
        "OrderIndex" integer NOT NULL,
        "Location" geometry(Point, 4326) NOT NULL,
        "CandidateNodeId" uuid NOT NULL,
        "EstimatedArrival" timestamp with time zone NOT NULL,
        "Pickups" uuid[] NOT NULL,
        CONSTRAINT "PK_stops" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_stops_driver_routes_DriverRouteId" FOREIGN KEY ("DriverRouteId") REFERENCES driver_routes ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_candidate_nodes_Location" ON candidate_nodes USING gist ("Location");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_candidate_nodes_ParticipantId" ON candidate_nodes ("ParticipantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_driver_routes_SolutionId" ON driver_routes ("SolutionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_driver_routes_SolutionId_OrderIndex" ON driver_routes ("SolutionId", "OrderIndex");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_optimisation_runs_TripId" ON optimisation_runs ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_optimisation_runs_TripId_StartedAt" ON optimisation_runs ("TripId", "StartedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_participants_Home" ON participants USING gist ("Home");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_participants_TripId" ON participants ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_participants_UserId" ON participants ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_solutions_OptimisationRunId" ON solutions ("OptimisationRunId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_stops_DriverRouteId" ON stops ("DriverRouteId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_stops_DriverRouteId_OrderIndex" ON stops ("DriverRouteId", "OrderIndex");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_trip_events_TripId" ON trip_events ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_trip_events_TripId_Timestamp" ON trip_events ("TripId", "Timestamp");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_trips_DestinationLocation" ON trips USING gist ("DestinationLocation");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    CREATE INDEX "IX_trips_OwnerId" ON trips ("OwnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260523051524_InitialSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260523051524_InitialSchema', '10.0.4');
    END IF;
END $EF$;
COMMIT;

