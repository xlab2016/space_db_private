using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Inference;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Driver for OpenAI Chat Completions API with streaming support.
    /// Delegates HTTP communication to <see cref="Magic.Drivers.Inference.OpenAI.OpenAIHttpClient"/>.
    /// Used by <see cref="OpenAIInference"/> to send requests and stream back deltas.</summary>
    public class OpenAIDriver : IStreamDevice
    {
        private readonly string _apiToken;
        private readonly string _apiBase;
        private readonly string _model;
        private bool _opened;

        public OpenAIDriver(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o-mini")
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

        /// <summary>Accepts UTF-8 JSON request payload bytes. Streaming responses are retrieved via <see cref="SendStreamingRequestAsync"/>.</summary>
        public Task<DeviceOperationResult> WriteAsync(byte[] bytes)
        {
            if (!_opened)
                return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Driver not opened"));
            return Task.FromResult(DeviceOperationResult.Success);
        }

        /// <summary>Returns end-of-stream immediately. Full streaming is handled via <see cref="SendStreamingRequestAsync"/>.</summary>
        public Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            return Task.FromResult((DeviceOperationResult.Success, (IStreamChunk?)null));
        }

        /// <summary>Starts a streaming inference request via <see cref="Magic.Drivers.Inference.OpenAI.OpenAIHttpClient"/>
        /// and enqueues deltas into <paramref name="responseDevice"/>.
        /// The HTTP request fires immediately; the SSE stream is read in parallel while this method returns.</summary>
        public Task SendStreamingRequestAsync(
            Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest request,
            OpenAIStreamingResponse responseDevice,
            CancellationToken cancellationToken = default)
        {
            var client = new Magic.Drivers.Inference.OpenAI.OpenAIHttpClient(_apiToken, _apiBase, _model);
            _ = client.SendStreamingAsync(
                request,
                delta => responseDevice.EnqueueDelta(delta),
                () => responseDevice.FinishStream(),
                cancellationToken);
            return Task.CompletedTask;
        }

        public Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Task.FromResult((DeviceOperationResult.Success, Array.Empty<byte>()));
        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);
        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Task.FromResult(DeviceOperationResult.Success);
        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Task.FromResult(DeviceOperationResult.Success);
        public Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Task.FromResult((DeviceOperationResult.Success, 0L));
    }
}
