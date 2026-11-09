using System.Net;
using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class VendorTests
{
    [Fact]
    public async Task DuplicateTigerLeadResponseBindsExistingIntake()
    {
        using var api = Client(request => Task.FromResult(Handler.Json(new { status = "Error", errorCode = "DUPLICATE_LEAD_VENDOR_ID", intakeId = "existing-intake" }, HttpStatusCode.Conflict)));
        Assert.Equal("existing-intake", await new BriefcaseDestination(api).Create(new IntakeRequest { LeadVendorId = "TIGER-SYNTHETIC" }, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "SAVE_ERROR", true)]
    [InlineData(HttpStatusCode.InternalServerError, "SERVER_ERROR", false)]
    [InlineData(HttpStatusCode.BadGateway, "GATEWAY_ERROR", false)]
    public async Task OnlyDocumentedValidationFailureReleasesFrozenRequest(HttpStatusCode status, string code, bool definitive)
    {
        using var api = Client(_ => Task.FromResult(Handler.Json(new { status = "Error", errorCode = code }, status)));
        var exception = await Record.ExceptionAsync(() => new BriefcaseDestination(api).Create(new(), default));
        Assert.NotNull(exception);
        Assert.Equal(definitive, exception is DestinationRejectedException);
    }

    [Fact]
    public async Task ExpiredBearerRefreshesOnceWithoutChangingPayload()
    {
        int auth = 0, posts = 0;
        using var api = new BriefCaseAiApiClient(new(), new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("token")) return Handler.Json(new { access_token = "token-" + ++auth });
            Assert.Contains("TIGER-SYNTHETIC", await request.Content!.ReadAsStringAsync());
            return ++posts == 1 ? Handler.Json(new { }, HttpStatusCode.Unauthorized) : Handler.Json(new { status = "Success", intakeId = "created" });
        })), new HttpClient());
        Assert.Equal("created", await new BriefcaseDestination(api).Create(new() { LeadVendorId = "TIGER-SYNTHETIC" }, default));
        Assert.Equal(2, auth); Assert.Equal(2, posts);
    }

    [Fact]
    public async Task UnknownInitOutcomeBlocksFreshSessionOnNextRun()
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", "Signed Retainer Agreement")] };
        var store = new MemoryStore(); var assets = new MemoryAssets(); int initializations = 0;
        using var api = Client(_ => { initializations++; throw new HttpRequestException("synthetic lost response"); });
        var state = BoundState();
        var delivery = new RetainerDelivery(source, store, assets, api, true, DocumentTypes.ExactMatches());
        await Assert.ThrowsAsync<HttpRequestException>(() => delivery.Deliver(state, default));
        Assert.Equal("InitUnresolved", store.States[1].Documents.Values.Single().Status);
        var recovered = await store.Read(1, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.Deliver(recovered, default));
        Assert.Equal(1, initializations); Assert.Equal(1, source.Downloads); Assert.Single(assets.Files);
    }

    [Fact]
    public async Task LostCompletionResponseRecoversByCompletingExistingSessionAndDoesNotReupload()
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", "Signed Retainer Agreement")] };
        var store = new MemoryStore(); var assets = new MemoryAssets(); int init = 0, complete = 0, puts = 0;
        using var api = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("init"))
            {
                init++;
                return Task.FromResult(Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "upload-1", uploadUrl = "https://storage.example.invalid/file?sig=synthetic", uploadMethod = "PUT", uploadHeaders = new { }, expiresAt = DateTimeOffset.UtcNow.AddHours(1), maxFileSizeBytes = RetainerDelivery.MaximumFileSize }));
            }
            if (++complete == 1) throw new HttpRequestException("synthetic completion response lost");
            return Task.FromResult(Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "upload-1" }));
        }, request =>
        {
            puts++; Assert.Null(request.Headers.Authorization); Assert.Equal(HttpMethod.Put, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        });
        var delivery = new RetainerDelivery(source, store, assets, api, true, DocumentTypes.ExactMatches());
        await Assert.ThrowsAsync<HttpRequestException>(() => delivery.Deliver(BoundState(), default));
        source.Documents = []; // The frozen bytes survive source deletion/changes.
        await delivery.Deliver(await store.Read(1, default), default);
        Assert.Equal(1, init); Assert.Equal(1, puts); Assert.Equal(2, complete);
        Assert.Equal("Delivered", store.States[1].Documents.Values.Single().Status);
        Assert.Null(store.States[1].Documents.Values.Single().UploadUrl);
    }

    [Fact]
    public async Task SignedUploadCannotForwardBearerOrCookieHeaders()
    {
        int requests = 0;
        using var api = Client(_ => throw new InvalidOperationException(), _ => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)); });
        var session = new DocumentState { UploadUrl = "https://storage.example.invalid/file", UploadHeaders = new() { ["Authorization"] = "synthetic" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Put(session, new MemoryStream([1, 2, 3]), "application/pdf"));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task CompletionMustMatchBothIntakeAndUploadIdentities()
    {
        var store = new MemoryStore(); var state = BoundState();
        state.Documents["synthetic"] = new() { Identity = "synthetic", UploadId = "upload-1", Status = "Uploaded" };
        using var api = Client(_ => Task.FromResult(Handler.Json(new { status = "Success", intakeId = "wrong-intake", uploadId = "upload-1" })));
        await Assert.ThrowsAsync<InvalidDataException>(() => new RetainerDelivery(new FakeSource(), store, new MemoryAssets(), api, true, DocumentTypes.ExactMatches()).Deliver(state, default));
        Assert.NotEqual("Delivered", state.Documents["synthetic"].Status);
    }

    [Fact]
    public async Task ReconciliationRequiresVerifiedFrozenBytesAndRecordsOperatorReference()
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", "Signed Retainer Agreement")] };
        var store = new MemoryStore(); var assets = new MemoryAssets();
        using (var uncertain = Client(_ => throw new HttpRequestException("synthetic lost initialization")))
            await Assert.ThrowsAsync<HttpRequestException>(() => new RetainerDelivery(source, store, assets, uncertain, true, DocumentTypes.ExactMatches()).Deliver(BoundState(), default));
        var session = store.States[1].Documents.Values.Single();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(assets.Files[session.Identity]));
        int completions = 0;
        using var api = Client(_ => { completions++; return Task.FromResult(Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "verified-upload" })); });
        var reconciliation = new UploadReconciliation(store, assets, api);
        var request = new UploadReconciliationRequest { SourceCaseId = 1, AssetIdentity = session.Identity, UploadId = "verified-upload", VerifiedContentSha256 = hash, OperatorReference = "SYNTHETIC-CHANGE-1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciliation.Reconcile(request, default));
        Assert.Equal(0, completions);
        request.FirmVerifiedExactFile = true;
        request.VerifiedContentSha256 = new string('0', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciliation.Reconcile(request, default));
        Assert.Equal(0, completions);
        request.VerifiedContentSha256 = hash;
        await reconciliation.Reconcile(request, default);
        Assert.Equal(1, completions);
        Assert.Equal("Delivered", store.States[1].Documents.Values.Single().Status);
        Assert.Equal("SYNTHETIC-CHANGE-1", Assert.Single(store.States[1].Reconciliations).OperatorReference);
    }

    [Fact]
    public async Task ExpiredIncompleteSessionIsReplacedOnlyAfterDefinitiveCompletionCheck()
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", "Signed Retainer Agreement")] };
        var store = new MemoryStore(); var assets = new MemoryAssets(); var events = new List<string>();
        int initializations = 0; string? frozenJson = null;
        var mappings = DocumentTypes.ExactMatches();
        using var api = Client(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("init"))
            {
                var json = await request.Content!.ReadAsStringAsync();
                if (frozenJson == null) frozenJson = json; else Assert.Equal(frozenJson, json);
                Assert.Equal("Signed Retainer Agreement", store.States[1].Documents.Values.Single().InitializationRequest!.DocumentType);
                events.Add("init"); initializations++;
                return Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "upload-" + initializations, uploadUrl = "https://storage.example.invalid/file", uploadMethod = "PUT", expiresAt = DateTimeOffset.UtcNow.AddHours(1), maxFileSizeBytes = RetainerDelivery.MaximumFileSize });
            }
            events.Add("complete");
            return initializations == 1 ? Handler.Json(new { status = "Error", errorCode = "UPLOAD_NOT_COMPLETED" }, HttpStatusCode.Conflict) : Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "upload-2" });
        }, _ =>
        {
            events.Add("put");
            if (initializations == 1) throw new HttpRequestException("synthetic interrupted upload");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        });
        var delivery = new RetainerDelivery(source, store, assets, api, true, mappings);
        await Assert.ThrowsAsync<HttpRequestException>(() => delivery.Deliver(BoundState(), default));
        var expired = await store.Read(1, default); expired.Documents.Values.Single().ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.Save(expired, default); source.Documents = []; events.Clear();
        mappings[0].DocumentType = "Other"; // A newer configuration cannot rewrite the frozen request.
        await delivery.Deliver(await store.Read(1, default), default);
        Assert.Equal(new[] { "complete", "init", "put", "complete" }, events);
        Assert.Equal("Delivered", store.States[1].Documents.Values.Single().Status);
    }

    [Theory]
    [InlineData("Signed Retainer Agreement")]
    [InlineData("Unsigned Retainer Agreement")]
    [InlineData("ID Document")]
    [InlineData("Police Report")]
    [InlineData("Medical Record")]
    [InlineData("Other")]
    public async Task InitializationSendsMappedTypeFromDurableRequest(string targetType)
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", "SA synthetic type")] };
        var store = new MemoryStore(); var assets = new MemoryAssets(); int initializations = 0;
        using var api = Client(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("init"))
            {
                initializations++;
                var frozen = store.States[1].Documents.Values.Single();
                Assert.Equal("Initializing", frozen.Status);
                Assert.Equal("SA synthetic type", frozen.SourceDocumentType);
                var json = await request.Content!.ReadAsStringAsync();
                Assert.Equal(JsonSerializer.Serialize(frozen.InitializationRequest, IntakeRequest.JsonOptions), json);
                using var parsed = JsonDocument.Parse(json);
                Assert.Equal(targetType, parsed.RootElement.GetProperty("documentType").GetString());
                Assert.Equal("TIGER-SYNTHETIC", parsed.RootElement.GetProperty("leadVendorId").GetString());
                return Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "upload-1", uploadUrl = "https://storage.example.invalid/file", uploadMethod = "PUT", expiresAt = DateTimeOffset.UtcNow.AddHours(1), maxFileSizeBytes = RetainerDelivery.MaximumFileSize });
            }
            return Handler.Json(new { status = "Success", intakeId = "intake-1", uploadId = "upload-1" });
        });
        var delivery = new RetainerDelivery(source, store, assets, api, true,
            [new() { SourceType = "SA synthetic type", DocumentType = targetType }]);
        var state = BoundState();
        await delivery.Deliver(state, default);
        await delivery.Deliver(await store.Read(1, default), default);
        Assert.Equal(1, initializations); // Same document bytes are not uploaded again.
        Assert.Equal("Delivered", state.Documents.Values.Single().Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown source type")]
    public async Task MissingTypeMappingBlocksBeforeDownloadStagingOrVendorHttp(string? sourceType)
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", sourceType)] };
        var assets = new MemoryAssets(); var state = BoundState(); int requests = 0;
        using var api = Client(_ => { requests++; throw new InvalidOperationException(); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RetainerDelivery(source, new MemoryStore(), assets, api, true,
            DocumentTypes.ExactMatches()).Deliver(state, default));
        Assert.Equal(0, requests); Assert.Equal(0, source.Downloads); Assert.Empty(assets.Files); Assert.Empty(state.Documents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySessionWithoutFrozenTypeCannotStartOrReplaceInitialization(bool expired)
    {
        var source = new FakeSource { Documents = [new(10, "synthetic.pdf", "Signed Retainer Agreement")] };
        var store = new MemoryStore(); var assets = new MemoryAssets();
        using (var rejected = Client(_ => Task.FromResult(Handler.Json(new { status = "Error", errorCode = "VALIDATION_ERROR" }, HttpStatusCode.BadRequest))))
            await Assert.ThrowsAsync<FirmApiException>(() => new RetainerDelivery(source, store, assets, rejected, true,
                DocumentTypes.ExactMatches()).Deliver(BoundState(), default));
        var state = await store.Read(1, default); var session = state.Documents.Values.Single();
        session.InitializationRequest = null;
        if (expired) { session.UploadId = "old-upload"; session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); }
        int requests = 0;
        using var api = Client(request =>
        {
            requests++; Assert.EndsWith("complete", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Handler.Json(new { status = "Error", errorCode = "UPLOAD_NOT_COMPLETED" }, HttpStatusCode.Conflict));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RetainerDelivery(source, store, assets, api, true,
            DocumentTypes.ExactMatches()).Deliver(state, default));
        Assert.Equal(expired ? 1 : 0, requests);
        Assert.Equal(expired ? "old-upload" : null, session.UploadId);
    }

    [Theory]
    [InlineData("synthetic.doc")]
    [InlineData("synthetic")]
    [InlineData("")]
    public void RetainerTypeValidationExcludesLegacyDocAndMissingExtensions(string name) => Assert.Throws<InvalidDataException>(() => RetainerDelivery.ValidateFileName(name));

    private static SyncState BoundState() => new() { SourceCaseId = 1, LeadVendorId = "TIGER-SYNTHETIC", IntakeId = "intake-1", Status = "Created" };
    internal static BriefCaseAiApiClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send, Func<HttpRequestMessage, Task<HttpResponseMessage>>? upload = null) => new(new(),
        new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("token") ? Task.FromResult(Handler.Json(new { access_token = "synthetic-token" })) : send(request))),
        new HttpClient(new Handler(upload ?? (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created))))));
}
