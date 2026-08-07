<#
.SYNOPSIS
    End-to-end test of the Annex A message model over ws-ISBM.

.DESCRIPTION
    Acts as an ISBM consumer against the same channels the CIR provider listens
    on, so it exercises the whole path: consumer posts a BOD, the CIR provider
    drains it, dispatches it, and posts the response BOD back.

    This is an integration test, not part of test-cir.ps1: it needs a live ws-ISBM
    provider with the two channels already created.

    Channel setup, if not done yet:

        Invoke-RestMethod "$isbm/channels" -Method Post -Headers $h `
            -ContentType application/json -Body (@{
                channelUri = '/OIIE/CIR/Request'; channelType = 'Request'
            } | ConvertTo-Json)

        Invoke-RestMethod "$isbm/channels" -Method Post -Headers $h `
            -ContentType application/json -Body (@{
                channelUri = '/OIIE/CIR/Publication'; channelType = 'Publication'
            } | ConvertTo-Json)

.EXAMPLE
    .\test-isbm-roundtrip.ps1 -CirApp cir-func-44p2f3n6 -IsbmApp isbm-func-44p2f3n6dv7p4 `
        -ResourceGroup HilmarRetiefRG
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CirApp,
    [Parameter(Mandatory = $true)][string] $IsbmApp,
    [Parameter(Mandatory = $true)][string] $ResourceGroup,

    [string] $RequestChannelUri = '/OIIE/CIR/Request',
    [string] $PublicationChannelUri = '/OIIE/CIR/Publication',
    [string] $Topic = 'ws-CIR',
    [string] $RegistryId = 'ISBM-Roundtrip',

    [switch] $Detailed
)

$ErrorActionPreference = 'Stop'

$cirNs = 'http://www.openoandm.org/ws-cir/'
$oaNs = 'http://www.openapplications.org/oagis/9'
$testCirid = '8B907609-5955-4694-B244-107B0101F22F'

# ---------------------------------------------------------------------------

Write-Host 'Fetching function keys...' -ForegroundColor DarkGray
$cirKey = az functionapp keys list -g $ResourceGroup -n $CirApp --query functionKeys.default -o tsv
if ($LASTEXITCODE -ne 0) { throw "Could not read the key for $CirApp." }
$isbmKey = az functionapp keys list -g $ResourceGroup -n $IsbmApp --query functionKeys.default -o tsv
if ($LASTEXITCODE -ne 0) { throw "Could not read the key for $IsbmApp." }

$cir = "https://$CirApp.azurewebsites.net/api"
$isbm = "https://$IsbmApp.azurewebsites.net/api"

$script:Passed = 0
$script:Failed = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Api {
    param([string] $Method, [string] $Uri, [string] $Key, $Body)

    $req = @{
        Method             = $Method
        Uri                = $Uri
        Headers            = @{ 'x-functions-key' = $Key }
        SkipHttpErrorCheck = $true
        ErrorAction        = 'Stop'
    }
    if ($null -ne $Body) {
        $json = ConvertTo-Json -InputObject $Body -Depth 12
        $req.Body = [Text.Encoding]::UTF8.GetBytes($json)
        $req.ContentType = 'application/json'
        if ($Detailed) { Write-Host "  -> $Method $Uri`n     $json" -ForegroundColor DarkGray }
    }
    elseif ($Detailed) { Write-Host "  -> $Method $Uri" -ForegroundColor DarkGray }

    $response = Invoke-WebRequest @req

    $raw = $response.Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        try { $parsed = $raw | ConvertFrom-Json } catch { }
    }
    if ($Detailed -and $raw) { Write-Host "  <- $raw" -ForegroundColor DarkGray }

    return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $parsed; Raw = $raw }
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

function Assert-True { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw $Message } }
function Assert-Equal { param($Expected, $Actual, [string] $What = 'value')
    if ($Expected -ne $Actual) { throw "expected $What '$Expected', got '$Actual'" } }

