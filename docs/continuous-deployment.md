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
| `build` | `dotnet build` and `dotnet test` on the merge commit. Nothing deploys if this fails. Runs on **windows-latest** — see below. |
| `databases` | `{Provider}/deploy/provision-databases.ps1` for CIR, CMS, ENG, MMS, REG-LOCATION. Creates any missing `acme-db-*`; skips those that exist. |
| `isbm` | `ISBMProvider/deploy/deploy-functionapp.ps1` |
| `providers` | `{Provider}/deploy/deploy-functionapp.ps1` for CIR, CMS, ENG, MMS, RDL, REG-LOCATION |
| `engines` | `deploy/engines/deploy-engine.ps1 -Engine all` |
| `sandbox` | `deploy/sandbox/deploy.ps1` — builds the React app into wwwroot and verifies `/health` |

The workflow calls the existing scripts rather than reimplementing them. The
ordering is not cosmetic: providers throw if their database is missing, engines
throw if their peer provider is missing, and the CIR and the sandbox read
function keys from apps deployed earlier in the chain.

### Why the build job runs on Windows

`EngProvider.E2E.Tests` creates a real database per run through
`(localdb)\MSSQLLocalDB` with integrated security (`EngHostFixture.cs`). LocalDB
is a Windows-only component, so on `ubuntu-latest` all fourteen of its tests
fail with `PlatformNotSupportedException` before reaching an assertion. The 150
tests in `Oiie.Sandbox.Tests` are unaffected and pass on either platform.

Only the `build` job needs Windows. Every deploying job runs on `ubuntu-latest`.

To move it back to Linux, `EngHostFixture` would need to take its connection
string from the environment instead of hard-coding LocalDB, after which the job
can use a SQL Server service container. That is the better end state — until it
happens, this suite only runs where a Windows host is available, which is why it
went unnoticed that it had never run in CI at all.

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
token GitHub presents. It is not a pattern and does not wildcard. A mismatch is
the usual cause of `AADSTS700213`.

> **This repository uses GitHub's immutable subject claim.** The subject is
> *not* the plain `repo:<owner>/<repo>:ref:refs/heads/main` form shown in most
> documentation. GitHub embeds the numeric account and repository ids:
>
> ```
> repo:hretief@6044359/OIIE@1327196407:ref:refs/heads/main
> ```
>
> This is enabled by `use_immutable_subject` on the repository's OIDC settings,
> which survives renames of either the account or the repository — that is the
> point of it. It is enforced above the repository, so it cannot be turned off
> here: `PUT /repos/hretief/OIIE/actions/oidc/customization/sub` with
> `use_default: true` returns success but leaves the setting unchanged.
>
> Confirm what GitHub will actually send before creating or editing a
> credential, instead of copying the plain form from a tutorial:
>
> ```powershell
> gh api repos/hretief/OIIE/actions/oidc/customization/sub
> ```
>
> The failing `azure/login` step also prints the exact `subject claim` it
> presented, which is the fastest way to diagnose a rejected token.

```powershell
$cred = @{
    name      = "github-main"
    issuer    = "https://token.actions.githubusercontent.com"
    subject   = "repo:hretief@6044359/OIIE@1327196407:ref:refs/heads/main"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Compress

# az on Windows needs @file for JSON parameters; an inline string is mangled.
Set-Content "$env:TEMP\fedcred.json" $cred -Encoding utf8
az ad app federated-credential create --id $appId --parameters "@$env:TEMP\fedcred.json"
```

Verify what was actually stored, rather than what you meant to store:

```powershell
az ad app federated-credential list --id $appId --query "[].subject" -o tsv
```

If you later add a GitHub Environment or deploy from tags, add a second
credential — each needs its own, and each must use the immutable prefix above. A
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

**RBAC role assignments.** Each function app and the sandbox web app reach
storage, Key Vault and Service Bus as their own managed identity, which requires
role assignments on those resources. The Bicep templates can create them, but
only when `assignRoles=true`, and the deploy scripts only pass that when given
`-AssignRoles`. CD never does.

The reason is not that the grants are unwanted but that ARM re-issues
`Microsoft.Authorization/roleAssignments/write` on every deployment even when the
assignment already exists and is byte-for-byte identical. A Contributor cannot
perform that write, so an unconditional grant makes every subsequent deployment
fail with `InvalidTemplateDeployment` — which is exactly how this surfaced, long
after the assignments had been correctly created by hand.

Granting CI `User Access Administrator` would fix it, at the price of letting
anyone who can push to `main` assign any role to any principal in the resource
group. The grants are a one-time bootstrap for long-lived apps, so that trade is
not worth making.

Run the bootstrap once per new environment, signed in as a principal with User
Access Administrator:

```powershell
./ISBMProvider/deploy/deploy-functionapp.ps1 -Environment dev -AssignRoles
./deploy/sandbox/deploy.ps1               -Environment dev -AssignRoles
```

Also needed if an app is deleted and recreated, since a new system-assigned
identity gets a new principal id and the old assignment no longer refers to it.
Confirm what an app actually holds before assuming it is missing:

```powershell
$p = az webapp identity show -g <rg> -n <app> --query principalId -o tsv
az role assignment list --assignee $p --all -o table
```

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
