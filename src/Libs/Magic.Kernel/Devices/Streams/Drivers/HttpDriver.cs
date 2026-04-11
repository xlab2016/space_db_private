using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>
    /// HTTP client driver: makes HTTP requests on behalf of the AGI <c>stream&lt;http&gt;</c> device.
    /// Supports GET, POST, PUT, DELETE methods with optional JSON body.
    /// <para>
    /// Usage in AGI:
    /// <code>
    /// var api := stream&lt;http&gt;;
    /// var response := json: await api.post("/api/v1/authenticate", { username, password });
    /// return response.success;
    /// </code>
    /// </para>
    /// </summary>
    public sealed class HttpDriver : IDisposable
    {
        private static readonly HttpClient _sharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        private string _baseUrl = "";

        /// <summary>Optional base URL prefix prepended to all relative request paths.</summary>
        public string BaseUrl
        {
            get => _baseUrl;
            set => _baseUrl = (value ?? "").TrimEnd('/');
        }

        /// <summary>Parses configuration from an AGI object literal (e.g. <c>{ baseUrl: "https://api.example.com" }</c>).</summary>
        public void ParseAndApplyConfig(object? config)
        {
            if (config is Dictionary<string, object?> dict)
            {
                if (dict.TryGetValue("baseUrl", out var baseUrlObj) && baseUrlObj is string baseUrl)
                    BaseUrl = baseUrl;
                else if (dict.TryGetValue("base", out var baseObj) && baseObj is string baseStr)
                    BaseUrl = baseStr;
            }
        }

        /// <summary>
        /// Performs an HTTP GET request and returns the response body as a string.
        /// </summary>
        public async Task<object?> GetAsync(string path)
        {
            var url = BuildUrl(path);
            try
            {
                var response = await _sharedClient.GetAsync(url).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseResponseBody(body, response.StatusCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[http] GET {url} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Performs an HTTP POST request with a JSON body and returns the parsed response.
        /// </summary>
        public async Task<object?> PostAsync(string path, object? body)
        {
            var url = BuildUrl(path);
            try
            {
                var json = SerializeBody(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _sharedClient.PostAsync(url, content).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseResponseBody(responseBody, response.StatusCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[http] POST {url} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Performs an HTTP PUT request with a JSON body and returns the parsed response.
        /// </summary>
        public async Task<object?> PutAsync(string path, object? body)
        {
            var url = BuildUrl(path);
            try
            {
                var json = SerializeBody(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _sharedClient.PutAsync(url, content).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseResponseBody(responseBody, response.StatusCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[http] PUT {url} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Performs an HTTP DELETE request and returns the parsed response.
        /// </summary>
        public async Task<object?> DeleteAsync(string path)
        {
            var url = BuildUrl(path);
            try
            {
                var response = await _sharedClient.DeleteAsync(url).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseResponseBody(responseBody, response.StatusCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[http] DELETE {url} failed: {ex.Message}");
                return null;
            }
        }

        private string BuildUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return _baseUrl;

            path = path.Trim();
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return path;

            if (string.IsNullOrWhiteSpace(_baseUrl))
                return path;

            return _baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        private static string SerializeBody(object? body)
        {
            if (body == null)
                return "{}";

            if (body is string str)
                return str;

            try
            {
                return JsonSerializer.Serialize(body);
            }
            catch
            {
                return body.ToString() ?? "{}";
            }
        }

        /// <summary>
        /// Parses an HTTP response body string.
        /// Attempts to deserialize as JSON; falls back to returning the raw string.
        /// </summary>
        public static object? ParseResponseBody(string body, System.Net.HttpStatusCode statusCode)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                // Return a minimal success/failure object.
                return new Dictionary<string, object?>
                {
                    ["success"] = (int)statusCode >= 200 && (int)statusCode < 300,
                    ["status"] = (long)(int)statusCode,
                    ["body"] = ""
                };
            }

            // Try JSON deserialization.
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                if (parsed != null)
                {
                    var result = new Dictionary<string, object?>();
                    foreach (var kv in parsed)
                        result[kv.Key] = JsonElementToObject(kv.Value);

                    // Inject success and status if not present.
                    if (!result.ContainsKey("success"))
                        result["success"] = (int)statusCode >= 200 && (int)statusCode < 300;
                    if (!result.ContainsKey("status"))
                        result["status"] = (long)(int)statusCode;

                    return result;
                }
            }
            catch { }

            // Try JSON array.
            try
            {
                var arr = JsonSerializer.Deserialize<List<JsonElement>>(body);
                if (arr != null)
                {
                    return new Dictionary<string, object?>
                    {
                        ["success"] = (int)statusCode >= 200 && (int)statusCode < 300,
                        ["status"] = (long)(int)statusCode,
                        ["data"] = arr.Select(JsonElementToObject).ToList()
                    };
                }
            }
            catch { }

            // Raw string body.
            return new Dictionary<string, object?>
            {
                ["success"] = (int)statusCode >= 200 && (int)statusCode < 300,
                ["status"] = (long)(int)statusCode,
                ["body"] = body
            };
        }

        private static object? JsonElementToObject(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => element.EnumerateObject()
                    .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
                JsonValueKind.Array => element.EnumerateArray()
                    .Select(JsonElementToObject).ToList<object?>(),
                _ => element.GetRawText()
            };
        }

        public void Dispose() { }
    }
}
