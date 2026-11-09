using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class StateCipher(string base64Key, string scope)
{
    private readonly byte[] key = Convert.FromBase64String(base64Key);
    public byte[] Encrypt(SyncState state)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(state, IntakeRequest.JsonOptions);
        var output = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Associated(state.SourceCaseId));
        return output;
    }
    public SyncState Decrypt(long caseId, byte[] payload)
    {
        if (payload.Length < 28) throw new InvalidDataException("Invalid state envelope.");
        var plain = new byte[payload.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain, Associated(caseId));
        var state = JsonSerializer.Deserialize<SyncState>(plain, IntakeRequest.JsonOptions) ?? throw new InvalidDataException("Invalid state.");
        if (state.SourceCaseId != caseId) throw new InvalidDataException("State identity mismatch.");
        return state;
    }
    private byte[] Associated(long id) => Encoding.UTF8.GetBytes(scope + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public byte[] LeadHash(string leadId) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(scope + "/" + leadId));
}
