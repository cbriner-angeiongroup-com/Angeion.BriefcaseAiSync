using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class BlobRetainerAssets(BlobContainerClient container, string scope) : IRetainerAssets
{
    private string PathFor(string identity)
    {
        if (identity.Length != 64 || identity.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid asset identity.");
        return IntakeDelivery.Hash(scope) + "/" + identity;
    }
    public async Task Stage(string identity, Stream bytes, CancellationToken ct)
    {
        var blob = container.GetBlobClient(PathFor(identity));
        try { await blob.UploadAsync(bytes, new BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All } }, ct); }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            // A prior stage may have succeeded before SQL commit. Require exact content before reuse.
            bytes.Position = 0;
            var expected = await SHA256.HashDataAsync(bytes, ct);
            await using var existing = await blob.OpenReadAsync(cancellationToken: ct);
            var actual = await SHA256.HashDataAsync(existing, ct);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Staged asset content mismatch.");
        }
    }
    public async Task<Stream> Open(string identity, CancellationToken ct) => await container.GetBlobClient(PathFor(identity)).OpenReadAsync(cancellationToken: ct);
}
