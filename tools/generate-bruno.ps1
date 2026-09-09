<#
.SYNOPSIS
    Generates Bruno collections covering every REST endpoint in the solution.

.DESCRIPTION
    Reads the routes out of the source rather than a hand-maintained list:
    [HttpTrigger] attributes in the Functions projects, and app.Map* calls in
    Oiie.Sandbox.Api. One collection per host, because each has its own base URL
    and its own key -- a single collection would need a variable per request to
    express that, which is what an environment file is for.

    The generated files are worth less than this script. A list of 180 requests
    typed once starts drifting from the routes the moment one changes, and the
    drift is invisible: a stale request 404s and looks like a broken service.
    Regenerating and diffing shows what actually moved.

    Requests are emitted with an empty body and a docs block naming the route,
    the verb and the originating function. Bodies are deliberately not invented:
    a fabricated payload that looks authoritative is worse than an obvious blank,
    because the first person to run it debugs the wrong thing.

    Curated collections are left alone. testing/bruno/cir and
    testing/bruno/sandbox carry real assertions and seeded data; this writes
    beside them, never over them.

.EXAMPLE
    ./tools/generate-bruno.ps1
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $OutputRoot) { $OutputRoot = Join-Path $RepoRoot 'testing/bruno' }

# Host map: project -> collection folder, request-name prefix, Azure app name.
#
# The prefix is what the request is named with, so a request read out of context
# still says which participant it belongs to.
$hosts = @(
    @{ Project = 'EngProvider';         Folder = 'eng';                Prefix = 'ENG';                App = 'acme-api-eng-dev' }
    @{ Project = 'EngEngine';           Folder = 'eng-engine';         Prefix = 'ENG-ENGINE';         App = 'acme-engn-eng-dev' }
    @{ Project = 'RegLocationProvider'; Folder = 'reglocation';        Prefix = 'REGLOCATION';        App = 'acme-api-reglocation-dev' }
    @{ Project = 'RegLocationEngine';   Folder = 'reglocation-engine'; Prefix = 'REGLOCATION-ENGINE'; App = 'acme-engn-reglocation-dev' }
    @{ Project = 'CirProvider';         Folder = 'cir-api';            Prefix = 'CIR';                App = 'acme-api-cir-dev' }
    @{ Project = 'ISBMProvider';        Folder = 'isbm';               Prefix = 'ISBM';               App = 'acme-api-isbm-dev' }
    @{ Project = 'MmsProvider';         Folder = 'mms';                Prefix = 'MMS';                App = 'acme-api-mms-dev' }
    @{ Project = 'MmsEngine';           Folder = 'mms-engine';         Prefix = 'MMS-ENGINE';         App = 'acme-engn-mms-dev' }
    @{ Project = 'CmsProvider';         Folder = 'cms';                Prefix = 'CMS';                App = 'acme-api-cms-dev' }
    @{ Project = 'CmsEngine';           Folder = 'cms-engine';         Prefix = 'CMS-ENGINE';         App = 'acme-engn-cms-dev' }
    @{ Project = 'RdlProvider';         Folder = 'rdl';                Prefix = 'RDL';                App = 'acme-api-rdl-dev' }
    @{ Project = 'RdlEngine';           Folder = 'rdl-engine';         Prefix = 'RDL-ENGINE';         App = 'acme-engn-rdl-dev' }
)

# ---------------------------------------------------------------------------
# Route extraction
# ---------------------------------------------------------------------------

# Turns an ASP.NET route template into something addressable.
#
# Constraints are stripped -- {scopeId:int} becomes {{scopeId}} -- because the
# constraint is a server-side parse rule and sending it literally produces a URL
# that 404s for a reason the caller cannot see. As a Bruno variable the request
# is runnable once the value is set, rather than needing to be edited first.
function ConvertTo-BrunoUrl([string]$route) {
    # The leading * marks an ASP.NET catch-all ({*channelUri}). It is a routing
    # instruction, not part of the name, so it is dropped rather than carried
    # into a variable nobody can set.
    [regex]::Replace($route, '\{\*?([^}:?]+)(:[^}]+)?\??\}', '{{$1}}')
}

# A filename that sorts and reads well: 'ENG GET itwins-itwinid'.
function ConvertTo-FileName([string]$prefix, [string]$verb, [string]$route) {
    # Everything that is not alphanumeric or a hyphen goes. Asterisks and braces
    # in particular: Set-Content reads a path as a wildcard pattern, so a file
    # named after a catch-all route fails on a parameter error that names
    # neither the file nor the route.
    $slug = $route -replace '\{\{', '' -replace '\}\}', '' -replace '[^A-Za-z0-9]', '-'
    $slug = ($slug -replace '-+', '-').Trim('-')
    if (-not $slug) { $slug = 'root' }
    "$prefix-$verb-$slug"
}

