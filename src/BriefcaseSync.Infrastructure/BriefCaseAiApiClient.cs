using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class ApiResponse
{
    public string? Status { get; set; }
    public string? ErrorCode { get; set; }
    public string? Field { get; set; }
    public string? Error { get; set; }
    public string? IntakeId { get; set; }
    public string? UploadId { get; set; }
    public string? UploadUrl { get; set; }
    public string? UploadMethod { get; set; }
    public Dictionary<string, string>? UploadHeaders { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long? MaxFileSizeBytes { get; set; }
    public string? IntakeStatus { get; set; }
}

public sealed class FirmApiException(HttpStatusCode statusCode, ApiResponse response) : Exception($"Firm API returned HTTP {(int)statusCode}; code={Safe(response.ErrorCode)}, field={Safe(response.Field)}.")
{
    // Structured response is available to authorized correction tooling, never logged automatically.
    public HttpStatusCode StatusCode { get; } = statusCode;
    public ApiResponse Response { get; } = response;
    public bool Transient => (int)StatusCode >= 500 || StatusCode == HttpStatusCode.RequestTimeout || (int)StatusCode == 429;
    private static string Safe(string? value) => value != null && value.Length <= 100 && value.All(c => char.IsLetterOrDigit(c) || c == '_') ? value : "unspecified";
}

public sealed class BriefCaseAiApiClient : IDisposable
{
    private readonly BriefcaseOptions options;
    private readonly HttpClient api;
    private readonly HttpClient storage;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? token;
    private DateTimeOffset tokenExpiry;
    public BriefCaseAiApiClient(BriefcaseOptions options, HttpClient? api = null, HttpClient? storage = null)
    {
        this.options = options;
        this.api = api ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.storage = storage ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(15) };
        if (ReferenceEquals(this.api, this.storage)) throw new ArgumentException("API and storage clients must be separate.");
    }
    public async Task Authenticate(CancellationToken ct = default, string? rejectedToken = null)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            if (token != null && tokenExpiry > DateTimeOffset.UtcNow && (rejectedToken == null || rejectedToken != token)) return;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.ApiUrl), "services/oauth2/token"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "client_credentials", ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret });
            using var response = await api.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OAuth token acquisition failed with HTTP {(int)response.StatusCode}.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("access_token", out var value) || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidOperationException("OAuth response omitted access_token.");
            token = value.GetString();
            tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.TryGetProperty("expires_in", out var lifetime) && lifetime.TryGetInt32(out var seconds) ? Math.Max(1, seconds - 30) : 1800);
        }
        finally { tokenLock.Release(); }
    }
    public async Task<(HttpStatusCode Status, ApiResponse Body)> Post(string path, object payload, CancellationToken ct = default)
    {
        await Authenticate(ct);
        for (int attempt = 0; ; attempt++)
        {
            var sentToken = token!;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.ApiUrl), "services/apexrest/api/angeion/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sentToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, IntakeRequest.JsonOptions), Encoding.UTF8, "application/json");
            using var response = await api.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { await Authenticate(ct, sentToken); continue; }
            ApiResponse body;
            try { body = JsonSerializer.Deserialize<ApiResponse>(await response.Content.ReadAsStringAsync(ct), IntakeRequest.JsonOptions) ?? new(); }
            catch (JsonException) { body = new() { ErrorCode = "MALFORMED_RESPONSE" }; }
            return (response.StatusCode, body);
        }
    }
    public static void RequireSuccess(HttpStatusCode status, ApiResponse body)
    {
        if (status != HttpStatusCode.OK || !string.Equals(body.Status, "Success", StringComparison.OrdinalIgnoreCase)) throw new FirmApiException(status, body);
    }
    public async Task Put(DocumentState session, Stream stream, string contentType, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(session.UploadUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo) || session.UploadMethod != "PUT")
            throw new InvalidOperationException("Upload session must specify an HTTPS URL and PUT.");
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        // The stream belongs to the caller. StreamContent disposal must not close it between attempts.
        request.Content = new StreamContent(new NonClosingStream(stream));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content.Headers.ContentLength = stream.Length - stream.Position;
        foreach (var (name, value) in session.UploadHeaders)
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe signed-upload header.");
            if (!request.Headers.TryAddWithoutValidation(name, value) && !request.Content.Headers.TryAddWithoutValidation(name, value)) throw new InvalidOperationException("Invalid signed-upload header.");
        }
        // No default credential headers may be present on the injected storage client.
        if (storage.DefaultRequestHeaders.Any()) throw new InvalidOperationException("Storage client must have no default headers.");
        try
        {
            using var response = await storage.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Created) throw new FirmApiException(response.StatusCode, new() { ErrorCode = "STORAGE_UPLOAD_FAILED" });
        }
        catch (HttpRequestException) { throw new HttpRequestException("Signed storage upload failed; retry the persisted session."); }
        catch (TaskCanceledException) { throw new TaskCanceledException("Signed storage upload was interrupted; retry the persisted session."); }
    }
    public void Dispose() { api.Dispose(); storage.Dispose(); tokenLock.Dispose(); }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
