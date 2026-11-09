# Briefcase Sync

Standalone C#/.NET 10 application that sends SmartAdvocate intakes and signed retainer documents to a configured BriefCase AI tenant. It has no CaseConnector, CaseWorks, or sibling-project runtime dependencies. Azure Container Apps Jobs starts a finite sync run on a configurable schedule.

A Blazor + Tailwind operations UI provides sync details, configuration, and case-number previews. See [UI setup and deployment](docs/ui.md). Run `dotnet run --project src/BriefcaseSync.Web` and open `http://localhost:5068` to try the synthetic Development workspace; preview case `SA-10428`.

**Tiger Lead ID is the shared key.** The exact SmartAdvocate UDF value becomes BriefCase `leadVendorId`; the returned `intakeId` is retained for document delivery. SmartAdvocate case IDs are internal identities; case numbers are never rewritten. The configured production trigger is main status `SE-10.010-SINGLE EVENT-Case Completed` (ID `32409`); secondary statuses never qualify a new intake. Verify tenant-specific IDs when using another environment.

## Behavior

1. Acquire a SQL session lock so scheduled and manual executions cannot overlap for the same scope.
2. Query `Search/caselookup?SearchType=CASESTATUS&Criteria={statusId}&Top=2`. `Top` means open/closed/all, not a maximum result count. Reject a truncated response when its total-count header disagrees.
3. Persist the completed discovery batch atomically. Reload each case and require its current main status to match the configured trigger before creating a new intake.
4. Load the primary contact, related people and all case UDF pages. Apply the supplied workbook's mapping profile and API validation rules.
5. Persist the Tiger ID binding, mapping version and frozen request before sending. The vendor's documented duplicate-ID response safely recovers a lost intake response.
6. Discover only documents in category `OnTask` and subcategory `Submitted File`, matching both configured tenant classification IDs. Map each document's exact `docType` through the UI-configurable document type mappings to BriefCase `documentType`, with no fallback. Stage immutable content in Blob Storage, then persist and execute upload initialization, signed PUT, and completion.
7. Revisit bound cases even after they leave the ready status, preserving pending deliveries and discovering later retainers. Previously dispatched work uses frozen inputs. A new, undispatched intake is not sent after its eligibility disappears.

The supplied BriefCase contract has **no intake read/update endpoint**. Changed answers for a created intake become `UpdateRequired` with the desired payload retained; another create is never treated as an update. This version is outbound only. It does not write back to SmartAdvocate or provision UDFs.

## Repository

| Location | Responsibility |
| --- | --- |
| `src/BriefcaseSync.Core` | Mapping, validation, identity, durable intent and orchestration |
| `src/BriefcaseSync.Infrastructure` | Direct vendor clients, SQL state, encryption, Blob assets and retainer delivery |
| `src/BriefcaseSync.Worker` | Configuration, cancellation, composition and aggregate operational output |
| `src/BriefcaseSync.Web` | Blazor UI, Entra authorization, audited previews and shared runtime settings |
| `config/mapping.json` | Workbook profile: 38 destinations, including conditional narrative groups; free-text plaintiff details are preserved in notes |
| `database/BriefcaseSync.Database` | Azure SQL schema/DACPAC; runtime never publishes schema |
| `infra` | Application resources, job schedule, and separately scoped shared-server database template |
| `tests` | Synthetic workflow/transport tests and opt-in real SQL tests |

## Build and verify

Requires the SDK pinned in `global.json`. No sibling repositories or vendor credentials are needed to build or run synthetic tests.

```powershell
dotnet restore BriefcaseSync.sln --locked-mode
dotnet test BriefcaseSync.sln -c Release --no-restore
dotnet build database/BriefcaseSync.Database/BriefcaseSync.Database.sqlproj -c Release
dotnet publish src/BriefcaseSync.Worker -c Release -o artifacts/worker
```

`eng/verify.ps1` also compiles Bicep. The SQL test creates and drops its own GUID-named database on an explicitly selected development SQL server; it never reuses an application database:

