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
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using RDGatewayAPI.Data;

namespace RDGatewayAPI.Functions
{
    public static class CreateToken
    {
        private const string AZURE_MANAGEMENT_API = "https://management.azure.com/";
        private const string MACHINE_TOKEN_PATTERN = "Host={0}&Port={1}&ExpiresOn={2}";
        private const string AUTH_TOKEN_PATTERN = "{0}&Signature=1|SHA256|{1}|{2}";
        private const string USER_OBJECTID_HEADER = "x-ms-client-object-id";

        private static readonly AzureServiceTokenProvider AzureManagementApiTokenProvider = new AzureServiceTokenProvider();
        private static readonly DateTime PosixBaseTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);

        private static async Task<X509Certificate2> GetCertificateAsync()
        {
            var signCertificateUrl = default(string);

            try
            {
                signCertificateUrl = Environment.GetEnvironmentVariable("SignCertificateUrl");

                // init a key vault client
                var keyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(AzureManagementApiTokenProvider.KeyVaultTokenCallback));

                // get the base64 encoded secret and decode
                var signCertificateSecret = await keyVaultClient.GetSecretAsync(signCertificateUrl).ConfigureAwait(false);
                var signCertificateBuffer = Convert.FromBase64String(signCertificateSecret.Value);

                // unwrap the json data envelope
                var envelope = JsonConvert.DeserializeAnonymousType(Encoding.UTF8.GetString(signCertificateBuffer), new { data = string.Empty, password = string.Empty });

                // return the certificate
                return new X509Certificate2(Convert.FromBase64String(envelope.data), envelope.password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (Exception exc)
            {
                throw new Exception($"Failed to load certificate from KeyVault by URL '{signCertificateUrl}'", exc);
            }
        }

        private static string GetToken(X509Certificate2 certificate, string host, int port)
        {
            // create the machine token and sign the data
            var machineToken = string.Format(CultureInfo.InvariantCulture, MACHINE_TOKEN_PATTERN, host, port, GetPosixLifetime());
            var machineTokenBuffer = Encoding.ASCII.GetBytes(machineToken);
            var machineTokenSignature = certificate.GetRSAPrivateKey().SignData(machineTokenBuffer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // wrap the machine token
            return string.Format(CultureInfo.InvariantCulture, AUTH_TOKEN_PATTERN, machineToken, certificate.Thumbprint, Uri.EscapeDataString(Convert.ToBase64String(machineTokenSignature)));

            Int64 GetPosixLifetime()
            {
                DateTime endOfLife;

                var tokenLifetime = Environment.GetEnvironmentVariable("TokenLifetime");

                if (string.IsNullOrEmpty(tokenLifetime))
                {
                    // default lifetime is 1 minute
                    endOfLife = DateTime.UtcNow.AddMinutes(1);
                }
                else
                {
                    try
                    {
                        // parse token lifetime
                        var duration = TimeSpan.Parse(tokenLifetime);

                        // apply lifetime from configuration
                        endOfLife = DateTime.UtcNow.Add(duration);
                    }
                    catch (Exception exc)
                    {
                        throw new ConfigurationErrorsException($"Failed to parse token lifetime '{tokenLifetime}' from configuration", exc);
                    }
                }

                // return lifetime in posix format
                return (Int64)endOfLife.Subtract(PosixBaseTime).TotalSeconds;
            }
        }

        [FunctionName("CreateToken")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "host/{host}/port/{port}")]HttpRequestMessage req,
                                                          [Queue("track-users")]  ICollector<string> trackUserQueue,
                                                          TraceWriter log, ExecutionContext executionContext,
                                                          string host, int port)
        {
            var user = req.Headers.TryGetValues(USER_OBJECTID_HEADER, out IEnumerable<string> values) ? values.FirstOrDefault() : default(string);

            if (string.IsNullOrEmpty(user) || !Guid.TryParse(user, out Guid userId))
            {
                log.Error($"BadRequest - missing or invalid request header '{USER_OBJECTID_HEADER}'");

                return req.CreateResponse(HttpStatusCode.BadRequest);
            }
            else
            {
                var userEntity = new UserEntity(req.GetCorrelationId())
                {
                    UserId = userId,
                    Host = host,
                    Port = port
                };

                trackUserQueue.Add(userEntity.ToJson());

                for (int i = 0; i < 1000; i++)
                {
                    userEntity = new UserEntity(Guid.NewGuid())
                    {
                        UserId = userId,
                        Host = host,
                        Port = port
                    };

                    trackUserQueue.Add(userEntity.ToJson());
                }
            }

            try
            {
                // get the signing certificate
                var certificate = await GetCertificateAsync().ConfigureAwait(false);

                // get the signed authentication token
                var response = new { token = GetToken(certificate, host, port) };

                return req.CreateResponse(HttpStatusCode.OK, response, "application/json");
            }
            catch (Exception exc)
            {
                log.Error($"Failed to process request {executionContext.InvocationId}", exc);

                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }
        }
    }
}