function New-Bod {
    param([string] $Name, [string] $DataArea)
    return @"
<$Name xmlns="$cirNs" xmlns:oa="$oaNs" releaseID="1.2.1" versionID="1.0">
  <oa:ApplicationArea>
    <oa:Sender><oa:LogicalID>test-isbm-roundtrip</oa:LogicalID></oa:Sender>
    <oa:CreationDateTime>$( (Get-Date).ToUniversalTime().ToString('o') )</oa:CreationDateTime>
    <oa:BODID>$( [guid]::NewGuid() )</oa:BODID>
  </oa:ApplicationArea>
  <DataArea>
$DataArea
  </DataArea>
</$Name>
"@
}

<#
    Opens an ISBM session.

    Topics belong only to sessions that FILTER what they read:

      ProviderRequest  topics -> which requests this provider will read
      Subscription     topics -> which publications this subscriber receives
      ConsumerRequest  none   -> topics are supplied on each PostRequest
      Publication      none   -> topics are supplied on each PostPublication

    This script opens the latter two, so it sends no topics. The provider runs
    with UnmappedMemberHandling.Disallow, so sending them is a 400
    DeserializationError rather than a harmless extra member.
#>
function Open-IsbmSession {
    param([string] $Route, [string] $ChannelUri, [switch] $WithTopics)

    $body = @{ channelUri = $ChannelUri }
    if ($WithTopics) { $body.topics = @($Topic) }

    $r = Invoke-Api POST "$isbm/$Route" $isbmKey $body
    if ($r.Status -ge 300) { throw "open $Route failed with $($r.Status): $($r.Raw)" }

    $id = $r.Body.sessionId
    if (-not $id) { $id = $r.Body.SessionID }
    if (-not $id) { $id = $r.Body.id }
    if (-not $id) { throw "no session id in: $($r.Raw)" }

    # The provider now confirms the entity is open before returning the id, so no
    # settling delay is needed. Invoke-SessionApi still retries on a Session fault
    # in case the target has not adopted that fix.
    return $id
}

<#
    Retries a session-scoped call while the provider still reports the session as
    missing. Same race as above, and it can surface on the first use rather than
    on the open.
#>
function Invoke-SessionApi {
    param([string] $Method, [string] $Uri, [string] $Key, $Body, [int] $Attempts = 3)

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $r = Invoke-Api $Method $Uri $Key $Body
        if ($r.Status -lt 300) { return $r }

        $isSessionFault = $r.Raw -match '"fault"\s*:\s*"Session"'
        if (-not $isSessionFault -or $attempt -eq $Attempts) { return $r }

        Write-Host "     session not visible yet (attempt $attempt); retrying in 3s" -ForegroundColor DarkGray
        Start-Sleep -Seconds 3
    }
}

function Close-IsbmSession {
    param([string] $Collection, [string] $SessionId)
    # There is no shared DELETE sessions/{id}; each type closes on its own route.
    if ($SessionId) { Invoke-Api DELETE "$isbm/$Collection/$SessionId" $isbmKey | Out-Null }
}

Write-Host "`nws-CIR over ws-ISBM round trip" -ForegroundColor White
Write-Host "CIR      : $cir"
Write-Host "ISBM     : $isbm"
Write-Host "Channels : $RequestChannelUri | $PublicationChannelUri"
Write-Host "Topic    : $Topic"

# Leave nothing behind from an aborted run.
Invoke-Api DELETE "$cir/registries/$RegistryId" $cirKey | Out-Null

$consumerSession = $null
$publisherSession = $null

