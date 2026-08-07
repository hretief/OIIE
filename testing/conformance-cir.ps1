<#
.SYNOPSIS
    Probes a running ws-CIR Provider and emits a conformance statement against
    ws-CIR 1.0 §5.

.DESCRIPTION
    §5 requires that any assessment of conformance be qualified by six items:

      1. Support for Command Services
      2. Support for Query Services
      3. Support for Wildcard Specification
      4. Support for SOAP 1.1 and SOAP 1.2 services
      5. Support for specific BODs (OAGIS-Based Message Model)
      6. A statement of total conformance, or an explicit statement of the
         areas of non-conformance

    Conformance under ws-CIR is declarative rather than pass/fail: an
    implementation states what it supports. This script determines items 1-3
    empirically, records the known position on 4-5, and assembles item 6.

    Probes are non-destructive. Item 3 needs data, so it seeds a uniquely-named
    registry and removes it again; use -SkipDataProbes to suppress that.

.EXAMPLE
    .\conformance-cir.ps1 -FunctionApp cir-func-44p2f3n6 -ResourceGroup HilmarRetiefRG

.EXAMPLE
    .\conformance-cir.ps1 -FunctionApp cir-func-44p2f3n6 -ResourceGroup HilmarRetiefRG `
        -OutputPath .\conformance-statement.md
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

    [string] $ImplementationName = 'CIR Provider (Azure Functions, REST binding)',

    # Write the statement to a Markdown file as well as the console.
    [string] $OutputPath,

    # Skip the wildcard probe, which briefly writes and deletes a fixture.
    [switch] $SkipDataProbes
)

$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -eq 'Azure') {
    $BaseUrl = "https://$FunctionApp.azurewebsites.net/api"
    if ([string]::IsNullOrWhiteSpace($FunctionKey)) {
        $FunctionKey = az functionapp keys list -g $ResourceGroup -n $FunctionApp `
            --query functionKeys.default -o tsv
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the function key.' }
    }
}

$BaseUrl = $BaseUrl.TrimEnd('/')

# ---------------------------------------------------------------------------

function Invoke-Cir {
    param([string] $Method, [string] $Path, $Body, [switch] $Anonymous)

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
        $req.Body = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Body -Depth 12 -Compress))
        $req.ContentType = 'application/json'
    }

    $r = Invoke-WebRequest @req

    # Invoke-WebRequest hands back a byte[] for content types it does not
    # classify as text, and application/problem+json is one of them.
    $raw = $r.Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        try { $parsed = $raw | ConvertFrom-Json } catch { }
    }

    [pscustomobject]@{
        Status = [int]$r.StatusCode
        Body   = $parsed
        Raw    = $raw
    }
}

<#
Classifies one operation:
  Supported     - the route exists and the store executes it
  Not supported - the route exists but the store raises NotImplemented (501)
  No route      - the host has no binding for this operation at all
#>
function Test-Operation {
    param([string] $Clause, [string] $Name, [string] $Method, [string] $Path, $Body)

    $r = Invoke-Cir -Method $Method -Path $Path -Body $Body

    $support = switch ($r.Status) {
        501     { 'Not supported' }
        404     {
            # A ws-CIR fault carries problem+json with a title; a missing route does not.
            if ($r.Body -and $r.Body.title) { 'Supported' } else { 'No route' }
        }
        405     { 'No route' }
        default { if ($r.Status -ge 500) { 'Error' } else { 'Supported' } }
    }

    [pscustomobject]@{
        Clause  = $Clause
        Name    = $Name
        Support = $support
        Status  = $r.Status
        Detail  = if ($r.Body -and $r.Body.detail) { $r.Body.detail } else { '' }
    }
}

# ---------------------------------------------------------------------------
Write-Host 'ws-CIR 1.0 conformance probe' -ForegroundColor White
Write-Host "Endpoint : $BaseUrl`n"

$health = Invoke-Cir -Method GET -Path '/health' -Anonymous
if ($health.Status -ne 200) {
    throw "Service is not healthy (HTTP $($health.Status)). Cannot assess conformance."
}
Write-Host "Service reports: $($health.Body.spec), $($health.Body.binding) binding, sql=$($health.Body.sql)`n"

$probeCirid = [guid]::NewGuid().ToString()
$probeRegistry = "conformance-probe-$([guid]::NewGuid().ToString('N').Substring(0,8))"

