<#
.SYNOPSIS
    End-to-end test of the OIIE Sandbox across ws-ISBM and ws-CIR.

.DESCRIPTION
    Checks preconditions before every step rather than only asserting outcomes.
    Most failures in this system are not wrong logic — they are a channel that does
    not exist, a session that died with it, a subscriber that opened after the
    publication, or a queue holding messages from an earlier run. Those all present
    as "nothing happened", and asserting on the outcome alone cannot tell them apart.

    Every failure is attributed to SANDBOX, ISBM or CIR, with the specific evidence
    that points there. The aim is that a failure can be handed to whoever owns that
    component as a description of what was observed, not as "it does not work".

.EXAMPLE
    .\test-sandbox.ps1 -ResourceGroup HilmarRetiefRG `
        -CirApp cir-func-44p2f3n6 -IsbmApp isbm-func-44p2f3n6dv7p4

.EXAMPLE
    .\test-sandbox.ps1 -SkipCir -Detailed
#>
[CmdletBinding()]
param(
    [string] $SandboxUrl = 'https://localhost:7180',

    [string] $ResourceGroup = 'HilmarRetiefRG',
    [string] $CirApp = 'cir-func-44p2f3n6',
    [string] $IsbmApp = 'isbm-func-44p2f3n6dv7p4',

    [string] $CirRequestChannel = '/OIIE/CIR/Request',
    [string] $CirPublicationChannel = '/OIIE/CIR/Publication',

    # Stop after the handover chain, before anything touches the registry.
    [switch] $SkipCir,

    # Tear everything down first, including the CIR provider's channel.
    [switch] $DayZero,

    # Required against a deployed instance, where admin endpoints are gated.
    # Retrieve with:
    #   az keyvault secret show --vault-name mndot --name sandbox-admin-key-demo --query value -o tsv
    [string] $AdminKey,

    [switch] $Detailed
)

$ErrorActionPreference = 'Stop'

# Self-signed development certificate.
if ($SandboxUrl -like 'https://localhost*') {
    $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
    $PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true
}

$script:Passed = 0
$script:Failed = 0
$script:Findings = [System.Collections.Generic.List[object]]::new()
$script:Concerns = [System.Collections.Generic.List[object]]::new()

# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------

function Write-Phase { param([string] $Name) Write-Host "`n$Name" -ForegroundColor Cyan }

function Pass { param([string] $Name, [string] $Detail)
    $script:Passed++
    Write-Host "  PASS  $Name" -ForegroundColor Green -NoNewline
    if ($Detail) { Write-Host "  $Detail" -ForegroundColor DarkGray } else { Write-Host '' }
}

<#
    Records a failure against the component that owns it.

    Owner is the point of this script. "The registration timed out" is true of the
    Sandbox, ISBM and CIR simultaneously and tells none of their owners anything.
    "Three ProcessRegistry requests are queued unread on /OIIE/CIR/Request under
    topic ws-CIR, and the CIR provider reports no open session" tells exactly one
    of them what to look at.
#>
function Fail {
    param(
        [string] $Name,
        [ValidateSet('SANDBOX', 'ISBM', 'CIR', 'ENVIRONMENT')][string] $Owner,
        [string] $Observed,
        [string] $Suggests
    )

    $script:Failed++
    Write-Host "  FAIL  $Name" -ForegroundColor Red
    Write-Host "        owner    : $Owner" -ForegroundColor Yellow
    Write-Host "        observed : $Observed" -ForegroundColor Yellow
    if ($Suggests) { Write-Host "        suggests : $Suggests" -ForegroundColor Yellow }

    $script:Findings.Add([pscustomobject]@{
            Check = $Name; Owner = $Owner; Observed = $Observed; Suggests = $Suggests
        })
}

function Info { param([string] $Message) Write-Host "        $Message" -ForegroundColor DarkGray }

<#
    Records something worth an owner's attention that is not a failure of the
    behaviour under test.

    Separate from Fail because conflating the two costs information in both
    directions: a red run that is really an environment note gets ignored, and a
    genuine defect hidden behind one gets missed. A concern is reported with the
    same owner and evidence as a failure, but does not fail the run.
#>
function Concern {
    param(
        [string] $Name,
        [ValidateSet('SANDBOX', 'ISBM', 'CIR', 'ENVIRONMENT')][string] $Owner,
        [string] $Observed,
        [string] $Suggests
    )

    Write-Host "  NOTE  $Name" -ForegroundColor DarkYellow
    Write-Host "        owner    : $Owner" -ForegroundColor DarkGray
    Write-Host "        observed : $Observed" -ForegroundColor DarkGray
    if ($Suggests) { Write-Host "        suggests : $Suggests" -ForegroundColor DarkGray }

    $script:Concerns.Add([pscustomobject]@{
            Check = $Name; Owner = $Owner; Observed = $Observed; Suggests = $Suggests
        })
}

# ---------------------------------------------------------------------------
# HTTP
# ---------------------------------------------------------------------------

function Invoke-Sandbox {
    param([string] $Method, [string] $Path, $Body)

    $req = @{
        Method             = $Method
        Uri                = "$SandboxUrl$Path"
        SkipHttpErrorCheck = $true
        ErrorAction        = 'Stop'
    }

    if ($AdminKey) { $req.Headers = @{ 'x-sandbox-admin-key' = $AdminKey } }

    if ($null -ne $Body) {
        $req.Body = ConvertTo-Json -InputObject $Body -Depth 12
        $req.ContentType = 'application/json'
    }

    if ($Detailed) { Write-Host "        -> $Method $Path" -ForegroundColor DarkGray }

    try { $response = Invoke-WebRequest @req }
    catch {
        return [pscustomobject]@{ Status = 0; Body = $null; Raw = $_.Exception.Message }
    }

    $raw = $response.Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($raw)) { try { $parsed = $raw | ConvertFrom-Json } catch { } }
    if ($Detailed -and $raw) { Write-Host "        <- $raw" -ForegroundColor DarkGray }

    return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $parsed; Raw = $raw }
}

