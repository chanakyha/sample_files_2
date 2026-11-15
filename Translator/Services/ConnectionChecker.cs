using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    /// Checks the LibreTranslate server / load balancer configured in the settings. Read-only: it
    /// only sends requests (the server is run and configured separately).
    /// Uses the load balancer's test routes: GET /lb/health and GET /lb/selftest (a real test of
    /// every worker), then one translation through the app's own code path.
    /// </summary>
    public static class ConnectionChecker
    {
        public static async Task<List<CheckResult>> RunAsync(string baseUrl, string apiKey)
        {
            var results = new List<CheckResult>
            {
                new CheckResult { Check = "Server address", Status = "Info", Details = baseUrl, Meaning = "Change it in Settings." }
            };

            var cache = new TranslationCache(Path.Combine(Path.GetTempPath(), "translator-check-" + Guid.NewGuid() + ".json"));
            var service = new LibreTranslateService(baseUrl, apiKey, cache);

            var languages = new CheckResult { Check = "Server reachable" };
            IReadOnlyCollection<string> supported = null;
            try
            {
                supported = await service.GetSupportedLanguagesAsync();
                languages.Status = "OK";
                languages.Details = $"{supported.Count} of 24 languages loaded: {string.Join(", ", supported)}";
                languages.Meaning = supported.Count < 24
                    ? "Missing languages are greyed out in the Translate menu. Set \"languages\": \"all\" in LibreTranslateServer\\config.json to load all."
                    : "All languages are available.";
            }
            catch (Exception ex)
            {
                languages.Status = "Failed";
                languages.Details = ex.Message;
                languages.Meaning = "The server is not running (start LibreTranslateServer\\start.cmd), still starting, the address is wrong, or a firewall blocks it.";
            }
            results.Add(languages);

            if (supported != null)
                results.AddRange(await LoadBalancerAsync(baseUrl, apiKey));

            var request = new CheckResult { Check = "Translation request" };
            if (supported == null)
            {
                request.Status = "Info";
                request.Details = "Not checked (server not reachable)";
                request.Meaning = "";
            }
            else
            {
                var target = supported.Contains("fr") ? "fr" : supported.FirstOrDefault(c => c != "en") ?? "en";
                var result = await service.TranslateAsync("Hello, how are you?", target);
                request.Status = result.Success ? "OK" : "Failed";
                request.Details = result.Success ? $"'Hello, how are you?' -> '{result.TranslatedText}'" : result.Error;
                request.Meaning = result.Success ? "Translation works through the app's own code."
                    : result.Error.Contains("403") ? "Wrong or missing API key." : "The server rejected the request.";
            }
            results.Add(request);

            var failed = results.Any(r => r.Status == "Failed");
            results.Add(new CheckResult
            {
                Check = "Overall",
                Status = request.Status != "OK" ? "Failed" : failed ? "Warning" : "OK",
                Details = request.Status != "OK" ? "Translation is not available."
                    : failed ? $"Translation works, but some workers failed their test ({service.Workers} healthy worker(s) used)."
                    : $"LibreTranslate is ready ({service.Workers} worker(s), {service.Parallel} parallel requests).",
                Meaning = ""
            });
            return results;
        }

        /// <summary>
        /// GET /lb/health, then GET /lb/selftest: one row per backend with the checks that failed.
        /// A plain LibreTranslate server (no /lb routes) gets one Info row.
        /// </summary>
        private static async Task<List<CheckResult>> LoadBalancerAsync(string baseUrl, string apiKey)
        {
            var rows = new List<CheckResult>();
            var url = baseUrl.TrimEnd('/');
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) }; // selftest waits for busy workers

            JsonDocument health;
            try
            {
                using var response = await http.GetAsync(url + "/lb/health");
                health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!health.RootElement.TryGetProperty("healthy", out _)) throw new JsonException("not the load balancer");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                rows.Add(new CheckResult
                {
                    Check = "Load balancer",
                    Status = "Info",
                    Details = "Not detected (single LibreTranslate server, or an older load balancer without /lb/health)",
                    Meaning = "For more speed, run LibreTranslateServer\\start.cmd with \"workers\" set in config.json."
                });
                return rows;
            }

            using (health)
            {
                var h = health.RootElement;
                var healthy = h.GetProperty("healthy").GetInt32();
                var total = h.GetProperty("total").GetInt32();
                rows.Add(new CheckResult
                {
                    Check = "Load balancer (/lb/health)",
                    Status = healthy == total ? "OK" : healthy > 0 ? "Warning" : "Failed",
                    Details = $"{h.GetProperty("status").GetString()}: {healthy} of {total} backend(s) healthy",
                    Meaning = "Requests are spread over the healthy backends (fewest active requests first)."
                });
            }

            try
            {
                var query = string.IsNullOrWhiteSpace(apiKey) ? "" : "?api_key=" + Uri.EscapeDataString(apiKey.Trim());
                using var response = await http.GetAsync(url + "/lb/selftest" + query);
                using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var r = report.RootElement;
                rows.Add(new CheckResult
                {
                    Check = "Worker self-test (/lb/selftest)",
                    Status = r.GetProperty("passed").GetBoolean() ? "OK" : "Failed",
                    Details = $"{r.GetProperty("backends_passed").GetInt32()} of {r.GetProperty("backends_total").GetInt32()} backend(s) passed in {r.GetProperty("ms").GetInt32()} ms",
                    Meaning = "Every worker tested directly: languages, detection of one sample per language, one translation."
                });

                foreach (var backend in r.GetProperty("backends").EnumerateArray())
                {
                    var checks = backend.GetProperty("checks").EnumerateArray().ToList();
                    var failedChecks = checks.Where(c => !c.GetProperty("passed").GetBoolean())
                        .Select(c => $"{c.GetProperty("check").GetString()}: {c.GetProperty("details").GetString()}").ToList();
                    var passed = backend.GetProperty("passed").GetBoolean();
                    rows.Add(new CheckResult
                    {
                        Check = (backend.GetProperty("local").GetBoolean() ? "  Worker " : "  Remote ") + backend.GetProperty("url").GetString(),
                        Status = passed ? "OK" : "Failed",
                        Details = passed
                            ? $"{checks.Count} checks passed ({string.Join(", ", checks.Select(c => c.GetProperty("check").GetString()))})"
                            : string.Join("; ", failedChecks),
                        Meaning = passed ? "" : "Down, still loading models, or wrong results: it gets no requests while it is unhealthy. See LibreTranslateServer\\logs."
                    });
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException ||
                                       ex is KeyNotFoundException || ex is InvalidOperationException)
            {
                rows.Add(new CheckResult { Check = "Worker self-test (/lb/selftest)", Status = "Warning", Details = ex.Message, Meaning = "The self-test route did not answer." });
            }
            return rows;
        }
    }
}
