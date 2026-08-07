#!/usr/bin/env pwsh
# ============================================================================
# ISBM Service Provider — End-to-End Test Script
#
# Runs through all ISBM flows: channels, security tokens, pub-sub,
# request-response, and notifications.
#
# Usage:
#   .\test-isbm.ps1                                    # localhost:7253
#   .\test-isbm.ps1 -BaseUrl "https://your-func.azurewebsites.net"
#   .\test-isbm.ps1 -ListenerUrl "https://webhook.site/your-id"
#   .\test-isbm.ps1 -SkipNotifications                # skip notification test
# ============================================================================

param(
    [string]$BaseUrl = "http://localhost:7253/api",
    [string]$ListenerUrl = "",
    [switch]$SkipNotifications,
    [switch]$SkipCleanup
)

$ErrorActionPreference = "Stop"
$headers = @{ "Content-Type" = "application/json" }

# Track created resources for cleanup
$script:createdChannels = @()
$script:openSessions = @()

function Write-Step { param([string]$msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Pass { param([string]$msg) Write-Host "  PASS: $msg" -ForegroundColor Green }
function Write-Fail { param([string]$msg) Write-Host "  FAIL: $msg" -ForegroundColor Red }
function Write-Info { param([string]$msg) Write-Host "  INFO: $msg" -ForegroundColor Yellow }

function Invoke-Isbm {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [int[]]$ExpectedStatus = @(200, 201, 204)
    )
    $url = "$BaseUrl$Path"
    $params = @{ Method = $Method; Uri = $url; Headers = $headers; UseBasicParsing = $true }
    if ($Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 }
        $params.Body = $json
    }
    try {
        $response = Invoke-WebRequest @params
        $status = $response.StatusCode
        if ($status -notin $ExpectedStatus) {
            Write-Fail "$Method $Path → $status (expected $($ExpectedStatus -join '/'))"
            return $null
        }
        if ($response.Content -and $response.Content.Length -gt 0) {
            return $response.Content | ConvertFrom-Json
        }
        return @{ _status = $status }
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        $detail = ""
        try { $detail = $_.ErrorDetails.Message } catch {}
        if ($status -in $ExpectedStatus) {
            return @{ _status = $status; _detail = $detail }
        }
        Write-Fail "$Method $Path → $status $detail"
        return $null
    }
}

# ============================================================================
# 1. CONFIGURATION DISCOVERY
# ============================================================================
Write-Step "1. Configuration Discovery"

$config = Invoke-Isbm -Method GET -Path "/configuration/supported-operations"
if ($config) {
    Write-Pass "GetSupportedOperations"
    Write-Info "Security Level: $($config.securityLevelConformance)"
    Write-Info "XML Filtering: $($config.isXmlFilteringEnabled), JSON Filtering: $($config.isJsonFilteringEnabled)"
    Write-Info "Conformance: $($config.conformanceStatement)"
}

$security = Invoke-Isbm -Method GET -Path "/configuration/security-details"
if ($security) {
    Write-Pass "GetSecurityDetails"
    Write-Info "TLS: $($security.isTlsEnabled), KMS: $($security.isKeyManagementServiceEnabled)"
}

# ============================================================================
# 2. CHANNEL MANAGEMENT
# ============================================================================
Write-Step "2. Channel Management"

# Create Publication channel
$pubChannel = Invoke-Isbm -Method POST -Path "/channels" -Body @{
    channelUri  = "test/pub-sub-channel"
    channelType = "Publication"
    description = "Test publication channel"
} -ExpectedStatus @(201)
if ($pubChannel) {
    Write-Pass "CreateChannel (Publication): $($pubChannel.channelUri)"
    $script:createdChannels += "test/pub-sub-channel"
}

# Create Request channel
$reqChannel = Invoke-Isbm -Method POST -Path "/channels" -Body @{
    channelUri  = "test/request-channel"
    channelType = "Request"
    description = "Test request channel"
} -ExpectedStatus @(201)
if ($reqChannel) {
    Write-Pass "CreateChannel (Request): $($reqChannel.channelUri)"
    $script:createdChannels += "test/request-channel"
}