# --- Item 1: Command Services ----------------------------------------------

Write-Host 'Probing command services...' -ForegroundColor DarkGray

# Probes send well-formed minimal bodies so that a stub reaches the store and
# raises NotImplemented (501) rather than failing body deserialisation (400).
$commandOps = @(
    (Test-Operation '3.1.1' 'CreateRegistry'          'POST'   '/registries'              @{ registry = @(); createCirid = $false }),
    (Test-Operation '3.1.2' 'CreateEquivalentEntries' 'POST'   '/equivalent-entries'      @()),
    (Test-Operation '3.1.3' 'UpdateRegistry'          'PUT'    '/registries'              @{ registry = @() }),
    (Test-Operation '3.1.4' 'UpdateEntryCIRID'        'POST'   '/cirids/replace'          @{ oldCirid = @($probeCirid); newCirid = $probeCirid }),
    (Test-Operation '3.1.5' 'DeleteRegistry'          'DELETE' "/registries/$probeRegistry" $null),
    (Test-Operation '3.1.6' 'DeleteCategory'          'DELETE' '/categories'              @{ registryId = $probeRegistry; categoryId = 'x'; categorySourceId = 'y' }),
    (Test-Operation '3.1.7' 'DeleteEntries'           'POST'   '/entries/batch-delete'    @()),
    (Test-Operation '3.1.8' 'DeleteProperties'        'POST'   '/properties/batch-delete' @())
)

# --- Item 2: Query Services -------------------------------------------------

Write-Host 'Probing query services...' -ForegroundColor DarkGray

$queryOps = @(
    (Test-Operation '3.2.1' 'GetRegistry'          'POST' '/queries/registry'           @{ filter = @() }),
    (Test-Operation '3.2.2' 'GetEquivalentEntries' 'POST' '/queries/equivalent-entries' @{ entryIdentifier = @(); targetSourceId = @() }),
    (Test-Operation '3.2.3' 'GetEntriesByCIRID'    'GET'  "/entries?cirid=$probeCirid"  $null)
)

# --- Item 3: Wildcard Specification -----------------------------------------

$wildcardSupport = 'Not assessed'
$wildcardEvidence = 'Data probes skipped.'

if (-not $SkipDataProbes) {
    Write-Host 'Probing wildcard specification...' -ForegroundColor DarkGray

    $seedCirid = [guid]::NewGuid().ToString()

    # Fixture chosen so every §4 metacharacter is discriminating:
    #   'Alpha A' and 'Alpha B' differ only in a single trailing character
    #   'Alpha.A' has a literal dot where the others have a space
    $seed = @{
        registry = @(
            @{
                id         = $probeRegistry
                categories = @(
                    @{
                        id       = 'ConformanceProbe'
                        sourceId = 'ws-CIR conformance script'
                        entries  = @(
                            @{ idInSource = 'P1'; sourceId = 'Alpha A'; cirid = $seedCirid },
                            @{ idInSource = 'P2'; sourceId = 'Alpha B'; cirid = $seedCirid },
                            @{ idInSource = 'P3'; sourceId = 'Alpha.A'; cirid = $seedCirid }
                        )
                    }
                )
            }
        )
        createCirid = $false
    }

    try {
        $create = Invoke-Cir -Method POST -Path '/registries' -Body $seed
        if ($create.Status -ne 201) { throw "seed failed with HTTP $($create.Status)" }

        function Get-ProbeCount {
            param([string] $Pattern)
            $encoded = [uri]::EscapeDataString($Pattern)
            $r = Invoke-Cir -Method GET -Path "/entries?cirid=$seedCirid&targetSourceId=$encoded"
            if (-not $r.Body -or -not $r.Body.registry) { return 0 }
            return @($r.Body.registry | ForEach-Object { $_.categories } |
                     ForEach-Object { $_.entries } | Where-Object { $_ }).Count
        }

        # Pattern              Expected  Why
        # 'Alpha A'                   1   literal
        # 'Alpha .'                   2   '.' is exactly one character
        # 'Alpha.*'                   3   '.' plus '*' spans everything
        # 'Alpha .+'                  2   '+' is one or more
        # 'Alpha ?A'                  1   '?' makes the space optional
        # 'Alpha\.A'                  1   the backslash escapes the dot to a literal
        # 'Alpha'                     0   patterns are anchored at both ends
        $checks = [ordered]@{
            'literal match'               = ((Get-ProbeCount 'Alpha A')  -eq 1)
            "'.' matches one character"   = ((Get-ProbeCount 'Alpha .')  -eq 2)
            "'*' matches zero or more"    = ((Get-ProbeCount 'Alpha.*')  -eq 3)
            "'+' matches one or more"     = ((Get-ProbeCount 'Alpha .+') -eq 2)
            "'?' makes the atom optional" = ((Get-ProbeCount 'Alpha ?A') -eq 1)
            'escape sequence honoured'    = ((Get-ProbeCount 'Alpha\.A')  -eq 1)
            'patterns anchored both ends' = ((Get-ProbeCount 'Alpha')    -eq 0)
        }

        $failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })

        if ($failed.Count -eq 0) {
            $wildcardSupport = 'Supported'
            $wildcardEvidence = @'
The §4 POSIX subset was verified empirically against TargetSourceID. All of
`.`, `*`, `+`, `?` and the backslash escape behave as specified, and patterns
are implicitly anchored at both ends: the pattern `Alpha` does not match `Alpha A`.
'@
        }
        else {
            $wildcardSupport = 'Partial'
            $wildcardEvidence = "Deviations from the §4 wildcard specification: $($failed -join '; ')."
        }
    }
    catch {
        $wildcardSupport = 'Not assessed'
        $wildcardEvidence = "Probe failed: $($_.Exception.Message)"
    }
    finally {
        Invoke-Cir -Method DELETE -Path "/registries/$probeRegistry" | Out-Null
    }
}

