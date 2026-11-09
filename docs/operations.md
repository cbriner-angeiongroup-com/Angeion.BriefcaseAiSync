# Deployment, monitoring and recovery

## Prepare an environment

1. Resolve source/destination tenant ownership, UDF paths and the tenant classification IDs for category `OnTask` / subcategory `Submitted File`. The shared profile configures production main status `SE-10.010-SINGLE EVENT-Case Completed` (ID `32409`); secondary statuses do not trigger a new intake. Verify environment overrides and saved managed settings use that ID for production; other tenants require their own verified ID. Replace all mapping placeholders and version the approved profile. Start with sending disabled.
2. Build/test the app and SQL project. Build the image, record its source revision and digest, and promote the same image/DACPAC between environments. `Dockerfile` uses a non-root .NET 10 runtime. `eng/verify.ps1` compiles/publishes local artifacts; CI additionally builds the container.
3. Select a dedicated application resource group and reviewed region. Use an existing Container Apps environment if required by the approved network path; otherwise the application template creates one. Record network reachability to SQL, Blob, Key Vault, registry and both vendor endpoints. SQL/Key Vault private endpoints and firewall permissions are separate; these templates do not open a shared SQL server to all Azure services.
4. Review `infra/database.bicep` in the explicitly selected shared SQL server's resource group (`caseworks-development`, `caseworks-qa`, or `caseworks-prod` only when that server is actually selected). It creates the owned database, not the server. Review the DACPAC deployment report and publish additive schema changes separately. Do not drop environment users/roles during DACPAC publication.
5. Provision `infra/bootstrap.bicep` in the application resource group before resolving the runtime client ID in the SQL connection string and granting external access. It declares the same identity name/location as the main template. Review/apply `infra/keyvault-access.bicep` in the existing vault's resource group to grant Key Vault Secrets User at each required secret; the vault must use Azure RBAC authorization. Separately grant AcrPull on the selected registry when applicable. Publish no vendor credentials in parameter files. The app template supplies scoped Blob access; SQL grants are independent.
6. As the database Entra administrator, apply the reviewed contained-user grants from `database/authorize-runtime.example.sql`, replacing the identity name. Runtime receives SELECT/INSERT/UPDATE on its table, no DDL or database-owner role. An observer needs only the health view. Use `Authentication=Active Directory Managed Identity;User Id=<runtime-client-id>` in the Azure SQL connection string.
7. Put the SmartAdvocate PAT, BriefCase OAuth client ID/secret, and base64 32-byte state encryption key in Key Vault. Preserve the key independently of the database and version references according to the rotation plan. Changing the state key without re-encrypting all state also changes HMAC identity claims and is unsupported.
8. Copy `infra/main.example.bicepparam` to a private environment file, replace all placeholders, and run `az deployment group what-if` against the application resource group. Start with `triggerType='Manual'`, `sendingEnabled=false`, and `mappingApproved=false`. Review before deployment. The job needs working secret access at creation; complete bootstrap grants first.
9. Run configuration validation and a manual dry-run with the approved configuration and source read access. Check primary selection, every UDF branch, paging totals, eligibility changes and document classification in the actual tenant. Then complete an explicitly authorized synthetic vendor pilot to verify OAuth, duplicate-ID behavior, upload/expiry/completion, token refresh and privacy of telemetry. Local tests do not establish live vendor parity.
10. Configure health/alerts, the operator, access policy, retention and restore objectives. Enable the approved mapping and sending only after pilot acceptance. Follow [the UI deployment guide](ui.md) for Entra, persistent cookie keys and web grants. Switch `triggerType='Schedule'` with an every-minute Azure pulse and save the business cron in the UI.

Development prerequisites were partially provisioned on November 9, 2026; see [the exact resource inventory and blockers](development-deployment.md). The database resource, app-prefixed vault objects, directory applications and images exist, but application hosting and SQL schema publication are still blocked. Other environments and unselected networking/vendor capabilities remain unverified.

## Configure the schedule

For a test case on `DOCKET ADMIN CASE`, opt in through the deployment-owned `SmartAdvocate:TestTrigger` settings: set `Enabled=true`, `MainStatusId=3886`, and the selected internal SA `SourceCaseId`. Apply the same settings to worker and preview host. An enabled test trigger adds this one case to normal production discovery and rechecks its current main status; it is not an isolated test-only run. Keep sending disabled for preview. Remote delivery still requires an approved, complete mapping and explicit session authorization. Turn the test trigger off after the test. Saved managed configuration does not replace these deployment-owned fields.

