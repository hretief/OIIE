<#
.SYNOPSIS
    Functional test pass over the ws-CIR Provider REST endpoints.

.DESCRIPTION
    Seeds a fixture registry, asserts every implemented operation and fault
    path against it, then tears the fixture down. A full pass leaves the
    database exactly as it found it, so the script is re-runnable.

    Operations still pending in the store are asserted to return 501 with a
    problem+json body naming the spec clause. Flip those to the success case
    as each one lands.

.EXAMPLE
    .\test-cir.ps1 -FunctionApp cir-func-44p2f3n6 -ResourceGroup HilmarRetiefRG

.EXAMPLE
    .\test-cir.ps1 -BaseUrl http://localhost:7071/api -FunctionKey local

.EXAMPLE
    .\test-cir.ps1 -FunctionApp cir-func-44p2f3n6 -ResourceGroup HilmarRetiefRG -Detailed
#>
[CmdletBinding(DefaultParameterSetName = 'Azure')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Azure')]
    [string] $FunctionApp,

    [Parameter(Mandatory = $true, ParameterSetName = 'Azure')]
    [string] $ResourceGroup,

    [Parameter(Mandatory = $true, ParameterSetName = 'Explicit')]
    [string] $BaseUrl,

    [string] $FunctionKey,

    [string] $RegistryId = 'CIR-Test',

    # Print request and response bodies for every case.
    [switch] $Detailed
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Fixture constants
# ---------------------------------------------------------------------------

$CategoryId       = 'Asset'
$CategorySourceId = 'MIMOSA OSA-EAI V3'
$CiridA           = '550e8400-e29b-41d4-a716-446655440000'
$CiridB           = '550e8400-e29b-41d4-a716-446655448778'
$CiridUnknown     = '11111111-2222-3333-4444-555555555555'

# §3.1.2 uses its own registry so the §09 GetRegistry counts stay stable.
$EquivRegistryId  = "$RegistryId-Equiv"
$CiridE1          = 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'
$CiridE2          = 'bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb'

# ---------------------------------------------------------------------------
# Endpoint resolution
# ---------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'Azure') {
    $BaseUrl = "https://$FunctionApp.azurewebsites.net/api"
    if ([string]::IsNullOrWhiteSpace($FunctionKey)) {
        Write-Host 'Fetching function key...' -ForegroundColor DarkGray
        $FunctionKey = az functionapp keys list -g $ResourceGroup -n $FunctionApp `
            --query functionKeys.default -o tsv
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the function key.' }
    }
}

$BaseUrl = $BaseUrl.TrimEnd('/')

# ---------------------------------------------------------------------------
# Harness
# ---------------------------------------------------------------------------

$script:Passed = 0
$script:Failed = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Cir {
    param(
        [string] $Method,
        [string] $Path,
        $Body,
        [switch] $Anonymous
    )

    $headers = @{}
    if (-not $Anonymous -and $FunctionKey) { $headers['x-functions-key'] = $FunctionKey }

    # Note: not $args -- that is an automatic variable inside a function.
    $req = @{
        Method             = $Method
        Uri                = "$BaseUrl$Path"
        Headers            = $headers
        SkipHttpErrorCheck = $true
        ErrorAction        = 'Stop'
    }

    if ($null -ne $Body) {
        # -InputObject, not the pipeline: piping an empty array sends zero items
        # and ConvertTo-Json then returns $null.
        $json = ConvertTo-Json -InputObject $Body -Depth 12 -Compress
        $req.Body = [Text.Encoding]::UTF8.GetBytes($json)
        $req.ContentType = 'application/json'
        if ($Detailed) { Write-Host "  -> $json" -ForegroundColor DarkGray }
    }

    $response = Invoke-WebRequest @req

    # Invoke-WebRequest hands back a byte[] for content types it does not
    # classify as text, and application/problem+json is one of them.
    $raw = $response.Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        try { $parsed = $raw | ConvertFrom-Json } catch { $parsed = $null }
    }

    if ($Detailed -and $raw) { Write-Host "  <- $raw" -ForegroundColor DarkGray }

    [pscustomobject]@{
        Status      = [int]$response.StatusCode
        Body        = $parsed
        Raw         = $raw
        ContentType = ($response.Headers['Content-Type'] -join ';')
    }
}

function Test-Case {
    param([string] $Name, [scriptblock] $Action)

    Write-Host "  $Name ... " -NoNewline
    try {
        & $Action
        $script:Passed++
        Write-Host 'PASS' -ForegroundColor Green
    }
    catch {
        $script:Failed++
        $script:Failures.Add("$Name : $($_.Exception.Message)")
        Write-Host 'FAIL' -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-Status {
    param($Response, [int] $Expected)
    if ($Response.Status -ne $Expected) {
        throw "expected HTTP $Expected, got $($Response.Status). Body: $($Response.Raw)"
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string] $What = 'value')
    if ($Expected -ne $Actual) {
        throw "expected $What '$Expected', got '$Actual'"
    }
}

function Assert-Parsed {
    param($Response)
    if ($null -eq $Response.Body) {
        throw "response body did not parse as JSON. Raw: $($Response.Raw)"
    }
}

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

# Flattens the nested Registry graph down to the entries it contains.
function Get-Entries {
    param($Body)
    if (-not $Body -or -not $Body.registry) { return @() }
    return @($Body.registry | ForEach-Object { $_.categories } |
             ForEach-Object { $_.entries } | Where-Object { $_ })
}

function Write-Section {
    param([string] $Title)
    Write-Host "`n$Title" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------

Write-Host "ws-CIR Provider functional test" -ForegroundColor White
Write-Host "Endpoint : $BaseUrl"
Write-Host "Registry : $RegistryId"

# Leave no fixture behind from an aborted earlier run.
Invoke-Cir -Method DELETE -Path "/registries/$RegistryId" | Out-Null
Invoke-Cir -Method DELETE -Path "/registries/$EquivRegistryId" | Out-Null

# ---------------------------------------------------------------------------
Write-Section '01  Health'

$health = Invoke-Cir -Method GET -Path '/health' -Anonymous

Test-Case 'returns 200' { Assert-Status $health 200 }
Test-Case 'SQL is reachable' {
    Assert-Equal $true $health.Body.sql 'sql'
    Assert-Equal 'healthy' $health.Body.status 'status'
}
Test-Case 'identifies spec and binding' {
    Assert-Equal 'ws-CIR 1.0' $health.Body.spec 'spec'
    Assert-Equal 'REST' $health.Body.binding 'binding'
}

if ($health.Status -ne 200 -or -not $health.Body.sql) {
    Write-Host "`nHealth check failed; aborting. $($health.Raw)" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
Write-Section '02  CreateRegistry (seed)'

$seed = @{
    registry = @(
        @{
            id          = $RegistryId
            description = @(@{ value = 'Conformance fixture'; languageId = 'en-US' })
            categories  = @(
                @{
                    id       = $CategoryId
                    sourceId = $CategorySourceId
                    entries  = @(
                        @{ idInSource = '234443';   sourceId = 'System A'; cirid = $CiridA; sourceOwnerId = 'Refinery-01'; name = 'Loop 106' },
                        @{ idInSource = '423ABC';   sourceId = 'System B'; cirid = $CiridA; name = 'Cmn Loop 106' },
                        @{
                            idInSource  = 'TIC-8106'
                            sourceId    = 'System C'
                            cirid       = $CiridB
                            name        = 'Top Temp Control'
                            description = @{ value = 'Column overhead loop'; languageId = 'en-US' }
                            inactive    = $false
                            properties  = @(
                                @{
                                    id            = 'ParentEntityID'
                                    dataType      = 'string'
                                    propertyValue = @(@{ key = 'IDInSource'; value = 'UNIT101' })
                                }
                            )
                        }
                    )
                }
            )
        }
    )
    createCirid = $false
}

$created = Invoke-Cir -Method POST -Path '/registries' -Body $seed
Test-Case 'returns 201' { Assert-Status $created 201 }

# ---------------------------------------------------------------------------
Write-Section '03  CreateRegistry (duplicate entry)'

$dup = @{
    registry = @(
        @{
            id         = $RegistryId
            categories = @(
                @{
                    id       = $CategoryId
                    sourceId = $CategorySourceId
                    entries  = @(@{ idInSource = '234443'; sourceId = 'System A'; name = 'Duplicate' })
                }
            )
        }
    )
    createCirid = $false
}

$duplicate = Invoke-Cir -Method POST -Path '/registries' -Body $dup
Test-Case 'returns 409' { Assert-Status $duplicate 409 }
Test-Case 'names DuplicateEntryFault' {
    Assert-Parsed $duplicate
    Assert-Equal 'DuplicateEntryFault' $duplicate.Body.title 'title'
    Assert-Equal 'DuplicateEntryFault' $duplicate.Body.faults[0].code 'faults[0].code'
}
Test-Case 'is application/problem+json' {
    Assert-True ($duplicate.ContentType -like '*application/problem+json*') `
        "expected problem+json, got '$($duplicate.ContentType)'"
}

# ---------------------------------------------------------------------------
Write-Section '04  GetEntriesByCIRID'

$byCirid = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridA"
Test-Case 'returns 200' { Assert-Status $byCirid 200 }
Test-Case 'returns both equivalent entries' {
    Assert-Equal 2 (Get-Entries $byCirid.Body).Count 'entry count'
}
Test-Case 'every entry carries the requested CIRID' {
    foreach ($e in Get-Entries $byCirid.Body) {
        Assert-Equal $CiridA.ToLower() ([string]$e.cirid).ToLower() 'cirid'
    }
}
Test-Case 'preserves the nested Registry graph' {
    $r = $byCirid.Body.registry[0]
    Assert-Equal $RegistryId $r.id 'registry id'
    Assert-Equal $CategorySourceId $r.categories[0].sourceId 'category sourceId'
}

# ---------------------------------------------------------------------------
Write-Section '05  GetEntriesByCIRID (TargetSourceID exact)'

$exact = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridA&targetSourceId=System%20B"
Test-Case 'returns 200' { Assert-Status $exact 200 }
Test-Case 'narrows to the requested source' {
    $entries = Get-Entries $exact.Body
    Assert-Equal 1 $entries.Count 'entry count'
    Assert-Equal 'System B' $entries[0].sourceId 'sourceId'
}

# ---------------------------------------------------------------------------
Write-Section '06  GetEntriesByCIRID (wildcard TargetSourceID)'

$wild = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridA&targetSourceId=System%20."
Test-Case 'returns 200' { Assert-Status $wild 200 }
Test-Case "'System .' matches both sources" {
    Assert-Equal 2 (Get-Entries $wild.Body).Count 'entry count'
}

# §4 wildcards are implicitly anchored at both ends, so a bare prefix must not match.
$unanchored = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridA&targetSourceId=System"
Test-Case "'System' does not match (patterns are anchored)" {
    Assert-Equal 0 (Get-Entries $unanchored.Body).Count 'entry count'
}

# ---------------------------------------------------------------------------
Write-Section '07  GetEntriesByCIRID (unknown / malformed)'

$unknown = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridUnknown"
Test-Case 'unknown CIRID returns 200, not 404' { Assert-Status $unknown 200 }
Test-Case 'unknown CIRID returns an empty list' {
    Assert-Equal 0 (Get-Entries $unknown.Body).Count 'entry count'
}

$malformed = Invoke-Cir -Method GET -Path '/entries?cirid=not-a-uuid'
Test-Case 'malformed CIRID returns 400' { Assert-Status $malformed 400 }

# ---------------------------------------------------------------------------
Write-Section '08  GetEquivalentEntries'

$equivBody = @{
    entryIdentifier = @(
        @{
            registryId       = $RegistryId
            categoryId       = $CategoryId
            categorySourceId = $CategorySourceId
            entryIdInSource  = '234443'
            entrySourceId    = 'System A'
        }
    )
    targetSourceId = @()
}

$equiv = Invoke-Cir -Method POST -Path '/queries/equivalent-entries' -Body $equivBody
Test-Case 'returns 200' { Assert-Status $equiv 200 }
Test-Case 'includes the specified entry and its equivalent' {
    $ids = (Get-Entries $equiv.Body | ForEach-Object { $_.idInSource }) | Sort-Object
    Assert-Equal '234443,423ABC' ($ids -join ',') 'idInSource set'
}
Test-Case 'de-duplicates across partial results' {
    Assert-Equal 2 (Get-Entries $equiv.Body).Count 'entry count'
}

# ---------------------------------------------------------------------------
Write-Section '09  GetRegistry'

<#
    Every filter is scoped to the fixture registry. A CIR is a shared registry
    server: assuming a globally empty database makes these assertions depend on
    what else happens to be stored, which is exactly the wrong property for a
    regression test. Scoping also keeps the "types AND together" semantics under
    test rather than incidental.
#>
function Invoke-GetRegistry {
    param($Filter)

    # A registryFilter is folded into each Filter element. Filters of the same
    # type OR together (§3.2.1), and every one carries the same registry ID, so
    # scoping cannot change the meaning of the filters under test.
    $scoped = @(
        foreach ($f in $Filter) {
            $copy = @{ registryFilter = @{ id = $RegistryId } }
            foreach ($k in $f.Keys) { $copy[$k] = $f[$k] }
            $copy
        }
    )
    if ($scoped.Count -eq 0) { $scoped = @(@{ registryFilter = @{ id = $RegistryId } }) }

    return Invoke-Cir -Method POST -Path '/queries/registry' -Body @{ filter = $scoped }
}

$all = Invoke-GetRegistry @()
Test-Case 'registry-scoped query returns 200' { Assert-Status $all 200 }
Test-Case 'absent filter types are logical TRUE, not an empty result' {
    Assert-Equal 3 (Get-Entries $all.Body).Count 'entry count'
}

# Unscoped, to prove the absent-filter rule really is TRUE rather than a no-op.
$unscoped = Invoke-Cir -Method POST -Path '/queries/registry' -Body @{ filter = @() }
Test-Case 'an empty filter list returns at least the fixture' {
    Assert-True ((Get-Entries $unscoped.Body).Count -ge 3) `
        "expected at least 3 entries, got $((Get-Entries $unscoped.Body).Count)"
}

$byName = Invoke-GetRegistry @(@{ entryFilter = @{ name = '.*Loop.*' } })
Test-Case 'EntryFilter Name wildcard narrows the result' {
    $names = (Get-Entries $byName.Body | ForEach-Object { $_.name }) | Sort-Object
    Assert-Equal 'Cmn Loop 106,Loop 106' ($names -join ',') 'name set'
}

$anchored = Invoke-GetRegistry @(@{ entryFilter = @{ name = 'Loop' } })
Test-Case 'patterns are anchored (bare Loop matches nothing)' {
    Assert-Equal 0 (Get-Entries $anchored.Body).Count 'entry count'
}

# Same type across two Filter elements must OR.
$ored = Invoke-GetRegistry @(
    @{ entryFilter = @{ name = 'Loop 106' } },
    @{ entryFilter = @{ name = 'Top Temp Control' } }
)
Test-Case 'multiple EntryFilters OR together' {
    $names = (Get-Entries $ored.Body | ForEach-Object { $_.name }) | Sort-Object
    Assert-Equal 'Loop 106,Top Temp Control' ($names -join ',') 'name set'
}

# Different types within one Filter must AND.
$anded = Invoke-GetRegistry @(
    @{
        categoryFilter = @{ id = $CategoryId }
        propertyFilter = @{ id = 'ParentEntityID' }
    }
)
Test-Case 'filter types AND together' {
    $entries = Get-Entries $anded.Body
    Assert-Equal 1 $entries.Count 'entry count'
    Assert-Equal 'TIC-8106' $entries[0].idInSource 'idInSource'
}

$byPropValue = Invoke-GetRegistry @(@{ propertyFilter = @{ key = 'IDInSource'; value = 'UNIT101' } })
Test-Case 'PropertyFilter matches on key and value together' {
    $entries = Get-Entries $byPropValue.Body
    Assert-Equal 1 $entries.Count 'entry count'
    Assert-Equal 'TIC-8106' $entries[0].idInSource 'idInSource'
}

# Key and Value must co-occur in the same PropertyValue, not merely both appear.
$mismatched = Invoke-GetRegistry @(@{ propertyFilter = @{ key = 'IDInSource'; value = 'NOT-UNIT101' } })
Test-Case 'PropertyFilter key and value must co-occur' {
    Assert-Equal 0 (Get-Entries $mismatched.Body).Count 'entry count'
}

$byCiridFilter = Invoke-GetRegistry @(@{ entryFilter = @{ cirid = $CiridA } })
Test-Case 'EntryFilter CIRID matches exactly' {
    Assert-Equal 2 (Get-Entries $byCiridFilter.Body).Count 'entry count'
}

$noMatch = Invoke-GetRegistry @(@{ categoryFilter = @{ sourceId = 'No Such Source' } })
Test-Case 'non-matching filter returns an empty list' {
    Assert-Equal 0 (Get-Entries $noMatch.Body).Count 'entry count'
}

$withProps = Invoke-GetRegistry @(@{ entryFilter = @{ idInSource = 'TIC-8106' } })
Test-Case 'associated Properties are returned with the Entry' {
    $entries = Get-Entries $withProps.Body
    Assert-Equal 1 $entries.Count 'entry count'
    Assert-Equal 'ParentEntityID' $entries[0].properties[0].id 'property id'
    Assert-Equal 'UNIT101' $entries[0].properties[0].propertyValue[0].value 'property value'
}

# ---------------------------------------------------------------------------
Write-Section '10  CreateEquivalentEntries'

$equivSeed = @{
    registry = @(
        @{
            id         = $EquivRegistryId
            categories = @(
                @{
                    id       = $CategoryId
                    sourceId = $CategorySourceId
                    entries  = @(
                        @{ idInSource = 'WITH-CIRID'; sourceId = 'Register'; cirid = $CiridE1 },
                        @{ idInSource = 'NO-CIRID-1'; sourceId = 'Register' },
                        @{ idInSource = 'NO-CIRID-2'; sourceId = 'Register' }
                    )
                }
            )
        }
    )
    createCirid = $false
}

$equivCreated = Invoke-Cir -Method POST -Path '/registries' -Body $equivSeed
Test-Case 'equivalence fixture seeds' { Assert-Status $equivCreated 201 }

function New-EquivRequest {
    param(
        [string] $ExistingId,
        [string] $NewId,
        [string] $NewSource = 'Ops',
        [string] $NewCirid,
        [string] $RegistryOverride,
        [string] $CategoryOverride
    )

    $entry = @{ idInSource = $NewId; sourceId = $NewSource }
    if ($NewCirid) { $entry.cirid = $NewCirid }

    return @{
        existingIdInSource = $ExistingId
        existingSourceId   = 'Register'
        registryId         = if ($RegistryOverride) { $RegistryOverride } else { $EquivRegistryId }
        categoryId         = if ($CategoryOverride) { $CategoryOverride } else { $CategoryId }
        categorySourceId   = $CategorySourceId
        entry              = $entry
    }
}

function Get-EquivEntry {
    param([string] $IdInSource)
    $r = Invoke-Cir -Method POST -Path '/queries/registry' -Body @{
        filter = @(@{
            registryFilter = @{ id = $EquivRegistryId }
            entryFilter    = @{ idInSource = $IdInSource }
        })
    }
    return @(Get-Entries $r.Body)[0]
}

function Get-CountByCirid {
    param([string] $Cirid)
    $r = Invoke-Cir -Method GET -Path "/entries?cirid=$Cirid"
    return (Get-Entries $r.Body).Count
}

# --- Rule 1: the existing Entry's CIRID wins -------------------------------

$rule1 = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'WITH-CIRID' -NewId 'ALIAS-1')
)
Test-Case 'returns 201' { Assert-Status $rule1 201 }
Test-Case 'new Entry adopts the existing CIRID' {
    Assert-Equal $CiridE1.ToLower() ([string](Get-EquivEntry 'ALIAS-1').cirid).ToLower() 'cirid'
    Assert-Equal 2 (Get-CountByCirid $CiridE1) 'equivalence set size'
}

# A CIRID supplied on the new Entry must not override the existing one.
$rule1b = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'WITH-CIRID' -NewId 'ALIAS-2' -NewSource 'CMMS' -NewCirid $CiridE2)
)
Test-Case 'a supplied CIRID is discarded when the existing Entry has one' {
    Assert-Status $rule1b 201
    Assert-Equal $CiridE1.ToLower() ([string](Get-EquivEntry 'ALIAS-2').cirid).ToLower() 'cirid'
    Assert-Equal 3 (Get-CountByCirid $CiridE1) 'equivalence set size'
    Assert-Equal 0 (Get-CountByCirid $CiridE2) 'discarded CIRID must not be in use'
}