# Duplicate channel (should fail)
$dup = Invoke-Isbm -Method POST -Path "/channels" -Body @{
    channelUri  = "test/pub-sub-channel"
    channelType = "Publication"
} -ExpectedStatus @(422)
if ($dup) { Write-Pass "Duplicate channel rejected (422)" }

# Get all channels
$channels = Invoke-Isbm -Method GET -Path "/channels"
if ($channels) {
    Write-Pass "GetChannels: $($channels.Count) channel(s)"
}

# Get single channel
$single = Invoke-Isbm -Method GET -Path "/channels/test/pub-sub-channel"
if ($single -and $single.channelUri -eq "test/pub-sub-channel") {
    Write-Pass "GetChannel: $($single.channelUri)"
}

# ============================================================================
# 3. PUBLISH-SUBSCRIBE FLOW
# ============================================================================
Write-Step "3. Publish-Subscribe Flow"

# Open publication session
$pubSession = Invoke-Isbm -Method POST -Path "/publication-sessions" -Body @{
    channelUri = "test/pub-sub-channel"
} -ExpectedStatus @(201)
if ($pubSession) {
    Write-Pass "OpenPublicationSession: $($pubSession.sessionId)"
    $pubSessionId = $pubSession.sessionId
}

# Open subscription session (with ListenerURL if provided)
$subBody = @{
    channelUri        = "test/pub-sub-channel"
    topics            = @("AssetEvent", "Alert")
    listenerUrl       = if ($ListenerUrl) { $ListenerUrl } else { $null }
    expirationListenerUrl = $null
    filterExpressions = @()
    filterNamespaces  = @{}
}
$subSession = Invoke-Isbm -Method POST -Path "/subscription-sessions" -Body $subBody -ExpectedStatus @(201)
if ($subSession) {
    Write-Pass "OpenSubscriptionSession: $($subSession.sessionId)"
    if ($ListenerUrl) { Write-Info "ListenerURL: $ListenerUrl" }
    $subSessionId = $subSession.sessionId
}

# Small delay for Service Bus subscription to be ready
Start-Sleep -Seconds 2

# Post publication
$pub = Invoke-Isbm -Method POST -Path "/sessions/$pubSessionId/publications" -Body @{
    messageContent = @{
        mediaType     = "application/xml"
        inlineContent = "<AssetEvent><Type>Install</Type><AssetId>PUMP-4421</AssetId><Site>PlantA</Site></AssetEvent>"
    }
    topics = @("AssetEvent")
    expiry = "P7D"
} -ExpectedStatus @(201)
if ($pub) {
    Write-Pass "PostPublication: messageId=$($pub.messageId)"
    $pubMessageId = $pub.messageId
}

# Small delay for message delivery
Start-Sleep -Seconds 2

# Read publication
$read = Invoke-Isbm -Method GET -Path "/sessions/$subSessionId/publication"
if ($read -and $read.messageId) {
    Write-Pass "ReadPublication: messageId=$($read.messageId)"
    Write-Info "Topics: $($read.topics -join ', ')"
    Write-Info "Content: $($read.messageContent.inlineContent)"
    if ($ListenerUrl) {
        Write-Info "Check your listener endpoint for the notification callback"
    }
} else {
    Write-Fail "ReadPublication: no message returned (404)"
}

# Remove publication
$remove = Invoke-Isbm -Method DELETE -Path "/sessions/$subSessionId/publication" -ExpectedStatus @(204)
if ($remove) { Write-Pass "RemovePublication" }

# Verify queue is now empty
$empty = Invoke-Isbm -Method GET -Path "/sessions/$subSessionId/publication" -ExpectedStatus @(404)
if ($empty) { Write-Pass "ReadPublication after Remove: 404 (empty, correct)" }

