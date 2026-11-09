using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BriefcaseSync.Infrastructure;

public sealed class SmartAdvocateClient(SmartAdvocateOptions options, HttpClient http) : IDisposable
{
    private string? token;
    private readonly SemaphoreSlim authentication = new(1, 1);
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task Authenticate(string? rejected, CancellationToken ct)
    {
        await authentication.WaitAsync(ct);
        try
        {
            if (token != null && (rejected == null || rejected != token)) return;
            var pat = !string.IsNullOrWhiteSpace(options.PersonalAccessToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.ApiUrl), pat ? "Users/authenticate/pat" : "users/authenticate"));
            request.Content = pat ? JsonContent.Create(new { Token = options.PersonalAccessToken }) : JsonContent.Create(new { options.Username, options.Password });
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("SmartAdvocate authentication failed.");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            token = body.RootElement.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("SmartAdvocate authentication omitted token.");
        }
        finally { authentication.Release(); }
    }

    public async Task<HttpResponseMessage> Get(string path, CancellationToken ct)
    {
        await Authenticate(null, ct);
        for (int attempt = 0; ; attempt++)
        {
            var usedToken = token;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.ApiUrl), path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", usedToken);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                await Authenticate(usedToken, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException("SmartAdvocate read failed.", null, status);
            }
            return response;
        }
    }

    public async Task<T> Read<T>(string path, CancellationToken ct)
    {
        using var response = await Get(path, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct) ?? throw new InvalidDataException("Missing source response.");
    }

    public async Task<int> CheckConnection(CancellationToken ct)
    {
        // Connectivity requires only source credentials, not SQL, mappings or eligibility settings.
        options.ValidateConnection();
        using var response = await Get("case/RequestStatuses", ct);
        await response.Content.LoadIntoBufferAsync(1_048_576, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (body.RootElement.ValueKind != JsonValueKind.Array || body.RootElement.GetArrayLength() == 0 ||
            body.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Invalid SmartAdvocate reference response.");
        return body.RootElement.GetArrayLength();
    }

    public async Task<List<T>> Pages<T>(string path, Func<T, long> identity, CancellationToken ct, Func<T, bool>? include = null)
    {
        var result = new List<T>();
        var seen = new HashSet<long>();
        var responseIdentities = new HashSet<long>();
        long? expectedTotal = null;
        for (int page = 0; page < options.MaxPages; page++)
        {
            var separator = path.Contains('?') ? '&' : '?';
            using var response = await Get($"{path}{separator}currentPage={page}&pageSize={options.PageSize}", ct);
            var batch = await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct) ?? throw new InvalidDataException("Missing source page.");
            var includedCount = 0;
            foreach (var item in batch)
            {
                var id = identity(item);
                if (id <= 0) throw new InvalidDataException("Invalid source page identity.");
                responseIdentities.Add(id);
                // Case UDF pages may repeat definition-only metadata, even when there are no more case answers.
                if (include != null && !include(item)) continue;
                if (!seen.Add(id)) throw new InvalidDataException("Invalid, duplicate or repeated source page identity.");
                result.Add(item); includedCount++;
            }
            if (response.Headers.TryGetValues("X-Total-Count", out var counts))
            {
                if (!long.TryParse(counts.Single(), out var total) || total < 0 || expectedTotal != null && total != expectedTotal)
                    throw new InvalidDataException("Source pagination total is invalid or changed during the read.");
                expectedTotal = total;
                if (responseIdentities.Count > total) throw new InvalidDataException("Source pagination exceeded reported total.");
                // Metadata can enumerate a field before its answer appears on a later page. It cannot prove completion.
                if (include == null && result.Count == total) return result;
            }
            // Without a total, a short page may be a server-imposed page-size cap. Read until empty.
            if (includedCount == 0)
            {
                if (expectedTotal != null && responseIdentities.Count != expectedTotal) throw new InvalidDataException("Source pagination is incomplete.");
                return result;
            }
        }
        throw new InvalidDataException("Source pagination exceeded configured maximum; nothing was silently truncated.");
    }

    public void Dispose() { http.Dispose(); authentication.Dispose(); }
}