# --- Rule 2: the supplied CIRID propagates backward ------------------------

$rule2 = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'NO-CIRID-1' -NewId 'ALIAS-3' -NewCirid $CiridE2)
)
Test-Case 'returns 201' { Assert-Status $rule2 201 }
Test-Case 'supplied CIRID propagates backward to the existing Entry' {
    # The existing Entry is modified even though the caller never asked for it.
    Assert-Equal $CiridE2.ToLower() ([string](Get-EquivEntry 'NO-CIRID-1').cirid).ToLower() 'existing entry cirid'
    Assert-Equal $CiridE2.ToLower() ([string](Get-EquivEntry 'ALIAS-3').cirid).ToLower() 'new entry cirid'
    Assert-Equal 2 (Get-CountByCirid $CiridE2) 'equivalence set size'
}

# --- Rule 3: neither has one, so the server mints --------------------------

$rule3 = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'NO-CIRID-2' -NewId 'ALIAS-4')
)
Test-Case 'returns 201' { Assert-Status $rule3 201 }
Test-Case 'server mints a CIRID and assigns it to both' {
    $minted = (Get-EquivEntry 'NO-CIRID-2').cirid
    Assert-True (-not [string]::IsNullOrWhiteSpace($minted)) 'existing entry should now carry a CIRID'
    Assert-True ([guid]::TryParse($minted, [ref]([guid]::Empty))) "minted value '$minted' is not a UUID"
    Assert-Equal ([string]$minted).ToLower() ([string](Get-EquivEntry 'ALIAS-4').cirid).ToLower() 'new entry cirid'
    Assert-Equal 2 (Get-CountByCirid $minted) 'equivalence set size'
}