# Close sessions
$close1 = Invoke-Isbm -Method DELETE -Path "/publication-sessions/$pubSessionId" -ExpectedStatus @(204)
if ($close1) { Write-Pass "ClosePublicationSession" }

$close2 = Invoke-Isbm -Method DELETE -Path "/subscription-sessions/$subSessionId" -ExpectedStatus @(204)
if ($close2) { Write-Pass "CloseSubscriptionSession" }

# ============================================================================
# 4. REQUEST-RESPONSE FLOW
# ============================================================================
Write-Step "4. Request-Response Flow"

# Open provider request session
$provBody = @{
    channelUri        = "test/request-channel"
    topics            = @("UsageReading")
    listenerUrl       = if ($ListenerUrl) { $ListenerUrl } else { $null }
    expirationListenerUrl = $null
    filterExpressions = @()
}
$provSession = Invoke-Isbm -Method POST -Path "/provider-request-sessions" -Body $provBody -ExpectedStatus @(201)
if ($provSession) {
    Write-Pass "OpenProviderRequestSession: $($provSession.sessionId)"
    $provSessionId = $provSession.sessionId
}

# Open consumer request session
$consSession = Invoke-Isbm -Method POST -Path "/consumer-request-sessions" -Body @{
    channelUri  = "test/request-channel"
    listenerUrl = if ($ListenerUrl) { $ListenerUrl } else { $null }
} -ExpectedStatus @(201)
if ($consSession) {
    Write-Pass "OpenConsumerRequestSession: $($consSession.sessionId)"
    $consSessionId = $consSession.sessionId
}

Start-Sleep -Seconds 2

# Consumer posts a request
$request = Invoke-Isbm -Method POST -Path "/sessions/$consSessionId/requests" -Body @{
    messageContent = @{
        mediaType     = "application/xml"
        inlineContent = "<GetUsageReadings><AssetId>PUMP-4421</AssetId><From>2026-07-01</From><To>2026-07-23</To></GetUsageReadings>"
    }
    topics = @("UsageReading")
    expiry = "PT1H"
} -ExpectedStatus @(201)
if ($request) {
    Write-Pass "PostRequest: messageId=$($request.messageId)"
    $requestMessageId = $request.messageId
}

Start-Sleep -Seconds 2

# Provider reads the request
$readReq = Invoke-Isbm -Method GET -Path "/sessions/$provSessionId/request"
if ($readReq -and $readReq.messageId) {
    Write-Pass "ReadRequest: messageId=$($readReq.messageId)"
    Write-Info "Content: $($readReq.messageContent.inlineContent)"
}

# Provider removes the request
$removeReq = Invoke-Isbm -Method DELETE -Path "/sessions/$provSessionId/request" -ExpectedStatus @(204)
if ($removeReq) { Write-Pass "RemoveRequest" }

# Provider posts a response
$response = Invoke-Isbm -Method POST -Path "/sessions/$provSessionId/requests/$requestMessageId/response" -Body @{
    messageContent = @{
        mediaType     = "application/xml"
        inlineContent = "<UsageReadings><Reading date='2026-07-15' value='1842.5' unit='kWh'/><Reading date='2026-07-22' value='1903.1' unit='kWh'/></UsageReadings>"
    }
} -ExpectedStatus @(201)
if ($response) {
    Write-Pass "PostResponse: messageId=$($response.messageId)"
}

Start-Sleep -Seconds 2

# Consumer reads the response
$readResp = Invoke-Isbm -Method GET -Path "/sessions/$consSessionId/requests/$requestMessageId/response"
if ($readResp -and $readResp.messageId) {
    Write-Pass "ReadResponse: messageId=$($readResp.messageId)"
    Write-Info "Content: $($readResp.messageContent.inlineContent)"
}

# Consumer removes the response
$removeResp = Invoke-Isbm -Method DELETE -Path "/sessions/$consSessionId/requests/$requestMessageId/response" -ExpectedStatus @(204)
if ($removeResp) { Write-Pass "RemoveResponse" }

