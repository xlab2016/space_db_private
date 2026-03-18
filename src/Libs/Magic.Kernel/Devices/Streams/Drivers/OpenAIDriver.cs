using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Inference;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Driver for OpenAI Chat Completions API with streaming support.
    /// Used by <see cref="OpenAIInference"/> to send requests and stream back deltas.</summary>
    public class OpenAIDriver : IStreamDevice
    {
        private readonly string _apiToken;
        private readonly string _apiBase;
        private readonly string _model;
        private bool _opened;

        public OpenAIDriver(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o")
        {
            _apiToken = apiToken ?? throw new ArgumentNullException(nameof(apiToken));
            _apiBase = apiBase.TrimEnd('/');
            _model = model;
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            _opened = true;
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> CloseAsync()
        {
            _opened = false;
            return Task.FromResult(DeviceOperationResult.Success);
        }

        /// <summary>Sends a streaming chat completion request.
        /// <paramref name="bytes"/> should be UTF-8 JSON of the request payload.</summary>
        public async Task<DeviceOperationResult> WriteAsync(byte[] bytes)
        {
            if (!_opened)
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Driver not opened");

            // Streaming responses are handled via ReadChunkAsync after writing.
            return DeviceOperationResult.Success;
        }

        /// <summary>Sends a chat completion request and streams back a single response chunk.</summary>
        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            // Full streaming implementation is in Magic.Drivers.Inference.OpenAI.
            // This stub returns end-of-stream immediately.
            return (DeviceOperationResult.Success, null);
        }

        /// <summary>Sends a streaming inference request and enqueues deltas into the response device.</summary>
        public async Task SendStreamingRequestAsync(
            object? payload,
            List<object?> history,
            string? systemPrompt,
            OpenAIStreamingResponse responseDevice,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

                var messages = new List<object>();

                if (!string.IsNullOrEmpty(systemPrompt))
                    messages.Add(new { role = "system", content = systemPrompt });

                // Add history items
                foreach (var item in history)
                {
                    if (item is IDictionary<string, object?> historyEntry)
                    {
                        var role = historyEntry.TryGetValue("role", out var r) ? r?.ToString() ?? "user" : "user";
                        var content = historyEntry.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                        messages.Add(new { role, content });
                    }
                }

                // Build user message from payload
                string? instruction = null;
                if (payload is IDictionary<string, object?> payloadDict)
                {
                    payloadDict.TryGetValue("instruction", out var instrObj);
                    instruction = instrObj?.ToString();
                    if (!string.IsNullOrEmpty(payloadDict.TryGetValue("system", out var sysObj) ? sysObj?.ToString() : null))
                        if (messages.Count == 0 || !(messages[0] is { } m && m.GetType().GetProperty("role")?.GetValue(m)?.ToString() == "system"))
                            messages.Insert(0, new { role = "system", content = sysObj!.ToString() });
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
                {
                    responseDevice.FinishStream();
                    return;
                }

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
                                    responseDevice.EnqueueDelta(text);
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
                responseDevice.FinishStream();
            }
        }

        public Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Task.FromResult((DeviceOperationResult.Success, Array.Empty<byte>()));
        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);
        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Task.FromResult(DeviceOperationResult.Success);
        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Task.FromResult(DeviceOperationResult.Success);
        public Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Task.FromResult((DeviceOperationResult.Success, 0L));
    }
}
