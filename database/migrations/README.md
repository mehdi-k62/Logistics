# Database migrations

The challenge requires database migrations and SQL Server is the chosen persistence technology. `001_initial.sql` is the reproducible baseline migration for the challenge database. Apply migrations in filename order on a clean database.

For a production system, this folder should be replaced/managed by EF Core migrations or an equivalent migration runner with deployment history. The challenge intentionally keeps the SQL DDL visible so the critical indexes, constraints and stored procedure are reviewable.
