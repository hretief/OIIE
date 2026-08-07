using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using IsbmProvider.Http;
using IsbmProvider.Models;

namespace IsbmProvider.Functions;

/// <summary>
/// ISBM Configuration Discovery Service (spec §5.9). Lets clients check compatibility up front —
/// including this deployment's REST-only partial-conformance statement.
/// </summary>
public sealed class ConfigurationDiscoveryFunctions(IConfiguration config)
{
    // GET /configuration/supported-operations  — GetSupportedOperations
    [Function("GetSupportedOperations")]
    public async Task<HttpResponseData> SupportedOperations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "configuration/supported-operations")] HttpRequestData req)
    {
        var level = int.TryParse(config["Isbm:SecurityLevelConformance"], out var l) ? l : (int)SecurityLevel.InterEnterprise;
        var body = new SupportedOperations
        {
            SecurityLevelConformance = level,
            DefaultExpiryDuration = config["Isbm:DefaultExpiryDuration"] ?? "P30D",
            AdditionalInformationUrl = config["Isbm:AdditionalInformationUrl"]
        };
        return await req.JsonAsync(body);
    }

    // GET /configuration/security-details  — GetSecurityDetails (401 without a valid token)
    [Function("GetSecurityDetails")]
    public async Task<HttpResponseData> SecurityDetails(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "configuration/security-details")] HttpRequestData req)
    {
        // TODO: require a valid SecurityToken via HTTP auth header; SecurityTokenFault (401) otherwise.
        var level = int.TryParse(config["Isbm:SecurityLevelConformance"], out var l) ? l : 3;
        var body = new Models.SecurityDetails
        {
            IsTlsEnabled = true,
            IsSecurityTokenRequired = level >= 3,
            IsSecurityTokenEncryptionEnabled = level >= 2,
            IsCertificateRequired = level >= 3,
            IsRbacEnabled = level >= 3,
            IsKeyManagementServiceEnabled = level >= 2,
            IsEndToEndMessageEncryptionEnabled = level >= 4
        };
        return await req.JsonAsync(body);
    }
}