try {
    # -----------------------------------------------------------------------
    Write-Host "`n01  Provider is listening" -ForegroundColor Cyan

    # A Function App can still be starting right after a publish, which shows up
    # as 503 "The service is unavailable" rather than a cold-start delay.
    $status = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $status = Invoke-Api GET "$cir/isbm/status" $cirKey
        if ($status.Status -eq 200) { break }
        Write-Host "     provider not ready ($($status.Status)); waiting 10s" -ForegroundColor DarkGray
        Start-Sleep -Seconds 10
    }

    Test-Case 'ISBM binding is enabled' {
        Assert-Equal 200 $status.Status 'status code'
        Assert-True ([bool]$status.Body.enabled) 'Isbm__Enabled is false; the provider will not poll'
    }
    # Sessions are opened lazily on the first drain, so establish them rather
    # than assuming an earlier run left some behind — /isbm/reset clears them.
    $warmup = Invoke-Api POST "$cir/isbm/drain" $cirKey
    Test-Case 'an initial drain opens sessions without error' {
        Assert-Equal 200 $warmup.Status 'status code'
        Assert-Equal 0 @($warmup.Body.errors).Count "errors: $($warmup.Body.errors -join '; ')"
    }

    $status = Invoke-Api GET "$cir/isbm/status" $cirKey
    Test-Case 'a session is open on each channel' {
        $kinds = @($status.Body.sessions | ForEach-Object { $_.kind })
        Assert-True ($kinds -contains 'ProviderRequest') "expected a ProviderRequest session: $($status.Raw)"
        Assert-True ($kinds -contains 'Subscription') "expected a Subscription session: $($status.Raw)"
    }

    # -----------------------------------------------------------------------
    Write-Host "`n02  Request-response: ProcessRegistry -> AcknowledgeRegistry" -ForegroundColor Cyan

    $consumerSession = Open-IsbmSession 'consumer-request-sessions' $RequestChannelUri
    Write-Host "  consumer session: $consumerSession" -ForegroundColor DarkGray

    $bod = New-Bod 'ProcessRegistry' @"
    <oa:Process acknowledgeCode="Always"/>
    <CreateRegistry>
      <Registry>
        <ID>$RegistryId</ID>
        <Category>
          <ID>Asset</ID>
          <CategorySourceID>MIMOSA OSA-EAI V3</CategorySourceID>
          <Entry>
            <IDInSource>BBHV0013</IDInSource>
            <SourceID>Asset Register</SourceID>
            <CIRID>$testCirid</CIRID>
            <Name>Boiler Blowdown Hand Valve 13</Name>
          </Entry>
        </Category>
      </Registry>
      <CreateCIRID>false</CreateCIRID>
    </CreateRegistry>
"@

    $post = Invoke-SessionApi POST "$isbm/sessions/$consumerSession/requests" $isbmKey @{
        messageContent = @{ mediaType = 'application/xml'; inlineContent = $bod }
        topics         = @($Topic)
        expiry         = 'P1D'
    }
    Test-Case 'consumer posts the request' {
        Assert-True ($post.Status -lt 300) "post request returned $($post.Status): $($post.Raw)"
    }

    # The response is read back by request message id, not by session alone.
    $requestMessageId = $post.Body.messageId
    if (-not $requestMessageId) { $requestMessageId = $post.Body.MessageID }
    Test-Case 'the post returns a request message id' {
        Assert-True ($null -ne $requestMessageId) "no messageId in: $($post.Raw)"
    }

    $drain = Invoke-Api POST "$cir/isbm/drain" $cirKey
    if ($drain.Body.discarded) {
        Write-Host '  DISCARDED PAYLOADS:' -ForegroundColor Yellow
        $drain.Body.discarded | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    }
    Test-Case 'provider drains without error' {
        Assert-Equal 0 @($drain.Body.errors).Count "errors: $($drain.Body.errors -join '; ')"
    }
    Test-Case 'provider handled one request and posted one response' {
        Assert-Equal 1 $drain.Body.requestsHandled 'requestsHandled'
        Assert-Equal 1 $drain.Body.responsesPosted 'responsesPosted'
    }

    Test-Case 'the BOD reached the registry' {
        $q = Invoke-Api POST "$cir/queries/registry" $cirKey @{
            filter = @(@{ registryFilter = @{ id = $RegistryId } })
        }
        $entries = @($q.Body.registry | ForEach-Object { $_.categories } |
                     ForEach-Object { $_.entries } | Where-Object { $_ })
        Assert-Equal 1 $entries.Count 'entry count'
        Assert-Equal $testCirid.ToLower() ([string]$entries[0].cirid).ToLower() 'cirid'
    }

    # The provider needs a moment to post the response after the drain returns.
    Start-Sleep -Seconds 2
    $read = Invoke-SessionApi GET "$isbm/sessions/$consumerSession/requests/$requestMessageId/response" $isbmKey
    Test-Case 'consumer receives a response' {
        Assert-True ($read.Status -lt 300 -and $read.Body) "read response returned $($read.Status): $($read.Raw)"
    }
    Test-Case 'the response is an AcknowledgeRegistry with no faults' {
        $content = $read.Body.messageContent.inlineContent
        if (-not $content) { $content = $read.Body.messageContent.content }
        Assert-True ($null -ne $content) "no message content in: $($read.Raw)"

        $xml = [xml]$content
        Assert-Equal 'AcknowledgeRegistry' $xml.DocumentElement.LocalName 'root element'
        Assert-Equal '1.2.1' $xml.DocumentElement.releaseID 'releaseID'
        Assert-True ($null -eq $xml.DocumentElement.DataArea.DuplicateEntryFault) 'unexpected fault'
    }
    Test-Case 'OriginalApplicationArea correlates the response' {
        $content = $read.Body.messageContent.inlineContent
        if (-not $content) { $content = $read.Body.messageContent.content }
        $xml = [xml]$content
        $orig = $xml.DocumentElement.DataArea.Acknowledge.OriginalApplicationArea
        Assert-True ($null -ne $orig) 'expected OriginalApplicationArea inside the Acknowledge verb'
        Assert-Equal 'test-isbm-roundtrip' $orig.Sender.LogicalID 'echoed sender'
    }

    Invoke-Api DELETE "$isbm/sessions/$consumerSession/requests/$requestMessageId/response" $isbmKey | Out-Null

    # -----------------------------------------------------------------------
    Write-Host "`n03  Publication: CancelRegistry has no response" -ForegroundColor Cyan

    $publisherSession = Open-IsbmSession 'publication-sessions' $PublicationChannelUri
    Write-Host "  publication session: $publisherSession" -ForegroundColor DarkGray

    $cancel = New-Bod 'CancelRegistry' @"
    <oa:Cancel/>
    <DeleteRegistry>
      <RegistryID>$RegistryId</RegistryID>
    </DeleteRegistry>
