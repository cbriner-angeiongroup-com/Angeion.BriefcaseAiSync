# Development deployment inventory — November 9, 2026

Status: **partially provisioned; application hosting is blocked by Azure resource-group access and SQL publication access.** No Container App, worker job, public application URL, or scheduled execution is running.

The intended application resource group is `briefcase-sync-development`, without an `rg-` prefix. Subscription: `Case Works Azure Subscription - Primary`. Application/SQL region: `southcentralus`. Existing shared resource group: `caseworks-development`.

## Created and verified

| Item | Name / location | Result |
| --- | --- | --- |
| Azure SQL database | `caseworksdevsql001/briefcase-sync-development` in `caseworks-development` | Online; Standard S0, 10 DTUs; application schema not published |
| Key Vault secret | `angdevkeyvault/briefcase-sync-development-sa-username` | Created from verified local SmartAdvocate development settings |
| Key Vault secret | `angdevkeyvault/briefcase-sync-development-sa-password` | Created from verified local SmartAdvocate development settings |
| Key Vault secret | `angdevkeyvault/briefcase-sync-development-briefcase-client-id` | Created from verified local BriefCase trial settings |
| Key Vault secret | `angdevkeyvault/briefcase-sync-development-briefcase-client-secret` | Created from verified local BriefCase trial settings |
| Key Vault secret | `angdevkeyvault/briefcase-sync-development-state-key` | New random 32-byte encryption key; preserved on reruns |
| Key Vault secret | `angdevkeyvault/briefcase-sync-development-entra-client-secret` | New web registration credential; expires February 7, 2027 |
| Key Vault cryptographic key | `angdevkeyvault/briefcase-sync-development-web-protection` | RSA-2048; wrap/unwrap operations for web Data Protection |
| Entra application registration | `Angeion.BriefcaseAiSync-development` | Client ID `98a85a68-7e00-4f7a-8837-efd49fa32267`; single tenant; three app roles |
| Entra enterprise application | Same web name | Service principal `65ea593e-5b93-4d76-ab54-d93068ce872e`; explicit role assignment required |
| Web app-role assignments | Current developer identity | `Sync.Preview` and `Sync.Configure`; View access is included by the application policies |
| Entra application registration | `Angeion.BriefcaseAiSync-development-deploy` | Client ID `098660c7-9d66-482e-a1b6-10831bdc77c3`; separate deployment identity |
| Entra enterprise application | Same deployment name | Service principal `b615e122-2b22-4b38-9353-f54eb445a037` |
| Federated identity credential | `github-development` on deployment registration | GitHub issuer; subject `repo:cbriner-angeiongroup-com/Angeion.BriefcaseAiSync:environment:development`; no deployment client secret |
| Registry RBAC assignment | Deployment service principal → `caseworksdevacr000` | `AcrPush`, scoped to that registry; assignment `3a9bd8c5-ab53-459a-bd98-c641b73026e2` |
| Worker container image | `caseworksdevacr000.azurecr.io/briefcase-sync/worker:development-20261109` | Build run `cdd` succeeded |
| Web container image | `caseworksdevacr000.azurecr.io/briefcase-sync/web:development-20261109` | Build run `cde` succeeded |
| GitHub environment | `cbriner-angeiongroup-com/Angeion.BriefcaseAiSync` → `development` | Created for manually dispatched deployments; restricted to the `main` branch |
| GitHub environment variables | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Three non-secret identifiers configured for federation |

The existing SQL server, registry and vault were **reused**, not created. The vault is in `eastus`; the existing registry and SQL server are in `southcentralus`. No shared-parent administrator, firewall, SKU or networking settings were changed.

Immutable images:

```text
caseworksdevacr000.azurecr.io/briefcase-sync/worker@sha256:63e3df3b0a062a77a4b130f286d086e130f740d3cf52589721bf3bf73d2e18c9
caseworksdevacr000.azurecr.io/briefcase-sync/web@sha256:dd507613adf9cac6d21150883191da43841e02fe74bca66741564ddd1faa79cc
```

Build contexts contained only application source, project/build files and mapping configuration. Local secrets, local build output, and the supplied source PDF/workbook were excluded. Credential values were transferred in memory over HTTPS and are absent from receipts and command arguments.

