using System.Data;
using System.Text.Json;
using BriefcaseSync.Core;
using Microsoft.Data.SqlClient;

namespace BriefcaseSync.Infrastructure;

public sealed class SqlOperationsRepository(AppOptions options) : IOperationsRepository
{
    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        var connection = new SqlConnection(options.SqlConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private SqlCommand Command(SqlConnection connection, string sql)
    {
        var c = connection.CreateCommand(); c.CommandText = sql; c.Parameters.Add("@scope", SqlDbType.NVarChar, 100).Value = options.Scope; return c;
    }
    public async Task<SettingsSnapshot> LoadSettings(AppOptions fallback, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = Command(connection, "SELECT Payload,Version,SavedAtUtc FROM sync.Configuration WHERE Scope=@scope;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(JsonSerializer.Deserialize<RuntimeSettings>(reader.GetString(0))!, reader.GetInt32(1), Utc(reader.GetDateTime(2)))
            : new(RuntimeSettings.From(fallback), 0, DateTimeOffset.UtcNow);
    }
    public async Task<SettingsSnapshot> SaveSettings(RuntimeSettings settings, int expectedVersion, string actor, CancellationToken ct)
    {
        settings.Validate();
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var command = Command(connection, "DECLARE @actual int=(SELECT Version FROM sync.Configuration WITH(UPDLOCK,HOLDLOCK) WHERE Scope=@scope); IF ISNULL(@actual,0)<>@expected THROW 51000,'CONFIGURATION_CONFLICT',1; IF @actual IS NULL INSERT sync.Configuration(Scope,Version,Payload,SavedAtUtc) VALUES(@scope,1,@payload,SYSUTCDATETIME()); ELSE UPDATE sync.Configuration SET Version=Version+1,Payload=@payload,SavedAtUtc=SYSUTCDATETIME() WHERE Scope=@scope; INSERT sync.AccessAudit(Scope,Actor,Action,ConfigurationVersion) VALUES(@scope,@actor,'ConfigurationSaved',@expected+1); SELECT Version,SavedAtUtc FROM sync.Configuration WHERE Scope=@scope;");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@expected", expectedVersion); command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(settings);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 150).Value = actor;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct); var version = reader.GetInt32(0); var at = Utc(reader.GetDateTime(1));
        await reader.DisposeAsync(); await transaction.CommitAsync(ct);
        return new(settings, version, at);
    }
    public async Task<OperationsDashboard> Dashboard(CancellationToken ct)
    {
        await using var connection = await Open(ct);
        var runs = new List<RunSummary>();
        using (var command = Command(connection, "SELECT TOP(30) Id,StartedAtUtc,FinishedAtUtc,Outcome,DryRun,Discovered,Processed,Failed,Attention,ConfigurationVersion FROM sync.Runs WHERE Scope=@scope ORDER BY StartedAtUtc DESC;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) runs.Add(new(reader.GetGuid(0), Utc(reader.GetDateTime(1)), reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2)), reader.GetString(3), reader.GetBoolean(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9)));
        var deliveries = new List<DeliverySummary>(); var cipher = new StateCipher(options.StateEncryptionKey, options.Scope);
        using (var command = Command(connection, "SELECT TOP(100) SourceCaseId,Status,UpdatedAtUtc,Payload FROM sync.Cases WHERE Scope=@scope ORDER BY UpdatedAtUtc DESC;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var state = cipher.Decrypt(reader.GetInt64(0), (byte[])reader[3]);
                deliveries.Add(new(state.SourceCaseId, reader.GetString(1), Utc(reader.GetDateTime(2)), state.Documents.Count, state.Documents.Values.Count(d => d.Status == "Delivered"), state.Errors.Select(e => e.Code).Distinct().ToArray()));
            }
        int total = 0, created = 0, attention = 0, pending = 0;
        using (var command = Command(connection, "SELECT Status,COUNT(*) FROM sync.Cases WHERE Scope=@scope GROUP BY Status;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var count = reader.GetInt32(1); total += count;
                switch (reader.GetString(0)) { case "Created": created += count; break; case "Discovered": case "Submitting": pending += count; break; default: attention += count; break; }
            }
        return new(runs, deliveries, total, created, attention, pending);
    }
    public async Task<SyncState?> FindState(long caseId, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = Command(connection, "SELECT Payload FROM sync.Cases WHERE Scope=@scope AND SourceCaseId=@id;"); command.Parameters.AddWithValue("@id", caseId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is byte[] bytes ? new StateCipher(options.StateEncryptionKey, options.Scope).Decrypt(caseId, bytes) : null;
    }
    public async Task Audit(string actor, string action, long? caseId, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = Command(connection, "INSERT sync.AccessAudit(Scope,Actor,Action,SourceCaseId) VALUES(@scope,@actor,@action,@id);");
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 150).Value = actor; command.Parameters.Add("@action", SqlDbType.VarChar, 40).Value = action;
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = (object?)caseId ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(ct);
    }
    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
