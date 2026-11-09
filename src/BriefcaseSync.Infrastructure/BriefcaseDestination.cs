using System.Net;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class BriefcaseDestination(BriefCaseAiApiClient client) : IBriefcaseDestination
{
    public async Task<string> Create(IntakeRequest request, CancellationToken ct)
    {
        var (status, response) = await client.Post("intake", request, ct);
        if (!(status == HttpStatusCode.Conflict && response.ErrorCode == "DUPLICATE_LEAD_VENDOR_ID"))
        {
            // Only documented validation rejections prove that nothing was created.
            if (status == HttpStatusCode.BadRequest && response.Status == "Error" && ValidationCodes.Contains(response.ErrorCode ?? ""))
                throw new DestinationRejectedException();
            BriefCaseAiApiClient.RequireSuccess(status, response);
        }
        if (string.IsNullOrWhiteSpace(response.IntakeId)) throw new InvalidOperationException("Intake response omitted its identity.");
        return response.IntakeId;
    }
    private static readonly HashSet<string> ValidationCodes = new(StringComparer.Ordinal)
    {
        "INVALID_JSON", "INVALID_REQUEST", "MISSING_REQUIRED_FIELD", "INVALID_VALUE", "INVALID_DATE",
        "INVALID_BOOLEAN", "INVALID_PICKLIST_VALUE", "INVALID_EMAIL", "INVALID_FORMAT", "VALUE_TOO_LONG",
        "INVALID_CASE_TYPE", "INVALID_CASE_SUB_TYPE", "SAVE_ERROR"
    };
}
