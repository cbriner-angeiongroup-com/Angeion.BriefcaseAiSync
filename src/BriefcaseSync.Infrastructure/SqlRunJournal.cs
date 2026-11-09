using System.Data;
using BriefcaseSync.Core;
using Cronos;
using Microsoft.Data.SqlClient;

namespace BriefcaseSync.Infrastructure;

public sealed class SqlRunJournal(AppOptions options, bool respectSchedule) : IRunJournal
{
    public async Task<Guid?> Begin(CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.SqlConnectionString); await connection.OpenAsync(ct);
        using var last = connection.CreateCommand(); last.CommandText = "SELECT MAX(StartedAtUtc) FROM sync.Runs WHERE Scope=@scope AND ConfigurationVersion=@version;";
        last.Parameters.AddWithValue("@scope", options.Scope); last.Parameters.AddWithValue("@version", options.ConfigurationVersion);
        var previous = await last.ExecuteScalarAsync(ct);
        var anchor = previous is DateTime at ? SqlOperationsRepository.Utc(at) : options.ConfigurationSavedAtUtc;
        var now = DateTimeOffset.UtcNow;
        if (respectSchedule && CronExpression.Parse(options.ScheduleCron).GetNextOccurrence(anchor, TimeZoneInfo.Utc) is {} next && next > now) return null;
        var id = Guid.NewGuid();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync.Runs SET Outcome='Interrupted',FinishedAtUtc=SYSUTCDATETIME() WHERE Scope=@scope AND Outcome='Running'; INSERT sync.Runs(Id,Scope,Outcome,DryRun,ConfigurationVersion) VALUES(@id,@scope,'Running',@dry,@version);";
        command.Parameters.AddWithValue("@scope", options.Scope); command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@dry", !options.SendingEnabled); command.Parameters.AddWithValue("@version", options.ConfigurationVersion);
        await command.ExecuteNonQueryAsync(ct); return id;
    }
    public async Task Complete(Guid runId, SyncResult? result, string outcome, CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.SqlConnectionString); await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync.Runs SET FinishedAtUtc=SYSUTCDATETIME(),Outcome=@outcome,Discovered=@discovered,Processed=@processed,Failed=@failed,Attention=@attention WHERE Id=@id AND Scope=@scope;";
        command.Parameters.AddWithValue("@id", runId); command.Parameters.AddWithValue("@scope", options.Scope); command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@discovered", result?.Discovered ?? 0); command.Parameters.AddWithValue("@processed", result?.Processed ?? 0);
        command.Parameters.AddWithValue("@failed", result?.Failed ?? 0); command.Parameters.AddWithValue("@attention", result?.Attention ?? 0);
        await command.ExecuteNonQueryAsync(ct);
    }
}