function Invoke-Cir {
    param([string] $Method, [string] $Path, $Body)

    if (-not $script:CirKey) { return [pscustomobject]@{ Status = 0; Body = $null; Raw = 'no key' } }

    $req = @{
        Method             = $Method
        Uri                = "https://$CirApp.azurewebsites.net/api$Path"
        Headers            = @{ 'x-functions-key' = $script:CirKey }
        SkipHttpErrorCheck = $true
        ErrorAction        = 'Stop'
    }

    if ($null -ne $Body) {
        $req.Body = ConvertTo-Json -InputObject $Body -Depth 12
        $req.ContentType = 'application/json'
    }

    try { $response = Invoke-WebRequest @req }
    catch {
        return [pscustomobject]@{ Status = 0; Body = $null; Raw = $_.Exception.Message }
    }

    $raw = $response.Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($raw)) { try { $parsed = $raw | ConvertFrom-Json } catch { } }

    return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $parsed; Raw = $raw }
}

<#
    Waits for a condition rather than sleeping a fixed time.

    The outbox drains every 2s and the inbox polls every 3s, so a fixed sleep is
    either too short and flaky or too long and slow. Worse, a fixed sleep that
    passes tells you nothing about how close to the edge it was.
#>
function Wait-For {
    param([scriptblock] $Condition, [int] $TimeoutSeconds = 45, [string] $What = 'condition')

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $elapsed = 0

    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) { return @{ Ok = $true; Seconds = $elapsed; Result = $result } }
        Start-Sleep -Seconds 1
        $elapsed++
    }

    return @{ Ok = $false; Seconds = $elapsed; Result = $null }
}

# ---------------------------------------------------------------------------

Write-Host "`nOIIE Sandbox end-to-end" -ForegroundColor White
Write-Host "Sandbox : $SandboxUrl"
Write-Host "ISBM    : $IsbmApp"
Write-Host "CIR     : $CirApp"

$script:CirKey = $null
if (-not $SkipCir) {
    $script:CirKey = az functionapp keys list -g $ResourceGroup -n $CirApp --query functionKeys.default -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) { $script:CirKey = $null }
}

# ===========================================================================
Write-Phase '01  Environment preconditions'
# ===========================================================================

$health = Invoke-Sandbox GET '/health/participants'

if ($health.Status -ne 200) {
    Fail 'Sandbox is running' 'ENVIRONMENT' `
        "GET /health/participants returned $($health.Status): $($health.Raw)" `
        'Start SimHost. Nothing below can run.'
    Write-Host "`nAborted: the Sandbox is not reachable." -ForegroundColor Red
    return
}

Pass 'Sandbox is running'

# A deployed instance with open admin endpoints is a destructive API on a public
# URL. Worth failing the run over, not warning about.
if ($SandboxUrl -notlike '*localhost*') {
    if (-not $health.Body.adminKeyRequired) {
        Fail 'admin endpoints are protected' 'ENVIRONMENT' `
            'adminKeyRequired is false on a non-local instance' `
            'Anyone who finds this URL can reset the databases and delete channels. Set Sandbox__AdminKey.'
    }
    elseif (-not $AdminKey) {
        Fail 'admin key supplied' 'ENVIRONMENT' `
            'the instance requires an admin key and none was given' `
            'Pass -AdminKey. Retrieve it from Key Vault: sandbox-admin-key-{environment}.'
        Write-Host "`nAborted: admin endpoints are gated." -ForegroundColor Red
        return
    }
    else { Pass 'admin endpoints are protected' }
}

$participants = @($health.Body.participants)
if ($participants.Count -lt 3) {
    Fail 'three participants configured' 'SANDBOX' `
        "found $($participants.Count): $($participants.participantId -join ', ')" `
        'Personalities/ must contain eng, reg-location and mms.'
}
else { Pass 'three participants configured' ($participants.participantId -join ', ') }

if (-not $health.Body.isbmConfigured) {
    Fail 'ISBM binding configured' 'SANDBOX' 'isbmConfigured is false' `
        'No personality declares an isbm.baseUrl.'
}
else { Pass 'ISBM binding configured' }

if (-not $health.Body.storageConfigured) {
    Info 'storageConfigured is false: BOD payload bodies will not be retained'
}

$secrets = Invoke-Sandbox GET '/health/secrets'
$placeholder = $secrets.Body.database -match 'REPLACE'
$missing = @($secrets.Body.secrets | Where-Object { -not $_.found })

if ($placeholder) {
    Fail 'configuration is filled in' 'ENVIRONMENT' `
        "Sandbox:Database is '$($secrets.Body.database)'" `
        'appsettings.Development.json still holds the template placeholder.'
}
elseif ($missing.Count -gt 0) {
    Fail 'Key Vault secrets resolve' 'ENVIRONMENT' `
        "missing: $($missing.secret -join ', ')" `
        'Run deploy/provision.ps1, or check the signed-in identity can read the vault.'
}
else { Pass 'configuration and secrets resolve' $secrets.Body.database }

$sql = Invoke-Sandbox GET '/health/sql'
$disconnected = @($sql.Body | Where-Object { -not $_.connected })
$users = @($sql.Body | ForEach-Object { $_.user } | Select-Object -Unique)

if ($disconnected.Count -gt 0) {
    Fail 'each participant connects as its own user' 'ENVIRONMENT' `
        "$($disconnected[0].participantId): $($disconnected[0].error)" `
        'A "Login failed" here is usually the wrong database, not the wrong password: contained users exist only inside their own database.'
}
elseif ($users.Count -lt @($sql.Body).Count) {
    Fail 'participants are isolated' 'SANDBOX' `
        "$($users.Count) distinct SQL user(s) across $(@($sql.Body).Count) participants" `
        'Participants are sharing a login, so the schema grants are not in force.'
}
else { Pass 'each participant connects as its own user' ($users -join ', ') }

if ($script:Failed -gt 0) {
    Write-Host "`nAborted: environment preconditions failed." -ForegroundColor Red
    $script:Findings | Format-List
    return
}

