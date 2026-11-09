namespace BriefcaseSync.Core;

public sealed record PersonDescriptionEntry(long ContactId, string? Role, string? FirstName, string? LastName,
    DateTimeOffset? DateOfBirth = null, bool? Deceased = null, string? Email = null, string? Phone = null);
