# Blazor operations workspace

The .NET 10 Blazor Web App uses Interactive Server components and Tailwind CSS 4. It shares the sync domain and vendor source adapter with the worker. There is no CaseConnector dependency and no React runtime.

## Start locally

```powershell
cd src/BriefcaseSync.Web
npm ci
npm run build:css
dotnet run
```

Open `http://localhost:5068`. The checked-in launch profile explicitly enables a **synthetic Development workspace**. Try `SA-10428` in Preview. Demo settings persist only in process memory, reset on restart, and never touch live SQL or vendor APIs. The application rejects demo mode outside Development. Without the launch profile, demo mode defaults to false and live Entra/key-storage configuration is required.

Pages include an overview with case outcomes and document acknowledgement counts, execution history, runtime configuration, and exact-case-number preview. The latest 30 runs and 100 case rows are displayed; overall case counters come from all durable rows. A healthy intake does not imply all retainers are acknowledged; the UI displays their counts separately. The history labels dry-runs explicitly and does not count a scheduled pulse skipped as not due as a sync execution.

### Local SmartAdvocate development preview

To read actual development cases without deploying the live UI, use the explicit local preview profile:

```powershell
# Import source credentials if they have not already been configured.
./eng/Configure-SmartAdvocateDevelopment.ps1
./eng/Configure-LocalPreview.ps1
dotnet run --project src/BriefcaseSync.Web --launch-profile local-preview
```

Setup requires local SQL Server with mixed authentication, developer publication rights, `sqlcmd`, and `sqlpackage`. It publishes the SQL project to the dedicated `BriefcaseSync_LocalPreview` database, provisions a restricted web login, and saves its connection and a state key in web user secrets. It preserves an existing local state key. Runtime has no DDL grants or delivery-state write grants. Rerunning setup rotates this local SQL login's password; restart the local preview host afterward. Setup affects only local SQL and web user secrets.

Local preview is restricted to Development, the exact SmartAdvocate development endpoint, the dedicated local SQL database/scope, and a fixed localhost listener on port 5068. Both peer address and Host must be loopback; forwarded headers are ignored. Local developer access lasts one hour and is attributed to the host developer's OS identity in SQL access audit. This is a local development grant; only run it on a trusted developer machine. Other local processes/users can access this loopback workspace. Deployed live mode continues to require Entra and persistent protected keys. The default `http` profile remains synthetic.

The banner reads **SMARTADVOCATE DEV**. Enter an exact development case number. Values remain hidden until a successful audited reveal. Sending and mapping approval are refused in this mode, and no destination client is constructed. This separate database has no prior delivery history; previews cannot establish whether another installation delivered a case. Missing eligibility status and unverified mapping paths block the delivery plan while allowing available mapped values to be inspected. No eligibility ID, Tiger ID, UDF binding or document classification is guessed.

## Preview semantics

Preview resolves an exact case number through the documented `case/CaseInfo` array response. It refuses no match, multiple matches, or a different returned case number. It loads current source data, existing delivery state and eligible document metadata. The same mapper/validator used for delivery produces the proposed payload; partial valid fields remain visible when other fields fail validation.

Explicitly disabled optional narrative parts appear as separate blocked rows with no value and their exclusion reason in the source column. They do not block their parent field or become outbound API keys. The parent counts only enabled answers. The production profile excludes the unverified helmet answer while retaining the other four vehicle answers. Required or enabled-part validation failures still block delivery. Frozen retries show the original payload without implying that current exclusions have changed it.

A live preview request is audited before loading case details. When no primary contact or multiple primary contacts are selected, preview still loads case details, UDF answers and eligible document metadata. It leaves primary-contact fields empty and flags the result as **Validation blocked**, with a message that the case cannot sync until exactly one primary contact is selected. The primary plaintiff is never substituted. The worker's source load still requires exactly one explicit primary contact and fails before delivery if that selection is missing or ambiguous.

If UDF responses fail completeness or ownership validation, preview discards the entire UDF answer set, retains other available case data and shows a separate sync blocker. Repeated answer pages are never accepted as a completed read. Repeated definition-only metadata is skipped; a definitions-only response means no saved answers were returned. Strict sync loading continues to reject incomplete or invalid answer responses.

The planner distinguishes new intake, validation block, ineligible case, identity conflict, frozen retry, acknowledged unchanged intake, and unsupported update. Frozen retries show the old request and original mapping version rather than pretending that newer source answers would be sent. The intake card is a presentation of outbound values, not a reproduction of BriefCase AI's proprietary UI or a promise of its server defaults.