# Close sessions
$closeP = Invoke-Isbm -Method DELETE -Path "/provider-request-sessions/$provSessionId" -ExpectedStatus @(204)
if ($closeP) { Write-Pass "CloseProviderRequestSession" }

$closeC = Invoke-Isbm -Method DELETE -Path "/consumer-request-sessions/$consSessionId" -ExpectedStatus @(204)
if ($closeC) { Write-Pass "CloseConsumerRequestSession" }

# ============================================================================
# 5. NOTIFICATION TEST (if ListenerURL provided)
# ============================================================================
if (-not $SkipNotifications -and $ListenerUrl) {
    Write-Step "5. Notification Test (dedicated)"

    # Open subscription with listener
    $notifySub = Invoke-Isbm -Method POST -Path "/subscription-sessions" -Body @{
        channelUri        = "test/pub-sub-channel"
        topics            = @("NotifyTest")
        listenerUrl       = $ListenerUrl
        expirationListenerUrl = $null
        filterExpressions = @()
        filterNamespaces  = @{}
    } -ExpectedStatus @(201)
    if ($notifySub) {
        Write-Pass "Subscription with ListenerURL: $($notifySub.sessionId)"
    }

    # Open pub session
    $notifyPub = Invoke-Isbm -Method POST -Path "/publication-sessions" -Body @{
        channelUri = "test/pub-sub-channel"
    } -ExpectedStatus @(201)

    Start-Sleep -Seconds 2

    # Publish — should trigger notification
    $notifyMsg = Invoke-Isbm -Method POST -Path "/sessions/$($notifyPub.sessionId)/publications" -Body @{
        messageContent = @{
            mediaType     = "application/xml"
            inlineContent = "<NotifyTest><Message>This should trigger a notification</Message></NotifyTest>"
        }
        topics = @("NotifyTest")
        expiry = "P1D"
    } -ExpectedStatus @(201)
    if ($notifyMsg) {
        Write-Pass "Published notification test message: $($notifyMsg.messageId)"
        Write-Info "Check $ListenerUrl for PUT /notifications/$($notifySub.sessionId)/$($notifyMsg.messageId)"
    }

    Start-Sleep -Seconds 5
    Write-Info "Waiting 5s for notification delivery..."

    # Cleanup
    Invoke-Isbm -Method DELETE -Path "/publication-sessions/$($notifyPub.sessionId)" -ExpectedStatus @(204) | Out-Null
    Invoke-Isbm -Method DELETE -Path "/subscription-sessions/$($notifySub.sessionId)" -ExpectedStatus @(204) | Out-Null
    Write-Pass "Notification test sessions closed"
}
elseif (-not $SkipNotifications) {
    Write-Step "5. Notification Test — SKIPPED (no -ListenerUrl provided)"
    Write-Info "Run with: .\test-isbm.ps1 -ListenerUrl 'https://webhook.site/your-id'"
}

# ============================================================================
# 6. SECURED CHANNEL TEST
# ============================================================================
Write-Step "6. Secured Channel Test"

$secureUser = "TestApp"
$securePass = "S3cure!Token"
$basicAuth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${secureUser}:${securePass}"))

# Create a secured channel with initial tokens
$secChannel = Invoke-Isbm -Method POST -Path "/channels" -Body @{
    channelUri     = "test/secure-channel"
    channelType    = "Publication"
    description    = "Secured channel for testing"
    securityTokens = @( @{ username = $secureUser; password = $securePass } )
} -ExpectedStatus @(201)
if ($secChannel) {
    Write-Pass "CreateChannel with initial token"
    if ($secChannel.securityTokenIds.Count -gt 0) {
        Write-Info "Token stored: $($secChannel.securityTokenIds[0])"
    }
    $script:createdChannels += "test/secure-channel"
}

