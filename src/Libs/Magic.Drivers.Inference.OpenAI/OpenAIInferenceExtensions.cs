using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Devices.Streams.Inference;

namespace Magic.Drivers.Inference.OpenAI
{
    /// <summary>Extension helpers for integrating the full OpenAI streaming driver with <see cref="OpenAIInference"/>.</summary>
    public static class OpenAIInferenceExtensions
    {
        /// <summary>Creates a fully-connected <see cref="OpenAIInference"/> instance wired to the real <see cref="OpenAIDriver"/>.</summary>
        public static OpenAIInference CreateOpenAIInference(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o")
        {
            return new OpenAIInference
            {
                Token = apiToken,
                ApiBase = apiBase,
                Model = model
            };
        }

        /// <summary>Sends a streaming request via <see cref="OpenAIDriver"/> and streams deltas into <paramref name="responseDevice"/>.</summary>
        public static Task StreamAsync(
            string apiToken,
            string apiBase,
            string model,
            object? payload,
            List<object?> history,
            string? systemPrompt,
            OpenAIStreamingResponse responseDevice,
            CancellationToken cancellationToken = default)
        {
            var driver = new OpenAIDriver(apiToken, apiBase, model);
            return driver.SendStreamingRequestAsync(payload, history, systemPrompt, responseDevice, cancellationToken);
        }
    }
}
