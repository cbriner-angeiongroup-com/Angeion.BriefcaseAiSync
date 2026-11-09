using System.Net;
using BriefcaseSync.Infrastructure;
using Microsoft.Data.SqlClient;

namespace BriefcaseSync.Web.Services;

public sealed record WorkspaceMode(bool Demo, bool LocalPreview = false)
{
    public DateTimeOffset SessionExpiresUtc { get; } = DateTimeOffset.UtcNow.AddHours(1);
    public const string DevelopmentApiUrl = "https://cwapps.n8protect.com/Development/VendorSmartAdvocate/";
    public const string LocalScope = "local-smartadvocate-development-preview";

    public static WorkspaceMode Create(bool demo, bool localPreview, bool development, AppOptions options)
    {
        if ((demo || localPreview) && !development)
            throw new InvalidOperationException("Development workspaces are restricted to Development.");
        if (demo && localPreview) throw new InvalidOperationException("Select either synthetic data or local development preview.");
        if (localPreview)
        {
            if (options.SmartAdvocate.ApiUrl != DevelopmentApiUrl || options.SendingEnabled || options.MappingApproved
                || options.Scope != LocalScope)
                throw new InvalidOperationException("Local preview requires the SmartAdvocate development connection, its dedicated local scope, and disabled sending and approval.");
            options.SmartAdvocate.ValidatePreview();
            var sql = new SqlConnectionStringBuilder(options.SqlConnectionString);
            if (sql.DataSource != "localhost" || sql.InitialCatalog != "BriefcaseSync_LocalPreview" || sql.IntegratedSecurity
                || sql.ConnectRetryCount != 0 || sql.UserID != "BriefcaseSync_LocalPreviewWeb")
                throw new InvalidOperationException("Configure the dedicated local preview SQL database and restricted web login.");
            if (Convert.FromBase64String(options.StateEncryptionKey).Length != 32)
                throw new InvalidOperationException("Configure the local state encryption key.");
        }
        return new(demo, localPreview);
    }

    public static bool AllowsLocalRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
        && context.Request.Host.Host is "localhost" or "127.0.0.1" or "::1";
}