# ===========================================================================
Write-Phase '02  Reset to a known state'
# ===========================================================================

$resetPath = if ($DayZero) { '/admin/reset/day-zero' } else { '/admin/reset' }
$reset = Invoke-Sandbox POST $resetPath

if ($reset.Status -ne 200) {
    Fail 'reset succeeds' 'SANDBOX' "$resetPath returned $($reset.Status): $($reset.Raw)" ''
    return
}

Pass 'reset succeeds' "$($reset.Body.sessionsClosed) session(s) closed"

# Fixtures live in the tables reset drops. Without them nothing classifies, and the
# stewardship assertions later would fail for a reason unrelated to messaging.
$engFixtures = $reset.Body.participants | Where-Object { $_.participantId -eq 'eng' }
$locFixtures = $reset.Body.participants | Where-Object { $_.participantId -eq 'reg-location' }

if ($engFixtures.classes -lt 3) {
    Fail 'ENG reference data loaded' 'SANDBOX' "$($engFixtures.classes) class(es)" `
        'Personalities/eng/Fixtures/classes.yaml did not load.'
}
elseif ($locFixtures.classes -ge $engFixtures.classes) {
    Fail 'fixtures are asymmetric' 'SANDBOX' `
        "eng has $($engFixtures.classes), reg-location has $($locFixtures.classes)" `
        'REG-LOCATION must hold FEWER classes than ENG, or graceful degradation cannot be observed.'
}
else {
    Pass 'reference data loaded, asymmetric as intended' `
        "eng $($engFixtures.classes) / reg-location $($locFixtures.classes)"
}

# The CIR registry lives in the provider's own database, which the Sandbox reset
# does not touch. Without clearing it, every run after the first re-registers
# identifiers that already exist and gets a DuplicateEntryFault — so the test could
# only ever pass once, and would look like a provider fault rather than a dirty
# fixture.
if (-not $SkipCir -and $script:CirKey) {
    $drop = Invoke-Cir DELETE '/registries/OIIE-SANDBOX'

    # 204 deleted, 404 nothing to delete. Both are the desired end state.
    if ($drop.Status -in 204, 404) {
        Pass 'CIR registry cleared' $(if ($drop.Status -eq 204) { 'deleted' } else { 'was already absent' })
    }
    else {
        Fail 'CIR registry cleared' 'CIR' `
            "DELETE /registries/OIIE-SANDBOX returned $($drop.Status): $($drop.Raw)" `
            'Registration will report duplicates from the previous run rather than testing a first registration.'
    }
}

$channels = Invoke-Sandbox GET '/admin/isbm/channels'

if ($channels.Status -ne 200) {
    Fail 'ISBM is reachable' 'ISBM' "GET channels returned $($channels.Status): $($channels.Raw)" `
        'The Sandbox cannot reach the ISBM provider.'
    return
}

$channelUris = @($channels.Body | ForEach-Object { $_.channelUri })

foreach ($required in @(
        @{ Uri = '/OIIE-SANDBOX/Enterprise/Site/Eng'; Type = 0; Owner = 'SANDBOX' },
        @{ Uri = '/OIIE-SANDBOX/Enterprise/Site/OandM'; Type = 0; Owner = 'SANDBOX' },
        @{ Uri = $CirRequestChannel; Type = 1; Owner = 'CIR' })) {

    $found = $channels.Body | Where-Object { $_.channelUri -eq $required.Uri }

    if (-not $found) {
        Fail "channel exists: $($required.Uri)" $required.Owner 'not present on the ISBM provider' `
            'Run POST /admin/isbm/channels/ensure.'
    }
    elseif ($found.channelType -ne $required.Type) {
        # A request BOD on a publication channel is never delivered to a provider
        # session, and nothing reports an error.
        Fail "channel type: $($required.Uri)" $required.Owner `
            "type is $($found.channelType), expected $($required.Type)" `
            'Delete and recreate it with the correct type.'
    }
    else { Pass "channel exists: $($required.Uri)" }
}

# ===========================================================================
Write-Phase '03  CIR provider readiness'
# ===========================================================================