# --- Faults ----------------------------------------------------------------

$badRegistry = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'WITH-CIRID' -NewId 'X-1' -RegistryOverride 'NoSuchRegistry')
)
Test-Case 'unknown registry returns 404 RegistryNotFoundFault' {
    Assert-Status $badRegistry 404
    Assert-Parsed $badRegistry
    Assert-Equal 'RegistryNotFoundFault' $badRegistry.Body.title 'title'
}

$badCategory = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'WITH-CIRID' -NewId 'X-2' -CategoryOverride 'NoSuchCategory')
)
Test-Case 'unknown category returns 404 CategoryNotFoundFault' {
    Assert-Status $badCategory 404
    Assert-Equal 'CategoryNotFoundFault' $badCategory.Body.title 'title'
}

$badEntry = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'NO-SUCH-ENTRY' -NewId 'X-3')
)
Test-Case 'unknown existing entry returns 404 EntryNotFoundFault' {
    Assert-Status $badEntry 404
    Assert-Equal 'EntryNotFoundFault' $badEntry.Body.title 'title'
}

$dupEntry = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'WITH-CIRID' -NewId 'ALIAS-1')
)
Test-Case 'duplicate new entry returns 409 DuplicateEntryFault' {
    Assert-Status $dupEntry 409
    Assert-Equal 'DuplicateEntryFault' $dupEntry.Body.title 'title'
}