```powershell
$env:BRIEFCASE_TEST_SQL_MASTER = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
./eng/verify.ps1 -SqlIntegration
```

Without that environment variable, only the SQL integration test is skipped. The GitHub verification workflow supplies an isolated SQL service and builds the container image. It does not publish or deploy.

## Configure and run

Defaults are sending-disabled, unapproved, and intentionally incomplete. Set configuration through .NET user secrets for local work and Key Vault references in Azure. The worker reads `appsettings.json`, the mapping file, user secrets, optional `appsettings.Local.json` beside the executable, then environment variables prefixed `BRIEFCASE_`.

| Setting | Required value |
| --- | --- |
| `Scope` | Stable source-instance/destination-tenant binding, shared by every replica of this integration |
| `SqlConnectionString` | Application-owned database; managed identity authentication in Azure |
| `StateEncryptionKey` | Base64-encoded 32 random bytes, backed up separately from SQL |
| `BlobServiceUri` | Owned account endpoint, required for sending |
| `SmartAdvocate:ApiUrl` | Actual tenant's HTTPS vendor API base with trailing slash |
| `SmartAdvocate:PersonalAccessToken` | Tenant PAT; username/password authentication is also supported |
| `SmartAdvocate:EligibleStatusId` | Main status only; production `32409` = `SE-10.010-SINGLE EVENT-Case Completed`, supplied by the shared profile |
| `SmartAdvocate:TestTrigger:Enabled`, `MainStatusId`, `SourceCaseId` | Optional extra trigger for one explicitly selected source case; disabled by default. Profile test main status `3886` = `DOCKET ADMIN CASE`. |
| `SmartAdvocate:RetainersEnabled` | Enable after signed-document classification is verified |
| `SmartAdvocate:RetainerCategoryId`, `RetainerSubcategoryId` | Tenant IDs for category `OnTask` AND subcategory `Submitted File` |
| `SmartAdvocate:EmergencyPhoneLabelPrefix` | Explicit emergency-label convention; production profile uses `EC:` in addition to the exact emergency phone type |
| `SmartAdvocate:PersonRoles` | Verified SA role IDs to person-block roles; profile maps `26411` (`(P)-Plaintiff`) to `Client`. Other roles block until verified. |
| `Briefcase:ApiUrl`, `ClientId`, `ClientSecret` | Destination tenant base and OAuth client credentials |
| `Mapping:LeadVendorIdUdfPath` | Production binding `Case Incident/LAW-TRIGERS-ID`; exact value becomes `leadVendorId` |
| `Mapping:Mappings` | Production workbook/UDF bindings supplied; helmet part explicitly disabled pending motorcycle verification, while other vehicle answers remain active |
| `Mapping:DescriptionEnabled`, `DescriptionContractVerified` | Shared profile enables the firm's person-block field, including the primary contact; mappings remain unapproved and sending-disabled |
| `MappingApproved`, `SendingEnabled` | Both must be true to write remotely |

Environment examples: `BRIEFCASE_SmartAdvocate__EligibleStatusId`, `BRIEFCASE_Briefcase__ClientSecret`, `BRIEFCASE_StateEncryptionKey`. Set `BRIEFCASE_MAPPING_FILE` to an absolute approved mapping JSON file, or package the approved `config/mapping.json` in the promoted image. Keep per-environment mapping arrays in the file rather than partially overriding array indices through environment variables.

To opt in to the test trigger, set `BRIEFCASE_SmartAdvocate__TestTrigger__Enabled=true` and `BRIEFCASE_SmartAdvocate__TestTrigger__SourceCaseId` to the selected internal SA case ID, using the same settings on the worker and preview host. The source must still have main status `3886`; secondary statuses never qualify. These deployment-owned settings are retained when managed runtime settings are loaded. Discovery adds only that one case to the normal production candidates; it does not scan all docket-admin cases or restrict the normal production workload. This switch does not approve mappings, enable sending, alter identities, or bypass document validation. Disable it after testing. Verify the test status ID separately for another tenant.

### SmartAdvocate development API and authentication

