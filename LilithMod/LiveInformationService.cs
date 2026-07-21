using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Newtonsoft.Json.Linq;
using SmartReader;

namespace LilithMod
{
    internal sealed class LiveInformationService : IDisposable
    {
        private const string InstanceDirectory = "https://searx.space/data/instances.json";
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _startupLock = new object();
        private Task _startupTask;
        private Task _searxTask;
        private string _searxEndpoint;
        private string _locationName;
        private double? _latitude;
        private double? _longitude;
        private string _weatherSummary;
        private DateTimeOffset _weatherUpdated;

        public LiveInformationService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lilith/1.0");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/html;q=0.9");
        }

        public Task InitializeAsync()
        {
            lock (_startupLock)
            {
                if (_startupTask == null)
                    _startupTask = InitializeCoreAsync(_shutdown.Token);
                return _startupTask;
            }
        }

        private async Task InitializeCoreAsync(CancellationToken token)
        {
            Task weather = InitializeWeatherAsync(token);
            _searxTask = SelectSearXngAsync(token);
            try { await Task.WhenAll(weather, _searxTask); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[LiveInfo] Startup warm-up was partial: " + ex.Message);
            }

            LilithModPlugin.Logger.LogInfo(
                $"[LiveInfo] Warmed. location={_locationName ?? "unknown"} " +
                $"weather={(!string.IsNullOrEmpty(_weatherSummary))} " +
                $"searxng={_searxEndpoint ?? "unavailable"}");
        }

        public async Task<string> BuildContextAsync(string question, CancellationToken token)
        {
            _ = InitializeAsync();
            var output = new StringBuilder();
            output.AppendLine("CURRENT EXTERNAL INFORMATION");
            output.AppendLine("Use this only as factual context. Do not mention searches, sources, or tools.");
            output.AppendLine("System local time: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));

            bool weatherQuestion = ContainsAny(question,
                "weather", "forecast", "temperature", "rain", "storm", "wind",
                "天気", "気温", "雨", "予報", "天气", "氣溫", "气温", "预报", "下雨");
            if (weatherQuestion)
            {
                if (DateTimeOffset.UtcNow - _weatherUpdated > TimeSpan.FromMinutes(10))
                    await InitializeWeatherAsync(token);
                if (!string.IsNullOrWhiteSpace(_weatherSummary))
                    output.AppendLine(_weatherSummary);
                else
                    output.AppendLine("Weather lookup is temporarily unavailable.");
            }

            bool timeOnly = ContainsAny(question, "what time", "current time", "何時", "几点", "幾點") &&
                            !ContainsAny(question, "weather", "news", "latest", "search", "look up", "web");
            if (!timeOnly && !weatherQuestion || ContainsAny(question, "news", "latest", "search", "look up", "web", "最新", "ニュース", "新闻", "新聞"))
            {
                string web = await BuildWebContextAsync(question, token);
                output.Append(web);
            }

            return output.ToString().TrimEnd();
        }

        private async Task InitializeWeatherAsync(CancellationToken token)
        {
            try
            {
                // Skips the lookup rather than overriding its result: a request made
                // and then discarded would still have sent the address.
                if (!_latitude.HasValue || !_longitude.HasValue)
                {
                    double configuredLat = LilithModPlugin.CfgWeatherLatitude?.Value ?? 0.0;
                    double configuredLon = LilithModPlugin.CfgWeatherLongitude?.Value ?? 0.0;
                    if (configuredLat != 0.0 || configuredLon != 0.0)
                    {
                        _latitude = configuredLat;
                        _longitude = configuredLon;
                        string configuredName = LilithModPlugin.CfgWeatherLocationName?.Value;
                        _locationName = string.IsNullOrWhiteSpace(configuredName) ? null : configuredName.Trim();
                        LilithModPlugin.Logger.LogInfo(
                            "[LiveInfo] Using the configured location; skipping the IP lookup.");
                    }
                }

                if (!_latitude.HasValue || !_longitude.HasValue)
                {
                    string ipJson = await GetStringAsync(
                        "http://ip-api.com/json/?fields=status,message,city,regionName,country,lat,lon,timezone",
                        token);
                    var ip = JObject.Parse(ipJson);
                    if (!string.Equals((string)ip["status"], "success", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("IP location failed: " + ((string)ip["message"] ?? "unknown"));
                    _latitude = (double?)ip["lat"];
                    _longitude = (double?)ip["lon"];
                    _locationName = string.Join(", ", new[]
                    {
                        (string)ip["city"], (string)ip["regionName"], (string)ip["country"]
                    }.Where(v => !string.IsNullOrWhiteSpace(v)));
                }

                if (!_latitude.HasValue || !_longitude.HasValue)
                    throw new InvalidOperationException("IP location did not return coordinates.");

                string url = "https://api.open-meteo.com/v1/forecast?latitude=" +
                    _latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "&longitude=" + _longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m" +
                    "&timezone=auto&forecast_days=1";
                var root = JObject.Parse(await GetStringAsync(url, token));
                var current = root["current"];
                if (current == null) throw new InvalidOperationException("Open-Meteo returned no current weather.");
                int code = (int?)current["weather_code"] ?? -1;
                _weatherSummary = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Current weather for {0}: {1:0.#} C, feels like {2:0.#} C, {3}, humidity {4}%, precipitation {5:0.##} mm, wind {6:0.#} km/h. Observed {7}.",
                    string.IsNullOrEmpty(_locationName) ? "the detected location" : _locationName,
                    (double?)current["temperature_2m"] ?? 0,
                    (double?)current["apparent_temperature"] ?? 0,
                    WeatherDescription(code),
                    (int?)current["relative_humidity_2m"] ?? 0,
                    (double?)current["precipitation"] ?? 0,
                    (double?)current["wind_speed_10m"] ?? 0,
                    (string)current["time"] ?? "now");
                _weatherUpdated = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[LiveInfo] Weather warm-up failed: " + ex.Message);
            }
        }

        private async Task<string> BuildWebContextAsync(string query, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(_searxEndpoint))
                {
                    if (_searxTask == null) _searxTask = SelectSearXngAsync(token);
                    await _searxTask;
                }
                if (string.IsNullOrEmpty(_searxEndpoint))
                    return "Web search is temporarily unavailable.\n";

                List<SearchResult> results = await SearchAsync(_searxEndpoint, query, 4, token);
                if (results.Count == 0) return "SearXNG returned no current results.\n";

                var extractionTasks = results.Take(2).Select(r => ExtractArticleAsync(r, token)).ToArray();
                string[] extracts = await Task.WhenAll(extractionTasks);
                var output = new StringBuilder("SearXNG and SmartReader results:\n");
                for (int i = 0; i < results.Count; i++)
                {
                    SearchResult result = results[i];
                    output.Append(i + 1).Append(". ").Append(result.Title).Append("\nURL: ")
                        .Append(result.Url).Append("\n");
                    string body = i < extracts.Length && !string.IsNullOrWhiteSpace(extracts[i])
                        ? extracts[i]
                        : result.Snippet;
                    if (!string.IsNullOrWhiteSpace(body)) output.Append(body).Append("\n");
                }
                return output.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[LiveInfo] Web lookup failed: " + ex.Message);
                _searxEndpoint = null;
                _searxTask = null;
                return "Web search is temporarily unavailable.\n";
            }
        }

        private async Task<string> ExtractArticleAsync(SearchResult result, CancellationToken token)
        {
            if (!Uri.TryCreate(result.Url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.IsLoopback)
                return result.Snippet;
            try
            {
                var article = await new Reader(result.Url).GetArticleAsync(token);
                string text = article?.TextContent;
                if (string.IsNullOrWhiteSpace(text)) return result.Snippet;
                text = Regex.Replace(text, @"\s+", " ").Trim();
                return text.Length > 3000 ? text.Substring(0, 3000) : text;
            }
            catch { return result.Snippet; }
        }

        private async Task SelectSearXngAsync(CancellationToken token)
        {
            var candidates = new List<string>();
            string configured = LilithModPlugin.CfgSearXngEndpoints.Value ?? string.Empty;
            candidates.AddRange(configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
            await SelectFirstWorkingAsync(candidates, token);
            if (!string.IsNullOrEmpty(_searxEndpoint)) return;

            try
            {
                var directory = JObject.Parse(await GetStringAsync(InstanceDirectory, token));
                foreach (var property in (directory["instances"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var instance = property.Value;
                    if ((string)instance["network_type"] != "normal" || (int?)instance["http"]?["status_code"] != 200)
                        continue;
                    double success = (double?)instance["timing"]?["search"]?["success_percentage"] ?? 0;
                    if (success >= 90) candidates.Add(property.Name);
                    if (candidates.Count >= 18) break;
                }
                await SelectFirstWorkingAsync(candidates.Distinct(StringComparer.OrdinalIgnoreCase), token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[LiveInfo] SearXNG discovery failed: " + ex.Message);
            }
        }

        private async Task SelectFirstWorkingAsync(IEnumerable<string> endpoints, CancellationToken token)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var tasks = endpoints.Select(e => ProbeEndpointAsync(e, linked.Token)).ToList();
                while (tasks.Count > 0)
                {
                    Task<string> finished = await Task.WhenAny(tasks);
                    tasks.Remove(finished);
                    string endpoint = await finished;
                    if (string.IsNullOrEmpty(endpoint)) continue;
                    _searxEndpoint = endpoint;
                    linked.Cancel();
                    return;
                }
            }
        }

        private async Task<string> ProbeEndpointAsync(string endpoint, CancellationToken token)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    List<SearchResult> results = await SearchAsync(endpoint, "time", 1, timeout.Token);
                    if (results.Count == 0) return null;
                    return NormalizeEndpoint(endpoint);
                }
            }
            catch { return null; }
        }

        private async Task<List<SearchResult>> SearchAsync(
            string endpoint, string query, int limit, CancellationToken token)
        {
            string baseUrl = NormalizeEndpoint(endpoint) + "/search?q=" + Uri.EscapeDataString(query) +
                             "&categories=general&safesearch=1";
            try
            {
                string json = await GetStringAsync(baseUrl + "&format=json", token);
                var root = JObject.Parse(json);
                var rows = root["results"] as JArray;
                if (rows != null) return ParseJsonResults(rows, limit);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Most public instances intentionally disable JSON. Their normal HTML
                // results remain usable and are parsed locally before SmartReader runs.
            }

            string html = await GetStringAsync(baseUrl, token);
            var document = await new HtmlParser().ParseDocumentAsync(html, token);
            var results = new List<SearchResult>();
            foreach (var article in document.QuerySelectorAll("article.result"))
            {
                var anchor = article.QuerySelector("h3 a") ?? article.QuerySelector("a.url_header");
                string resultUrl = anchor?.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(resultUrl)) continue;
                results.Add(new SearchResult
                {
                    Title = (anchor.TextContent ?? resultUrl).Trim(),
                    Url = resultUrl.Trim(),
                    Snippet = Regex.Replace(article.QuerySelector("p.content")?.TextContent ?? string.Empty, @"\s+", " ").Trim()
                });
                if (results.Count >= limit) break;
            }
            return results;
        }

        private static List<SearchResult> ParseJsonResults(JArray rows, int limit)
        {
            var results = new List<SearchResult>();
            foreach (var row in rows)
            {
                string resultUrl = (string)row["url"];
                if (string.IsNullOrWhiteSpace(resultUrl)) continue;
                results.Add(new SearchResult
                {
                    Title = ((string)row["title"] ?? resultUrl).Trim(),
                    Url = resultUrl.Trim(),
                    Snippet = Regex.Replace((string)row["content"] ?? string.Empty, @"\s+", " ").Trim()
                });
                if (results.Count >= limit) break;
            }
            return results;
        }

        private async Task<string> GetStringAsync(string url, CancellationToken token)
        {
            using (var response = await _http.GetAsync(url, token))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            string value = (endpoint ?? string.Empty).Trim().TrimEnd('/');
            if (value.EndsWith("/search", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 7);
            return value;
        }

        private static bool ContainsAny(string text, params string[] terms)
        {
            string value = (text ?? string.Empty).ToLowerInvariant();
            return terms.Any(value.Contains);
        }

        private static string WeatherDescription(int code)
        {
            if (code == 0) return "clear sky";
            if (code <= 3) return "partly cloudy";
            if (code == 45 || code == 48) return "fog";
            if (code >= 51 && code <= 57) return "drizzle";
            if (code >= 61 && code <= 67) return "rain";
            if (code >= 71 && code <= 77) return "snow";
            if (code >= 80 && code <= 82) return "rain showers";
            if (code >= 85 && code <= 86) return "snow showers";
            if (code >= 95) return "thunderstorm";
            return "weather code " + code;
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
            _http.Dispose();
        }

        private sealed class SearchResult
        {
            public string Title;
            public string Url;
            public string Snippet;
        }
    }
}