# --- Atomicity across the batch (§3.1) -------------------------------------

$batch = Invoke-Cir -Method POST -Path '/equivalent-entries' -Body @(
    (New-EquivRequest -ExistingId 'WITH-CIRID' -NewId 'ATOMIC-OK'),
    (New-EquivRequest -ExistingId 'NO-SUCH-ENTRY' -NewId 'ATOMIC-BAD')
)
Test-Case 'a failing batch member rolls the whole batch back' {
    Assert-Status $batch 404
    Assert-True ($null -eq (Get-EquivEntry 'ATOMIC-OK')) `
        'ATOMIC-OK was committed despite a later fault in the same batch'
}

$equivTorn = Invoke-Cir -Method DELETE -Path "/registries/$EquivRegistryId"
Test-Case 'equivalence fixture tears down' { Assert-Status $equivTorn 204 }

# ---------------------------------------------------------------------------
Write-Section '11  UpdateRegistry / UpdateEntryCIRID / Delete family'

# Own registry again, so the section 09 counts stay stable.
$MutRegistryId = "$RegistryId-Mut"
$CiridM1 = 'cccccccc-3333-4333-8333-cccccccccccc'
$CiridM2 = 'dddddddd-4444-4444-8444-dddddddddddd'

Invoke-Cir -Method DELETE -Path "/registries/$MutRegistryId" | Out-Null

$mutSeed = @{
    registry = @(
        @{
            id         = $MutRegistryId
            categories = @(
                @{
                    id       = $CategoryId
                    sourceId = $CategorySourceId
                    entries  = @(
                        @{
                            idInSource = 'M-1'; sourceId = 'Register'; cirid = $CiridM1
                            name = 'Original Name'; sourceOwnerId = 'Owner-A'
                            properties = @(
                                @{ id = 'ParentEntityID'; dataType = 'string'; propertyValue = @(@{ key = 'IDInSource'; value = 'OLD-PARENT' }) },
                                @{ id = 'ChildEntityID';  dataType = 'string'; propertyValue = @(@{ key = 'IDInSource'; value = 'CHILD-1' }) }
                            )
                        },
                        @{ idInSource = 'M-2'; sourceId = 'Register'; cirid = $CiridM2; name = 'Second' }
                    )
                },
                @{
                    id       = 'Doomed'
                    sourceId = $CategorySourceId
                    entries  = @(@{ idInSource = 'D-1'; sourceId = 'Register' })
                }
            )
        }
    )
    createCirid = $false
}

$mutCreated = Invoke-Cir -Method POST -Path '/registries' -Body $mutSeed
Test-Case 'mutation fixture seeds' { Assert-Status $mutCreated 201 }

function Get-MutEntry {
    param([string] $IdInSource)
    $r = Invoke-Cir -Method POST -Path '/queries/registry' -Body @{
        filter = @(@{
            registryFilter = @{ id = $MutRegistryId }
            entryFilter    = @{ idInSource = $IdInSource }
        })
    }
    return @(Get-Entries $r.Body)[0]
}

function Get-MutEntryCount {
    $r = Invoke-Cir -Method POST -Path '/queries/registry' -Body @{
        filter = @(@{ registryFilter = @{ id = $MutRegistryId } })
    }
    return (Get-Entries $r.Body).Count
}

# --- §3.1.3 UpdateRegistry -------------------------------------------------

$update = Invoke-Cir -Method PUT -Path '/registries' -Body @{
    registry = @(
        @{
            id         = $MutRegistryId
            categories = @(
                @{
                    id       = $CategoryId
                    sourceId = $CategorySourceId
                    entries  = @(
                        @{
                            idInSource = 'M-1'; sourceId = 'Register'
                            name = 'Renamed'
                            properties = @(
                                @{ id = 'ParentEntityID'; dataType = 'string'; propertyValue = @(@{ key = 'IDInSource'; value = 'NEW-PARENT' }) }
                            )
                        }
                    )
                }
            )
        }
    )
}

Test-Case 'UpdateRegistry returns 204' { Assert-Status $update 204 }

$m1 = Get-MutEntry 'M-1'
Test-Case 'supplied attributes are replaced' {
    Assert-Equal 'Renamed' $m1.name 'name'
}
Test-Case 'omitted attributes are cleared (snapshot semantics)' {
    Assert-True ([string]::IsNullOrEmpty([string]$m1.sourceOwnerId)) `
        "sourceOwnerId should have been cleared, got '$($m1.sourceOwnerId)'"
}
Test-Case 'CIRID is preserved when omitted' {
    Assert-Equal $CiridM1.ToLower() ([string]$m1.cirid).ToLower() 'cirid'
}
Test-Case 'supplied property is updated' {
    $parent = $m1.properties | Where-Object { $_.id -eq 'ParentEntityID' }
    Assert-Equal 'NEW-PARENT' $parent.propertyValue[0].value 'property value'
}
Test-Case 'unsupplied children are left alone, not deleted' {
    Assert-True ($null -ne ($m1.properties | Where-Object { $_.id -eq 'ChildEntityID' })) `
        'ChildEntityID was removed; omitted children must be preserved'
    Assert-Equal 3 (Get-MutEntryCount) 'entry count'
}

$updateMissing = Invoke-Cir -Method PUT -Path '/registries' -Body @{
    registry = @(@{ id = 'NoSuchRegistry'; categories = @() })
}
Test-Case 'UpdateRegistry on an unknown registry returns 404' {
    Assert-Status $updateMissing 404
    Assert-Equal 'RegistryNotFoundFault' $updateMissing.Body.title 'title'
}

# --- §3.1.4 UpdateEntryCIRID ----------------------------------------------

$collapse = Invoke-Cir -Method POST -Path '/cirids/replace' -Body @{
    oldCirid = @($CiridM2)
    newCirid = $CiridM1
}
Test-Case 'UpdateEntryCIRID returns 204' { Assert-Status $collapse 204 }
Test-Case 'the two clusters collapse onto one CIRID' {
    Assert-Equal $CiridM1.ToLower() ([string](Get-MutEntry 'M-2').cirid).ToLower() 'cirid'
    Assert-Equal 0 (Get-CountByCirid $CiridM2) 'old CIRID must no longer be in use'
}

$noopCollapse = Invoke-Cir -Method POST -Path '/cirids/replace' -Body @{
    oldCirid = @($CiridUnknown)
    newCirid = $CiridM1
}
Test-Case 'an unmatched OldCIRID is a no-op, not a fault' {
    # §3.1.4 defines no faults.
    Assert-Status $noopCollapse 204
}

# --- §3.1.8 DeleteProperties ----------------------------------------------

$delProp = Invoke-Cir -Method POST -Path '/properties/batch-delete' -Body @(
    @{
        registryId = $MutRegistryId; categoryId = $CategoryId; categorySourceId = $CategorySourceId
        entryIdInSource = 'M-1'; entrySourceId = 'Register'; propertyId = 'ChildEntityID'
    }
)
Test-Case 'DeleteProperties returns 204' { Assert-Status $delProp 204 }
Test-Case 'the property is gone and its Entry survives' {
    $e = Get-MutEntry 'M-1'
    Assert-True ($null -ne $e) 'entry should still exist'
    Assert-True ($null -eq ($e.properties | Where-Object { $_.id -eq 'ChildEntityID' })) 'property should be gone'
}

$delPropMissing = Invoke-Cir -Method POST -Path '/properties/batch-delete' -Body @(
    @{
        registryId = $MutRegistryId; categoryId = $CategoryId; categorySourceId = $CategorySourceId
        entryIdInSource = 'M-1'; entrySourceId = 'Register'; propertyId = 'NoSuchProperty'
    }
)
Test-Case 'unknown property returns 404 PropertyNotFoundFault' {
    Assert-Status $delPropMissing 404
    Assert-Equal 'PropertyNotFoundFault' $delPropMissing.Body.title 'title'
}

# --- §3.1.7 DeleteEntries -------------------------------------------------

$delEntryAtomic = Invoke-Cir -Method POST -Path '/entries/batch-delete' -Body @(
    @{ registryId = $MutRegistryId; categoryId = $CategoryId; categorySourceId = $CategorySourceId; entryIdInSource = 'M-2'; entrySourceId = 'Register' },
    @{ registryId = $MutRegistryId; categoryId = $CategoryId; categorySourceId = $CategorySourceId; entryIdInSource = 'NOPE'; entrySourceId = 'Register' }
)
Test-Case 'a failing batch member rolls the whole delete back' {
    Assert-Status $delEntryAtomic 404
    Assert-True ($null -ne (Get-MutEntry 'M-2')) 'M-2 was deleted despite a later fault in the same batch'
}

$delEntry = Invoke-Cir -Method POST -Path '/entries/batch-delete' -Body @(
    @{ registryId = $MutRegistryId; categoryId = $CategoryId; categorySourceId = $CategorySourceId; entryIdInSource = 'M-2'; entrySourceId = 'Register' }
)
Test-Case 'DeleteEntries returns 204' { Assert-Status $delEntry 204 }
Test-Case 'the entry is gone' { Assert-True ($null -eq (Get-MutEntry 'M-2')) 'M-2 should be gone' }

# --- §3.1.6 DeleteCategory ------------------------------------------------

$delCat = Invoke-Cir -Method DELETE -Path '/categories' -Body @{
    registryId = $MutRegistryId; categoryId = 'Doomed'; categorySourceId = $CategorySourceId
}
Test-Case 'DeleteCategory returns 204' { Assert-Status $delCat 204 }
Test-Case 'the category cascaded to its entries' {
    Assert-True ($null -eq (Get-MutEntry 'D-1')) 'D-1 should have gone with its category'
    Assert-Equal 1 (Get-MutEntryCount) 'entry count'
}

$delCatMissing = Invoke-Cir -Method DELETE -Path '/categories' -Body @{
    registryId = $MutRegistryId; categoryId = 'NoSuchCategory'; categorySourceId = $CategorySourceId
}
Test-Case 'unknown category returns 404 CategoryNotFoundFault' {
    Assert-Status $delCatMissing 404
    Assert-Equal 'CategoryNotFoundFault' $delCatMissing.Body.title 'title'
}

$mutTorn = Invoke-Cir -Method DELETE -Path "/registries/$MutRegistryId"
Test-Case 'mutation fixture tears down' { Assert-Status $mutTorn 204 }

# ---------------------------------------------------------------------------
Write-Section '12  Annex A BOD message model'

$BodRegistryId = "$RegistryId-Bod"
$cirNs = 'http://www.openoandm.org/ws-cir/'
$oaNs  = 'http://www.openapplications.org/oagis/9'

function Invoke-Bod {
    param([string] $Xml)

    $headers = @{ 'x-functions-key' = $FunctionKey }
    $response = Invoke-WebRequest -Method Post -Uri "$BaseUrl/bods" `
        -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($Xml)) `
        -ContentType 'application/xml' -SkipHttpErrorCheck -ErrorAction Stop

    $raw = $response.Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }

    $doc = $null
    if (-not [string]::IsNullOrWhiteSpace($raw) -and $raw.TrimStart().StartsWith('<')) {
        try { $doc = [xml]$raw } catch { $doc = $null }
    }

    return [pscustomobject]@{ Status = [int]$response.StatusCode; Xml = $doc; Raw = $raw }
}

function New-Bod {
    param([string] $Name, [string] $DataArea)
    return @"
<?xml version="1.0" encoding="utf-8"?>
<$Name xmlns="$cirNs" xmlns:oa="$oaNs" releaseID="1.2.1" versionID="1.0">
  <oa:ApplicationArea>
    <oa:Sender><oa:LogicalID>test-cir.ps1</oa:LogicalID></oa:Sender>
    <oa:CreationDateTime>$( (Get-Date).ToUniversalTime().ToString('o') )</oa:CreationDateTime>
    <oa:BODID>$( [guid]::NewGuid() )</oa:BODID>
  </oa:ApplicationArea>
  <DataArea>
$DataArea
  </DataArea>
</$Name>
"@
}

Invoke-Cir -Method DELETE -Path "/registries/$BodRegistryId" | Out-Null

# --- Catalogue -------------------------------------------------------------

$catalogue = Invoke-Cir -Method GET -Path '/bods/catalogue'
Test-Case 'catalogue returns 200' { Assert-Status $catalogue 200 }
Test-Case 'releaseID is 1.2.1 and versionID is 1.0' {
    Assert-Equal '1.2.1' $catalogue.Body.releaseId 'releaseId'
    Assert-Equal '1.0' $catalogue.Body.versionId 'versionId'
}
Test-Case 'catalogue lists all eleven request BODs' {
    Assert-Equal 11 $catalogue.Body.requestBods.Count 'request BOD count'
}
Test-Case 'no Sync verb appears in the catalogue' {
    $verbs = $catalogue.Body.requestBods | ForEach-Object { $_.verb } | Sort-Object -Unique
    Assert-True ($verbs -notcontains 'Sync') 'ws-CIR defines no Sync verb'
}

# --- ProcessRegistry -> AcknowledgeRegistry --------------------------------

$processRegistry = Invoke-Bod (New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="Always"/>
    <CreateRegistry>
      <Registry>
        <ID>$BodRegistryId</ID>
        <Category>
          <ID>$CategoryId</ID>
          <CategorySourceID>$CategorySourceId</CategorySourceID>
          <Entry>
            <IDInSource>B-1</IDInSource>
            <SourceID>Register</SourceID>
            <CIRID>$CiridA</CIRID>
            <Name>BOD Seeded Entry</Name>
          </Entry>
        </Category>
      </Registry>
      <CreateCIRID>false</CreateCIRID>
    </CreateRegistry>
"@)

Test-Case 'ProcessRegistry returns 200' { Assert-Status $processRegistry 200 }
Test-Case 'the response is an AcknowledgeRegistry BOD' {
    Assert-True ($null -ne $processRegistry.Xml) "expected XML, got: $($processRegistry.Raw)"
    Assert-Equal 'AcknowledgeRegistry' $processRegistry.Xml.DocumentElement.LocalName 'root element'
}
Test-Case 'OriginalApplicationArea is carried inside the response verb' {
    $orig = $processRegistry.Xml.DocumentElement.DataArea.Acknowledge.OriginalApplicationArea
    Assert-True ($null -ne $orig) "expected OriginalApplicationArea in the Acknowledge verb, got: $($processRegistry.Raw)"
    Assert-Equal 'test-cir.ps1' $orig.Sender.LogicalID 'echoed sender'
}
Test-Case 'the response carries the mandated BOD attributes' {
    Assert-Equal '1.2.1' $processRegistry.Xml.DocumentElement.releaseID 'releaseID'
    Assert-Equal '1.0' $processRegistry.Xml.DocumentElement.versionID 'versionID'
}
Test-Case 'no fault elements means success' {
    # AcknowledgeRegistry has no noun: its DataArea is the verb plus fault
    # elements, so a DataArea with only the verb is a success.
    $da = $processRegistry.Xml.DocumentElement.DataArea
    Assert-True ($null -eq $da.DuplicateEntryFault) "expected no faults, got: $($processRegistry.Raw)"
    Assert-True ($null -eq $da.CreateRegistryFault) 'unexpected CreateRegistryFault'
}
Test-Case 'the entry actually landed in the store' {
    $r = Invoke-Cir -Method POST -Path '/queries/registry' -Body @{
        filter = @(@{ registryFilter = @{ id = $BodRegistryId } })
    }
    Assert-Equal 1 (Get-Entries $r.Body).Count 'entry count'
}

# --- Faults travel in the Acknowledge noun ---------------------------------

$dupBod = Invoke-Bod (New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="Always"/>
    <CreateRegistry>
      <Registry>
        <ID>$BodRegistryId</ID>
        <Category>
          <ID>$CategoryId</ID>
          <CategorySourceID>$CategorySourceId</CategorySourceID>
          <Entry><IDInSource>B-1</IDInSource><SourceID>Register</SourceID></Entry>
        </Category>
      </Registry>
    </CreateRegistry>
"@)

Test-Case 'a faulting Process BOD still returns 200 with an Acknowledge' {
    Assert-Status $dupBod 200
    Assert-Equal 'AcknowledgeRegistry' $dupBod.Xml.DocumentElement.LocalName 'root element'
}
Test-Case 'the fault appears as its own element in the DataArea' {
    # Each fault is an element named for the fault, per AcknowledgeRegistry.xsd.
    $fault = $dupBod.Xml.DocumentElement.DataArea.DuplicateEntryFault
    Assert-True ($null -ne $fault) "expected a DuplicateEntryFault element, got: $($dupBod.Raw)"
}
Test-Case 'the fault detail is in a Description child, not element text' {
    # Every fault element declares an optional Description of TextType, so a
    # client reading InnerText alone would see nothing.
    $fault = $dupBod.Xml.DocumentElement.DataArea.DuplicateEntryFault
    Assert-True (-not [string]::IsNullOrWhiteSpace($fault.Description)) `
        "expected a Description child, got: $($dupBod.Raw)"
}
Test-Case 'only faults declared for this BOD are emitted' {
    $names = @($dupBod.Xml.DocumentElement.DataArea.ChildNodes |
        Where-Object { $_.LocalName -ne 'Acknowledge' } | ForEach-Object { $_.LocalName })
    $allowed = @('CreateRegistryFault', 'CreateCategoryFault', 'DuplicateEntryFault', 'DuplicatePropertyFault')
    foreach ($n in $names) {
        Assert-True ($allowed -contains $n) "'$n' is not declared for AcknowledgeRegistry"
    }
}