function Get-FunctionRoutes([string]$projectPath, [string]$project) {
    $found = @()

    Get-ChildItem -Path $projectPath -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $lines = Get-Content $_.FullName

            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -notmatch 'HttpTrigger\(') { continue }

                # The attribute often wraps, so the Route may be on the next
                # line. Joining one line forward covers every case in this
                # solution without needing a full parser.
                $blob = $lines[$i]
                if ($blob -notmatch 'Route\s*=' -and $i + 1 -lt $lines.Count) {
                    $blob += ' ' + $lines[$i + 1]
                }

                $routeMatch = [regex]::Match($blob, 'Route\s*=\s*"([^"]*)"')
                if (-not $routeMatch.Success) { continue }

                $verbs = [regex]::Matches($blob, '"(get|post|put|delete|patch)"') |
                    ForEach-Object { $_.Groups[1].Value }
                if (-not $verbs) { $verbs = @('get') }

                # The [Function] attribute sits a few lines above the trigger.
                $fn = ''
                for ($j = [Math]::Max(0, $i - 8); $j -lt $i; $j++) {
                    $fm = [regex]::Match($lines[$j], '\[Function\("([^"]+)"\)\]')
                    if ($fm.Success) { $fn = $fm.Groups[1].Value }
                }

                foreach ($v in ($verbs | Sort-Object -Unique)) {
                    $found += [pscustomobject]@{
                        Project  = $project
                        Verb     = $v.ToUpperInvariant()
                        Route    = $routeMatch.Groups[1].Value
                        Function = $fn
                        Source   = $_.Name
                    }
                }
            }
        }

    $found
}

function Get-SandboxRoutes([string]$projectPath) {
    $found = @()

    Get-ChildItem -Path $projectPath -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $file = $_.Name
            Select-String -Path $_.FullName -Pattern 'app\.Map(Get|Post|Put|Delete|Patch)\("([^"]+)"' |
                ForEach-Object {
                    $m = [regex]::Match($_.Line, 'app\.Map(Get|Post|Put|Delete|Patch)\("([^"]+)"')
                    $found += [pscustomobject]@{
                        Project  = 'Oiie.Sandbox.Api'
                        Verb     = $m.Groups[1].Value.ToUpperInvariant()
                        Route    = $m.Groups[2].Value
                        Function = ''
                        Source   = $file
                    }
                }
        }

    $found
}

# ---------------------------------------------------------------------------
# Emission
# ---------------------------------------------------------------------------

function Write-Request($route, [string]$prefix, [int]$seq, [string]$folder, [bool]$sandbox) {
    $url = ConvertTo-BrunoUrl $route.Route
    $name = "$prefix $($route.Verb) $($route.Route)"
    $file = ConvertTo-FileName $prefix $route.Verb $url

    # Function routes are relative ('scopes/{id}'); sandbox minimal-API routes
    # are absolute ('/admin/reset'). Joining both the same way yields '//admin'
    # on one of them, which some hosts route and others 404 -- so the separator
    # is decided here rather than assumed.
    $path = $url.TrimStart('/')

    # Sandbox admin routes authenticate by header, set once on the collection.
    # Everything else is a Function App and takes its key on the query string.
    $suffix = if ($sandbox) { '' } else { '?code={{functionKey}}' }
    $verbLower = $route.Verb.ToLowerInvariant()
    $bodyKind = if ($route.Verb -in @('POST', 'PUT', 'PATCH')) { 'json' } else { 'none' }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('meta {')
    [void]$sb.AppendLine("  name: $name")
    [void]$sb.AppendLine('  type: http')
    [void]$sb.AppendLine("  seq: $seq")
    [void]$sb.AppendLine('}')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("$verbLower {")
    [void]$sb.AppendLine("  url: {{baseUrl}}/$path$suffix")
    [void]$sb.AppendLine("  body: $bodyKind")
    [void]$sb.AppendLine('  auth: none')
    [void]$sb.AppendLine('}')

    if ($bodyKind -eq 'json') {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('body:json {')
        [void]$sb.AppendLine('  {}')
        [void]$sb.AppendLine('}')
    }

    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('docs {')
    [void]$sb.AppendLine("  $($route.Verb) /$($route.Route.TrimStart('/'))")
    if ($route.Function) { [void]$sb.AppendLine("  Function: $($route.Function)") }
    [void]$sb.AppendLine("  Source: $($route.Source)")

    if ($path -match '\{\{') {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('  Route parameters are Bruno variables. Set them in the environment or')
        [void]$sb.AppendLine('  on the request before running.')
    }

    if ($bodyKind -eq 'json') {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('  The body is an empty object: this file records that the endpoint')
        [void]$sb.AppendLine('  exists and how to reach it, not what to send. Supply a payload before')
        [void]$sb.AppendLine('  running, or expect a 400 naming the member that is missing.')
    }

    [void]$sb.AppendLine('}')

    Set-Content -LiteralPath (Join-Path $folder "$file.bru") -Value $sb.ToString().TrimEnd() -Encoding utf8
}