Field mapping shows included, omitted and blocked destinations with source bindings. Retainer preview reads metadata only, so a previously delivered document is described as a content comparison candidate rather than incorrectly claiming that changed bytes will be skipped. Existing incomplete sessions show resume/reconciliation actions. Request JSON displays the proposed flat intake without credentials or upload URLs.

The enabled person narrative includes the main contact even for a single-person lead. An unmapped or conflicting person role is shown as a blocked `description` field while available primary-contact fields remain visible. The preview does not substitute the injured client for a different primary contact or silently drop a person with an unverified role.

Preview constructs **no destination client**, performs no vendor writes, stages no Blob bytes and changes no case delivery state. Its only database writes are protected access audit records. It is an observation, not a send approval or a frozen future commit; source/state/configuration can change afterward. There is no "send this preview" action.

Values and filenames are absent from rendered HTML until a user with `Sync.Preview` explicitly reveals them. An access audit must succeed before disclosure. The UI hides values again after two minutes, when starting another preview, or when leaving the component. Server-side session expiry is checked for every service action; an old interactive circuit cannot continue using an expired session. Demo audit is intentionally in memory/no external writes and is labeled synthetic.

## Shared settings and schedule

Runtime configuration is held in `sync.Configuration` as a versioned JSON contract. Changes and actor ID are recorded atomically in `sync.AccessAudit`. SQL uses a serializable version comparison, so stale editors fail instead of overwriting a newer save. Editing a draft does not mutate deployment defaults or a previously published version.

Editable settings include UTC cron, eligibility status, delivery/approval gates, tenant classification IDs for `OnTask` / `Submitted File` documents, timeout, phone type labels, Tiger ID UDF path, mapping version and the complete mapping JSON. Tenant endpoints, credentials, scope, encryption key and numeric contact-choice dictionaries remain deployment-owned. The editable contract contains no credential fields. Incomplete drafts can retain status `0` and unapproved source paths while delivery is off; approval/sending require complete bindings.

The document type mapping editor saves `DocumentTypeMappings` with editable SmartAdvocate source names and a BriefCase destination dropdown. Six explicit name-for-name pairs are supplied initially: `Signed Retainer Agreement`, `Unsigned Retainer Agreement`, `ID Document`, `Police Report`, `Medical Record`, and `Other`. Source matching is ordinal and exact; blank sources, duplicate sources and unsupported destinations are rejected, including when delivery is disabled. Rows may be added or removed; there is no implicit `Other` or signed-retainer fallback. Mappings use the same versioned, audited configuration contract and are copied independently from deployment defaults and editor drafts. No database schema change is needed.

The user confirmed on November 9, 2026 that the document record's `docType` is the source business classification for this integration. The source adapter preserves that value verbatim; Core resolves it through the configured mappings. The worker sends the mapped value as required `documentType` in `retainer/init`. Missing or unmapped values block that document before download, staging or initialization, with no filename, category or file-format inference. Preview displays both the source `docType` and mapped BriefCase `documentType` after audited disclosure, and shows mapping-required actions for missing bindings. Pending documents display their frozen type rather than a newly edited mapping.

The entire initialization request (intake ID, Tiger ID, mapped type, filename and staged byte size) and source type are stored in encrypted document state before HTTP. Retries and permitted expired-session replacements use that frozen request. A mapping change does not retag or re-upload unchanged, acknowledged content. Older pending documents without a frozen request require explicit operator review before initialization; existing upload IDs can still complete or resume, but cannot be replaced with a guessed type. Uncertain initialization still requires the existing reconciliation workflow. These additions use the encrypted state envelope; runtime DDL is not needed.

`ManagedConfigurationEnabled=true` makes both UI and worker use the same saved settings. The worker loads a fresh snapshot before validation/execution, then keeps it for that run. A running sync never observes half-edited mappings. Saving with delivery enabled may cause the next due scheduled run to write to BriefCase AI; the form explains this consequence.

The Azure job is now an **every-minute wake-up pulse** (`* * * * *`). The saved five-field UTC cron determines the business schedule. Under the existing global run lock, the worker compares the schedule against the latest run start for that configuration version (or the save time if none). A delayed pulse performs one due run, not a burst of missed runs. Durable run history is written before discovery and on completion/failure. A crashed Running marker is marked Interrupted by the next due run after lock recovery.

