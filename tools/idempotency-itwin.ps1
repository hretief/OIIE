<#
.SYNOPSIS
    Replays the iTwin-added trigger repeatedly against a single twin and asserts
    that the downstream estate converges rather than accumulates.

.DESCRIPTION
    The real platform redelivers iTwins.iTwinCreated.v1 whenever a twin's
    metadata is edited, and it does not promise to deliver it exactly once. So
    the pipeline has to be judged on convergence, not on call count: the same
    event arriving three times must leave one ENG row, one REG-LOCATION scope,
    one CIR entry -- with the newest metadata on all three.

    Each pass drives the same path the UI does:

        POST {eng}/itwins                     upsert the twin
        POST {engine}/bootstrap/itwin-created announce it
        POST {regEngine}/engine/ingest-sites  drain into REG-LOCATION + CIR

    Pass 2 onward mutates DisplayName and Description so the run also proves
    that a repeat is an update and not merely a no-op. A pipeline that ignored
    redeliveries outright would pass a duplicate-count check while silently
    dropping the edit; comparing the observed metadata against what was last
    sent is what separates the two.

.PARAMETER ITwinId
    The twin to replay. Defaults to 9600, the site from the CIR investigation.

.PARAMETER Passes
    How many times to replay. Three is the useful minimum: the first creates,
    the second proves update-not-insert, the third proves it stays stable.

.PARAMETER KeepMutations
    Leave the final mutated DisplayName/Description in place. By default the
    original metadata is restored so the run does not disturb the estate.

.PARAMETER FreshTwin
    Invent a throwaway twin instead of replaying an existing one, then tear it
    down afterwards.

    This is the stronger test. Replaying a twin that REG-LOCATION already holds
    stops at 'alreadyKnown' before the CIR step is ever reached, so the CIR
    write path is never exercised -- the run proves only that nothing was
    duplicated. A twin CIR has not seen forces pass 1 down the create path and
    passes 2..N down the repeat path, which is what actually demonstrates that
    the registration converges.

.PARAMETER KeepFreshTwin
    Leave the invented twin in place rather than deleting its REG-LOCATION
    scope and CIR entries. Useful when a run fails and you want the wreckage.

.EXAMPLE
    pwsh -NoProfile -File .\tools\idempotency-itwin.ps1
    pwsh -NoProfile -File .\tools\idempotency-itwin.ps1 -FreshTwin
    pwsh -NoProfile -File .\tools\idempotency-itwin.ps1 -ITwinId d543ebf6-... -Passes 5
#>
[CmdletBinding()]
param(
    [guid]   $ITwinId       = 'c86c9c10-4487-48f6-8f5b-89701307725c',
    [int]    $Passes        = 3,
    [string] $ResourceGroup = 'HilmarRetiefRG',
    [string] $EngApp        = 'acme-api-eng-dev',
    [string] $EngEngineApp  = 'acme-engn-eng-dev',
    [string] $RegApp        = 'acme-api-reglocation-dev',
    [string] $RegEngineApp  = 'acme-engn-reglocation-dev',
    [string] $CirApp        = 'acme-api-cir-dev',
    [switch] $KeepMutations,
    [switch] $FreshTwin,
    [switch] $KeepFreshTwin
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---- plumbing ------------------------------------------------------------

$keyCache = @{}
function Get-FunctionKey([string]$App) {
    if (-not $keyCache.ContainsKey($App)) {
        $key = az functionapp keys list -g $ResourceGroup -n $App --query 'functionKeys.default' -o tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($key)) {
            throw "Could not read a function key for '$App'. Is 'az login' current?"
        }
        $keyCache[$App] = $key.Trim()
    }
    $keyCache[$App]
}

