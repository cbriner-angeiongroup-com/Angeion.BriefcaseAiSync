using System.Net;
using System.Text;
using System.Text.Json;
using BriefcaseSync.Core;

namespace BriefcaseSync.Tests;

internal sealed class MemoryStore : ISyncStore
{
    public readonly Dictionary<long, SyncState> States = [];
    public bool Busy;
    public int Saves;
    public Task<IAsyncDisposable?> TryAcquireRun(CancellationToken ct) => Task.FromResult<IAsyncDisposable?>(Busy ? null : new Lease());
    public Task Discover(IReadOnlyList<long> ids, CancellationToken ct)
    {
        foreach (var id in ids) States.TryAdd(id, new() { SourceCaseId = id });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<long>> Cases(CancellationToken ct) => Task.FromResult<IReadOnlyList<long>>(States.Keys.ToArray());
    public Task<SyncState> Read(long id, CancellationToken ct) => Task.FromResult(Clone(States[id]));
    public Task Save(SyncState state, CancellationToken ct) { States[state.SourceCaseId] = Clone(state); Saves++; return Task.CompletedTask; }
    public static SyncState Clone(SyncState state) => JsonSerializer.Deserialize<SyncState>(JsonSerializer.Serialize(state))!;
    private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}

internal sealed class FakeDestination : IBriefcaseDestination
{
    public readonly List<IntakeRequest> Requests = [];
    public Func<IntakeRequest, string>? OnCreate;
    public Task<string> Create(IntakeRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(OnCreate?.Invoke(request) ?? "synthetic-intake");
    }
}

internal sealed class FakeSource : ISmartAdvocateSource
{
    public IReadOnlyList<long> Ids = [1];
    public bool DiscoveryFails;
    public SourceSnapshot Snapshot = Fixtures.Snapshot();
    public IReadOnlyList<SourceDocument> Documents = [];
    public int Downloads;
    public Task<IReadOnlyList<long>> Discover(CancellationToken ct) => DiscoveryFails ? throw new IOException("synthetic") : Task.FromResult(Ids);
    public Task<SourceSnapshot> Load(long id, CancellationToken ct) => Task.FromResult(Snapshot);
    public Task<IReadOnlyList<SourceDocument>> Retainers(long id, CancellationToken ct) => Task.FromResult(Documents);
    public Task<Stream> Download(long id, CancellationToken ct) { Downloads++; return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("synthetic retainer bytes"))); }
}

internal sealed class MemoryAssets : IRetainerAssets
{
    public readonly Dictionary<string, byte[]> Files = [];
    public async Task Stage(string id, Stream bytes, CancellationToken ct)
    {
        using var copy = new MemoryStream(); await bytes.CopyToAsync(copy, ct); Files.TryAdd(id, copy.ToArray());
    }
    public Task<Stream> Open(string id, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Files[id]));
}

internal sealed class FakeRetainers : IRetainerDelivery
{
    public int Calls;
    public Task Deliver(SyncState state, CancellationToken ct) { Calls++; return Task.CompletedTask; }
}

internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

internal static class Fixtures
{
    public static MappingOptions Mapping() => new()
    {
        LeadVendorIdUdfPath = "Identity/Tiger", MappingVersion = "synthetic-v1",
        Mappings = [new() { StandardProperty = "PrimaryContactNameFirst", Destination = "clientFirstName", Required = true },
            new() { StandardProperty = "PrimaryContactNameLast", Destination = "clientLastName", Required = true },
            new() { StandardProperty = "PrimaryContactEmailAddress", Destination = "clientEmail" },
            new() { UdfPath = "Incident/Type", Destination = "caseType" }]
    };
    public static SourceSnapshot Snapshot() => new(new()
    {
        Id = 1, PrimaryContactNameFirst = "Synthetic", PrimaryContactNameLast = "Person", PrimaryContactEmailAddress = "synthetic@example.invalid",
        CustomFields = [new() { Path = "Identity/Tiger", FieldValue = "TIGER-SYNTHETIC-1", Enabled = true },
            new() { Path = "Incident/Type", FieldValue = "Automobile Accident", Enabled = true }]
    }, true, null);
}