function Write-Collection([string]$folder, [string]$name, [string]$app, [bool]$sandbox) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $folder 'environments') | Out-Null

    $manifest = @{ version = '1'; name = $name; type = 'collection'; ignore = @('node_modules', '.git') } | ConvertTo-Json
    Set-Content -Path (Join-Path $folder 'bruno.json') -Value $manifest -Encoding utf8

    $collection = if ($sandbox) {
@'
headers {
  x-sandbox-admin-key: {{adminKey}}
}

docs {
  Generated by tools/generate-bruno.ps1. Do not hand-edit: rerun the generator.

  Admin routes authenticate with x-sandbox-admin-key, set once here for the
  whole collection. Read the value from Key Vault:

    az keyvault secret show --vault-name mndot --name sandbox-admin-key-dev --query value -o tsv
}
'@
    } else {
@'
docs {
  Generated by tools/generate-bruno.ps1. Do not hand-edit: rerun the generator.

  Every request carries the Function App key as ?code={{functionKey}}. Read it
  with:

    az functionapp keys list -g HilmarRetiefRG -n <app> --query functionKeys.default -o tsv
}
'@
    }

    Set-Content -Path (Join-Path $folder 'collection.bru') -Value $collection -Encoding utf8

    $secret = if ($sandbox) { 'adminKey' } else { 'functionKey' }
    $envFile = @"
vars {
  baseUrl: https://$app.azurewebsites.net/api
}

vars:secret [
  $secret
]
"@

    if ($sandbox) {
        # The sandbox is an App Service, not a Function App: its routes are not
        # under /api.
        $envFile = @"
vars {
  baseUrl: https://$app.azurewebsites.net
}

vars:secret [
  $secret
]
"@
    }

    Set-Content -Path (Join-Path $folder 'environments/azure.bru') -Value $envFile -Encoding utf8
}

# ---------------------------------------------------------------------------

$total = 0

foreach ($h in $hosts) {
    $projectPath = Join-Path $RepoRoot $h.Project
    if (-not (Test-Path $projectPath)) {
        Write-Warning "Skipped $($h.Project): not found."
        continue
    }

    $routes = @(Get-FunctionRoutes $projectPath $h.Project | Sort-Object Route, Verb)
    $folder = Join-Path $OutputRoot $h.Folder

    if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
    Write-Collection $folder "$($h.Prefix) ($($h.App))" $h.App $false

    $seq = 1
    foreach ($r in $routes) {
        Write-Request $r $h.Prefix $seq $folder $false
        $seq++
    }

    Write-Host ("{0,-22} {1,3} request(s) -> testing/bruno/{2}" -f $h.Prefix, $routes.Count, $h.Folder)
    $total += $routes.Count
}

# Sandbox last: it is an App Service rather than a Function App, so it differs
# in both base URL shape and authentication.
$sandboxPath = Join-Path $RepoRoot 'Oiie.Sandbox.Api'
$sandboxRoutes = @(Get-SandboxRoutes $sandboxPath | Sort-Object Route, Verb)
$sandboxFolder = Join-Path $OutputRoot 'sandbox-api'

if (Test-Path $sandboxFolder) { Remove-Item -Recurse -Force $sandboxFolder }
Write-Collection $sandboxFolder 'SANDBOX (acme-api-sandbox-dev)' 'acme-api-sandbox-dev' $true

$seq = 1
foreach ($r in $sandboxRoutes) {
    Write-Request $r 'SANDBOX' $seq $sandboxFolder $true
    $seq++
}

Write-Host ("{0,-22} {1,3} request(s) -> testing/bruno/sandbox-api" -f 'SANDBOX', $sandboxRoutes.Count)
$total += $sandboxRoutes.Count

Write-Host ''
Write-Host "$total request(s) generated."