Import the existing `Cameron - SmartAdvocate` development partner while Azure CLI is signed into the
Case Works subscription, then verify both hosts' configuration without SQL or a sync run:

```powershell
./eng/Configure-SmartAdvocateDevelopment.ps1
dotnet run --project src/BriefcaseSync.Worker -- --check-smartadvocate
dotnet run --project src/BriefcaseSync.Web -- --check-smartadvocate
```

The development vendor base is `https://cwapps.n8protect.com/Development/VendorSmartAdvocate/`.
Authentication POSTs username/password JSON to `users/authenticate`; subsequent reads use its bearer
token. The existing client caches that token and refreshes once when a GET receives HTTP 401.
The import reads Azure account `caseworksdevasa000`, table `CaseConnectorTradingPartner`, partition
`default`, row `17a78518-9aa4-42fe-aa63-9ecf741b8cbb`. It verifies the partner, exact development URL,
and authentication method before merging API URL/username/password into both hosts' .NET user secrets.
It clears any local PAT to select username/password authentication and sets local sending and approval
to false. Unrelated secrets are preserved. Credential values travel over stdin, never command arguments
or repository files. User secrets are local development storage, not an encrypted production secret store.
Rerun the import after credentials rotate. If needed, sign in with
`az login --tenant 36e4d61d-ea81-465c-bc28-dcb1302d8d46` first.

Each `--check-smartadvocate` command authenticates through the application's actual client, reads
`case/RequestStatuses`, and prints only a reference count or a fixed failure code. Redirects are disabled.
It exits before SQL, mapping validation, web authentication setup, sync execution or destination writes.
The default Blazor launch profile still shows the synthetic workspace. For actual development case reads,
run `./eng/Configure-LocalPreview.ps1`, then
`dotnet run --project src/BriefcaseSync.Web --launch-profile local-preview`.
See [local preview requirements](docs/ui.md#local-smartadvocate-development-preview) and
[deployed live workspace requirements](docs/ui.md#live-authentication-and-deployment).

Verified October 28, 2026: both worker and web connection checks authenticated and read 322 request
statuses from this development API. No case reads, sync runs, destination writes or Azure changes were
performed by those checks.

```powershell
dotnet user-secrets init --project src/BriefcaseSync.Worker
# Set private runtime settings using your approved secret workflow; do not paste secrets into Git.
dotnet run --project src/BriefcaseSync.Worker -- --validate-config
dotnet run --project src/BriefcaseSync.Worker
```

With `SendingEnabled=false`, a run reads SmartAdvocate, acquires the SQL lock and validates mappings. It creates no vendor records, stages no files, and saves no case delivery state; execution history is recorded in SQL. Configuration validation makes no network calls. A dry-run still needs complete source bindings, SQL access and the state key; it does not bypass correctness checks.

Exit code `0` means the run completed without failures/attention, or another run already holds the lock; `1` means case failures or reconciliation/update attention; `2` means cancellation, timeout, invalid configuration or dependency failure. Standard output contains aggregate counts and fixed event/code values only.

With managed configuration enabled, both hosts use the UI's versioned SQL settings. Azure wakes the worker every minute; the saved UTC cron determines when syncs are due. Apply the updated SQL project/grants and wake-up pulse when upgrading the worker-only version.

## Deployment and contracts

Read [architecture](docs/architecture.md), [mapping and open decisions](docs/mapping.md), and [deployment/recovery](docs/operations.md). Development prerequisites were partially provisioned on November 9, 2026. App hosting remains blocked by resource-group and SQL publication permissions. See [the development inventory and deployment status](docs/development-deployment.md). No live vendor records were created.

Authoritative inputs: [SmartAdvocate CaseSync API](https://documenter.getpostman.com/view/5921778/UVeJM5kQ), `Farrar and Ball UDF Creation and Field Mapping.xlsx` from Downloads, and `Angeion_Lead_API_Specification.pdf` from the original connector repository. [Provenance](docs/provenance.json) records the inspected local source hashes. The workbook does not supply actual tenant UDF paths.