if ($SkipCir -or -not $script:CirKey) {
    Info 'skipped'
}
else {
    $status = Invoke-Cir GET '/isbm/status'

    if ($status.Status -ne 200) {
        Fail 'CIR provider responds' 'CIR' "GET /isbm/status returned $($status.Status)" `
            'The Function App may be stopped or the key wrong.'
    }
    elseif (-not $status.Body.enabled) {
        Fail 'CIR ISBM binding enabled' 'CIR' 'enabled is false' `
            'Isbm__Enabled is off, so the provider will never poll.'
    }
    else {
        Pass 'CIR provider responds' "topics: $($status.Body.topics -join ', ')"

        if ($status.Body.requestChannelUri -ne $CirRequestChannel) {
            Fail 'CIR listens on the expected channel' 'CIR' `
                "provider: $($status.Body.requestChannelUri); sandbox posts to: $CirRequestChannel" `
                'The two must match. Change cir.channelUri in the personality files, or Isbm__RequestChannelUri on the provider.'
        }
        else { Pass 'CIR listens on the channel the Sandbox posts to' }

        # Sessions are opened lazily on the first drain, so an empty list here is
        # normal rather than a fault — but only until a drain has been asked for.
        $drain = Invoke-Cir POST '/isbm/drain'

        if ($drain.Status -ne 200) {
            Fail 'CIR opens its sessions' 'CIR' "POST /isbm/drain returned $($drain.Status): $($drain.Raw)" `
                'The provider could not open a session on its own channel.'
        }
        elseif (@($drain.Body.errors).Count -gt 0) {
            Fail 'CIR opens its sessions' 'CIR' "drain errors: $($drain.Body.errors -join '; ')" `
                'Session open or read failed on the provider side.'
        }
        else {
            $status = Invoke-Cir GET '/isbm/status'
            $kinds = @($status.Body.sessions | ForEach-Object { $_.kind })

            if ($kinds -notcontains 'ProviderRequest') {
                Fail 'CIR holds a ProviderRequest session' 'CIR' `
                    "sessions after drain: $(if ($kinds) { $kinds -join ', ' } else { 'none' })" `
                    'A drain completed without error yet opened no request session. Nothing the Sandbox posts will ever be read.'
            }
            else {
                # Newest first: a provider may hold several, and the oldest is not
                # the one it is using.
                $session = @($status.Body.sessions |
                    Where-Object { $_.kind -eq 'ProviderRequest' } |
                    Sort-Object { [datetime]$_.openedUtc } -Descending)[0]

                $age = ((Get-Date).ToUniversalTime() - [datetime]$session.openedUtc).TotalMinutes

                # Age is the most diagnostic field available. A session older than the
                # last channel rebuild is dead, and its owner does not know: the read
                # fails with a Session fault that a poll loop typically swallows.
                # Age alone proves nothing: /admin/reset deliberately leaves the CIR
                # channel alone, so a long-lived session there is expected and healthy.
                # Only a channel rebuild kills it, and only day zero rebuilds it.
                if ($DayZero -and $age -gt 5) {
                    Fail 'CIR session survived day zero' 'CIR' `
                        "ProviderRequest session opened $([int]$age) minutes ago, before this run rebuilt the channel" `
                        'Deleting a channel destroys its sessions. The provider is polling an id the broker no longer knows. POST /isbm/reset then /isbm/drain.'
                }
                else {
                    Pass 'CIR holds a ProviderRequest session' "$([int]$age)m old"
                    if ($age -gt 60) {
                        Info 'session predates this run; healthy unless its channel has been rebuilt since'
                    }
                }
            }
        }
    }
}

# ===========================================================================
Write-Phase '04  Subscriptions are established'
# ===========================================================================

# This has to pass before anything is published. A subscription receives only what
# arrives after it opens, so publishing into a channel with no open subscription
# loses the message silently — and the symptom is an empty archive, identical to a
# provider that is not delivering.
$subscribed = Wait-For -TimeoutSeconds 30 -What 'subscriptions' -Condition {
    $sessions = Invoke-Sandbox GET '/health/isbm/sessions'
    $gaps = @($sessions.Body | Where-Object { @($_.missingSubscriptions).Count -gt 0 })
    if ($gaps.Count -eq 0) { return $sessions.Body }
    return $null
}

if (-not $subscribed.Ok) {
    $sessions = Invoke-Sandbox GET '/health/isbm/sessions'

    foreach ($participant in $sessions.Body | Where-Object { @($_.missingSubscriptions).Count -gt 0 }) {
        $owner = if ($participant.error) { 'SANDBOX' } else { 'SANDBOX' }

        Fail "subscription open: $($participant.participantId)" $owner `
            "no session on $($participant.missingSubscriptions -join ', ')$(if ($participant.error) { " (error: $($participant.error))" })" `
            'The inbox pump has not opened it. Anything published before it does is lost, so publishing now would fail for a reason unrelated to the provider.'
    }

    Write-Host "`nAborted: publishing without subscriptions would produce a misleading failure." -ForegroundColor Red
    $script:Findings | Format-List
    return
}

foreach ($participant in $subscribed.Result | Where-Object { @($_.subscriberChannels).Count -gt 0 }) {
    Pass "subscription open: $($participant.participantId)" ($participant.subscriberChannels -join ', ')
}

# Prove delivery itself before relying on it. One process subscribes and publishes
# on the same channel seconds apart, so a failure here cannot be participant
# configuration, session lifecycle or timing — it is the provider.
$loopback = Invoke-Sandbox POST '/admin/isbm/loopback'

if ($loopback.Status -ne 200) {
    $failed = @($loopback.Body.steps | Where-Object { -not $_.ok })[0]

    Fail 'publish-subscribe delivery works' 'ISBM' `
        "$($failed.step): $($failed.detail)" `
        ('A publication posted after a confirmed-open subscription on the same channel and ' +
         'topic was not delivered back. Nothing downstream can pass, and every later failure ' +
         'would be a symptom of this one.')

    Write-Host "`nAborted: the bus is not delivering publications." -ForegroundColor Red
    $script:Findings | Format-List
    return
}

Pass 'publish-subscribe delivery works' "$($loopback.Body.channelUri) topic $($loopback.Body.topic)"

# ===========================================================================
Write-Phase '05  ENG authors and releases'
# ===========================================================================

$tag = Invoke-Sandbox POST '/admin/eng/tags' @{
    tagNumber          = 'TIC-106'
    serviceDescription = 'Top temperature control'
    unitNumber         = '101'
    classKey           = 'rdl:TemperatureIndicatingController'
    rangeMinimum       = 0
    rangeMaximum       = 250
    controlAction      = 'Reverse'
}

if ($tag.Status -ne 200) {
    Fail 'ENG accepts a tag' 'SANDBOX' "returned $($tag.Status): $($tag.Raw)" ''
    return
}
Pass 'ENG accepts a tag' 'TIC-106, WorkInProgress'

