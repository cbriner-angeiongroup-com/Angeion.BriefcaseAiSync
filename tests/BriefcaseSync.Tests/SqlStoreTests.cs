using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class SqlStoreTests
{
    [SqlFact]
    public async Task SqlEnforcesExclusiveRunsUniqueTigerClaimsAndAtomicDiscovery()
    {
        // Dedicated disposable database. Never uses an existing application database.
        var database = "BriefcaseSync_Test_" + Guid.NewGuid().ToString("N");
        var masterOptions = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BRIEFCASE_TEST_SQL_MASTER")) { InitialCatalog = "master" };
        await using var master = new SqlConnection(masterOptions.ConnectionString);
        await master.OpenAsync();
        using (var create = new SqlCommand($"CREATE DATABASE [{database}];", master)) await create.ExecuteNonQueryAsync();
        try
        {
            var connectionString = new SqlConnectionStringBuilder(masterOptions.ConnectionString) { InitialCatalog = database }.ConnectionString;
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                foreach (var batch in Regex.Split(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schema.sql")), @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    using var command = new SqlCommand(batch, connection); await command.ExecuteNonQueryAsync();
                }
            }
            var options = new AppOptions { Scope = "synthetic", SqlConnectionString = connectionString, StateEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            var first = new SqlSyncStore(options); var competing = new SqlSyncStore(options);
            await using (var lease = await first.TryAcquireRun(default))
            {
                Assert.NotNull(lease); Assert.Null(await competing.TryAcquireRun(default));
                await first.Discover([1, 2], default);
                await first.Save(new() { SourceCaseId = 1, LeadVendorId = "TIGER-SYNTHETIC" }, default);
                await Assert.ThrowsAsync<IntakeValidationException>(() => first.Save(new() { SourceCaseId = 2, LeadVendorId = "TIGER-SYNTHETIC" }, default));
                Assert.Null((await first.Read(2, default)).LeadVendorId);
                await Assert.ThrowsAsync<SqlException>(() => first.Discover([3, -1], default));
                Assert.Equal(new long[] { 1, 2 }, (await first.Cases(default)).Order());
                Assert.Equal("TIGER-SYNTHETIC", (await first.Read(1, default)).LeadVendorId);
                var operations = new SqlOperationsRepository(options);
                var draft = new RuntimeSettings { EligibleStatusId = 321, Mapping = Fixtures.Mapping() };
                var saved = await operations.SaveSettings(draft, 0, "synthetic-operator", default);
                Assert.Equal(1, saved.Version);
                await Assert.ThrowsAsync<SqlException>(() => operations.SaveSettings(draft, 0, "synthetic-stale-operator", default));
                Assert.Equal(321, (await operations.LoadSettings(options, default)).Settings.EligibleStatusId);
                options.ConfigurationVersion = saved.Version; options.ConfigurationSavedAtUtc = saved.SavedAtUtc.AddHours(-1);
                options.ScheduleCron = "* * * * *";
                var journal = new SqlRunJournal(options, true);
                var runId = await journal.Begin(default); Assert.NotNull(runId);
                await journal.Complete(runId.Value, new(2, 2, 0, 0, false), "Completed", default);
                Assert.Null(await journal.Begin(default));
                var dashboard = await operations.Dashboard(default);
                Assert.Equal("Completed", Assert.Single(dashboard.Runs).Outcome); Assert.Equal(2, dashboard.TotalCases);
            }
            await using var nextLease = await competing.TryAcquireRun(default);
            Assert.NotNull(nextLease); // Lock is explicitly released before pooling.
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (!Regex.IsMatch(database, @"^BriefcaseSync_Test_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected test database identity.");
            using var drop = new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];", master);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRIEFCASE_TEST_SQL_MASTER")))
            Skip = "Set BRIEFCASE_TEST_SQL_MASTER to an isolated development SQL server with create-database rights.";
    }
}
