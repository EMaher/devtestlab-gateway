using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Flurl;
using Flurl.Http;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;

namespace RDGatewayAPI
{
    public static class CreateToken
    {
        private const string AZURE_MANAGEMENT_API = "https://management.azure.com/";
        private const string MACHINE_TOKEN_PATTERN = "Host={0}&Port={1}&ExpiresOn={2}";
        private const string AUTH_TOKEN_PATTERN = "{0}&Signature=1|SHA256|{1}|{2}";

        private static readonly AzureServiceTokenProvider AzureManagementApiTokenProvider = new AzureServiceTokenProvider();
        private static readonly DateTime PosixBaseTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);

        private static async Task<X509Certificate2> GetCertificateAsync(RequestContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            try
            {
                // init a key vault client
                var keyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(AzureManagementApiTokenProvider.KeyVaultTokenCallback));

                // get the base64 encoded secret and decode
                var signCertificateSecret = await keyVaultClient.GetSecretAsync(context.SignCertificateUrl);
                var signCertificateBuffer = Convert.FromBase64String(signCertificateSecret.Value);

                context.Log.Info($"Downloaded certificate from KeyVault ({signCertificateBuffer.Length} bytes)");

                // unwrap the json data envelope
                var envelope = JsonConvert.DeserializeAnonymousType(Encoding.UTF8.GetString(signCertificateBuffer), new { data = string.Empty, password = string.Empty });

                // return the certificate
                return new X509Certificate2(Convert.FromBase64String(envelope.data), envelope.password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (Exception exc)
            {
                context.Log.Error(exc.Message, exc);

                throw new Exception($"Failed to load certificate from KeyVault by URL '{context.SignCertificateUrl}'", exc);
            }
        }

        private static async Task<DnsEndPoint> GetRDPEndPoint(RequestContext requestContext, string computeVmResourceId)
        {
            if (requestContext == null)
            {
                throw new ArgumentNullException(nameof(requestContext));
            }

            if (computeVmResourceId == null)
            {
                throw new ArgumentNullException(nameof(computeVmResourceId));
            }

            // request token for Azure communication
            var azureToken = await AzureManagementApiTokenProvider.GetAccessTokenAsync(AZURE_MANAGEMENT_API);

            dynamic computeVmResource = await AZURE_MANAGEMENT_API
                .AppendPathSegment(computeVmResourceId)
                .SetQueryParam("api-version", "2016-05-15")
                .WithOAuthBearerToken(azureToken)
                .GetJsonAsync();

            // loop over the assigned network interfaces
            foreach (var networkInterface in computeVmResource.properties.networkProfile.networkInterfaces)
            {
                dynamic networkInterfaceResource = await AZURE_MANAGEMENT_API
                    .AppendPathSegment((string)networkInterface.id)
                    .SetQueryParam("api-version", "2016-05-15")
                    .WithOAuthBearerToken(azureToken)
                    .GetJsonAsync();

                // loop over the assigned load balancer inboud NAT rules
                foreach (var loadBalancerInboundNatRule in ((IEnumerable<dynamic>)networkInterfaceResource.properties.ipConfigurations).SelectMany(ipConfiguration => ((IEnumerable<dynamic>)ipConfiguration.properties.loadBalancerInboundNatRules)))
                {
                    dynamic loadBalancerInboundNatRuleResource = await AZURE_MANAGEMENT_API
                        .AppendPathSegment((string)loadBalancerInboundNatRule.id)
                        .SetQueryParam("api-version", "2016-05-15")
                        .WithOAuthBearerToken(azureToken)
                        .GetJsonAsync();

                    // if the load balancer inbound NAT rule is pointing to port 3389 in  the backend dig deeper
                    if (loadBalancerInboundNatRuleResource.properties.backendPort == 3389)
                    {
                        dynamic frontendIPConfigurationResource = await AZURE_MANAGEMENT_API
                            .AppendPathSegment((string)loadBalancerInboundNatRuleResource.frontendIPConfiguration.id)
                            .SetQueryParam("api-version", "2016-05-15")
                            .WithOAuthBearerToken(azureToken)
                            .GetJsonAsync();

                        dynamic publicIPAddressResource = await AZURE_MANAGEMENT_API
                            .AppendPathSegment((string)frontendIPConfigurationResource.publicIPAddress.id)
                            .SetQueryParam("api-version", "2016-05-15")
                            .WithOAuthBearerToken(azureToken)
                            .GetJsonAsync();

                        // found all information needed for a RDP connection - return a DNS end point with the gathered information
                        return new DnsEndPoint(publicIPAddressResource.properties.dnsSettings.fqdn, loadBalancerInboundNatRuleResource.properties.frontendPort);
                    }
                }
            }

            throw new Exception($"Could not resolve RDP end point for compute VM '{computeVmResourceId}'");
        }

        private static async Task<string> GetTokenAsync(RequestContext requestContext, X509Certificate2 certificate)
        {
            if (requestContext == null)
            {
                throw new ArgumentNullException(nameof(requestContext));
            }

            if (certificate == null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }


            // request token for Azure communication
            var azureToken = await AzureManagementApiTokenProvider.GetAccessTokenAsync(AZURE_MANAGEMENT_API);

            // fetch the lab VM resource to gather information needed for token creation
            dynamic labVmResource = await AZURE_MANAGEMENT_API
                .AppendPathSegment($"subscriptions/{requestContext.Properties.SubscriptionId}/resourceGroups/{requestContext.Properties.ResourceGroupName}/providers/Microsoft.DevTestLab/labs/{requestContext.Properties.LabName}/virtualmachines/{requestContext.Properties.VirtualMachineName}")
                .SetQueryParam("api-version", "2016-05-15")
                .WithOAuthBearerToken(azureToken)
                .GetJsonAsync();

            // if the lab VM disallows public IP addresses we need to dig deeper to get the required information
            var rdpEndPoint = (DnsEndPoint)(labVmResource.properties.disallowPublicIpAddress
                                ? GetRDPEndPoint(requestContext, labVmResource.properties.computeId)
                                : new DnsEndPoint(labVmResource.properties.fqdn, 3389));

            // create the machine token and sign the data
            var machineToken = string.Format(CultureInfo.InvariantCulture, MACHINE_TOKEN_PATTERN, rdpEndPoint.Host, rdpEndPoint.Port, GetPosixLifetime());
            var machineTokenBuffer = Encoding.ASCII.GetBytes(machineToken);
            var machineTokenSignature = certificate.GetRSAPrivateKey().SignData(machineTokenBuffer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // wrap the machine token
            return string.Format(CultureInfo.InvariantCulture, AUTH_TOKEN_PATTERN, machineToken, certificate.Thumbprint, Uri.EscapeDataString(Convert.ToBase64String(machineTokenSignature)));

            Int64 GetPosixLifetime()
            {
                // default lifetime is 1 minute
                var endOfLife = DateTime.UtcNow.AddMinutes(1);

                if (TimeSpan.TryParse(requestContext.TokenLifetime, out TimeSpan lifetime))
                {
                    // apply lifetime from configuration
                    endOfLife = DateTime.UtcNow.Add(lifetime);
                }

                return (Int64)endOfLife.Subtract(PosixBaseTime).TotalSeconds;
            }
        }

        [FunctionName("CreateToken")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.DevTestLab/labs/{labName}/virtualmachines/{virtualMachineName}/users/{userId}")]HttpRequestMessage req, TraceWriter log, ExecutionContext executionContext, string subscriptionId, string userId, string resourceGroupName, string labName, string virtualMachineName)
        {
            log.Info("C# HTTP trigger function processed a request.");

            RequestContext requestContext;

            try
            {
                requestContext = new RequestContext(executionContext, log)
                {
                    Properties = new RequestProperties()
                    {
                        SubscriptionId = subscriptionId,
                        UserId = userId,
                        ResourceGroupName = resourceGroupName,
                        LabName = labName,
                        VirtualMachineName = virtualMachineName
                    }
                };

                requestContext.Validate();
            }
            catch (Exception exc)
            {
                log.Error($"Failed to validate request {executionContext.InvocationId}: {exc.StackTrace.ToString()}", exc);

                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            try
            {
                // get the signing certificate
                var certificate = await GetCertificateAsync(requestContext);

                // get the signed authentication token
                var token = await GetTokenAsync(requestContext, certificate);

                return req.CreateResponse(HttpStatusCode.OK, token);
            }
            catch (Exception exc)
            {
                log.Error($"Failed to process request {executionContext.InvocationId}: {exc.StackTrace.ToString()}", exc);

                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }
        }

        private class RequestContext
        {
            public RequestContext(ExecutionContext executionContext, TraceWriter log)
            {
                // init execution context
                ExecutionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));

                // init log writer
                Log = log ?? throw new ArgumentNullException(nameof(log));

                // init properties
                Properties = new RequestProperties();
            }

            public string SignCertificateUrl => Environment.GetEnvironmentVariable("SignCertificateUrl");

            public string TokenLifetime => Environment.GetEnvironmentVariable("TokenLifetime");

            public RequestProperties Properties { get; set; }

            public ExecutionContext ExecutionContext { get; }

            public TraceWriter Log { get; }

            public void Validate()
            {
                // add validation logic here if needed
            }
        }

        private class RequestProperties
        {
            public string SubscriptionId { get; set; }

            public string UserId { get; set; }

            public string ResourceGroupName { get; set; }

            public string LabName { get; set; }

            public string VirtualMachineName { get; set; }
        }
    }
}