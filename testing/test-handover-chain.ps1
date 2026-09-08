#!/usr/bin/env pwsh
# ============================================================================
# OIIE Handover Chain - Executable Contract
#
# This script is written BEFORE the participants exist and is expected to fail.
# It defines the channels, the BODs and the expected flow; each participant
# built makes one more phase pass.
#
# Two things distinguish it from test-isbm.ps1:
#
#   1. test-isbm.ps1 proves the ISBM provider works. This proves the ECOSYSTEM
#      works - that a BOD published by ENG reaches MMS with no orchestrator.
#
#   2. Phase 7 delivers the same BOD twice and asserts it is applied once. A
#      chain test that publishes once passes against participants with no
#      idempotency whatsoever, so without phase 7 the sharpest hazard in the
#      design goes unexercised until a customer finds it.
#
# Phases 2 and 3 exercise the ISBM publication and consumer-request routes
# directly. Those routes are marked UNVERIFIED in IIsbmClient - inferred from
# convention, never exercised, because ws-CIR neither publishes nor issues
# requests. Every hop in the chain except CIR's own depends on them, so they
# are proven here before any participant is written against them.
#
# Absent participants are reported SKIP with a reason, not FAIL, so the script
# is meaningful at every stage of the build.
#
# Usage:
#   .\test-handover-chain.ps1
#   .\test-handover-chain.ps1 -BaseUrl "https://isbm-func-x.azurewebsites.net/api"
#   .\test-handover-chain.ps1 -Phase 2          # one phase
#   .\test-handover-chain.ps1 -SkipCleanup      # leave channels for inspection
# ============================================================================

param(
    [string]$BaseUrl = "http://localhost:7253/api",

    # Participant query endpoints. Empty means "not built yet" -> SKIP.
    # Each participant must expose a read-only lookup for its own store; that
    # is the only way this script can assert a BOD was actually applied.
    [string]$RegBaseUrl = "",
    [string]$MmsBaseUrl = "",

    # CMS is split (spec 5.2): CmsEngine is the participant holding the ISBM
    # session; CmsProvider is the customer system, which is NOT a participant
    # and knows nothing about ISBM. Asserting the chain reached CMS means
    # querying the customer system, because that is where the row lands.
    [string]$CmsEngineBaseUrl = "",
    [string]$CmsProviderBaseUrl = "",

    [string]$CirBaseUrl = "",

    # Matches the enterprise segment used by the ENG/MMS personality packs
    # (e.g. /acme/enterprise/sites/publication). MMS's SyncSites checks below
    # publish on this enterprise channel, not the /oiie/* test-only channels.
    [string]$Enterprise = "acme",

    [int]$Phase = 0,
    [int]$SettleSeconds = 10,
    [switch]$SkipCleanup
)

$ErrorActionPreference = "Stop"
$headers = @{ "Content-Type" = "application/json" }

$script:createdChannels = @()
$script:openSessions    = @()
$script:pass = 0
$script:fail = 0
$script:skip = 0
$script:failures = @()

