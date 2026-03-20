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

        public OpenAIHttpClient(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o-mini")
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

        /// <summary>Sends a streaming chat completion request to the OpenAI API using a typed <see cref="OpenAIInferenceRequest"/>.
        /// Fields are composed into a structured XML prompt to clearly separate instructions from data
        /// and prevent prompt injection.
        /// The HTTP request is awaited immediately so the response headers are received before reading the SSE stream.
        /// Calls <paramref name="onDelta"/> for each text delta and <paramref name="onFinish"/> when the stream ends.</summary>
        public async Task SendStreamingAsync(
            OpenAIInferenceRequest request,
            Action<string> onDelta,
            Action onFinish,
            CancellationToken cancellationToken = default)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

            var messages = new List<object>();

            // Promote system to a dedicated system role message.
            if (!string.IsNullOrEmpty(request.System))
                messages.Add(new { role = "system", content = request.System });

            // Replay conversation history.
            foreach (var item in request.History)
            {
                if (item is IDictionary<string, object?> historyEntry)
                {
                    var role = historyEntry.TryGetValue("role", out var r) ? r?.ToString() ?? "user" : "user";
                    var entryContent = historyEntry.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                    messages.Add(new { role, content = entryContent });
                }
            }

            // Build structured XML user message from typed fields.
            var userMessage = BuildStructuredPrompt(
                request.Data,
                request.System,
                request.Instruction,
                null,           // history already added above as chat messages
                null,           // tools (reserved for future use)
                request.Skills);

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
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            // Await the HTTP request immediately so the response headers are received before delegating to the stream loop.
            var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await RunStreamLoopAsync(httpClient, response, onDelta, onFinish, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Reads the SSE stream from the HTTP response, enqueuing text deltas via <paramref name="onDelta"/>.
        /// Calls <paramref name="onFinish"/> when the stream ends or an error occurs.</summary>
        private static async Task RunStreamLoopAsync(
            HttpClient httpClient,
            HttpResponseMessage response,
            Action<string> onDelta,
            Action onFinish,
            CancellationToken cancellationToken)
        {
            try
            {
                using var _ = response;

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
                httpClient.Dispose();
            }
        }
    }
}
