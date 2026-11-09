using System.Net;
using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class SmartAdvocateTests
{
    [Fact]
    public async Task DevelopmentConnectionAuthenticatesWithUsernamePasswordWithoutRequiringSyncSettings()
    {
        var options = new SmartAdvocateOptions
        {
            ApiUrl = "https://sa.example.invalid/Development/VendorSmartAdvocate/",
            Username = "synthetic-user", Password = "synthetic-password"
        };
        int authentications = 0, reads = 0;
        using var client = new SmartAdvocateClient(options, new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                authentications++;
                Assert.Equal(options.ApiUrl + "users/authenticate", request.RequestUri!.ToString());
                Assert.Null(request.Headers.Authorization);
                using var form = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("synthetic-user", form.RootElement.GetProperty("username").GetString());
                Assert.Equal("synthetic-password", form.RootElement.GetProperty("password").GetString());
                return Handler.Json(new { token = "synthetic-token" });
            }
            reads++;
            Assert.Equal(options.ApiUrl + "case/RequestStatuses", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            return Handler.Json(new[] { new { id = 1, description = "Synthetic" } });
        })));
        Assert.Equal(1, await client.CheckConnection(default));
        Assert.Equal(1, await client.CheckConnection(default));
        Assert.Equal(1, authentications); Assert.Equal(2, reads);
        Assert.Equal(0, options.EligibleStatusId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[null]")]
    public async Task ConnectionCheckRejectsUnusableReferenceData(string body)
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckConnection(default));
    }

    [Fact]
    public async Task ConnectionCheckRejectsInsecureUrlBeforeSendingCredentials()
    {
        var options = Options(); options.ApiUrl = "http://sa.example.invalid/";
        using var client = new SmartAdvocateClient(options,
            new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send."))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckConnection(default));
    }

    [Fact]
    public async Task StatusLookupUsesStatusIdAndTopAsCaseStateFilter()
    {
        using var client = Client(request =>
        {
            Assert.Contains("SearchType=CASESTATUS", request.RequestUri!.Query);
            Assert.Contains("Criteria=321", request.RequestUri.Query);
            Assert.Contains("Top=2", request.RequestUri.Query);
            var response = Handler.Json(new[] { new { caseID = 1 }, new { caseID = 2 } });
            response.Headers.Add("X-Total-Count", "2"); return response;
        });
        Assert.Equal(new long[] { 1, 2 }, await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).Discover(default));
    }

    [Fact]
    public async Task TruncatedLookupFailsInsteadOfSilentlySkippingCases()
    {
        using var client = Client(_ => { var response = Handler.Json(new[] { new { caseID = 1 } }); response.Headers.Add("X-Total-Count", "2"); return response; });
        await Assert.ThrowsAsync<InvalidDataException>(() => new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).Discover(default));
    }

    [Theory]
    [InlineData(321, 999, 321, true)]
    [InlineData(999, 321, 321, false)]
    [InlineData(0, 321, 321, false)]
    [InlineData(0, 0, 0, false)]
    public async Task OnlyMainStatusCanQualifyANewIntake(long mainStatus, long secondaryStatus, long configuredStatus, bool eligible)
    {
        var options = Options(); options.EligibleStatusId = configuredStatus;
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = mainStatus, secondStatuses = new[] { new { statusID = secondaryStatus } } }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = true } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Synthetic")),
            "/CaseSyncAPI/case/1/udf" => Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("Unexpected path")
        }, options);
        var source = new SmartAdvocateSource(client, options, Fixtures.Mapping());
        Assert.Equal(eligible, (await source.Load(1, default)).Eligible);
        Assert.Equal(eligible, (await source.LoadPreview(1, default)).Snapshot.Eligible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousPrimaryContactNeverFallsBackToThePrimaryPlaintiff(bool ambiguous)
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[]
            {
                new { id = 10, contactID = 100, primaryContact = ambiguous, primary = true },
                new { id = 11, contactID = 101, primaryContact = ambiguous, primary = false }
            }),
            _ => throw new InvalidOperationException("No contact details or UDFs should be read without an unambiguous selection.")
        });
        var error = await Assert.ThrowsAsync<PrimaryContactSelectionException>(() => new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).Load(1, default));
        Assert.Equal(ambiguous, error.Ambiguous);
    }

    [Theory]
    [InlineData(false, 1, 999, 1, false)]
    [InlineData(true, 1, 999, 1, true)]
    [InlineData(true, 2, 999, 1, false)]
    [InlineData(true, 1, 777, 1, false)]
    [InlineData(true, 2, 321, 1, true)]
    public async Task TestTriggerRequiresOptInSelectedCaseAndMatchingMainStatus(bool enabled, long caseId, long mainStatus, long selectedCase, bool eligible)
    {
        var options = Options();
        options.TestTrigger = new() { Enabled = enabled, SourceCaseId = selectedCase, MainStatusId = 999 };
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path == $"/CaseSyncAPI/case/{caseId}" => Handler.Json(new { caseID = caseId, caseStatusID = mainStatus, secondStatuses = new[] { new { statusID = 999 } } }),
            var path when path == $"/CaseSyncAPI/case/{caseId}/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = true } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Synthetic")),
            var path when path == $"/CaseSyncAPI/case/{caseId}/udf" => Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("Unexpected path")
        }, options);
        var source = new SmartAdvocateSource(client, options, Fixtures.Mapping());
        Assert.Equal(eligible, (await source.Load(caseId, default)).Eligible);
        Assert.Equal(eligible, (await source.LoadPreview(caseId, default)).Snapshot.Eligible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestTriggerAddsOnlyExplicitCaseAfterCompleteProductionDiscovery(bool enabled)
    {
        var options = Options();
        options.TestTrigger = new() { Enabled = enabled, MainStatusId = 999, SourceCaseId = 3 };
        var requests = 0;
        using var client = Client(request =>
        {
            requests++;
            Assert.Contains("Criteria=321", request.RequestUri!.Query);
            Assert.DoesNotContain("Criteria=999", request.RequestUri.Query);
            var response = Handler.Json(new[] { new { caseID = 1 } });
            response.Headers.Add("X-Total-Count", "1");
            return response;
        }, options);
        var actual = await new SmartAdvocateSource(client, options, Fixtures.Mapping()).Discover(default);
        Assert.Equal(enabled ? new long[] { 1, 3 } : new long[] { 1 }, actual);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(999, 0)]
    [InlineData(-1, 1)]
    public async Task InvalidTestTriggerFailsBeforeAnySourceRequest(long mainStatus, long selectedCase)
    {
        var options = Options();
        options.TestTrigger = new() { Enabled = true, MainStatusId = mainStatus, SourceCaseId = selectedCase };
        using var client = Client(_ => throw new InvalidOperationException("No requests allowed."), options);
        Assert.Throws<InvalidOperationException>(options.ValidatePreview);
        var source = new SmartAdvocateSource(client, options, Fixtures.Mapping());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.Discover(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadPreview(1, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewWithoutOnePrimaryContactLoadsCaseDataButBlocksSync(bool ambiguous)
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321, incident = new { incidentDate = "2026-01-02T00:00:00Z" } }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[]
            {
                new { id = 10, contactID = 100, primaryContact = ambiguous, primary = true },
                new { id = 11, contactID = 101, primaryContact = ambiguous, primary = false }
            }),
            "/CaseSyncAPI/case/1/udf" => request.RequestUri.Query.Contains("currentPage=0")
                ? Handler.Json(new[] { new { udfid = 2, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true } })
                : Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("No contact should be selected implicitly, and preview must not download or write anything.")
        });
        var loaded = await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).LoadPreview(1, default);
        Assert.Equal(ambiguous ? "AMBIGUOUS_PRIMARY_CONTACT" : "MISSING_PRIMARY_CONTACT", Assert.Single(loaded.Errors).Code);
        Assert.Null(loaded.Snapshot.Case.PrimaryContactNameFirst);
        Assert.Null(loaded.Snapshot.Case.PrimaryContactNameLast);
        Assert.Null(loaded.Snapshot.Case.PrimaryContactEmailAddress);
        Assert.Null(loaded.Snapshot.Case.PrimaryContactPhoneNumber);
        Assert.All(loaded.Snapshot.Case.SourceFields.Values, value => Assert.Null(value));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), loaded.Snapshot.Case.IncidentDate);
        Assert.Single(loaded.Snapshot.Case.CustomFields);
        Assert.True(loaded.Snapshot.Eligible);
        var mapping = Fixtures.Mapping();
        mapping.Mappings.Add(new() { StandardProperty = "IncidentDate", Destination = "incidentDate" });
        mapping.Mappings.Add(new() { SourceField = "PrimaryContactMobilePhone", Destination = "clientMobilePhone" });
        mapping.Mappings.Add(new() { SourceField = "PrimaryContactLanguages", Destination = "clientLanguagesSpoken" });
        var plan = PreviewPlanner.Build("SYNTHETIC-1", loaded.Snapshot, null, mapping, [new(7, "synthetic.pdf")], loaded.Errors);
        Assert.Equal("Validation blocked", plan.Action);
        Assert.Contains("cannot sync", plan.Explanation);
        Assert.Equal("2026-01-02", plan.ProposedIntake!.IncidentDate);
        Assert.Equal("Blocked with intake", Assert.Single(plan.Documents).Action);
        Assert.DoesNotContain(plan.Errors, e => e.Code == "MAPPING_ERROR");
        Assert.Equal("Omitted", plan.Fields.Single(f => f.Key == "clientMobilePhone").Outcome);
    }

    [Fact]
    public async Task PrimaryContactCanBeDifferentFromInjuredClientAndDefinitionRowsAreNotCaseAnswers()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = false, primary = true }, new { id = 11, contactID = 101, primaryContact = true, primary = false } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Injured")),
            "/CaseSyncAPI/contact/fullInfo/101" => Handler.Json(Contact(101, "Representative")),
            "/CaseSyncAPI/case/1/udf" => request.RequestUri.Query.Contains("currentPage=0")
                ? Handler.Json(new object[] { new { udfid = 1, udfName = "definition only" }, new { udfid = 2, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true } })
                : Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("Unexpected path")
        });
        var snapshot = await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).Load(1, default);
        Assert.True(snapshot.Eligible); Assert.Equal("Representative", snapshot.Case.PrimaryContactNameFirst);
        Assert.Equal("5551234567", snapshot.Case.PrimaryContactPhoneNumber); Assert.Single(snapshot.Case.CustomFields);
        Assert.Equal("No", snapshot.Case.SourceFields["PrimaryContactSmsOptOut"]);
        var preview = await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).LoadPreview(1, default);
        Assert.Empty(preview.Errors);
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(preview.Snapshot));
    }

    [Theory]
    [InlineData("MVA-FB-SE", true)]
    [InlineData("Other", false)]
    [InlineData(null, false)]
    public async Task ConfiguredCaseTypeUsesHeaderAndOnlyTranslatesApprovedClassification(string? caseType, bool accepted)
    {
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/mapping.json")));
        var configured = profile.RootElement.GetProperty("Mapping").Deserialize<MappingOptions>()!;
        var mapping = Fixtures.Mapping();
        mapping.Mappings.Add(new() { StandardProperty = "PrimaryContactPhoneNumber", Destination = "clientPhone" });
        mapping.Mappings.RemoveAll(m => m.Destination == "caseType");
        mapping.Mappings.Add(configured.Mappings.Single(m => m.Destination == "caseType"));
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321, caseType }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = true } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Synthetic")),
            "/CaseSyncAPI/case/1/udf" => request.RequestUri.Query.Contains("currentPage=0")
                ? Handler.Json(new[]
                {
                    new { udfid = 2, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true },
                    new { udfid = 3, caseID = 1, screenName = "Incident", udfShortName = "Type", udfValue = "ITC", isActive = true }
                })
                : Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("Unexpected path")
        });
        var source = new SmartAdvocateSource(client, Options(), mapping);
        var snapshot = await source.Load(1, default);
        Assert.Equal(caseType, snapshot.Case.CaseType);
        if (accepted)
        {
            Assert.Equal("Automobile Accident", new IntakeMapper(mapping).Map(snapshot.Case).CaseType);
            var preview = await source.LoadPreview(1, default);
            Assert.Equal("Automobile Accident", PreviewPlanner.Build("SYNTHETIC-1", preview.Snapshot, null, mapping, [], preview.Errors).ProposedIntake!.CaseType);
        }
        else
        {
            var error = Assert.Throws<IntakeValidationException>(() => new IntakeMapper(mapping).Map(snapshot.Case));
            Assert.Contains(error.Errors, e => e.Field == "caseType");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewDiscardsIncompleteOrInvalidUdfsAndStrictSyncStillFails(bool repeatedPage)
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = true } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Synthetic")),
            "/CaseSyncAPI/case/1/udf" => Handler.Json(repeatedPage
                ? new object[] { new { udfid = 2, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true } }
                : request.RequestUri.Query.Contains("currentPage=0")
                    ? new object[] { new { udfid = 2, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true },
                        new { udfid = 3, caseID = 999, screenName = "Incident", udfShortName = "Type", udfValue = "Automobile Accident", isActive = true } }
                    : Array.Empty<object>()),
            _ => throw new InvalidOperationException("Unexpected path")
        });
        var source = new SmartAdvocateSource(client, Options(), Fixtures.Mapping());
        var loaded = await source.LoadPreview(1, default);
        Assert.Equal("Synthetic", loaded.Snapshot.Case.PrimaryContactNameFirst);
        Assert.Empty(loaded.Snapshot.Case.CustomFields);
        Assert.Equal("SOURCE_UDF_UNAVAILABLE", Assert.Single(loaded.Errors).Code);
        var plan = PreviewPlanner.Build("SYNTHETIC-1", loaded.Snapshot, null, Fixtures.Mapping(), [], loaded.Errors);
        Assert.Equal("Validation blocked", plan.Action);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.Load(1, default));
    }

    [Fact]
    public async Task DefinitionOnlyUdfMetadataIsNotMistakenForRepeatedAnswers()
    {
        var udfReads = 0;
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(Array.Empty<object>()),
            "/CaseSyncAPI/case/1/udf" => Definitions(),
            _ => throw new InvalidOperationException("Unexpected path")
        });
        HttpResponseMessage Definitions()
        {
            udfReads++;
            return Handler.Json(new[] { new { udfid = 10, udfName = "Synthetic definition", isActive = true } });
        }
        var loaded = await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).LoadPreview(1, default);
        Assert.Equal("MISSING_PRIMARY_CONTACT", Assert.Single(loaded.Errors).Code);
        Assert.Empty(loaded.Snapshot.Case.CustomFields);
        Assert.Equal(1, udfReads);
    }

    [Fact]
    public async Task UdfAnswersContinueAcrossPagesWithRecurringDefinitionMetadata()
    {
        var udfReads = 0;
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = true } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Synthetic")),
            "/CaseSyncAPI/case/1/udf" => Page(),
            _ => throw new InvalidOperationException("Unexpected path")
        });
        HttpResponseMessage Page()
        {
            object definition = new { udfid = 10, udfName = "Synthetic definition", isActive = true };
            return Handler.Json(udfReads++ switch
            {
                0 => new object[] { definition, new { udfid = 2, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true } },
                1 => new object[] { definition, new { udfid = 3, caseID = 1, screenName = "Incident", udfShortName = "Type", udfValue = "Automobile Accident", isActive = true } },
                2 => new object[] { definition },
                _ => throw new InvalidOperationException("The answer stream should have completed.")
            });
        }
        var snapshot = await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).Load(1, default);
        Assert.Equal(3, udfReads);
        Assert.Equal(2, snapshot.Case.CustomFields.Count);
        Assert.Equal("TIGER-SYNTHETIC", new IntakeMapper(Fixtures.Mapping()).LeadVendorId(snapshot.Case));
        Assert.Equal("Automobile Accident", IntakeMapper.Udf(snapshot.Case, "Incident/Type"));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task MetadataDoesNotProveAnswerCompletionAndReportedTotalsRemainChecked(int total, bool complete)
    {
        var reads = 0;
        using var client = Client(_ =>
        {
            var response = Handler.Json(reads++ switch
            {
                0 => new[] { new MetadataRow(10, 0), new MetadataRow(3, 0), new MetadataRow(2, 1) },
                1 => new[] { new MetadataRow(10, 0), new MetadataRow(3, 1) },
                2 => new[] { new MetadataRow(10, 0) },
                _ => throw new InvalidOperationException("Unexpected page")
            });
            response.Headers.Add("X-Total-Count", total.ToString()); return response;
        });
        var task = client.Pages<MetadataRow>("case/1/udf", row => row.Id, default, include: row => row.CaseId != 0);
        if (complete) Assert.Equal(new long[] { 2, 3 }, (await task).Select(row => row.Id));
        else await Assert.ThrowsAsync<InvalidDataException>(() => task);
        Assert.Equal(3, reads);
    }

    private sealed record MetadataRow(long Id, long CaseId);

    [Theory]
    [InlineData("OnTask", "Submitted File")]
    [InlineData("ontask", "submitted file")]
    [InlineData(" OnTask ", " Submitted File ")]
    public async Task DocumentsRequireBothOnTaskSubmittedFileNamesAndConfiguredIds(string category, string subcategory)
    {
        var options = Options();
        options.RetainersEnabled = true; options.RetainerCategoryId = 75; options.RetainerSubcategoryId = 614;
        var pages = 0;
        using var client = Client(request =>
        {
            Assert.Equal("/CaseSyncAPI/case/1/documents", request.RequestUri!.AbsolutePath);
            Assert.Contains("categoryIDs=75&subcategoryIDs=614", request.RequestUri.Query);
            return Handler.Json(pages++ == 0 ? new[]
            {
                Document(1, category, subcategory),
                Document(2, "Other", "Submitted File"),
                Document(3, "OnTask", "Other"),
                Document(4, null, "Submitted File"),
                Document(5, "OnTask", null),
                Document(6, "", ""),
                Document(7, "OnTask", "Submitted File", categoryId: 76),
                Document(8, "OnTask", "Submitted File", subcategoryId: 615),
                Document(9, "OnTask", "Submitted File", deleted: true),
                Document(10, "OnTask Other", "Submitted File"),
                Document(11, "OnTask", "Submitted File Other")
            } : Array.Empty<CaseDocument>());
        }, options);
        var documents = await new SmartAdvocateSource(client, options, Fixtures.Mapping()).Retainers(1, default);
        Assert.Equal(1, Assert.Single(documents).Id);
        Assert.Equal("Signed Retainer Agreement", documents[0].DocType);
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task MatchingDocumentClassificationDoesNotAllowAnotherCasesDocument()
    {
        var options = Options();
        options.RetainersEnabled = true; options.RetainerCategoryId = 75; options.RetainerSubcategoryId = 614;
        var document = Document(1, "OnTask", "Submitted File"); document.CaseId = 2;
        using var client = Client(request => Handler.Json(request.RequestUri!.Query.Contains("currentPage=0")
            ? new[] { document } : Array.Empty<CaseDocument>()), options);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SmartAdvocateSource(client, options, Fixtures.Mapping()).Retainers(1, default));
    }

    [Fact]
    public async Task DisabledDocumentSyncDoesNotReadSourceDocuments()
    {
        using var client = Client(_ => throw new InvalidOperationException("Document sync is disabled."));
        Assert.Empty(await new SmartAdvocateSource(client, Options(), Fixtures.Mapping()).Retainers(1, default));
    }

    [Theory]
    [InlineData("SA Signed Retainer Agreement")]
    [InlineData("Other")]
    [InlineData(null)]
    public async Task SourceCarriesDocumentDocTypeVerbatimWithoutInferringFromClassification(string? docType)
    {
        var options = Options();
        options.RetainersEnabled = true; options.RetainerCategoryId = 75; options.RetainerSubcategoryId = 614;
        int pages = 0;
        using var client = Client(_ => Handler.Json(pages++ == 0 ? new[]
        {
            new { documentID = 10, caseID = 1, documentName = "synthetic.pdf", docType,
                categoryID = 75, categoryName = "OnTask", subCategoryID = 614, subCategoryName = "Submitted File" }
        } : []), options);
        var documents = await new SmartAdvocateSource(client, options, Fixtures.Mapping()).Retainers(1, default);
        Assert.Equal(docType, Assert.Single(documents).DocType);
    }

    private static CaseDocument Document(long id, string? category, string? subcategory,
        long categoryId = 75, long subcategoryId = 614, bool deleted = false) => new()
    {
        DocumentId = id, CaseId = 1, DocumentName = "synthetic.pdf", DocType = "Signed Retainer Agreement", CategoryId = categoryId,
        SubCategoryId = subcategoryId, CategoryName = category, SubCategoryName = subcategory, DeleteFlag = deleted
    };

    [Fact]
    public async Task RepeatedPageFailsAndNeverReturnsPartialResult()
    {
        var options = Options(); options.PageSize = 1;
        using var client = Client(_ => Handler.Json(new[] { new { caseID = 1 } }), options);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Pages<CaseLookup>("case/synthetic", c => c.CaseId, default));
    }

    [Fact]
    public async Task SourceUnauthorizedRefreshesTokenOnce()
    {
        int reads = 0;
        using var client = Client(_ => ++reads == 1 ? Handler.Json(new { }, HttpStatusCode.Unauthorized) : Handler.Json(new { caseID = 1 }));
        Assert.Equal(1, (await client.Read<CaseLookup>("case/1", default)).CaseId); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ShortPagesWithoutTotalsContinueUntilEmpty()
    {
        int reads = 0;
        using var client = Client(_ => ++reads < 3 ? Handler.Json(new[] { new { caseID = reads } }) : Handler.Json(Array.Empty<object>()));
        var result = await client.Pages<CaseLookup>("case/synthetic", c => c.CaseId, default);
        Assert.Equal(new long[] { 1, 2 }, result.Select(c => c.CaseId)); Assert.Equal(3, reads);
    }

    private static object Contact(long id, string first) => new { contactID = id, firstName = first, lastName = "Synthetic", active = true,
        phones = new[] { new { name = "Cell", phoneNumber = "5551234567", primary = true, useForTexting = true } },
        emails = Array.Empty<object>(), addresses = Array.Empty<object>() };
    private static SmartAdvocateOptions Options() => new() { ApiUrl = "https://sa.example.invalid/CaseSyncAPI/", PersonalAccessToken = "synthetic-pat", EligibleStatusId = 321 };
    private static SmartAdvocateClient Client(Func<HttpRequestMessage, HttpResponseMessage> send, SmartAdvocateOptions? options = null) => new(options ?? Options(), new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("authenticate") ? Handler.Json(new { token = "synthetic-token" }) : send(request)))));
}