Existing installations must deploy the additive SQL schema and new grants, enable managed configuration, and change the Azure pulse to every minute. Leaving the old 15-minute Azure schedule will limit how quickly UI schedule changes can be honored. Setting managed configuration false leaves the deployment configuration authoritative; the UI then refuses saves.

## Live authentication and deployment

Publish the updated SQL project before either host. Apply the separate worker/web contained-user grants from `database/authorize-runtime.example.sql`. Worker can write Cases/Runs and read Configuration. Web can read Cases/Runs, write Configuration and append AccessAudit; it cannot modify delivery state or run outcomes.

Create a single-tenant Entra confidential web application and assign explicit app roles:

| Role | Access |
| --- | --- |
| `Sync.View` | Operational overview and history |
| `Sync.Preview` | Overview/history plus preview and sensitive disclosure |
| `Sync.Configure` | Overview/history plus settings changes |

Configuration does not imply sensitive preview permission. Register `https://<actual-host>/signin-oidc` and the appropriate signed-out callback. The app uses authorization code + PKCE, validates the configured tenant, uses secure HTTP-only cookies, and stores no downstream user tokens. Sessions expire after one hour; role assignment changes are effective on fresh sign-in, with that bounded existing-session lifetime. Entra role authorization, Azure RBAC, SQL grants and network reachability remain separate authorities. UI visibility is not the permission boundary: the service checks each operation again.

`infra/bootstrap.bicep` provisions a separate web identity. Grant it access only to the source PAT, state key and Entra client-secret through the existing Key Vault access template with the correct secret-name list; it receives no BriefCase destination-write credentials. Grant managed-identity SQL permissions and registry pull separately.

Live settings are supplied through user secrets or `BRIEFCASE_` environment variables:

- `Scope`, `SqlConnectionString`, `StateEncryptionKey`, `ManagedIdentityClientId`, `SmartAdvocate__ApiUrl`, and `SmartAdvocate__PersonalAccessToken`.
- `Entra__TenantId`, `Entra__ClientId`, and `Entra__ClientSecret`.
- `Web__DataProtectionBlobUri` (a private keyring Blob URI) and `Web__DataProtectionKeyUri` (a versionless Key Vault key URI). Grant scoped Blob access and key wrap/unwrap operations to the web identity. Preserve old Key Vault key versions for existing keyring decryption.
- `Web__KnownProxies__0` or `Web__KnownProxyNetworks__0` for reviewed ingress addresses/CIDRs. Configure exact AllowedHosts using the framework setting `AllowedHosts` or `BRIEFCASE_AllowedHosts`.

Build with `docker build -f Dockerfile.Web ...` and review `infra/web.bicep`. It references the existing application environment/storage, owns a private web-key container, uses Key Vault-protected persistent Data Protection keys and defaults to one web replica. WebSocket/circuit routing, trusted forwarded HTTPS, sign-in callbacks, certificate/custom host, resource networking and authorization must be validated in the actual Azure topology before launch. No public URL/deployment is claimed by the checked-in template. Use the same immutable image through promotion.

## Design-system adoption and verification

The canonical `MassTortArchitecture/apps/design-system/app/globals.css` is copied byte-for-byte to `Styles/reference/globals.css`. `docs/design-system-provenance.json` pins its hash and upstream revision. The copy retains the reference's exact semantic names; native Blazor shell/components use them. Application changes live in `Styles/app.css`, not in the reference. `eng/Verify-DesignSystem.ps1` verifies the copy offline. The reference font fallbacks are retained; no unlicensed font binaries are introduced.

The workspace implements graphite framing, indigo actions, text-supported statuses, compact/comfortable density, light/dark themes, visible focus, a skip link, responsive navigation, labeled controls and loading/empty/error/success/restricted states. Operational telemetry does not export content: framework logging providers are disabled, user-facing errors use fixed messages and responses are no-store. Access/configuration audit is durable and separately permissioned, not sampled diagnostics.

`eng/verify.ps1` restores/builds Tailwind, verifies tokens, runs .NET tests, builds SQL/Bicep and publishes both hosts. Browser checks are separate:

```powershell
cd src/BriefcaseSync.Web
npx playwright install chromium
npm run test:browser
```

Browser tests cover disclosure, JSON/field/retainer views, unknown cases, filter/detail behavior, settings validation/persistence, dark theme and narrow-screen navigation. Screenshots are synthetic and written under ignored `artifacts/ui`. CI also builds both container images. Live Entra/SQL/Key Vault/vendor acceptance requires the deployment-specific pilot.
