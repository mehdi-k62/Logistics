using Microsoft.Data.SqlClient;
using System.Data;
using Xunit;

namespace Logistics.IntegrationTests;

public sealed class SqlServerConcurrencyTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LOGISTICS_TEST_CONNECTION_STRING")
        ?? "Server=192.168.33.37,1433;Database=Logistics;User Id=sa;Password=Mehd!K@1;TrustServerCertificate=True;Encrypt=False";

    [Fact]
    public async Task Three_workers_racing_for_one_remaining_capacity_exactly_one_succeeds()
    {
        var voyageId = "V100";
        var bookingIds = new[] { "B001", "B002", "B003" };
        var processIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        await using (var setup = new SqlConnection(ConnectionString))
        {
            await setup.OpenAsync();
            await ExecuteAsync(setup, $"""
                IF DB_ID('Logistics') IS NULL THROW 51000, 'Logistics database does not exist. Run database/schema.sql first.', 1;

                DELETE FROM dbo.CapacityReservations;
                DELETE FROM dbo.VoyageAssignments;
                DELETE FROM dbo.ReplanningProcesses;
                DELETE FROM dbo.Bookings;
                DELETE FROM dbo.Voyages;

                INSERT INTO dbo.Voyages(Id, State, TotalCapacity, ReservedCapacity, Version)
                VALUES ('{voyageId}', 'Available', 1, 0, 0);
                """);
        }

        var barrier = new Barrier(3);
        var results = new Task<ReserveResult>[3];

        for (var i = 0; i < 3; i++)
        {
            var worker = i;
            results[i] = Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                return await TryReserveAsync(voyageId, bookingIds[worker], processIds[worker], 1);
            });
        }

        var completed = await Task.WhenAll(results);

        Assert.Equal(1, completed.Count(x => x.Succeeded));
        Assert.Equal(2, completed.Count(x => !x.Succeeded));

        await using var verify = new SqlConnection(ConnectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT ReservedCapacity FROM dbo.Voyages WHERE Id = @voyageId;
            SELECT COUNT(*) FROM dbo.CapacityReservations WHERE VoyageId = @voyageId AND State = 'Active';
            """;
        command.Parameters.Add(new SqlParameter("@voyageId", SqlDbType.VarChar, 10) { Value = voyageId });

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
    }

    [Fact]
    public async Task Duplicate_process_reservation_is_not_allowed()
    {
        var voyageId = "V100";
        var bookingId = string.Empty;
        var processId = Guid.NewGuid();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(connection, $"""
            DELETE FROM dbo.CapacityReservations WHERE VoyageId = '{voyageId}';
            DELETE FROM dbo.Voyages WHERE Id = '{voyageId}';
            INSERT INTO dbo.Voyages(Id, State, TotalCapacity, ReservedCapacity, Version)
            VALUES ('{voyageId}', 'Available', 2, 0, 0);
            """);

        await ReserveAsync(connection, voyageId, bookingId, processId, 1);

        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => ReserveAsync(connection, voyageId, bookingId, processId, 1));
        Assert.Contains("Violation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ReserveResult> TryReserveAsync(string voyageId, string bookingId, Guid processId, int units)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await ReserveAsync(connection, voyageId, bookingId, processId, units);
            return new ReserveResult(true, null);
        }
        catch (SqlException ex)
        {
            return new ReserveResult(false, ex.Number.ToString());
        }
    }

    private static async Task ReserveAsync(SqlConnection connection, string voyageId, string bookingId, Guid processId, int units)
    {
        await using var command = new SqlCommand("dbo.ReserveCapacity", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };

        command.Parameters.Add(new SqlParameter("@VoyageId", SqlDbType.VarChar, 10) { Value = voyageId });
        command.Parameters.Add(new SqlParameter("@BookingId", SqlDbType.VarChar, 10) { Value = bookingId });
        command.Parameters.Add(new SqlParameter("@ProcessId", SqlDbType.UniqueIdentifier) { Value = processId });
        command.Parameters.Add(new SqlParameter("@Units", SqlDbType.Int) { Value = units });

        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ReserveResult(bool Succeeded, string? Error);
}