# The gate must block an unclassified tag, or the release workflow proves nothing.
$bad = Invoke-Sandbox POST '/admin/eng/tags' @{
    tagNumber = 'ZZZ-999'; serviceDescription = 'Gate probe'; unitNumber = '101'
}
$blocked = Invoke-Sandbox POST '/admin/eng/promote' @{ name = "Gate probe $(Get-Date -Format HHmmss)" }

if ($blocked.Status -eq 200) {
    Fail 'validation gate blocks an unclassified tag' 'SANDBOX' `
        'promote succeeded with an unclassified tag present' `
        'The gate is not enforcing classification.'
}
else { Pass 'validation gate blocks an unclassified tag' "$($blocked.Body.findings.Count) finding(s)" }

# Classify the probe so it stops blocking. It stays in the model and is published
# alongside TIC-106, which is why every assertion below selects by identifier.
Invoke-Sandbox POST '/admin/eng/tags' @{
    tagNumber = 'ZZZ-999'; serviceDescription = 'Gate probe'; unitNumber = '101'
    classKey  = 'rdl:Instrument'
} | Out-Null

$promote = Invoke-Sandbox POST '/admin/eng/promote' @{ name = "Test $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" }

if ($promote.Status -ne 200) {
    Fail 'ENG promotes a named version' 'SANDBOX' "returned $($promote.Status): $($promote.Raw)" ''
    return
}
Pass 'ENG promotes a named version' "$($promote.Body.tagCount) tag(s) released"

# Distinguish "not posted" from "posted and not delivered". They have nothing in
# common as problems.
$posted = Wait-For -What 'ENG outbox posts' -Condition {
    $outbox = Invoke-Sandbox GET '/admin/eng/outbox'
    $item = @($outbox.Body)[0]
    if ($item.state -eq 2) { return $item }
    if ($item.state -eq 3) { return $item }
    return $null
}

if (-not $posted.Ok) {
    Fail 'ENG outbox drains' 'SANDBOX' 'the outbox item never left Pending' `
        'The dispatcher is not running, or ISBM is unreachable.'
}
elseif ($posted.Result.state -eq 3) {
    Fail 'ENG posts to ISBM' 'ISBM' $posted.Result.lastError `
        'The provider rejected the publication. Its fault text is more precise than anything the Sandbox can infer.'
}
else { Pass 'ENG posts to ISBM' "after $($posted.Seconds)s" }

# ===========================================================================
Write-Phase '06  REG-LOCATION receives and governs'
# ===========================================================================

# Selected by identifier, not by position. The gate probe leaves a second tag in
# play, and an index would silently assert against whichever happened to sort first.
$queued = Wait-For -What 'stewardship queue' -Condition {
    $queue = Invoke-Sandbox GET '/admin/reg-location/stewardship'
    return @($queue.Body | Where-Object { $_.sourceIdentifier -eq 'TIC-106' })[0]
}

if (-not $queued.Ok) {
    # A subscription only receives what is published after it opens. Reset closes
    # every session, so a publication issued before the inbox reopened is gone.
    $archive = Invoke-Sandbox GET '/admin/reg-location/messages'
    $inbound = @($archive.Body | Where-Object { $_.direction -eq 1 })

    if ($inbound.Count -eq 0) {
        $sessions = Invoke-Sandbox GET '/health/isbm/sessions'
        $loc = $sessions.Body | Where-Object { $_.participantId -eq 'reg-location' }
        $sub = @($loc.sessions | Where-Object { $_.kind -eq 'Subscription' })[0]
        $poll = @($loc.polling)[0]

        # Three different problems with three different owners, and from outside they
        # all look like an empty archive.
        if (-not $poll -or $poll.polls -eq 0) {
            Fail 'REG-LOCATION polls its subscription' 'SANDBOX' `
                'the inbox pump has never polled this binding' `
                'The hosted service is not running. On App Service this is usually Always On being off.'
        }
        elseif ($poll.lastPollSecondsAgo -gt 30) {
            Fail 'REG-LOCATION polls its subscription' 'SANDBOX' `
                "last poll was $($poll.lastPollSecondsAgo)s ago; the interval is 3s" `
                'The pump has stalled or the app was unloaded.'
        }
        elseif ($poll.failures -gt 0) {
            Fail 'REG-LOCATION reads its subscription' 'SANDBOX' `
                "$($poll.failures) failed read(s); last: $($poll.lastError)" `
                'The pump is polling and erroring rather than finding nothing.'
        }
        else {
            Fail 'REG-LOCATION receives the publication' 'ISBM' `
                ("ENG posted successfully. REG-LOCATION polled $($poll.polls) time(s) on " +
                 "$($poll.channelUri) topic $($poll.topics -join ',') using session " +
                 "$($poll.sessionId) (open $($sub.ageSeconds)s), got $($poll.emptyReads) empty " +
                 'read(s) and no messages, with no errors') `
                ('A loopback on this same channel and topic delivered successfully in this run, ' +
                 'so the channel and topic are right and the provider does deliver. What differs ' +
                 'is that this subscription was opened earlier and by a different session.')
        }
    }
    else {
        Fail 'REG-LOCATION handles the publication' 'SANDBOX' `
            "message archived but not handled: $($inbound[0].processingDetail)" `
            'The BOD arrived; the handler did not queue it.'
    }
    return
}

Pass 'REG-LOCATION receives and queues for stewardship' "after $($queued.Seconds)s"

$proposal = $queued.Result

