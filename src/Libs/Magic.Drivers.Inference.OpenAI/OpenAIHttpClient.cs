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

        /// <summary>Builds a structured XML prompt from individual sections to prevent prompt injection.
        /// Sections with null or empty values are omitted. Non-string values are serialised as JSON.</summary>
        public static string BuildStructuredPrompt(
            object? data,
            string? system,
            string? instruction,
            IReadOnlyList<object?>? history,
            object? mcp,
            object? skills)
        {
            var sb = new StringBuilder();

            AppendSection(sb, "system", system);
            AppendSection(sb, "instruction", instruction);

            if (data != null)
                AppendSection(sb, "data", Serialize(data));

            if (history != null && history.Count > 0)
                AppendSection(sb, "history", Serialize(history));

            if (mcp != null)
                AppendSection(sb, "mcp", Serialize(mcp));

            if (skills != null)
                AppendSection(sb, "skills", Serialize(skills));

            return sb.ToString().Trim();
        }

        private static void AppendSection(StringBuilder sb, string tag, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            sb.Append('<').Append(tag).AppendLine(">")
              .AppendLine(value.Trim())
              .Append("</").Append(tag).AppendLine(">");
        }

        private static string Serialize(object? value)
        {
            if (value is string s)
                return s;
            return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>Sends a streaming chat completion request to the OpenAI API.
        /// The <paramref name="payload"/> dictionary may contain keys: <c>data</c>, <c>system</c>,
        /// <c>instruction</c>, <c>mcp</c>, <c>skills</c>. These are composed into a structured XML
        /// prompt to clearly separate instructions from data and prevent prompt injection.
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

                // Add top-level system prompt as a dedicated system role message.
                if (!string.IsNullOrEmpty(systemPrompt))
                    messages.Add(new { role = "system", content = systemPrompt });

                // Replay conversation history.
                foreach (var item in history)
                {
                    if (item is IDictionary<string, object?> historyEntry)
                    {
                        var role = historyEntry.TryGetValue("role", out var r) ? r?.ToString() ?? "user" : "user";
                        var entryContent = historyEntry.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                        messages.Add(new { role, content = entryContent });
                    }
                }

                // Extract structured fields from payload and build XML prompt.
                string? userMessage = null;

                if (payload is IDictionary<string, object?> payloadDict)
                {
                    payloadDict.TryGetValue("data", out var dataObj);
                    payloadDict.TryGetValue("system", out var sysObj);
                    payloadDict.TryGetValue("instruction", out var instrObj);
                    payloadDict.TryGetValue("mcp", out var mcpObj);
                    payloadDict.TryGetValue("skills", out var skillsObj);

                    var payloadSystem = sysObj?.ToString();

                    // If a system value is provided inside the payload and no top-level system prompt
                    // was given, promote it to a system role message so it is handled correctly by the API.
                    if (!string.IsNullOrEmpty(payloadSystem) && string.IsNullOrEmpty(systemPrompt))
                    {
                        var hasSystem = messages.Count > 0 &&
                            messages[0].GetType().GetProperty("role")?.GetValue(messages[0])?.ToString() == "system";
                        if (!hasSystem)
                            messages.Insert(0, new { role = "system", content = payloadSystem });
                    }

                    userMessage = BuildStructuredPrompt(
                        dataObj,
                        payloadSystem,
                        instrObj?.ToString(),
                        null,   // history already added above as chat messages
                        mcpObj,
                        skillsObj);
                }
                else if (payload is string payloadStr)
                {
                    userMessage = payloadStr;
                }

                if (!string.IsNullOrEmpty(userMessage))
                    messages.Add(new { role = "user", content = userMessage });

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