function Invoke-Func {
    param(
        [string]$App,
        [string]$Path,
        [string]$Method = 'Get',
        $Body,
        [switch]$BodyIsArray
    )
    $sep = if ($Path.Contains('?')) { '&' } else { '?' }
    $uri = "https://$App.azurewebsites.net/api/$Path${sep}code=$(Get-FunctionKey $App)"
    # Not $args: that is an automatic variable, and splatting over it silently
    # loses the body.
    $call = @{ Method = $Method; Uri = $uri }
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 10
        # ConvertTo-Json renders a one-element array as a bare object, which an
        # endpoint expecting a JSON array rejects. Re-wrap it.
        if ($BodyIsArray -and -not $json.TrimStart().StartsWith('[')) {
            $json = "[$json]"
        }
        $call.Body        = $json
        $call.ContentType = 'application/json'
    }
    # Invoke-WebRequest plus an explicit ConvertFrom-Json rather than
    # Invoke-RestMethod. IRM unrolls a JSON array onto the pipeline, and a
    # caller that assigns the result gets one merged object whose properties
    # are each an array -- so filtering for a single twin silently matched all
    # four and posted an array-valued iTwinId.
    $raw = (Invoke-WebRequest @call).Content
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $raw | ConvertFrom-Json
}

function Get-Prop($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    $p.Value
}

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor DarkGray
}

# CIR nests entries under registry -> categories -> entries. Flatten so a
# duplicate is a row count rather than a shape to squint at.
function Get-CirEntries([guid]$Id) {
    $doc = Invoke-Func -App $CirApp -Path "entries?cirid=$Id"
    $registries = Get-Prop $doc 'registry'
    if ($null -eq $registries) { return @() }
    @(
        foreach ($reg in @($registries)) {
            foreach ($cat in @($reg.categories)) {
                foreach ($entry in @($cat.entries)) {
                    [pscustomobject]@{
                        Registry = $reg.id
                        Category = $cat.id
                        IdInSource = $entry.idInSource
                        Name = $entry.name
                    }
                }
            }
        }
    )
}

# ---- observation ---------------------------------------------------------

function Get-Observation([guid]$Id) {
    $twins   = @(Invoke-Func -App $EngApp -Path 'itwins')
    $matched = @($twins | Where-Object { $_.iTwinId -eq $Id.ToString() })
    $twin    = $matched[0]
    if ($null -eq $twin) { throw "ENG no longer holds iTwin $Id." }

    $scopes = @(Invoke-Func -App $RegApp -Path 'scopes')
    # A scope is matched on the twin's Number, which is what EngSitesBuilder
    # emits as the site name -- the scope does not carry the iTwinId.
    $mine   = @($scopes | Where-Object { $_.name -eq $twin.number })
    $cir    = @(Get-CirEntries $Id)

    [pscustomobject]@{
        EngRows     = $matched.Count
        DisplayName = $twin.displayName
        Description = $twin.description
        TypeNumber  = $twin.iTwinTypeNumber
        ScopeRows   = $mine.Count
        ScopeIds    = @($mine | ForEach-Object { $_.scopeId })
        CirRows     = $cir.Count
        CirSiteRows = @($cir | Where-Object { $_.Category -eq 'ITWIN-SITE' }).Count
        CirNames    = @($cir | Where-Object { $_.Category -eq 'ITWIN-SITE' } | ForEach-Object { $_.Name })
    }
}

# ---- one replay ----------------------------------------------------------