## Blocked or not deployed

| Item | State / required next step |
| --- | --- |
| Resource group `briefcase-sync-development` | Azure returned `AuthorizationFailed` for `Microsoft.Resources/subscriptions/resourcegroups/write`. An authorized subscription administrator must create it. |
| Runtime deployment access | Grant the operator Contributor plus scoped role-assignment permission on the new group. The federated deployment identity also needs the appropriate app-group deployment rights. |
| SQL schema | DACPAC deploy-report connection failed with SQL login not authorized. An authorized SQL publisher must publish the SQL project into this database. |
| SQL contained users and grants | Pending the worker/web identities and SQL administrator access; no runtime DDL permission will be granted. |
| Container Apps environment and Log Analytics workspace | Prepared in Bicep; cannot create in the unavailable application group. |
| Worker and web managed identities | Prepared; not created. |
| Worker Container Apps Job | Prepared; not created or executed. Manual trigger and sending-disabled/unapproved settings are fixed for initial deployment. |
| Web Container App | Prepared; not created. |
| Storage account, blob service, `retainer-assets` and `web-keys` containers | Prepared; not created. |
| Runtime Blob, registry, secret and protection-key grants | Prepared; worker/web identities do not yet exist. |
| Web Entra redirect URL | Pending the actual host; registration currently has no redirect URI. Configure once with the interactive deployment option. |
| Trusted ingress proxy CIDRs | Must be selected and validated before the web host deploys; no broad trust range is guessed. |
| Action group and failure alert | Prepared as disabled resources; no recipient is invented. Missing-run alerting and delivery acceptance require the accepted schedule and an operator. |
| Custom DNS, certificates, private endpoints and private DNS | Not created. Network topology and custom hostname remain unselected. |
| GitHub automatic deployment | No push-triggered deployment. The prepared workflow is manual and cannot complete until app-group and SQL permissions are provided. |

## Prepared deployment files

- `infra/development.settings.json`: verified development references and immutable image digests; no credentials.
- `infra/main.bicep` and `infra/web.bicep`: now support username/password or PAT source authentication through Key Vault references.
- `infra/development-access.bicep`: least-scope runtime access to the shared registry and individual secrets/key.
- `infra/monitoring.bicep`: disabled failure-alert/action-group definitions using fixed events and aggregate counts only.
- `eng/Prepare-DevelopmentCloud.py`: idempotent preparation of app-prefixed shared-vault and directory objects; never rotates an existing state key.
- `eng/Deploy-Development.ps1`: previews each deployment, refuses shared-parent changes/deletions, publishes the DACPAC separately, then deploys pinned hosts with delivery disabled.
- `eng/AuthorizeDevelopmentSql`: explicit deployment-only SQL grants tool; never linked into runtime hosts.
- `.github/workflows/deploy-development.yml`: manual OIDC deployment using the `development` GitHub environment.

Resume after access is resolved:

```powershell
./eng/Deploy-Development.ps1 -ConfigureWebRedirect -KnownProxyNetworks @('<approved ingress CIDR>')
```

Use an authorized interactive SQL publisher for the first schema/user publication. Later federated deployments also require their own SQL publication permissions. Do not substitute managed-identity runtime permissions for deployment permissions.

## Validation and delivery gates

The database resource is Online. Both registry build runs succeeded. The web registration's three roles and assignment requirement, deployment federation subject, Key Vault object names, and registry role grant were read back after creation. Detailed metadata receipts are stored locally under ignored `artifacts/development-deployment`; they contain no secret values.

`eng/verify.ps1 -SqlIntegration` passed with 141 tests, including the isolated local SQL test. The SQL project, both host publications, all seven Bicep templates, and the deployment-only SQL authorization tool built successfully. This local SQL result does not establish Azure SQL login access; the Azure publication attempt was separately rejected.

Initial deployment keeps `SendingEnabled=false`, `MappingApproved=false`, `RetainersEnabled=false`, eligibility status `0`, and the worker trigger Manual. No vendor writes, cloud sync runs, or case reads were performed by provisioning. App hosting, sign-in, SQL schema connectivity, ingress acceptance, and alert delivery are not yet validated.