if ($proposal.requestedClassKey -ne 'rdl:TemperatureIndicatingController') {
    Fail 'the sender class survives the hop' 'SANDBOX' `
        "requestedClassKey is '$($proposal.requestedClassKey)'" `
        'ENG classified the tag against the leaf; that must not be lost in transit.'
}
elseif ($proposal.boundClassKey -ne 'rdl:Instrument') {
    Fail 'class binding degrades to a known ancestor' 'SANDBOX' `
        "boundClassKey is '$($proposal.boundClassKey)', expected 'rdl:Instrument'" `
        'REG-LOCATION does not hold the leaf class, so it should bind at the nearest ancestor it does hold.'
}
elseif (-not $proposal.classDegraded) {
    Fail 'degradation is reported' 'SANDBOX' 'classDegraded is false while bound to an ancestor' `
        'Binding to an ancestor must be visible, or a receiver silently understates what it was told.'
}
else { Pass 'class binding degrades and says so' "$($proposal.requestedClassKey) -> $($proposal.boundClassKey)" }

if ($proposal.propertiesMapped -lt 2) {
    Fail 'class-governed properties map' 'SANDBOX' `
        "propertiesMapped is $($proposal.propertiesMapped), expected 2" `
        'Both ranges are in rdl:Instrument, so both should map.'
}
elseif ($proposal.propertiesUnmapped -lt 3) {
    Fail 'unknown properties are retained' 'SANDBOX' `
        "propertiesUnmapped is $($proposal.propertiesUnmapped), expected 3" `
        'ControlAction belongs to the unheld leaf class, and two eng: fields have no definition at all. All three must be kept, not dropped.'
}
else {
    Pass 'properties split correctly' `
        "$($proposal.propertiesMapped) mapped, $($proposal.propertiesUnmapped) retained"
}

$approve = Invoke-Sandbox POST '/admin/reg-location/approve'

if ($approve.Status -ne 200) {
    Fail 'steward approves' 'SANDBOX' "returned $($approve.Status): $($approve.Raw)" ''
    return
}
Pass 'steward approves and republishes' "$($approve.Body.locationCodes -join ', ')"

$locations = Invoke-Sandbox GET '/admin/reg-location/locations'
$location = @($locations.Body | Where-Object { $_.sourceIdentifier -eq 'TIC-106' })[0]

if (-not $location) {
    Fail 'the approved location exists' 'SANDBOX' `
        "no location sourced from ENG:TIC-106; found $(@($locations.Body).Count) location(s)" ''
    return
}

if ($location.sourceIdentifier -ne 'TIC-106') {
    Fail 'origin is retained' 'SANDBOX' "sourceIdentifier is '$($location.sourceIdentifier)'" `
        'Without the originating identifier the registry cannot later assert equivalence.'
}
elseif ($location.locationCode -eq 'TIC-106') {
    Fail 'the registry assigns its own identifier' 'SANDBOX' `
        'locationCode equals the source identifier' `
        'A registry that adopts the sender key has nothing to reconcile, and the CIR becomes pointless.'
}
else { Pass 'the registry assigns its own identifier' "$($location.locationCode) from ENG:$($location.sourceIdentifier)" }

# ===========================================================================
Write-Phase '07  MMS consumes'
# ===========================================================================

$expectedForeignId = "REG-LOCATION:$($location.locationCode)"

$received = Wait-For -What 'MMS records' -Condition {
    $records = Invoke-Sandbox GET '/admin/mms/locations'
    return @($records.Body | Where-Object { $_.foreignIdentifier -eq $expectedForeignId })[0]
}