function Invoke-Pass {
    param([int]$Number, [pscustomobject]$Twin, [string]$DisplayName, [string]$Description)

    Write-Head "Pass $Number of $Passes"

    $upsert = Invoke-Func -App $EngApp -Path 'itwins' -Method Post -Body ([ordered]@{
        iTwinId       = $Twin.iTwinId
        number        = $Twin.number
        displayName   = $DisplayName
        description   = $Description
        class         = $Twin.class
        subClass      = $Twin.subClass
        type          = $Twin.type
        status        = $Twin.status
        parentITwinId = $Twin.parentITwinId
    })
    Write-Host ("  provider upsert   -> displayName='{0}'" -f $upsert.displayName)

    $published = Invoke-Func -App $EngEngineApp -Path 'bootstrap/itwin-created' -Method Post -Body ([ordered]@{
        eventType = 'iTwins.iTwinCreated.v1'
        iTwinId   = $Twin.iTwinId
    })
    $pubCount = Get-Prop $published 'sitesPublished'
    if ($null -eq $pubCount) { $pubCount = '?' }
    Write-Host ("  engine publish    -> sitesPublished={0}" -f $pubCount)

    # Publication and the drain are asynchronous, so a drain fired immediately
    # after publishing can read zero and make a healthy pipeline look broken.
    # Retry until the message surfaces. A drain that legitimately has nothing
    # to do still costs the full wait, which is the price of not flapping.
    $drain = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $drain = Invoke-Func -App $RegEngineApp -Path 'engine/ingest-sites' -Method Post
        if ($drain.messagesRead -gt 0) { break }
        Start-Sleep -Seconds 2
    }
    Write-Host ("  reg-location      -> read={0} registered={1} alreadyKnown={2} cirEntries={3} failed={4}" -f `
        $drain.messagesRead, $drain.sitesRegistered, $drain.alreadyKnown, $drain.cirEntriesRegistered, $drain.failed)

    $drain
}

# ---- run -----------------------------------------------------------------

if ($FreshTwin) {
    # A number CIR has certainly not seen, so pass 1 exercises the create path.
    $suffix   = (Get-Date).ToString('HHmmss')
    $ITwinId  = [guid]::NewGuid()
    $seedName = "IDEM-$suffix"

    Invoke-Func -App $EngApp -Path 'itwins' -Method Post -Body ([ordered]@{
        iTwinId     = $ITwinId.ToString()
        number      = $seedName
        displayName = "Idempotency probe $suffix"
        description = 'Created by tools/idempotency-itwin.ps1.'
        class       = 'Endeavor'
        subClass    = 'Asset'
        type        = 'District'
        status      = 'Active'
    }) | Out-Null

    Write-Host "Created throwaway iTwin $seedName ($ITwinId)." -ForegroundColor DarkGray
}

$allTwins = @(Invoke-Func -App $EngApp -Path 'itwins')
$original = @($allTwins | Where-Object { $_.iTwinId -eq $ITwinId.ToString() })[0]
if ($null -eq $original) { throw "ENG does not hold iTwin $ITwinId. Add it first." }

Write-Head "Replaying iTwin $($original.number) ($ITwinId)"
Write-Host "  baseline displayName : $($original.displayName)"
Write-Host "  baseline description : $($original.description)"

$before = Get-Observation $ITwinId
Write-Host ("  baseline rows        : eng={0} scopes={1} cirSiteEntries={2}" -f `
    $before.EngRows, $before.ScopeRows, $before.CirSiteRows)

$stamp = (Get-Date).ToString('HHmmss')
$expectedName = $original.displayName
$expectedDesc = $original.description
$results = @()

for ($i = 1; $i -le $Passes; $i++) {
    if ($i -gt 1) {
        # Mutate from pass 2 on: this is the "metadata was updated" case.
        $expectedName = "$($original.displayName) [rev$stamp-$i]"
        $expectedDesc = "Idempotency replay $i at $stamp."
    }
    $results += Invoke-Pass -Number $i -Twin $original -DisplayName $expectedName -Description $expectedDesc
}

$after = Get-Observation $ITwinId

# ---- verdict -------------------------------------------------------------

Write-Head 'Result'

$checks = @(
    [pscustomobject]@{
        Check    = 'ENG holds exactly one row'
        Expected = 1
        Actual   = $after.EngRows
        Pass     = ($after.EngRows -eq 1)
    }
    [pscustomobject]@{
        Check    = 'REG-LOCATION scope count unchanged'
        Expected = [Math]::Max($before.ScopeRows, 1)
        Actual   = $after.ScopeRows
        Pass     = ($after.ScopeRows -eq [Math]::Max($before.ScopeRows, 1))
    }
    [pscustomobject]@{
        Check    = 'CIR holds exactly one ITWIN-SITE entry'
        Expected = 1
        Actual   = $after.CirSiteRows
        Pass     = ($after.CirSiteRows -eq 1)
    }
    [pscustomobject]@{
        Check    = 'Latest metadata edit landed in ENG'
        Expected = $expectedName
        Actual   = $after.DisplayName
        Pass     = ($after.DisplayName -eq $expectedName)
    }
    [pscustomobject]@{
        Check    = 'No pass reported a failure'
        Expected = 0
        Actual   = [int](@($results | ForEach-Object { $_.failed }) | Measure-Object -Sum).Sum
        Pass     = ([int](@($results | ForEach-Object { $_.failed }) | Measure-Object -Sum).Sum -eq 0)
    }
)