# --- acknowledgeCode controls whether a response exists at all -------------

$neverBod = Invoke-Bod (New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="Never"/>
    <CreateRegistry>
      <Registry>
        <ID>$BodRegistryId</ID>
        <Category>
          <ID>$CategoryId</ID>
          <CategorySourceID>$CategorySourceId</CategorySourceID>
          <Entry><IDInSource>B-1</IDInSource><SourceID>Register</SourceID></Entry>
        </Category>
      </Registry>
    </CreateRegistry>
"@)
Test-Case "acknowledgeCode='Never' suppresses the response even on fault" {
    Assert-Status $neverBod 202
    Assert-True ([string]::IsNullOrWhiteSpace($neverBod.Raw)) 'expected no body'
}

$onErrorOk = Invoke-Bod (New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="OnError"/>
    <CreateRegistry>
      <Registry>
        <ID>$BodRegistryId</ID>
        <Category>
          <ID>$CategoryId</ID>
          <CategorySourceID>$CategorySourceId</CategorySourceID>
          <Entry><IDInSource>B-2</IDInSource><SourceID>Register</SourceID></Entry>
        </Category>
      </Registry>
    </CreateRegistry>
"@)
Test-Case "acknowledgeCode='OnError' stays silent when nothing went wrong" {
    Assert-Status $onErrorOk 202
}

$unknownCode = Invoke-Bod (New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="SomeVendorCode"/>
    <CreateRegistry>
      <Registry>
        <ID>$BodRegistryId</ID>
        <Category>
          <ID>$CategoryId</ID>
          <CategorySourceID>$CategorySourceId</CategorySourceID>
          <Entry><IDInSource>B-3</IDInSource><SourceID>Register</SourceID></Entry>
        </Category>
      </Registry>
    </CreateRegistry>
"@)
Test-Case 'an unrecognised acknowledgeCode is legal and defaults to Always' {
    # ResponseCodeContentType is a union with normalizedString.
    Assert-Status $unknownCode 200
    Assert-Equal 'AcknowledgeRegistry' $unknownCode.Xml.DocumentElement.LocalName 'root element'
}

# --- GetRegistry -> ShowRegistry -------------------------------------------

$showRegistry = Invoke-Bod (New-Bod 'GetRegistry' @"
    <oa:Process/>
    <GetRegistry>
      <Filter>
        <RegistryFilter><ID>$BodRegistryId</ID></RegistryFilter>
      </Filter>
    </GetRegistry>
"@)

Test-Case 'GetRegistry returns a ShowRegistry BOD' {
    Assert-Status $showRegistry 200
    Assert-Equal 'ShowRegistry' $showRegistry.Xml.DocumentElement.LocalName 'root element'
}
Test-Case 'the Show noun is named for the response type' {
    $noun = $showRegistry.Xml.DocumentElement.DataArea.GetRegistryResponse
    Assert-True ($null -ne $noun) "expected a GetRegistryResponse noun, got: $($showRegistry.Raw)"
}
Test-Case 'the Registry graph round-trips through XML' {
    $reg = $showRegistry.Xml.DocumentElement.DataArea.GetRegistryResponse.Registry
    Assert-Equal $BodRegistryId $reg.ID 'registry ID'
    # Category declares CategorySourceID; only Entry uses a bare SourceID.
    Assert-Equal $CategorySourceId $reg.Category.CategorySourceID 'category CategorySourceID'
}

$getWithGetVerb = Invoke-Bod (New-Bod 'GetRegistry' @"
    <oa:Get><oa:Expression/></oa:Get>
    <GetRegistry>
      <Filter><RegistryFilter><ID>$BodRegistryId</ID></RegistryFilter></Filter>
    </GetRegistry>
"@)
Test-Case 'oa:Get is also accepted despite the schema declaring oa:Process' {
    # The Annex A catalogue lists the verb as Get while GetRegistry.xsd declares
    # oa:Process. Both are accepted so neither reading breaks.
    Assert-Status $getWithGetVerb 200
    Assert-Equal 'ShowRegistry' $getWithGetVerb.Xml.DocumentElement.LocalName 'root element'
}

# --- GetEntriesByCIRID -> ShowEntriesByCIRID -------------------------------

$showByCirid = Invoke-Bod (New-Bod 'GetEntriesByCIRID' @"
    <oa:Process/>
    <GetEntriesByCIRID>
      <CIRID>$CiridA</CIRID>
    </GetEntriesByCIRID>
"@)
Test-Case 'GetEntriesByCIRID returns a ShowEntriesByCIRID BOD' {
    Assert-Status $showByCirid 200
    Assert-Equal 'ShowEntriesByCIRID' $showByCirid.Xml.DocumentElement.LocalName 'root element'
}

# --- Cancel BODs have no response ------------------------------------------

$cancelEntries = Invoke-Bod (New-Bod 'CancelEntries' @"
    <oa:Cancel/>
    <DeleteEntries>
      <EntryIdentifier>
        <RegistryID>$BodRegistryId</RegistryID>
        <CategoryID>$CategoryId</CategoryID>
        <CategorySourceID>$CategorySourceId</CategorySourceID>
        <EntryIDInSource>B-2</EntryIDInSource>
        <EntrySourceID>Register</EntrySourceID>
      </EntryIdentifier>
    </DeleteEntries>
"@)
Test-Case 'a Cancel BOD returns 202 with no response BOD' {
    Assert-Status $cancelEntries 202
    Assert-True ([string]::IsNullOrWhiteSpace($cancelEntries.Raw)) 'Cancel BODs define no response'
}

# --- Unknown and malformed BODs --------------------------------------------

# A recognised BOD whose noun cannot be read must come back as a fault on the
# response BOD, not as a transport error and not as silence. A sender cannot
# tell a discarded request apart from a provider that is asleep.
$malformedNoun = Invoke-Bod (New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="Always"/>
    <CreateRegistry>
      <Registry>
        <Category>
          <ID>$CategoryId</ID>
          <CategorySourceID>$CategorySourceId</CategorySourceID>
        </Category>
      </Registry>
      <CreateCIRID>false</CreateCIRID>
    </CreateRegistry>
"@)
Test-Case 'an unreadable noun still returns an Acknowledge, not silence' {
    Assert-Status $malformedNoun 200
    Assert-Equal 'AcknowledgeRegistry' $malformedNoun.Xml.DocumentElement.LocalName 'root element'
}
Test-Case 'the fault names what could not be read' {
    $da = $malformedNoun.Xml.DocumentElement.DataArea
    $fault = $da.ChildNodes | Where-Object { $_.LocalName -like '*Fault' } | Select-Object -First 1
    Assert-True ($null -ne $fault) "expected a fault element, got: $($malformedNoun.Raw)"
    Assert-True ($fault.Description -match 'ID') `
        "expected the Description to name the missing element, got '$($fault.Description)'"
}

$unknownBod = Invoke-Bod (New-Bod 'SyncRegistry' "    <oa:Sync/>`n    <CreateRegistry/>")
Test-Case 'an unrecognised BOD returns 400' { Assert-Status $unknownBod 400 }

$malformed = Invoke-Bod '<ProcessRegistry><unclosed>'
Test-Case 'malformed XML returns 400' { Assert-Status $malformed 400 }

$bodTorn = Invoke-Cir -Method DELETE -Path "/registries/$BodRegistryId"
Test-Case 'BOD fixture tears down' { Assert-Status $bodTorn 204 }

# ---------------------------------------------------------------------------
Write-Section '13  ws-ISBM binding'

<#
    These assert the binding is wired and reports its own state honestly. They do
    not assert a round trip through a broker: that needs a live ws-ISBM provider
    with the channels created, which is an integration test rather than part of
    this suite.
#>

$isbmStatus = Invoke-Cir -Method GET -Path '/isbm/status'
Test-Case 'status returns 200' { Assert-Status $isbmStatus 200 }
Test-Case 'status reports configuration without leaking secrets' {
    Assert-Parsed $isbmStatus
    Assert-True ($null -ne $isbmStatus.Body.requestChannelUri) 'requestChannelUri should be present'
    Assert-True ($isbmStatus.Raw -notmatch '"apiKey"') 'the API key must not be echoed'
    Assert-True ($isbmStatus.Raw -notmatch '"securityToken"') 'the security token must not be echoed'
}
Test-Case 'the two channels are distinct' {
    Assert-True ($isbmStatus.Body.requestChannelUri -ne $isbmStatus.Body.publicationChannelUri) `
        'request and publication channels must differ'
}

$drain = Invoke-Cir -Method POST -Path '/isbm/drain'
Test-Case 'drain returns 200 whether or not ISBM is configured' { Assert-Status $drain 200 }

if ($isbmStatus.Body.enabled) {
    Write-Host '  (ISBM enabled - asserting live drain)' -ForegroundColor DarkGray
    Test-Case 'drain reports no errors' {
        Assert-Equal 0 @($drain.Body.errors).Count "errors: $($drain.Body.errors -join '; ')"
    }
    Test-Case 'a session is open on the request channel' {
        $kinds = @($isbmStatus.Body.sessions | ForEach-Object { $_.kind })
        Assert-True ($kinds -contains 'ProviderRequest') 'expected a ProviderRequest session'
    }
}
else {
    Write-Host '  (ISBM disabled - listener dormant, as configured)' -ForegroundColor DarkGray
    Test-Case 'a dormant listener drains to an idle report' {
        Assert-Equal 0 $drain.Body.requestsHandled 'requestsHandled'
        Assert-Equal 0 $drain.Body.publicationsHandled 'publicationsHandled'
    }
}

# ---------------------------------------------------------------------------
Write-Section '14  DeleteRegistry'

$deleteUnknown = Invoke-Cir -Method DELETE -Path '/registries/NoSuchRegistry'
Test-Case 'unknown registry returns 404' { Assert-Status $deleteUnknown 404 }
Test-Case 'names RegistryNotFoundFault' {
    Assert-Parsed $deleteUnknown
    Assert-Equal 'RegistryNotFoundFault' $deleteUnknown.Body.title 'title'
}

$deleted = Invoke-Cir -Method DELETE -Path "/registries/$RegistryId"
Test-Case 'returns 204' { Assert-Status $deleted 204 }

# ---------------------------------------------------------------------------
Write-Section '15  Verify cascade'

$after = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridA"
Test-Case 'delete cascaded to entries' {
    Assert-Equal 0 (Get-Entries $after.Body).Count 'entry count'
}

$afterB = Invoke-Cir -Method GET -Path "/entries?cirid=$CiridB"
Test-Case 'delete cascaded to properties owner' {
    Assert-Equal 0 (Get-Entries $afterB.Body).Count 'entry count'
}

# ---------------------------------------------------------------------------

$total = $script:Passed + $script:Failed
Write-Host "`n$('-' * 60)"
Write-Host "Passed: $($script:Passed) / $total" -ForegroundColor Green

if ($script:Failed -gt 0) {
    Write-Host "Failed: $($script:Failed)" -ForegroundColor Red
    Write-Host ''
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'All functional tests passed.' -ForegroundColor Green
exit 0