# Try to open a session WITHOUT auth → should get 401
$noAuth = Invoke-Isbm -Method POST -Path "/publication-sessions" -Body @{
    channelUri = "test/secure-channel"
} -ExpectedStatus @(401)
if ($noAuth) {
    Write-Pass "Open session without auth rejected (401)"
}

# Open a session WITH auth → should succeed
$authHeaders = @{ "Content-Type" = "application/json"; "Authorization" = "Basic $basicAuth" }
try {
    $authBody = @{ channelUri = "test/secure-channel" } | ConvertTo-Json
    $authResponse = Invoke-WebRequest -Method POST -Uri "$BaseUrl/publication-sessions" `
        -Headers $authHeaders -Body $authBody -UseBasicParsing
    if ($authResponse.StatusCode -eq 201) {
        $authSession = $authResponse.Content | ConvertFrom-Json
        Write-Pass "Open session with auth: $($authSession.sessionId)"

        # Publish on the secured channel
        $pubBody = @{
            messageContent = @{
                mediaType     = "application/xml"
                inlineContent = "<SecureMessage><Data>Confidential</Data></SecureMessage>"
            }
            topics = @("SecureTest")
            expiry = "P1D"
        } | ConvertTo-Json -Depth 10
        $pubResponse = Invoke-WebRequest -Method POST `
            -Uri "$BaseUrl/sessions/$($authSession.sessionId)/publications" `
            -Headers $authHeaders -Body $pubBody -UseBasicParsing
        if ($pubResponse.StatusCode -eq 201) {
            $pubMsg = $pubResponse.Content | ConvertFrom-Json
            Write-Pass "PostPublication on secured channel: $($pubMsg.messageId)"
        }

        # Close the session
        Invoke-WebRequest -Method DELETE `
            -Uri "$BaseUrl/publication-sessions/$($authSession.sessionId)" `
            -Headers $authHeaders -UseBasicParsing | Out-Null
        Write-Pass "Session closed"
    }
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    Write-Fail "Open session with auth failed: $status"
}

# Delete the secured channel (requires auth)
try {
    Invoke-WebRequest -Method DELETE -Uri "$BaseUrl/channels/test/secure-channel" `
        -Headers $authHeaders -UseBasicParsing | Out-Null
    Write-Pass "DeleteChannel with auth (secured channel removed)"
    # Remove from cleanup list since we already deleted it
    $script:createdChannels = $script:createdChannels | Where-Object { $_ -ne "test/secure-channel" }
}
catch {
    Write-Fail "DeleteChannel with auth failed"
}

# ============================================================================
# 7. CLEANUP
# ============================================================================
if (-not $SkipCleanup) {
    Write-Step "6. Cleanup"
    foreach ($ch in $script:createdChannels) {
        $del = Invoke-Isbm -Method DELETE -Path "/channels/$ch" -ExpectedStatus @(204, 404)
        if ($del) { Write-Pass "Deleted channel: $ch" }
    }
}
else {
    Write-Step "6. Cleanup — SKIPPED (-SkipCleanup)"
}

# ============================================================================
# SUMMARY
# ============================================================================
Write-Host "`n" -NoNewline
Write-Host "============================================" -ForegroundColor White
Write-Host " ISBM End-to-End Test Complete" -ForegroundColor White
Write-Host "============================================" -ForegroundColor White
Write-Host ""
Write-Host "Tested:" -ForegroundColor White
Write-Host "  - Configuration Discovery (supported-operations, security-details)"
Write-Host "  - Channel Management (create, get, list, duplicate rejection, delete)"
Write-Host "  - Pub-Sub (open, publish, read, remove, close)"
Write-Host "  - Request-Response (open, post request, read, respond, read response, close)"
Write-Host "  - Secured Channels (create with token, reject without auth, accept with auth)"
if ($ListenerUrl -and -not $SkipNotifications) {
    Write-Host "  - Notifications (publish → listener callback)" -ForegroundColor Green
}
Write-Host ""