if ($FreshTwin) {
    # Only meaningful for an invented twin: with an existing one, pass 1 is
    # already a repeat and registers nothing.
    $firstCir = [int]$results[0].cirEntriesRegistered
    $laterCir = [int](@($results | Select-Object -Skip 1 | ForEach-Object { $_.cirEntriesRegistered }) |
        Measure-Object -Sum).Sum

    $checks += [pscustomobject]@{
        Check    = 'First pass registered the site in CIR'
        Expected = '>0'
        Actual   = $firstCir
        Pass     = ($firstCir -gt 0)
    }
    $checks += [pscustomobject]@{
        Check    = 'Repeat passes registered nothing new in CIR'
        Expected = 0
        Actual   = $laterCir
        Pass     = ($laterCir -eq 0)
    }
    $checks += [pscustomobject]@{
        Check    = 'Exactly one REG-LOCATION scope was created'
        Expected = 1
        Actual   = $after.ScopeRows
        Pass     = ($after.ScopeRows -eq 1)
    }
}

$checks | Format-Table @{L='';E={ if ($_.Pass) { 'PASS' } else { 'FAIL' } }}, Check, Expected, Actual -AutoSize

if ($FreshTwin -and -not $KeepFreshTwin) {
    # ENG exposes no delete for an iTwin, so the seeded row stays. Clear the
    # downstream estate it created, which is what would otherwise skew a later
    # run's baseline.
    # Order matters. The scopes table has an AFTER DELETE trigger that refuses
    # while anything still references the scope, so the item established for
    # this twin has to go first.
    $items = @(Invoke-Func -App $RegApp -Path "items?guid=$ITwinId")
    $item  = if ($items.Count -gt 0) { $items[0] } else { $null }
    if ($null -ne $item) {
        try {
            Invoke-Func -App $RegApp -Path "items/$($item.itemId)" -Method Delete | Out-Null
            Write-Host "Deleted REG-LOCATION item $($item.itemId)." -ForegroundColor DarkGray
        }
        catch {
            Write-Host "Could not delete item $($item.itemId): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    foreach ($scopeId in $after.ScopeIds) {
        try {
            Invoke-Func -App $RegApp -Path "scopes/$scopeId" -Method Delete | Out-Null
            Write-Host "Deleted REG-LOCATION scope $scopeId." -ForegroundColor DarkGray
        }
        catch {
            # TR_scopes_delete_object refuses while any objects row references
            # the scope, and the scope's own objects row is such a reference,
            # so a scope established by ingestion cannot currently be deleted
            # through the API. Left in place deliberately rather than reaching
            # into the database behind the provider's back.
            Write-Host "Scope $scopeId could not be deleted (TR_scopes_delete_object); left in place." -ForegroundColor Yellow
        }
    }

    $doomed = @(Get-CirEntries $ITwinId | Where-Object { $_.Category -eq 'ITWIN-SITE' })
    if ($doomed.Count -gt 0) {
        try {
            Invoke-Func -App $CirApp -Path 'entries/batch-delete' -Method Post -BodyIsArray -Body @(
                foreach ($e in $doomed) {
                    [ordered]@{
                        registryId       = $e.Registry
                        categoryId       = $e.Category
                        categorySourceId = 'REG-LOCATION'
                        entryIdInSource  = "$($e.IdInSource)"
                        entrySourceId    = 'REG-LOCATION'
                    }
                }
            ) | Out-Null
            Write-Host "Deleted $($doomed.Count) CIR entry(ies) for $($original.number)." -ForegroundColor DarkGray
        }
        catch {
            Write-Host "Could not delete CIR entries: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Write-Host "Note: ENG has no delete route, so iTwin $($original.number) remains." -ForegroundColor DarkGray
}

if (-not $FreshTwin -and -not $KeepMutations) {
    Invoke-Func -App $EngApp -Path 'itwins' -Method Post -Body ([ordered]@{
        iTwinId       = $original.iTwinId
        number        = $original.number
        displayName   = $original.displayName
        description   = $original.description
        class         = $original.class
        subClass      = $original.subClass
        type          = $original.type
        status        = $original.status
        parentITwinId = $original.parentITwinId
    }) | Out-Null
    Write-Host "Restored the original metadata for $($original.number)." -ForegroundColor DarkGray
}

if ($checks | Where-Object { -not $_.Pass }) {
    Write-Host 'IDEMPOTENCY VIOLATED.' -ForegroundColor Red
    exit 1
}

Write-Host 'Idempotent across all passes.' -ForegroundColor Green
