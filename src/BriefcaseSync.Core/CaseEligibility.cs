namespace BriefcaseSync.Core;

public static class CaseEligibility
{
    public static bool MatchesMainStatus(long mainStatusId, long triggerStatusId) =>
        triggerStatusId > 0 && mainStatusId == triggerStatusId;

    public static bool Matches(long sourceCaseId, long mainStatusId, long productionStatusId, TestTriggerOptions testTrigger) =>
        MatchesMainStatus(mainStatusId, productionStatusId)
        || testTrigger.Enabled && sourceCaseId > 0 && sourceCaseId == testTrigger.SourceCaseId
            && MatchesMainStatus(mainStatusId, testTrigger.MainStatusId);
}

public sealed class TestTriggerOptions
{
    public bool Enabled { get; set; }
    public long MainStatusId { get; set; }
    public long SourceCaseId { get; set; }

    public void Validate()
    {
        if (MainStatusId < 0 || SourceCaseId < 0 || Enabled && (MainStatusId == 0 || SourceCaseId == 0))
            throw new InvalidOperationException("The enabled test trigger requires a positive main status ID and one explicit source case ID.");
    }
}
