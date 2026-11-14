using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Translator.Services
{
    public class CheckResult
    {
        public string Check { get; set; }
        public string Status { get; set; }   // OK, Warning, Failed, Info
        public string Details { get; set; }
        public string Meaning { get; set; }
    }

    /// <summary>
    /// Checks whether the network (e.g. corporate Wi-Fi) allows access to the translation service.
    /// </summary>
    public static class ConnectionChecker
    {
        private const string Host = "translate.googleapis.com";

        private static readonly Uri TestUri = new Uri(
            "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q=Bonjour");

        public static async Task<List<CheckResult>> RunAsync()
        {
            var results = new List<CheckResult>();

            results.Add(await CheckDnsAsync());

            var proxyResult = CheckProxy(out var usesProxy);
            results.Add(proxyResult);

            results.Add(await CheckTcpAsync(usesProxy));
            results.AddRange(await CheckHttpsAsync());

            var request = results.Last();
            results.Add(new CheckResult
            {
                Check = "Overall",
                Status = request.Status == "OK" ? "OK" : "Failed",
                Details = request.Status == "OK"
                    ? "Translation will work on this network."
                    : "Translation is not available on this network.",
                Meaning = request.Status == "OK" ? "" : "See the 'Translation request' row for the reason."
            });

            return results;
        }

        private static async Task<CheckResult> CheckDnsAsync()
        {
            var result = new CheckResult { Check = "DNS lookup" };
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(Host);
                result.Status = "OK";
                result.Details = Host + " -> " + string.Join(", ", addresses.Take(3).Select(a => a.ToString()));
                result.Meaning = "The server name can be found.";
            }
            catch (Exception ex)
            {
                result.Status = "Failed";
                result.Details = ex.Message;
                result.Meaning = "The name cannot be resolved. The network may block the site or only allow access through a proxy.";
            }
            return result;
        }

        private static CheckResult CheckProxy(out bool usesProxy)
        {
            var result = new CheckResult { Check = "Proxy" };
            try
            {
                var proxy = HttpClient.DefaultProxy;
                var proxyUri = proxy.GetProxy(TestUri);
                usesProxy = proxyUri != null && proxyUri != TestUri && !proxy.IsBypassed(TestUri);

                result.Status = "Info";
                result.Details = usesProxy ? "Using proxy: " + proxyUri : "No proxy (direct connection)";
                result.Meaning = usesProxy
                    ? "Traffic goes through the corporate proxy (taken from Windows settings)."
                    : "The app connects to the internet directly.";
            }
            catch (Exception ex)
            {
                usesProxy = false;
                result.Status = "Warning";
                result.Details = ex.Message;
                result.Meaning = "Could not read the proxy settings.";
            }
            return result;
        }

        private static async Task<CheckResult> CheckTcpAsync(bool usesProxy)
        {
            var result = new CheckResult { Check = "Direct connection (port 443)" };
            using var client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync(Host, 443);
                if (await Task.WhenAny(connect, Task.Delay(5000)) != connect)
                    throw new TimeoutException("Timed out after 5 seconds");
                await connect;

                result.Status = "OK";
                result.Details = "Connected to " + Host + ":443";
                result.Meaning = "A firewall is not blocking the connection.";
            }
            catch (Exception ex)
            {
                // With a proxy, a failed direct connection is expected and not a problem by itself
                result.Status = usesProxy ? "Warning" : "Failed";
                result.Details = ex.Message;
                result.Meaning = usesProxy
                    ? "Direct connections are blocked, but the proxy may still allow it. See the 'Translation request' row."
                    : "A firewall is blocking the connection.";
            }
            return result;
        }

        private static async Task<IEnumerable<CheckResult>> CheckHttpsAsync()
        {
            string issuer = null;
            var certErrors = SslPolicyErrors.None;

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    issuer = cert?.Issuer;
                    certErrors = errors;
                    return errors == SslPolicyErrors.None; // keep normal validation
                }
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            var request = new CheckResult { Check = "Translation request" };
            try
            {
                using var response = await http.GetAsync(TestUri);
                var body = await response.Content.ReadAsStringAsync();
                var code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode && body.TrimStart().StartsWith("[["))
                {
                    request.Status = "OK";
                    request.Details = "HTTP " + code + " - 'Bonjour' was translated successfully";
                    request.Meaning = "The translation service is reachable.";
                }
                else if (response.IsSuccessStatusCode)
                {
                    request.Status = "Failed";
                    request.Details = "HTTP " + code + " but received a web page instead of translation data";
                    request.Meaning = "Probably a web filter 'blocked' page. Ask IT to allow " + Host + ".";
                }
                else
                {
                    request.Status = "Failed";
                    request.Details = "HTTP " + code + " " + response.ReasonPhrase;
                    request.Meaning = code switch
                    {
                        403 => "Blocked by a web filter or policy. Ask IT to allow " + Host + ".",
                        407 => "The proxy needs your Windows login, which the app does not send yet.",
                        429 => "Not a corporate block: Google is rate-limiting requests. Try again later.",
                        _ => "The request was rejected."
                    };
                }
            }
            catch (TaskCanceledException)
            {
                request.Status = "Failed";
                request.Details = "Timed out after 10 seconds";
                request.Meaning = "A firewall or proxy is silently dropping the request.";
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || certErrors != SslPolicyErrors.None)
            {
                request.Status = "Failed";
                request.Details = "Certificate error: " + certErrors;
                request.Meaning = "The company inspects encrypted traffic and its certificate is not trusted on this PC.";
            }
            catch (Exception ex)
            {
                request.Status = "Failed";
                request.Details = ex.InnerException?.Message ?? ex.Message;
                request.Meaning = "The request could not be completed.";
            }

            var certificate = new CheckResult { Check = "Security certificate" };
            if (issuer == null)
            {
                certificate.Status = "Info";
                certificate.Details = "Not checked (no secure connection was made)";
                certificate.Meaning = "";
            }
            else if (certErrors != SslPolicyErrors.None)
            {
                certificate.Status = "Failed";
                certificate.Details = "Issuer: " + issuer + " (" + certErrors + ")";
                certificate.Meaning = "The certificate is not trusted. Usually corporate SSL inspection.";
            }
            else if (issuer.IndexOf("Google", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     issuer.IndexOf("GTS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                certificate.Status = "OK";
                certificate.Details = "Issuer: " + issuer;
                certificate.Meaning = "Genuine Google certificate. Traffic is not being inspected.";
            }
            else
            {
                certificate.Status = "Warning";
                certificate.Details = "Issuer: " + issuer;
                certificate.Meaning = "The company inspects encrypted traffic (SSL inspection). It works, but IT can see the text.";
            }

            return new[] { certificate, request };
        }
    }
}
