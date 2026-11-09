using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

// Deployment-only grant tool: fixed development target, no vendor clients or runtime schema publication.
if (args.Length != 1 || args[0] != "--apply")
{
    Console.Error.WriteLine("Explicit --apply is required for development SQL grants.");
    return 2;
}
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var token = await new AzureCliCredential().GetTokenAsync(new TokenRequestContext(["https://database.windows.net/.default"]), timeout.Token);
    await using var connection = new SqlConnection("Server=tcp:caseworksdevsql001.database.windows.net,1433;Initial Catalog=briefcase-sync-development;Encrypt=True;TrustServerCertificate=False;ConnectRetryCount=0;Connect Timeout=15;") { AccessToken = token.Token };
    await connection.OpenAsync(timeout.Token);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        IF DATABASE_PRINCIPAL_ID(N'briefcase-sync-development-runtime') IS NULL
            CREATE USER [briefcase-sync-development-runtime] FROM EXTERNAL PROVIDER;
        IF DATABASE_PRINCIPAL_ID(N'briefcase-sync-development-web') IS NULL
            CREATE USER [briefcase-sync-development-web] FROM EXTERNAL PROVIDER;
        GRANT SELECT, INSERT, UPDATE ON OBJECT::sync.Cases TO [briefcase-sync-development-runtime];
        GRANT SELECT ON OBJECT::sync.Health TO [briefcase-sync-development-runtime];
        GRANT SELECT ON OBJECT::sync.Configuration TO [briefcase-sync-development-runtime];
        GRANT SELECT, INSERT, UPDATE ON OBJECT::sync.Runs TO [briefcase-sync-development-runtime];
        GRANT SELECT ON OBJECT::sync.Cases TO [briefcase-sync-development-web];
        GRANT SELECT ON OBJECT::sync.Runs TO [briefcase-sync-development-web];
        GRANT SELECT, INSERT, UPDATE ON OBJECT::sync.Configuration TO [briefcase-sync-development-web];
        GRANT INSERT ON OBJECT::sync.AccessAudit TO [briefcase-sync-development-web];
        """;
    await command.ExecuteNonQueryAsync(timeout.Token);
    Console.WriteLine("DEVELOPMENT_SQL_RUNTIME_GRANTS_APPLIED");
    return 0;
}
catch
{
    Console.Error.WriteLine("DEVELOPMENT_SQL_RUNTIME_GRANTS_BLOCKED");
    return 1;
}