The Azure job uses a **five-field UTC wake-up pulse**, now every minute (`* * * * *`). The saved business cron defaults to every 15 minutes (`*/15 * * * *`); the worker checks durable run history to decide when a sync is due. It is independent of local machine time and America/Chicago daylight-saving changes. [Azure scheduling semantics](https://learn.microsoft.com/en-us/azure/container-apps/jobs).

```powershell
az containerapp job update --name <job-name> --resource-group <application-resource-group> --cron-expression '* * * * *'
az containerapp job start --name <job-name> --resource-group <application-resource-group>
```

Use Bicep for the pulse and the UI for business schedules. A manual job start also respects the saved due time. Apply the additive SQL schema and new grants before upgrading either host. `replicaRetryLimit=0` leaves retry to durable application work on the next scheduled/manual execution. The SQL lock serializes overlapping executions; it is not sufficient to configure `parallelism=1`, because separate job executions can overlap. The worker's timeout should remain below the job timeout so it can cancel and exit.

## Health and operator response

Collect aggregate console events through the environment's Log Analytics configuration. If using an existing environment, its configured workspace is authoritative; the new workspace output is not proof that existing-environment console data flows there. Configure resource diagnostics for job executions, SQL, Blob and Key Vault according to environment policy. Avoid request/dependency instrumentation that would export document URLs, SQL text, credentials or payloads.

Use an observer identity with SELECT on `sync.Health` to monitor status counts and oldest/latest state times. Configure alert queries and an action group/operator for failed or missing scheduled executions, increasing `RetryRequired`, `Rejected`, `IdentityConflict`, `UpdateRequired`, and unresolved uploads. Job exit `1` indicates failures/attention, and `2` indicates run-level failure. A successful overlapping-run skip is not a fresh processing heartbeat. Retainer outcomes are inside encrypted state; the current aggregate SQL view exposes case status, so correlate job failures with protected upload review rather than assuming `Created` means all retainers completed.

Protected inspection tools must decrypt state with the same key and associated scope/case identity; grant that access only to integration operators. Never copy decoded payloads, names, filenames, signed URLs or raw vendor errors into general logs or tickets. No public operator UI/API is implemented.

| State | Response |
| --- | --- |
| `Discovered` | Pending current source load and eligibility check |
| `RetryRequired` | Correct source/dependency issue; next run preserves previously frozen intent |
| `Rejected` | A documented validation rejection created nothing; source correction may be remapped with the same Tiger ID |
| `Created` | Intake link is acknowledged; inspect document state separately |
| `UpdateRequired` | Desired changed answers are retained; arrange firm-side correction until a supported update API exists |
| `IdentityConflict` | Compare the existing binding and actual Tiger ID; do not generate another ID or erase state |
| Upload `Initializing`/`InitUnresolved` | Firm-side reconciliation is required before any fresh upload initialization |

## Document type configuration and upgrades

In Configuration, map each selected source document's exact `docType` to one of the six supported BriefCase document types. Initial mappings pair identical names. A missing or unmapped source value blocks the document before download/staging and does not initialize a vendor upload. Do not use a blanket signed-retainer or Other fallback. Source selection still requires OnTask / Submitted File and both configured IDs.

Before upgrading an installation with pending documents, inspect older document sessions in protected state. New sessions retain a full frozen initialization request including the mapped type; older sessions may lack it. Existing upload IDs can resume or complete using their current session. Fresh initialization, including replacement of an expired session, requires a valid frozen type and cannot be silently filled from current mappings. Arrange a separately reviewed state migration/recovery for legacy pending work; do not reset uncertain initialization markers. Delivered documents are not automatically retagged or duplicated after mapping changes. State remains inside the encrypted envelope, with no new SQL schema or runtime DDL.

## Reconcile an uncertain upload initialization

Do not reset the marker or infer failure from a missing search result. Ask the firm to locate the attempted upload using the existing intake and protected case/file correlation. Require confirmation of the exact source file/content checksum, intake and upload session. The API has no lookup by client correlation; the application cannot invent this evidence.

If the firm verifies the exact file and supplies a completed upload ID, an authorized operator can run the same worker with `--reconcile-upload <private-json-path>`. The command only repeats safe completion, checks both returned IDs, compares the attested SHA-256 against frozen Blob bytes, and stores an encrypted reconciliation record and change-ticket reference. It never initializes another session. Example request shape uses synthetic placeholders:

```json
{
  "SourceCaseId": 123,
  "AssetIdentity": "<existing 64-character asset identity>",
  "UploadId": "<firm-verified upload ID>",
  "VerifiedContentSha256": "<firm-verified SHA-256>",
  "FirmVerifiedExactFile": true,
  "OperatorReference": "<restricted change/audit reference>"
}
```

The attestation is an operator action, not machine proof from the completion response (which contains no checksum). Restrict Azure job start/override and runtime/secret access accordingly; Azure execution audit supplies caller identity, while the protected application record supplies the verified file and reference. If the upload is still incomplete or the firm cannot prove its outcome, this command refuses to mark it delivered; keep the marker and coordinate a separately reviewed vendor recovery. No automatic reset capability is provided.

## Stop, restore and migrate

To stop delivery, set `SendingEnabled=false`, return the trigger to Manual, and stop an active execution when needed. Frozen intents remain available for recovery. Preserve the database, state key and asset container.

Restore SQL, Blob versions and encryption-key version together. Before enabling writes, reconcile restored pending effects against firm records: a SQL restore may resurrect an already acknowledged upload. Intake replay relies on the documented unique Tiger ID; upload initialization has no comparable idempotency guarantee. Define and demonstrate environment-specific recovery objectives before production.

The old connector's encrypted local delivery files are not automatically imported. Existing leads must retain their original Tiger ID and intake relationship. Obtain a protected inventory of existing submissions/sessions and perform a reviewed migration before enabling this application for the same population; never run both schedulers independently. This repository does not delete or modify the original connector project.

Storage soft-delete/versioning defaults are technical recovery settings, not a business retention policy. Do not age out frozen pending/uncertain assets or their state. Agree on retention, orphan-staging cleanup and completed-asset lifecycle with the data owner before production.
