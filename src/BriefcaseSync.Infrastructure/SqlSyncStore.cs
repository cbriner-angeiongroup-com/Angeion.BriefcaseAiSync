using System.Data;
using Microsoft.Data.SqlClient;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

// A session-owned SQL application lock serializes scheduled/manual runs across every replica.
// All state commands use that same open connection; loss of the lock connection fails the run.
public sealed class SqlSyncStore(AppOptions options) : ISyncStore
{
    private readonly StateCipher cipher = new(options.StateEncryptionKey, options.Scope);
    private readonly string lockResource = "briefcase-sync/" + options.Scope;
    private SqlConnection? session;
    private SqlConnection Session => session ?? throw new InvalidOperationException("Acquire the run lock before using state.");

    public async Task<IAsyncDisposable?> TryAcquireRun(CancellationToken ct)
    {
        if (session != null) throw new InvalidOperationException("A run is already active on this store.");
        // Transparent session recovery would lose sp_getapplock ownership. Always fail the run instead.
        var connection = new SqlConnection(new SqlConnectionStringBuilder(options.SqlConnectionString) { ConnectRetryCount = 0 }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @result;";
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "briefcase-sync/" + options.Scope;
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (result == -1) { await connection.DisposeAsync(); return null; }
            if (result < 0) throw new InvalidOperationException("SQL run lock acquisition failed.");
            session = connection;
            return new RunLock(this);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task Discover(IReadOnlyList<long> caseIds, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await Session.BeginTransactionAsync(ct);
        foreach (var id in caseIds)
        {
            using var command = Command("IF NOT EXISTS (SELECT 1 FROM sync.Cases WHERE Scope=@scope AND SourceCaseId=@id) INSERT sync.Cases(Scope,SourceCaseId,Status,Payload) VALUES(@scope,@id,'Discovered',@payload);", id);
            command.Transaction = transaction;
            command.Parameters.Add("@payload", SqlDbType.VarBinary, -1).Value = cipher.Encrypt(new() { SourceCaseId = id });
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<long>> Cases(CancellationToken ct)
    {
        using var command = Command("SELECT SourceCaseId FROM sync.Cases WHERE Scope=@scope ORDER BY UpdatedAtUtc,SourceCaseId;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public async Task<SyncState> Read(long caseId, CancellationToken ct)
    {
        using var command = Command("SELECT Payload FROM sync.Cases WHERE Scope=@scope AND SourceCaseId=@id;", caseId);
        var bytes = await command.ExecuteScalarAsync(ct) as byte[] ?? throw new InvalidOperationException("Missing discovered case.");
        return cipher.Decrypt(caseId, bytes);
    }

    public async Task Save(SyncState state, CancellationToken ct)
    {
        using var command = Command("UPDATE sync.Cases SET LeadHash=@lead,Status=@status,Payload=@payload,UpdatedAtUtc=SYSUTCDATETIME() WHERE Scope=@scope AND SourceCaseId=@id;", state.SourceCaseId);
        command.Parameters.Add("@lead", SqlDbType.Binary, 32).Value = state.LeadVendorId == null ? DBNull.Value : cipher.LeadHash(state.LeadVendorId);
        command.Parameters.Add("@status", SqlDbType.VarChar, 32).Value = state.Status;
        command.Parameters.Add("@payload", SqlDbType.VarBinary, -1).Value = cipher.Encrypt(state);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("State update failed.");
        }
        catch (SqlException e) when (e.Number is 2601 or 2627)
        {
            throw new IntakeValidationException([new("leadVendorId", "IDENTITY_ALREADY_BOUND", "Tiger Lead ID already belongs to another source case.")]);
        }
    }

    private SqlCommand Command(string sql, long? id = null)
    {
        var command = Session.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("@scope", SqlDbType.NVarChar, 100).Value = options.Scope;
        if (id != null) command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return command;
    }

    private sealed class RunLock(SqlSyncStore owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var connection = owner.session;
            owner.session = null;
            if (connection == null) return;
            try
            {
                // Explicit release before pooled connection disposal; session locks must not survive pooling.
                using var command = connection.CreateCommand();
                command.CommandText = "EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';";
                command.Parameters.AddWithValue("@resource", owner.lockResource);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { SqlConnection.ClearPool(connection); throw; }
            finally { await connection.DisposeAsync(); }
        }
    }
}