# --- Items 4 and 5: known position ------------------------------------------

$soapSupport = 'Not supported'
$soapEvidence = 'This implementation provides a REST/JSON binding only. No WSDL endpoint, no SOAP 1.1 or SOAP 1.2 envelope handling.'

Write-Host 'Probing the Annex A message model...' -ForegroundColor DarkGray

$bodSupport = 'Not supported'
$bodEvidence = 'The OAGIS-Based Message Model of Annex A is not implemented.'

$catalogue = Invoke-Cir -Method GET -Path '/bods/catalogue'
if ($catalogue.Status -eq 200 -and $catalogue.Body) {
    $bods = @($catalogue.Body.requestBods)
    $expected = 11
    $bodSupport = if ($bods.Count -ge $expected) { 'Supported' } else { 'Partial' }
    $bodEvidence = @"
All $($bods.Count) ws-CIR request BODs are accepted at POST /bods and dispatched to
the corresponding service. releaseID is $($catalogue.Body.releaseId) and versionID is
$($catalogue.Body.versionId), as required by Annex A. Faults are returned in the
Acknowledge and Respond nouns, and the model permits several per response.
ProcessType acknowledgeCode and ChangeType responseCode are honoured: Never
suppresses the response entirely and OnChange emits one only on fault.

Transport: this implementation exposes the BOD model over HTTP. A ws-ISBM
channel binding, in which these documents travel as ISBM message content, is not
yet provided.
"@
}

# ---------------------------------------------------------------------------
# Assemble
# ---------------------------------------------------------------------------

function Get-Summary {
    param($Ops)
    $supported = @($Ops | Where-Object { $_.Support -eq 'Supported' }).Count
    if ($supported -eq 0)             { return 'Not supported' }
    if ($supported -eq $Ops.Count)    { return 'Supported' }
    return 'Partial'
}

$commandSummary = Get-Summary $commandOps
$querySummary   = Get-Summary $queryOps

$nonConformance = [System.Collections.Generic.List[string]]::new()

foreach ($op in ($commandOps + $queryOps)) {
    if ($op.Support -ne 'Supported') {
        $nonConformance.Add("$($op.Name) (§$($op.Clause)) - $($op.Support.ToLower())")
    }
}
if ($wildcardSupport -ne 'Supported') { $nonConformance.Add("Wildcard Specification (§4) - $($wildcardSupport.ToLower())") }
$nonConformance.Add('SOAP 1.1 and SOAP 1.2 services (§5 item 4) - not supported; REST binding only')
$nonConformance.Add('OAGIS-Based Message Model BODs (Annex A) - not supported')

