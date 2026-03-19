using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Drivers.Inference.OpenAI
{
    /// <summary>Standalone OpenAI Chat Completions streaming HTTP client.
    /// Has no dependency on Magic.Kernel — can be used independently.</summary>
    public class OpenAIHttpClient
    {
        private readonly string _apiToken;
        private readonly string _apiBase;
        private readonly string _model;

        public OpenAIHttpClient(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o")
        {
            _apiToken = apiToken ?? throw new ArgumentNullException(nameof(apiToken));
            _apiBase = apiBase.TrimEnd('/');
            _model = model;
        }

        /// <summary>Sends a streaming chat completion request to the OpenAI API.
        /// Calls <paramref name="onDelta"/> for each text delta and <paramref name="onFinish"/> when the stream ends.</summary>
        public async Task SendStreamingAsync(
            object? payload,
            IReadOnlyList<object?> history,
            string? systemPrompt,
            Action<string> onDelta,
            Action onFinish,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

                var messages = new List<object>();

                if (!string.IsNullOrEmpty(systemPrompt))
                    messages.Add(new { role = "system", content = systemPrompt });

                foreach (var item in history)
                {
                    if (item is IDictionary<string, object?> historyEntry)
                    {
                        var role = historyEntry.TryGetValue("role", out var r) ? r?.ToString() ?? "user" : "user";
                        var entryContent = historyEntry.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                        messages.Add(new { role, content = entryContent });
                    }
                }

                string? instruction = null;
                if (payload is IDictionary<string, object?> payloadDict)
                {
                    payloadDict.TryGetValue("instruction", out var instrObj);
                    instruction = instrObj?.ToString();

                    if (payloadDict.TryGetValue("system", out var sysObj) && !string.IsNullOrEmpty(sysObj?.ToString()))
                    {
                        var hasSystem = messages.Count > 0 &&
                            messages[0].GetType().GetProperty("role")?.GetValue(messages[0])?.ToString() == "system";
                        if (!hasSystem)
                            messages.Insert(0, new { role = "system", content = sysObj!.ToString() });
                    }
                }
                else if (payload is string payloadStr)
                {
                    instruction = payloadStr;
                }

                if (!string.IsNullOrEmpty(instruction))
                    messages.Add(new { role = "user", content = instruction });

                var requestBody = new
                {
                    model = _model,
                    messages,
                    stream = true
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var url = $"{_apiBase}/v1/chat/completions";
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return;

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new System.IO.StreamReader(stream);

                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line.Substring(6).Trim();
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var choice = choices[0];
                            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var contentProp))
                            {
                                var text = contentProp.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    onDelta(text);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Malformed SSE line — skip.
                    }
                }
            }
            finally
            {
                onFinish();
            }
        }
    }
}
