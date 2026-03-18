using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;

namespace Magic.Kernel.Devices.Streams.Inference
{
    /// <summary>OpenAI-backed inference stream device. Integrates with the OpenAI Chat Completions API (streaming).
    /// Registered in <see cref="Magic.Kernel.Core.OS.Hal.DefGen"/> via <c>stream&lt;inference,openai&gt;</c>.</summary>
    public class OpenAIInference : InferenceDevice
    {
        /// <summary>OpenAI API base URL. Defaults to the standard OpenAI endpoint.</summary>
        public string ApiBase { get; set; } = "https://api.openai.com";

        /// <summary>Model name to use for completions.</summary>
        public string Model { get; set; } = "gpt-4o";

        protected override IStreamDevice CreateDriver(string apiToken)
        {
            return new OpenAIDriver(apiToken, ApiBase, Model);
        }

        protected override async Task<object?> WriteRequestAsync(object? payload)
        {
            var responseDevice = new OpenAIStreamingResponse(Token, ApiBase, Model, History, SystemPrompt, payload);
            // Start streaming in the background via the OpenAI driver.
            var driver = new OpenAIDriver(Token, ApiBase, Model);
            await driver.OpenAsync().ConfigureAwait(false);
            _ = driver.SendStreamingRequestAsync(payload, History, SystemPrompt, responseDevice);
            return responseDevice;
        }
    }
}
