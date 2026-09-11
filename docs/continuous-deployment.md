# Continuous deployment

`.github/workflows/deploy.yml` deploys the whole solution to the `dev`
environment on every push to `main`. It can also be run by hand from the Actions
tab (`workflow_dispatch`).

## What it does

```
build -> databases -> isbm -> providers -> engines -> sandbox
```

| Job | What runs |
| --- | --- |
| `build` | `dotnet build` and `dotnet test` on the merge commit. Nothing deploys if this fails. |
| `databases` | `{Provider}/deploy/provision-databases.ps1` for CIR, CMS, ENG, MMS, REG-LOCATION. Creates any missing `acme-db-*`; skips those that exist. |
| `isbm` | `ISBMProvider/deploy/deploy-functionapp.ps1` |
| `providers` | `{Provider}/deploy/deploy-functionapp.ps1` for CIR, CMS, ENG, MMS, RDL, REG-LOCATION |
| `engines` | `deploy/engines/deploy-engine.ps1 -Engine all` |
| `sandbox` | `deploy/sandbox/deploy.ps1` — builds the React app into wwwroot and verifies `/health` |

The workflow calls the existing scripts rather than reimplementing them. The
ordering is not cosmetic: providers throw if their database is missing, engines
throw if their peer provider is missing, and the CIR and the sandbox read
function keys from apps deployed earlier in the chain.

## One-time setup

Nothing below can be done by the pipeline itself. Until it is done the first run
fails at `azure/login` with a token error.

> **This has already been done for `dev`.** What follows is the record of how,
> and what to repeat for another environment or tenant. The identifiers are
> deliberately not written down here -- this repository is public, and a tenant
> id, subscription id and client id published together are a map of the estate
> even though none of them is a credential. Retrieve them with:
>
> ```powershell
> az ad app list --display-name "github-openom-deploy" --query "[0].appId" -o tsv
> az account show --query "{tenantId:tenantId, subscriptionId:id}" -o json
> ```

### Privileges you will need

Two gates, usually two different administrators. Neither is grantable by the
deploying user:

| Gate | Right needed | Symptom if missing |
| --- | --- | --- |
| Entra directory | Application Administrator, or `Users can register applications` enabled | `Insufficient privileges to complete the operation` on app create |
| Subscription | Owner or User Access Administrator on the resource group | `does not have authorization to perform action 'Microsoft.Authorization/roleAssignments/write'` |

Contributor on the resource group is **not** sufficient for the second: it can
read role assignments but not create them.

### 1. Create the app registration

```powershell
$appId = az ad app create --display-name "github-openom-deploy" --query appId -o tsv
az ad sp create --id $appId
```

### 2. Add the federated credential

The `subject` must match exactly — it is how Entra decides whether to trust the
token GitHub presents. `repo:<owner>/<repo>:ref:refs/heads/main` covers pushes to
`main`; a mismatch here is the usual cause of `AADSTS70021`.

> **Do not use the portal's numeric-id form.** Some Entra portal builds ask for
> an Organization ID and Repository ID and build a subject of the shape
> `repo:owner@6044359/repo@1327196407:ref:refs/heads/main`. That is GitHub's
> *immutable* subject format, and GitHub only sends it if the repository's OIDC
> subject claim template has been customised to use `repository_owner_id` and
> `repository_id`. By default it sends the plain name form below, so a credential
> created that way rejects every token. This happened during the original setup
> and was corrected with `az ad app federated-credential update`.
>
> Note also that `hretief` is a GitHub *user* account, not an organisation, so
> there is no organisation id to supply in the first place.

```powershell
$cred = @{
    name      = "github-main"
    issuer    = "https://token.actions.githubusercontent.com"
    subject   = "repo:hretief/OIIE:ref:refs/heads/main"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Compress

az ad app federated-credential create --id $appId --parameters $cred
```

Verify what was actually stored, rather than what you meant to store:

```powershell
az ad app federated-credential list --id $appId --query "[].subject" -o tsv
```

If you later add a GitHub Environment or deploy from tags, add a second
credential — `subject` is not a pattern and does not wildcard. A
`workflow_dispatch` run started from `main` presents the same subject as a push,
so the manual-run button needs no extra credential; a dispatch from any other
branch does.

### 3. Grant it rights on the resource group

```powershell
$subId = az account show --query id -o tsv
$spId  = az ad sp show --id $appId --query id -o tsv

az role assignment create `
    --assignee-object-id $spId `
    --assignee-principal-type ServicePrincipal `
    --role Contributor `
    --scope "/subscriptions/$subId/resourceGroups/HilmarRetiefRG"
```

Contributor on the resource group covers the function apps, plans, storage, and
SQL databases. Two further grants are needed:

```powershell
# Key Vault: the sandbox deploy reads and creates sandbox-admin-key-dev
az role assignment create `
    --assignee-object-id $spId `
    --assignee-principal-type ServicePrincipal `
    --role "Key Vault Secrets Officer" `
    --scope "/subscriptions/$subId/resourceGroups/HilmarRetiefRG/providers/Microsoft.KeyVault/vaults/mndot"
```

### 4. Add the repository secrets

Settings -> Secrets and variables -> Actions:

| Secret | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | `$appId` from step 1 |
| `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | `az account show --query id -o tsv` |

There is no client secret. That is the point of OIDC — the three values above
are identifiers, not credentials.

### The identity in use for dev

The `dev` pipeline does **not** run as a purpose-built registration. During
setup an existing app registration, `SolutionsDbSync` (created 2023-11-30), was
renamed to `github-openom-deploy` and reused, because the tenant would not
permit creating a new one.

It was checked at the time and carried nothing harmful across: no client
secrets, no certificates, no admin-consented API permissions beyond delegated
Graph `User.Read`, and no role assignments other than the two granted for this
pipeline. With no credentials on it, nothing else can authenticate as it.

Two things follow:

- `SolutionsDbSync` no longer exists under that name. Anything that referred to
  it by name now points at `github-openom-deploy`. The `appId` is unchanged, so
  any system still holding it would authenticate as the deployment identity.
- If a purpose-built registration becomes available, moving to it means creating
  it, re-making the two role assignments, re-adding the federated credential, and
  updating `AZURE_CLIENT_ID`. Nothing in this repository changes.

## What CD still does not do

**SQL database users.** Each provider authenticates to SQL as its user-assigned
managed identity, and that identity needs a database user created inside the
database. `az` cannot do this; it requires a connection to the database as a SQL
AD admin. The workflow passes `-SkipSqlGrant` where the scripts offer it, and the
`CREATE USER` statement each script prints at the end remains a manual step, run
once per database. See the README beside each provider.

A provider deploys successfully without it and then fails at runtime on its first
query — so if a newly created database returns 500s, this is the reason.

**Enabling a disabled engine.** `deploy-engine.ps1` preserves an existing
`Enabled` or `Isbm__Enabled` app setting rather than reapplying the default, so
an engine stopped deliberately stays stopped across deployments. The workflow
does not pass `-ForceEnable`. To bring an engine back, either set the app setting
in the portal or run the script locally with `-ForceEnable`.

**Production.** The pipeline targets `dev` only. The provider and engine scripts
accept `-Environment prod`, but the sandbox script accepts `dev`, `ci` and
`demo`, so a prod stage needs that reconciled first.