if (-not $received.Ok) {
    $archive = Invoke-Sandbox GET '/admin/mms/messages'
    $inbound = @($archive.Body | Where-Object { $_.direction -eq 1 })

    if ($inbound.Count -eq 0) {
        Fail 'MMS receives the republication' 'ISBM' `
            'REG-LOCATION republished but nothing arrived at MMS' `
            'Check the O&M channel and whether the MMS subscription was open at the time.'
    }
    else {
        Fail 'MMS handles the republication' 'SANDBOX' `
            "archived but not handled: $($inbound[0].processingDetail)" ''
    }
    return
}

$record = $received.Result
Pass 'MMS creates a record' "$($record.equipmentNumber) from $($record.foreignIdentifier)"

if ($record.resolved) {
    Fail 'MMS starts unresolved' 'SANDBOX' 'resolved is true before any registration' `
        'MMS must not know the shared identity until the registry tells it.'
}
else {
    Pass 'three identifiers, no link between them' `
        "ENG:TIC-106 / $($location.locationCode) / $($record.equipmentNumber)"
}

# ===========================================================================
Write-Phase '08  CIR registration and resolution'
# ===========================================================================

if ($SkipCir) {
    Info 'skipped'
}
else {
    $engReg = Invoke-Sandbox POST '/admin/eng/cir/register'

    if ($engReg.Body.note) {
        Fail 'ENG has something to register' 'SANDBOX' $engReg.Body.note ''
    }
    elseif (@($engReg.Body.faults).Count -gt 0) {
        $fault = @($engReg.Body.faults)[0]

        if ($fault -like 'NoResponse*') {
            # Order matters here, and it did not used to.
            #
            # /admin/cir/diagnose opens a *competing* provider-request session on the
            # CIR provider's own channel. ISBM hands a queued request to one provider
            # session, so probing before the drain can check the message out to the
            # Sandbox and leave the drain with nothing to find. That produced the
            # reading "the queue emptied but the drain handled nothing", which was
            # attributed to CIR when the probe had caused it.
            #
            # So: drain first, observe second. The probe can then only see what the
            # provider genuinely left behind.
            $forced = Invoke-Cir POST '/isbm/drain'
            Start-Sleep -Seconds 2
            $diag = Invoke-Sandbox GET '/admin/cir/diagnose'

            $handled = [int]$forced.Body.requestsHandled
            $posted = [int]$forced.Body.responsesPosted
            $queued = [int]$diag.Body.pendingRequests

            # The correlation id is the handle for both sides: it travels in
            # ApplicationArea/BODID, so the provider's logs can be searched on the
            # same value the sender used. Fetched once and shared by every branch.
            $last = Invoke-Sandbox GET '/admin/cir/last?participantId=eng'
            $exchange = @($last.Body) | Where-Object { $_.correlationId } | Select-Object -First 1

            if ($exchange) {
                $evidence = "BODID $($exchange.correlationId), ISBM message " +
                            "$($exchange.requestMessageId) on consumer session " +
                            "$($exchange.consumerSessionId); $($exchange.bod) posted to " +
                            "$($exchange.channelUri) topic $($exchange.topic), outcome " +
                            "$($exchange.outcome) after $($exchange.waitedSeconds)s"
            }
            else {
                # An empty exchange is itself a finding, and reporting blanks as though
                # they were observations is worse than saying nothing. The raw body is
                # included because the shape of a failure is the fastest way to tell a
                # 404 from an empty array from an auth redirect.
                Fail 'the last CIR exchange was captured' 'SANDBOX' `
                    ("GET /admin/cir/last returned $($last.Status): " +
                     $(if ($last.Raw) { $last.Raw } else { '(empty body)' })) `
                    ('Without it there is no BODID to search the provider logs on, and no ' +
                     'request XML to hand over. The exchange is written to each participant ' +
                     'schema as CirExchange — if that table is empty the registration never ' +
                     'reached the post, and if it is missing the deployed build predates it.')
                $evidence = 'no exchange was captured'
            }

            if (@($forced.Body.errors).Count -gt 0) {
                Fail 'CIR consumes the request' 'CIR' `
                    ("an explicit drain reported: $($forced.Body.errors -join '; '). " +
                     "$queued request(s) remain queued on $($diag.Body.channelUri) " +
                     "topic '$($diag.Body.subscribedTopics -join ', ')'. $evidence") `
                    'The provider cannot read its own channel. Its session is most likely dead — deleting a channel destroys its sessions, and a poll loop that swallows the fault keeps reporting itself healthy.'
            }
            elseif ($handled -gt 0) {
                # The drain runs only after the client has already given up, so a
                # request the drain can still find is a request that sat unread for
                # the whole timeout. That is a scheduling fault in the provider's
                # listener, and it is NOT evidence about acknowledgement handling:
                # no response could have arrived during the wait, because the request
                # had not been consumed during the wait.
                #
                # Whether an acknowledgement followed the drain is a separate question,
                # and the client is no longer listening to answer it. Resume the wait
                # on the original session before attributing a second fault.
                #
                # The drain also counts a response at the moment it posts one, so
                # responsesPosted answers the acknowledgement question without
                # depending on the sender being able to read it back. A drain that
                # handled the request and posted nothing is a provider that did not
                # answer; one that posted and was still not read is a delivery fault.
                $late = Invoke-Sandbox GET "/admin/cir/await-response?participantId=eng&seconds=30"

                if ($posted -gt 0 -and -not $late.Body.answered) {
                    Fail 'CIR responds to the request' 'CIR' `
                        ("an explicit drain handled $handled request(s) and posted " +
                         "$posted response(s), but a further $($late.Body.waitedSeconds)s " +
                         "wait on the original consumer session never saw one" +
                         $(if ($late.Body.error) { " ($($late.Body.error))" }) +
                         ". $evidence") `
                        ('The provider did answer, so acknowledgement handling is not the fault. ' +
                         'The response was posted but never delivered back to the sender: check ' +
                         'that PostResponse correlates on OriginalMessageID and that the sender ' +
                         'reads the same message id it posted.')
                }
                elseif ($late.Body.answered) {
                    # The behaviour under test is that a consumed request is answered,
                    # and it was: the drain produced an Acknowledge. When it was picked
                    # up is a real fault but a different one, owned by the hosting
                    # configuration rather than by the BOD handling, so it is reported
                    # beside the pass instead of hidden inside a failure.
                    Pass 'CIR handles the request it consumes' `
                        "answered with $($late.Body.verb) once drained"

                    Concern 'CIR consumes its channel on its own' 'CIR' `
                        ("the request sat unread for the full $($exchange.waitedSeconds)s timeout " +
                         "and was consumed only when POST /isbm/drain was called explicitly, " +
                         "which handled $handled request(s) and answered with " +
                         "$($late.Body.verb). $evidence") `
                        ('Acknowledgement handling is correct — the fault is entirely in when the ' +
                         'request is picked up. The listener is a timer trigger, so the app has to ' +
                         'be running for it to fire; on a Consumption plan nothing wakes it, because ' +
                         'an ISBM post is a write to the broker rather than an HTTP request to this ' +
                         'app. Always On on Basic or higher, or a Premium plan, fixes it.')
                }
                else {
                    Fail 'CIR consumes its channel on its own' 'CIR' `
                        ("the request sat unread for the full timeout and was consumed only " +
                         "when POST /isbm/drain was called explicitly, which handled $handled " +
                         "request(s), posted $posted response(s)" +
                         $(if (@($forced.Body.discarded).Count -gt 0) { ", discarded $($forced.Body.discarded -join '; ')" }) +
                         ". A further $($late.Body.waitedSeconds)s wait on the original session " +
                         "produced no acknowledgement" +
                         $(if ($late.Body.error) { " ($($late.Body.error))" }) +
                         ". $evidence") `
                        ('Two faults, and they are separable. The listener is not firing on its ' +
                         'own — a timer trigger on a Consumption plan needs the scale controller ' +
                         'to wake the app. Beyond that, the request was consumed and no ' +
                         'acknowledgement followed, and a Process verb with acknowledgeCode="Always" ' +
                         'must produce one. Hand over the XML at ' +
                         'GET /admin/cir/last?participantId=eng rather than a description of it, ' +
                         'and search the provider logs for the BODID above.')
                }
            }
            elseif ($queued -gt 0) {
                Fail 'CIR consumes the request' 'CIR' `
                    ("$queued request(s) still queued on $($diag.Body.channelUri) " +
                     "topic '$($diag.Body.subscribedTopics -join ', ')' after an explicit " +
                     "drain that reported no errors and handled nothing. $evidence") `
                    'A drain that reports success while leaving the queue untouched means the read is returning nothing. If the provider session is older than the last channel rebuild it is dead, and a 404 with a Session fault reads the same as an empty queue unless the body is inspected.'
            }
            else {
                # Nothing queued and nothing handled on demand: something consumed it
                # during the 120s wait — almost certainly a timer invocation, which
                # polls every 15s. That is still a missing response, but the drain
                # counters cannot prove which invocation took it.
                Fail 'CIR responds to the request' 'CIR' `
                    ("the queue is empty and the explicit drain handled nothing, so the " +
                     "request was consumed during the wait without a reply. $evidence") `
                    ('Consumed by a timer invocation rather than by this drain — the counters ' +
                     'cannot tell them apart, so the provider logs keyed on the BODID are the ' +
                     'only place that distinguishes them. Either way no AcknowledgeRegistry ' +
                     'came back.')
            }

            if ($Detailed -and $exchange.requestXml) {
                Write-Host "`n        Request sent:" -ForegroundColor DarkGray
                Write-Host "        $($exchange.requestXml)" -ForegroundColor DarkGray
            }
        }
        else {
            # The whole response, not one field: a fault shape we did not anticipate
            # parses to an empty message, which reads as "no detail available" when
            # the detail was there all along.
            Fail 'ENG registers with the CIR' 'CIR' `
                ($engReg.Raw ?? $fault) `
                'The registry returned a fault rather than an acknowledgement.'
        }
    }
    else { Pass 'ENG registers with the CIR' "$($engReg.Body.registered) entry(ies)" }

    if ($script:Failed -eq 0) {
        $locReg = Invoke-Sandbox POST '/admin/reg-location/cir/register'

        if (@($locReg.Body.faults).Count -gt 0) {
            Fail 'REG-LOCATION asserts equivalence' 'CIR' ($locReg.Body.faults -join '; ') `
                'Equivalence is what links LOC-000001 to ENG:TIC-106. Registration alone would create a second identity.'
        }
        else { Pass 'REG-LOCATION asserts equivalence' "$($locReg.Body.equivalencesAsserted)" }

        $mmsReg = Invoke-Sandbox POST '/admin/mms/cir/register'

        if (@($mmsReg.Body.faults).Count -gt 0) {
            Fail 'MMS asserts equivalence' 'CIR' ($mmsReg.Body.faults -join '; ') ''
        }
        else { Pass 'MMS asserts equivalence' "$($mmsReg.Body.equivalencesAsserted)" }

        $resolve = Invoke-Sandbox GET `
            "/admin/mms/cir/resolve?sourceId=REG-LOCATION&idInSource=$($location.locationCode)"

        if (-not $resolve.Body.cirid) {
            Fail 'MMS resolves the foreign identifier' 'CIR' `
                "no CIRID returned: $($resolve.Body.detail)" `
                'The entries registered but the registry did not assign or return a shared identity.'
        }
        elseif (@($resolve.Body.equivalents).Count -lt 3) {
            Fail 'all three identifiers share one identity' 'CIR' `
                "$(@($resolve.Body.equivalents).Count) equivalent(s): $(@($resolve.Body.equivalents | ForEach-Object { "$($_.sourceID):$($_.idInSource)" }) -join ', ')" `
                'Expected ENG, REG-LOCATION and MMS under one CIRID. Fewer means an equivalence assertion did not take.'
        }
        else {
            Pass 'all three identifiers share one identity' `
                (@($resolve.Body.equivalents | ForEach-Object { "$($_.sourceID):$($_.idInSource)" }) -join ' = ')
        }

        # The cache is what makes stale-mapping correction possible later.
        $again = Invoke-Sandbox GET `
            "/admin/mms/cir/resolve?sourceId=REG-LOCATION&idInSource=$($location.locationCode)"

        if (-not $again.Body.fromCache) {
            Fail 'resolution is cached' 'SANDBOX' 'the second resolve did not come from the cache' `
                'Every read would hit the registry, and stale-mapping behaviour could not be demonstrated.'
        }
        else { Pass 'resolution is cached' }
    }
}

# ===========================================================================

Write-Host "`n============================================" -ForegroundColor White
Write-Host " $script:Passed passed, $script:Failed failed" -ForegroundColor $(if ($script:Failed) { 'Red' } else { 'Green' })
Write-Host '============================================' -ForegroundColor White

if ($script:Concerns.Count -gt 0) {
    Write-Host "`nConcerns (not failures):" -ForegroundColor DarkYellow

    foreach ($group in $script:Concerns | Group-Object Owner) {
        Write-Host "`n  $($group.Name)" -ForegroundColor DarkYellow

        foreach ($concern in $group.Group) {
            Write-Host "    $($concern.Check)"
            Write-Host "      observed: $($concern.Observed)" -ForegroundColor DarkGray
            if ($concern.Suggests) { Write-Host "      suggests: $($concern.Suggests)" -ForegroundColor DarkGray }
        }
    }
}

if ($script:Failed -gt 0) {
    Write-Host "`nFindings by owner:" -ForegroundColor Yellow

    foreach ($group in $script:Findings | Group-Object Owner) {
        Write-Host "`n  $($group.Name)" -ForegroundColor Yellow

        foreach ($finding in $group.Group) {
            Write-Host "    $($finding.Check)"
            Write-Host "      observed: $($finding.Observed)" -ForegroundColor DarkGray
            if ($finding.Suggests) { Write-Host "      suggests: $($finding.Suggests)" -ForegroundColor DarkGray }
        }
    }

    Write-Host "`n  Hand a section to whoever owns that component. Each line is what was" -ForegroundColor DarkGray
    Write-Host '  observed from outside, not a diagnosis of their code.' -ForegroundColor DarkGray
    exit 1
}

exit 0