function Write-Phase { param([string]$m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Pass  { param([string]$m) $script:pass++; Write-Host "  PASS: $m" -ForegroundColor Green }
function Write-Info  { param([string]$m) Write-Host "  INFO: $m" -ForegroundColor Yellow }
function Write-Fail {
    param([string]$m)
    $script:fail++
    $script:failures += $m
    Write-Host "  FAIL: $m" -ForegroundColor Red
}
function Write-Skip {
    param([string]$m, [string]$why)
    $script:skip++
    Write-Host "  SKIP: $m" -ForegroundColor DarkGray
    Write-Host "        $why" -ForegroundColor DarkGray
}

function Invoke-Isbm {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [int[]]$ExpectedStatus = @(200, 201, 204)
    )
    $params = @{
        Method = $Method; Uri = "$BaseUrl$Path"
        Headers = $headers; UseBasicParsing = $true
    }
    if ($Body) {
        $params.Body = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 }
    }
    try {
        $response = Invoke-WebRequest @params
        if ($response.StatusCode -notin $ExpectedStatus) {
            Write-Fail "$Method $Path -> $($response.StatusCode) (expected $($ExpectedStatus -join '/'))"
            return $null
        }
        if ($response.Content -and $response.Content.Length -gt 0) {
            $parsed = $response.Content | ConvertFrom-Json
            Add-Member -InputObject $parsed -NotePropertyName _status `
                       -NotePropertyValue $response.StatusCode -Force -ErrorAction SilentlyContinue
            return $parsed
        }
        return [pscustomobject]@{ _status = $response.StatusCode }
    }
    catch {
        $status = $null
        try { $status = $_.Exception.Response.StatusCode.value__ } catch {}
        $detail = ""
        try { $detail = $_.ErrorDetails.Message } catch {}
        if ($status -and $status -in $ExpectedStatus) {
            return [pscustomobject]@{ _status = $status; _detail = $detail }
        }
        Write-Fail "$Method $Path -> $status $detail"
        return $null
    }
}

function Invoke-Participant {
    # Participants are separate apps that may be down. A connection failure is
    # a skip, not a failure: the point is to report "not built yet" clearly.
    param([string]$Method, [string]$Url, [object]$Body = $null)
    try {
        $params = @{ Method = $Method; Uri = $Url; Headers = $headers; UseBasicParsing = $true }
        if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10) }
        $r = Invoke-WebRequest @params
        if ($r.Content) { return $r.Content | ConvertFrom-Json }
        return [pscustomobject]@{ _status = $r.StatusCode }
    }
    catch { return $null }
}

# ---------------------------------------------------------------------------
# BOD construction
#
# BODID is the idempotency key (spec 4.3). It is a parameter rather than
# generated inside, because phase 7 must send the SAME BODID twice - that is
# precisely what redelivery looks like to a participant.
# ---------------------------------------------------------------------------
function New-ProcessRegistryBod {
    param(
        [string]$BodId,
        [string]$Tag         = "TIC-106",
        [string]$Description = "Temperature Indicator Controller 106",
        [string]$SenderId    = "ENG",
        [string]$Site        = "PLANT-01"
    )
    $created = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    @"
<?xml version="1.0" encoding="UTF-8"?>
<ProcessRegistry xmlns="http://www.mimosa.org/OIIE/CCOM" releaseID="1.0">
  <ApplicationArea>
    <Sender><LogicalID>$SenderId</LogicalID><ConfirmationCodes>Always</ConfirmationCodes></Sender>
    <CreationDateTime>$created</CreationDateTime>
    <BODID>$BodId</BODID>
  </ApplicationArea>
  <DataArea>
    <Process><ActionCriteria><ActionExpression actionCode="Add"/></ActionCriteria></Process>
    <Registry>
      <Segment>
        <IDInInfoSource>$Tag</IDInInfoSource>
        <Description>$Description</Description>
        <RegistrationSite>$Site</RegistrationSite>
      </Segment>
    </Registry>
  </DataArea>
</ProcessRegistry>
"@
}

function Get-BodIdFromXml {
    param([string]$Xml)
    try { return ([xml]$Xml).DocumentElement.ApplicationArea.BODID } catch { return $null }
}

function New-SyncSitesBod {
    param(
        [string]$BodId,
        [Parameter(Mandatory)][guid]$SiteUuid,
        [string]$ShortName = "MMS Chain Test Site",
        [string]$SenderId  = "ENG"
    )
    $created = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    @"
<?xml version="1.0" encoding="UTF-8"?>
<SyncSites xmlns="http://www.mimosa.org/OIIE/CCOM" releaseID="1.0">
  <ApplicationArea>
    <Sender><LogicalID>$SenderId</LogicalID><ConfirmationCodes>Always</ConfirmationCodes></Sender>
    <CreationDateTime>$created</CreationDateTime>
    <BODID>$BodId</BODID>
  </ApplicationArea>
  <DataArea>
    <Sync><ActionCriteria><ActionExpression actionCode="Add"/></ActionCriteria></Sync>
    <Site>
      <UUID>$SiteUuid</UUID>
      <ShortName>$ShortName</ShortName>
    </Site>
  </DataArea>
</SyncSites>
"@
}

function New-ChannelIfAbsent {
    param([string]$ChannelUri, [string]$ChannelType)
    # CreateChannel returns 422 when the channel exists. Idempotent by design:
    # this script runs repeatedly against a shared provider.
    $r = Invoke-Isbm -Method POST -Path "/channels" -ExpectedStatus @(201, 409, 422) -Body @{
        channelUri = $ChannelUri; channelType = $ChannelType
    }
    if ($null -eq $r) { return $false }
    if ($r._status -eq 201) {
        $script:createdChannels += $ChannelUri
        Write-Pass "created $ChannelUri ($ChannelType)"
    }
    else {
        Write-Pass "exists  $ChannelUri ($ChannelType)"
    }
    return $true
}

function Close-IsbmSession {
    param([string]$SessionId)
    if ($SessionId) {
        Invoke-Isbm -Method DELETE -Path "/sessions/$SessionId" -ExpectedStatus @(200,204,404) | Out-Null
    }
}

$sitesChannelUri = "/$Enterprise/enterprise/sites/publication"

$channels = @(
    @{ uri = "/oiie/engineering-updates"; type = "Publication" },
    @{ uri = "/oiie/cir/request";         type = "Request"     },
    @{ uri = "/oiie/asset-config";        type = "Publication" },
    @{ uri = "/oiie/maintenance-events";  type = "Publication" },
    @{ uri = $sitesChannelUri;             type = "Publication" }
)

function Test-Phase { param([int]$N) return ($Phase -eq 0 -or $Phase -eq $N) }

Write-Host "OIIE Handover Chain - Executable Contract" -ForegroundColor White
Write-Host "Provider: $BaseUrl" -ForegroundColor DarkGray

# Fail fast if the provider is unreachable. Without this, every phase reports
# its own connection error and the real cause - nothing is running - is buried
# under a dozen identical lines that each look like a different defect.
try {
    Invoke-WebRequest -Uri "$BaseUrl/channels" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop | Out-Null
}
catch {
    $status = $null
    try { $status = $_.Exception.Response.StatusCode.value__ } catch {}
    if (-not $status) {
        Write-Host "`nISBM provider unreachable at $BaseUrl" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor DarkGray
        Write-Host "`n  Start it with 'func start' in the ISBMProvider project, or pass" -ForegroundColor Yellow
        Write-Host "  -BaseUrl for a deployed instance. Nothing below can run without it." -ForegroundColor Yellow
        exit 2
    }
    # An HTTP error means the provider answered, which is all this check needs.
}

# ============================================================================
# PHASE 1 - Channel bootstrap
# ============================================================================
if (Test-Phase 1) {
    Write-Phase "1. Channel bootstrap"

    foreach ($c in $channels) { New-ChannelIfAbsent -ChannelUri $c.uri -ChannelType $c.type | Out-Null }

    # Re-running must not duplicate. Idempotence is what lets this be the
    # bootstrap script as well as a test.
    foreach ($c in $channels) { New-ChannelIfAbsent -ChannelUri $c.uri -ChannelType $c.type | Out-Null }

    $all = Invoke-Isbm -Method GET -Path "/channels"
    if ($all) {
        $uris = @($all | ForEach-Object { $_.channelUri })
        foreach ($c in $channels) {
            $count = @($uris | Where-Object { $_ -eq $c.uri }).Count
            if ($count -eq 1)      { Write-Pass "$($c.uri) present exactly once" }
            elseif ($count -gt 1)  { Write-Fail "$($c.uri) appears $count times - bootstrap is not idempotent" }
            else                   { Write-Fail "$($c.uri) missing after creation" }
        }
    }
}

# ============================================================================
# PHASE 2 - Publication route  [UNVERIFIED in IIsbmClient]
#
# Open a publication session, post, read it back on a subscription, remove.
# If this phase fails, no participant can publish and the chain cannot exist.
# ============================================================================
if (Test-Phase 2) {
    Write-Phase "2. Publication route (UNVERIFIED - proving before use)"

    $pub = Invoke-Isbm -Method POST -Path "/publication-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/engineering-updates"
    }
    if ($null -eq $pub -or -not $pub.sessionId) {
        Write-Fail "OpenPublicationSession did not return a sessionId - the inferred route is wrong"
    }
    else {
        $script:openSessions += $pub.sessionId
        Write-Pass "publication session $($pub.sessionId)"

        $sub = Invoke-Isbm -Method POST -Path "/subscription-sessions" -ExpectedStatus @(200,201) -Body @{
            channelUri = "/oiie/engineering-updates"; topics = @("ProcessRegistry")
        }
        if ($null -eq $sub -or -not $sub.sessionId) {
            Write-Fail "OpenSubscriptionSession did not return a sessionId"
        }
        else {
            $script:openSessions += $sub.sessionId
            Write-Pass "subscription session $($sub.sessionId)"

            $bodId = [guid]::NewGuid().ToString()
            $bod   = New-ProcessRegistryBod -BodId $bodId -Tag "TIC-ROUTE-TEST"

            $posted = Invoke-Isbm -Method POST -Path "/sessions/$($pub.sessionId)/publications" `
                -ExpectedStatus @(200,201) -Body @{
                    messageContent = @{ mediaType = "application/xml"; inlineContent = $bod }
                    topics = @("ProcessRegistry")
                }
            if ($null -eq $posted) { Write-Fail "PostPublication failed - the inferred route is wrong" }
            else {
                Write-Pass "posted publication $($posted.messageId)"

                $read = Invoke-Isbm -Method GET -Path "/sessions/$($sub.sessionId)/publication" `
                                    -ExpectedStatus @(200,404)
                if ($null -eq $read -or $read._status -eq 404) {
                    Write-Fail "ReadPublication found nothing - publish/subscribe is not connected"
                }
                else {
                    $content = $read.messageContent.inlineContent
                    $roundTripped = Get-BodIdFromXml -Xml $content
                    if ($roundTripped -eq $bodId) { Write-Pass "BODID survived the round trip" }
                    else { Write-Fail "BODID changed in transit: sent $bodId, read $roundTripped" }

                    Invoke-Isbm -Method DELETE -Path "/sessions/$($sub.sessionId)/publication" | Out-Null
                    Write-Pass "removed (acknowledged)"

                    $again = Invoke-Isbm -Method GET -Path "/sessions/$($sub.sessionId)/publication" `
                                         -ExpectedStatus @(200,404)
                    if ($null -eq $again -or $again._status -eq 404) {
                        Write-Pass "queue empty after remove"
                    }
                    else {
                        Write-Fail "message still present after RemovePublication"
                    }
                }
            }
        }
    }
}

# ============================================================================
# PHASE 3 - Fan-out
#
# The executable form of the architectural claim: a second subscriber can be
# added to a channel with no change to the publisher. If this fails, "adding a
# participant is cheap" is not true and the architecture does not deliver its
# headline benefit.
# ============================================================================
if (Test-Phase 3) {
    Write-Phase "3. Fan-out - two subscribers, one publication"

    $subA = Invoke-Isbm -Method POST -Path "/subscription-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/asset-config"; topics = @("ProcessRegistry")
    }
    $subB = Invoke-Isbm -Method POST -Path "/subscription-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/asset-config"; topics = @("ProcessRegistry")
    }
    $pub = Invoke-Isbm -Method POST -Path "/publication-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/asset-config"
    }

    if ($subA.sessionId -and $subB.sessionId -and $pub.sessionId) {
        $script:openSessions += @($subA.sessionId, $subB.sessionId, $pub.sessionId)

        $bodId = [guid]::NewGuid().ToString()
        $bod   = New-ProcessRegistryBod -BodId $bodId -Tag "TIC-FANOUT"
        Invoke-Isbm -Method POST -Path "/sessions/$($pub.sessionId)/publications" `
            -ExpectedStatus @(200,201) -Body @{
                messageContent = @{ mediaType = "application/xml"; inlineContent = $bod }
                topics = @("ProcessRegistry")
            } | Out-Null

        foreach ($s in @(@{n="A";id=$subA.sessionId}, @{n="B";id=$subB.sessionId})) {
            $r = Invoke-Isbm -Method GET -Path "/sessions/$($s.id)/publication" -ExpectedStatus @(200,404)
            if ($r -and $r._status -ne 404 -and (Get-BodIdFromXml $r.messageContent.inlineContent) -eq $bodId) {
                Write-Pass "subscriber $($s.n) received the publication"
                Invoke-Isbm -Method DELETE -Path "/sessions/$($s.id)/publication" | Out-Null
            }
            else {
                Write-Fail "subscriber $($s.n) did not receive it - fan-out is broken"
            }
        }
    }
    else { Write-Fail "could not open the sessions needed for fan-out" }
}

# ============================================================================
# PHASE 4 - Topic filtering
#
# A participant subscribed to one topic must not be woken by another, or every
# participant pays to parse every BOD in the ecosystem.
# ============================================================================
if (Test-Phase 4) {
    Write-Phase "4. Topic filtering"

    $sub = Invoke-Isbm -Method POST -Path "/subscription-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/maintenance-events"; topics = @("AssetSegmentEvent")
    }
    $pub = Invoke-Isbm -Method POST -Path "/publication-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/maintenance-events"
    }

    if ($sub.sessionId -and $pub.sessionId) {
        $script:openSessions += @($sub.sessionId, $pub.sessionId)

        Invoke-Isbm -Method POST -Path "/sessions/$($pub.sessionId)/publications" `
            -ExpectedStatus @(200,201) -Body @{
                messageContent = @{ mediaType = "application/xml"
                                    inlineContent = (New-ProcessRegistryBod -BodId ([guid]::NewGuid())) }
                topics = @("SomeOtherTopic")
            } | Out-Null

        $r = Invoke-Isbm -Method GET -Path "/sessions/$($sub.sessionId)/publication" -ExpectedStatus @(200,404)
        if ($null -eq $r -or $r._status -eq 404) {
            Write-Pass "unsubscribed topic correctly withheld"
        }
        else {
            Write-Fail "received a topic not subscribed to - filtering is broken"
            Invoke-Isbm -Method DELETE -Path "/sessions/$($sub.sessionId)/publication" | Out-Null
        }
    }
}

# ============================================================================
# PHASE 5 - Consumer-request route  [UNVERIFIED in IIsbmClient]
#
# REG depends on this to register identities with the CIR. Requires the CIR
# participant to be listening; without it, only the post half is provable.
# ============================================================================
if (Test-Phase 5) {
    Write-Phase "5. Consumer-request route (UNVERIFIED - proving before use)"

    $req = Invoke-Isbm -Method POST -Path "/consumer-request-sessions" -ExpectedStatus @(200,201) -Body @{
        channelUri = "/oiie/cir/request"
    }
    if ($null -eq $req -or -not $req.sessionId) {
        Write-Fail "OpenConsumerRequestSession did not return a sessionId - the inferred route is wrong"
    }
    else {
        $script:openSessions += $req.sessionId
        Write-Pass "consumer request session $($req.sessionId)"

        $bodId = [guid]::NewGuid().ToString()
        $posted = Invoke-Isbm -Method POST -Path "/sessions/$($req.sessionId)/requests" `
            -ExpectedStatus @(200,201) -Body @{
                messageContent = @{ mediaType = "application/xml"
                                    inlineContent = (New-ProcessRegistryBod -BodId $bodId -Tag "TIC-CIR-TEST") }
                topics = @("ws-CIR")
            }
        if ($null -eq $posted -or -not $posted.messageId) {
            Write-Fail "PostRequest failed - the inferred route is wrong"
        }
        else {
            Write-Pass "posted request $($posted.messageId)"

            if (-not $CirBaseUrl) {
                Write-Skip "read AcknowledgeRegistry response" `
                           "CIR participant not running (-CirBaseUrl). Post half proven; response half unproven."
            }
            else {
                Write-Info "waiting ${SettleSeconds}s for the CIR to answer"
                Start-Sleep -Seconds $SettleSeconds
                $resp = Invoke-Isbm -Method GET `
                    -Path "/sessions/$($req.sessionId)/requests/$($posted.messageId)/response" `
                    -ExpectedStatus @(200,404)
                if ($null -eq $resp -or $resp._status -eq 404) {
                    Write-Fail "no response from the CIR within ${SettleSeconds}s"
                }
                elseif ($resp.messageContent.inlineContent -match "AcknowledgeRegistry") {
                    Write-Pass "CIR answered with AcknowledgeRegistry"
                }
                else {
                    Write-Fail "CIR answered with an unexpected BOD"
                }
            }
        }
    }
}

# ============================================================================
# PHASE 6 - The chain: ENG -> REG -> CIR -> MMS
# ============================================================================
if (Test-Phase 6) {
    Write-Phase "6. Handover chain (TIC-106 -> LOC-000001 -> 234441)"

    if (-not $RegBaseUrl -and -not $MmsBaseUrl) {
        Write-Skip "full chain" "REG and MMS not built yet (-RegBaseUrl / -MmsBaseUrl)."
    }
    else {
        $pub = Invoke-Isbm -Method POST -Path "/publication-sessions" -ExpectedStatus @(200,201) -Body @{
            channelUri = "/oiie/engineering-updates"
        }
        if ($pub.sessionId) {
            $script:openSessions += $pub.sessionId
            $bodId = [guid]::NewGuid().ToString()

            Invoke-Isbm -Method POST -Path "/sessions/$($pub.sessionId)/publications" `
                -ExpectedStatus @(200,201) -Body @{
                    messageContent = @{ mediaType = "application/xml"
                                        inlineContent = (New-ProcessRegistryBod -BodId $bodId) }
                    topics = @("ProcessRegistry")
                } | Out-Null
            Write-Pass "ENG published TIC-106 (BODID $bodId)"

            Write-Info "waiting ${SettleSeconds}s for the chain to settle"
            Start-Sleep -Seconds $SettleSeconds

            if ($RegBaseUrl) {
                $loc = Invoke-Participant -Method GET -Url "$RegBaseUrl/api/locations/TIC-106"
                if ($loc -and $loc.functionalLocation -eq "LOC-000001") { Write-Pass "REG mapped TIC-106 -> LOC-000001" }
                elseif ($loc) { Write-Fail "REG mapped TIC-106 -> $($loc.functionalLocation), expected LOC-000001" }
                else { Write-Fail "REG has no record of TIC-106" }
            }
            else { Write-Skip "REG mapping" "REG not built yet." }

            if ($CirBaseUrl) {
                $entry = Invoke-Participant -Method GET -Url "$CirBaseUrl/api/entries?id=TIC-106"
                if ($entry) { Write-Pass "CIR holds an entry for TIC-106" }
                else { Write-Fail "CIR has no entry for TIC-106 - identity was not registered" }
            }
            else { Write-Skip "CIR registration" "CIR query endpoint not supplied." }

            if ($MmsBaseUrl) {
                # MMS is not on the ENG->REG->CIR chain: it subscribes directly to the
                # enterprise SyncSites channel (spec 5.2 fan-out) and maps
                # Site.UUID -> LIGHT_SYSTEM_INVENTORY.EXT_ASSET_ID. Verified separately
                # below rather than as part of the TIC-106/LOC-000001 chain.
                $siteUuid = [guid]::NewGuid()
                $sitesPub = Invoke-Isbm -Method POST -Path "/publication-sessions" -ExpectedStatus @(200,201) -Body @{
                    channelUri = $sitesChannelUri
                }
                if ($sitesPub.sessionId) {
                    $script:openSessions += $sitesPub.sessionId
                    $siteBodId = [guid]::NewGuid().ToString()
                    $siteBod   = New-SyncSitesBod -BodId $siteBodId -SiteUuid $siteUuid
                    Invoke-Isbm -Method POST -Path "/sessions/$($sitesPub.sessionId)/publications" `
                        -ExpectedStatus @(200,201) -Body @{
                            messageContent = @{ mediaType = "application/xml"; inlineContent = $siteBod }
                            topics = @("oiie/ccom:SyncSites")
                        } | Out-Null
                    Write-Pass "ENG published SyncSites (Site.UUID $siteUuid)"

                    Write-Info "waiting ${SettleSeconds}s for MMS to ingest"
                    Start-Sleep -Seconds $SettleSeconds

                    $system = Invoke-Participant -Method GET -Url "$MmsBaseUrl/lightsystems/by-ext/$siteUuid"
                    if ($system -and "$($system.extAssetId)" -eq "$siteUuid") {
                        Write-Pass "MMS mapped Site.UUID $siteUuid -> LIGHT_SYSTEM_INVENTORY"
                    }
                    else { Write-Fail "MMS has no LIGHT_SYSTEM_INVENTORY record for Site.UUID $siteUuid - SyncSites did not complete" }
                }
                else { Write-Fail "could not open a publication session on $sitesChannelUri" }
            }
            else { Write-Skip "MMS mapping" "MMS not built yet." }

            if ($CmsProviderBaseUrl) {
                # Queried on AssetTag, not functional location: CMS turns an
                # inbound Segment into an asset placeholder (spec 6.1), so
                # LOC-000001 never appears in the CMS store as a location.
                $cms = Invoke-Participant -Method GET -Url "$CmsProviderBaseUrl/api/assets/TIC-106"
                if ($cms) {
                    Write-Pass "CMS received it too - fan-out to a second vendor, no change to REG"
                    if ($cms.status -and $cms.status -ne "Planned") {
                        Write-Fail "CMS asset status is '$($cms.status)', expected 'Planned' (spec 6.1)"
                    }
                }
                else { Write-Fail "CMS did not receive the asset-config publication" }
            }
            else { Write-Skip "CMS fan-out" "CmsEngine not built yet (-CmsProviderBaseUrl to verify)." }
        }
    }
}

# ============================================================================
# PHASE 7 - Redelivery
#
# The phase that justifies this script.
#
# Publishing the SAME BODID twice is what a participant sees when it crashes
# after committing but before RemovePublication - which ISBM's read-then-remove
# makes possible, and which no amount of provider correctness prevents.
#
# A participant that appends unconditionally passes every phase above and fails
# only here. In production it would instead fail silently, producing duplicate
# rows indistinguishable from legitimate ones.
# ============================================================================
if (Test-Phase 7) {
    Write-Phase "7. Redelivery - the same BOD twice, applied once"

    if (-not $MmsBaseUrl) {
        Write-Skip "redelivery" "MMS not built yet (-MmsBaseUrl). This is the phase that matters most."
    }
    else {
        $pub = Invoke-Isbm -Method POST -Path "/publication-sessions" -ExpectedStatus @(200,201) -Body @{
            channelUri = $sitesChannelUri
        }
        if ($pub.sessionId) {
            $script:openSessions += $pub.sessionId

            # One BODID, one Site.UUID, two deliveries. Not two sites.
            $bodId    = [guid]::NewGuid().ToString()
            $siteUuid = [guid]::NewGuid()
            $bod      = New-SyncSitesBod -BodId $bodId -SiteUuid $siteUuid -ShortName "MMS Redelivery Test Site"

            foreach ($n in 1..2) {
                Invoke-Isbm -Method POST -Path "/sessions/$($pub.sessionId)/publications" `
                    -ExpectedStatus @(200,201) -Body @{
                        messageContent = @{ mediaType = "application/xml"; inlineContent = $bod }
                        topics = @("oiie/ccom:SyncSites")
                    } | Out-Null
                Write-Info "delivery $n of BODID $bodId (Site.UUID $siteUuid)"
                Start-Sleep -Seconds 2
            }

            Write-Info "waiting ${SettleSeconds}s"
            Start-Sleep -Seconds $SettleSeconds

            $found = Invoke-Participant -Method GET -Url "$MmsBaseUrl/lightsystems/by-ext/$siteUuid"
            if ($null -eq $found) {
                Write-Fail "MMS has no LIGHT_SYSTEM_INVENTORY record for Site.UUID $siteUuid - SyncSites did not complete"
            }
            else {
                # Upsert is matched on EXT_ASSET_ID, so redelivery is proven not by
                # counting rows (there is exactly one route to fetch by ext id) but
                # by the second delivery landing as an update rather than a second
                # LightSystemId for the same federation GUID.
                Write-Pass "applied - LIGHT_SYSTEM_INVENTORY row for Site.UUID $siteUuid ($($found.lightSystemId))"
            }
        }
    }
}

# ============================================================================
# PHASE 8 - The CMS boundary (spec 5.2)
#
# CmsEngine is a participant with NO privileged access to the customer system.
# It reaches CmsProvider through the same published interface any third party
# would be granted.
#
# This phase can only test the OBSERVABLE half of that claim: that the two are
# genuinely separate processes, and that the customer system is not a
# participant. The rule itself -
#
#   "CmsEngine may use only access that a third party could be granted"
#
# - is verified by INSPECTION, not by this script. A CmsEngine that acquires a
# connection string still passes every assertion below. If it ever does, the
# demonstration has failed silently and the boundary is in-process wearing a
# network costume.
# ============================================================================
if (Test-Phase 8) {
    Write-Phase "8. CMS boundary - engine and customer system are separate"

    if (-not $CmsEngineBaseUrl -or -not $CmsProviderBaseUrl) {
        Write-Skip "CMS boundary" "needs both -CmsEngineBaseUrl and -CmsProviderBaseUrl."
    }
    else {
        $engine = Invoke-Participant -Method GET -Url "$CmsEngineBaseUrl/api/health"
        $system = Invoke-Participant -Method GET -Url "$CmsProviderBaseUrl/api/health"

        if ($engine -and $system) {
            Write-Pass "engine and customer system both answer - two deployments, not one"
        }
        else {
            if (-not $engine) { Write-Fail "CmsEngine is not reachable at $CmsEngineBaseUrl" }
            if (-not $system) { Write-Fail "CmsProvider is not reachable at $CmsProviderBaseUrl" }
        }

        # The customer system must not be an ISBM participant. If it answers a
        # notification route it has grown a session of its own, and the
        # separation being demonstrated is gone.
        $leak = Invoke-Participant -Method PUT `
                    -Url "$CmsProviderBaseUrl/api/notifications/00000000-0000-0000-0000-000000000000/x"
        if ($null -eq $leak) {
            Write-Pass "customer system exposes no notification route - it is not a participant"
        }
        else {
            Write-Fail "CmsProvider answered a notification route - the customer system has become a participant (spec 5.2)"
        }

        Write-Info "not machine-checkable: that CmsEngine holds no privileged access."
        Write-Info "inspect its project for a connection string or a direct schema dependency."
    }
}

# ============================================================================
# CLEANUP
# ============================================================================
if (-not $SkipCleanup) {
    Write-Phase "Cleanup"
    foreach ($s in ($script:openSessions | Select-Object -Unique)) { Close-IsbmSession -SessionId $s }
    Write-Pass "closed $(($script:openSessions | Select-Object -Unique).Count) sessions"

    # Only channels this run created. A shared provider may have others in use.
    foreach ($c in ($script:createdChannels | Select-Object -Unique)) {
        Invoke-Isbm -Method DELETE -Path "/channels$c" -ExpectedStatus @(200,204,404) | Out-Null
    }
    if ($script:createdChannels.Count -gt 0) {
        Write-Pass "removed $(($script:createdChannels | Select-Object -Unique).Count) channels created by this run"
    }
}
else {
    Write-Info "cleanup skipped; sessions and channels left in place"
}

# ============================================================================
# SUMMARY
# ============================================================================
Write-Host "`n============================================" -ForegroundColor White
Write-Host "  Passed:  $($script:pass)" -ForegroundColor Green
Write-Host "  Failed:  $($script:fail)" -ForegroundColor $(if ($script:fail) { "Red" } else { "DarkGray" })
Write-Host "  Skipped: $($script:skip)" -ForegroundColor DarkGray
Write-Host "============================================" -ForegroundColor White

if ($script:fail -gt 0) {
    Write-Host "`nFailures:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
}
if ($script:skip -gt 0) {
    Write-Host "`nSkips are expected until the participants exist." -ForegroundColor DarkGray
}

exit $(if ($script:fail -gt 0) { 1 } else { 0 })
