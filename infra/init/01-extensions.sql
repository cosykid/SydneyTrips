-- Belt-and-braces backup to the EF Core migration. Postgis/postgis images already enable PostGIS
-- in the template1 database, but explicitly enabling here keeps things consistent if the volume
-- pre-exists from a non-PostGIS image.

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