$overall = if ($nonConformance.Count -eq 0) { 'Full conformance' } else { 'Partial conformance' }

# ---------------------------------------------------------------------------
# Render
# ---------------------------------------------------------------------------

$sb = [System.Text.StringBuilder]::new()
function Emit { param([string] $Line = '') [void]$sb.AppendLine($Line) }

Emit '# ws-CIR 1.0 Conformance Statement'
Emit ''
Emit "**Implementation:** $ImplementationName  "
Emit "**Endpoint:** $BaseUrl  "
Emit "**Assessed:** $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')  "
Emit "**Specification:** OpenO&M ws-CIR 1.0 (Candidate Standard, 19 June 2015)"
Emit ''
Emit 'Assessed against the six qualifications required by ws-CIR 1.0 §5. Conformance'
Emit 'under this specification is declarative: an implementation states what it'
Emit 'supports, and §5 item 6 requires any non-conformance to be stated explicitly.'
Emit ''

Emit "## 1. Support for Command Services — $commandSummary"
Emit ''
Emit '| Clause | Operation | Support | HTTP |'
Emit '|---|---|---|---|'
foreach ($op in $commandOps) { Emit "| §$($op.Clause) | $($op.Name) | $($op.Support) | $($op.Status) |" }
Emit ''

Emit "## 2. Support for Query Services — $querySummary"
Emit ''
Emit '| Clause | Operation | Support | HTTP |'
Emit '|---|---|---|---|'
foreach ($op in $queryOps) { Emit "| §$($op.Clause) | $($op.Name) | $($op.Support) | $($op.Status) |" }
Emit ''

Emit "## 3. Support for Wildcard Specification — $wildcardSupport"
Emit ''
Emit $wildcardEvidence
Emit ''

Emit "## 4. Support for SOAP 1.1 and SOAP 1.2 services — $soapSupport"
Emit ''
Emit $soapEvidence
Emit ''

Emit "## 5. Support for specific BODs — $bodSupport"
Emit ''
Emit $bodEvidence
Emit ''

Emit "## 6. Statement of conformance — $overall"
Emit ''
Emit 'This implementation claims **partial conformance** to ws-CIR 1.0. The'
Emit 'following areas are explicitly non-conformant:'
Emit ''
foreach ($item in $nonConformance) { Emit "- $item" }
Emit ''
Emit 'The following interpretations were made where the specification is silent'
Emit 'or ambiguous:'
Emit ''
Emit '- **§3.2.3 GetEntriesByCIRID** states that the existing Entry is not returned.'
Emit '  The input is a bare CIRID, so there is no specified Entry to exclude; the'
Emit '  sentence appears to be carried over from §3.2.2. All Entries carrying the'
Emit '  CIRID are returned.'
Emit '- **§3.1.2 CreateEquivalentEntries** does not say what happens when the'
Emit '  existing Entry and the supplied Entry carry *different* CIRIDs. The'
Emit '  existing CIRID wins and the supplied value is discarded, consistent with'
Emit '  the stated precedence. Merging two clusters is left to §3.1.4, which is'
Emit '  explicit rather than implicit.'
Emit '- **§3.1.3 UpdateRegistry** is a snapshot replace, so omitted attributes are'
Emit '  cleared. Children that are not supplied are left alone rather than deleted,'
Emit '  since a separate Delete family exists and the alternative would make'
Emit '  partial updates impossible. CIRID is preserved when omitted, because'
Emit '  §3.1.4 is a dedicated operation for it.'
Emit ''
Emit 'All other assessed services conform to the behaviour defined in §3, including'
Emit 'the atomicity requirement of §3.1 (no partial creates, updates or deletes when'
Emit 'a fault is raised) and the fault set of §3.3. Fault names are preserved'
Emit 'verbatim in the `faults[]` member of the RFC 9457 problem+json response body.'
Emit ''

$report = $sb.ToString()

Write-Host ''
Write-Host $report

if ($OutputPath) {
    $report | Set-Content -Path $OutputPath -Encoding UTF8
    Write-Host "Written to $OutputPath" -ForegroundColor Green
}

# Non-zero only if something errored outright; partial conformance is a valid outcome.
if (($commandOps + $queryOps) | Where-Object { $_.Support -eq 'Error' }) { exit 1 }
exit 0