"@

    $publish = Invoke-SessionApi POST "$isbm/sessions/$publisherSession/publications" $isbmKey @{
        messageContent = @{ mediaType = 'application/xml'; inlineContent = $cancel }
        topics         = @($Topic)
        expiry         = 'P1D'
    }
    Test-Case 'publisher posts the Cancel BOD' {
        Assert-True ($publish.Status -lt 300) "post publication returned $($publish.Status): $($publish.Raw)"
    }

    Start-Sleep -Seconds 2
    $drain2 = Invoke-Api POST "$cir/isbm/drain" $cirKey
    if ($drain2.Body.discarded) {
        Write-Host '  DISCARDED PAYLOADS:' -ForegroundColor Yellow
        $drain2.Body.discarded | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    }
    Test-Case 'provider handles the publication' {
        Assert-Equal 0 @($drain2.Body.errors).Count "errors: $($drain2.Body.errors -join '; ')"
        Assert-Equal 1 $drain2.Body.publicationsHandled 'publicationsHandled'
    }
    Test-Case 'no response was posted for a Cancel BOD' {
        Assert-Equal 0 $drain2.Body.responsesPosted 'responsesPosted'
    }
    Test-Case 'the registry was deleted' {
        $q = Invoke-Api POST "$cir/queries/registry" $cirKey @{
            filter = @(@{ registryFilter = @{ id = $RegistryId } })
        }
        $entries = @($q.Body.registry | ForEach-Object { $_.categories } |
                     ForEach-Object { $_.entries } | Where-Object { $_ })
        Assert-Equal 0 $entries.Count 'entry count'
    }
}
finally {
    Close-IsbmSession 'consumer-request-sessions' $consumerSession
    Close-IsbmSession 'publication-sessions' $publisherSession
    Invoke-Api DELETE "$cir/registries/$RegistryId" $cirKey | Out-Null
}

$total = $script:Passed + $script:Failed
Write-Host "`n$('-' * 60)"
Write-Host "Passed: $($script:Passed) / $total" -ForegroundColor Green

if ($script:Failed -gt 0) {
    Write-Host "Failed: $($script:Failed)" -ForegroundColor Red
    Write-Host ''
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Round trip complete: ws-CIR BODs are flowing over ws-ISBM.' -ForegroundColor Green
exit 0
