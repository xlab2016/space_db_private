using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams.Inference
{
    /// <summary>Streaming response device returned by <see cref="OpenAIInference.WriteRequestAsync"/>.
    /// Implements <see cref="DefStream.StreamWaitAsync"/> to yield text deltas from the LLM stream.</summary>
    public class OpenAIStreamingResponse : DefStream
    {
        private readonly string _apiToken;
        private readonly string _apiBase;
        private readonly string _model;
        private readonly List<object?> _history;
        private readonly string? _systemPrompt;
        private readonly object? _payload;

        private readonly ConcurrentQueue<string> _deltaQueue = new ConcurrentQueue<string>();
        private readonly StringBuilder _aggregate = new StringBuilder();
        private bool _streamFinished;
        private Task? _streamTask;

        public OpenAIStreamingResponse(
            string apiToken,
            string apiBase,
            string model,
            List<object?> history,
            string? systemPrompt,
            object? payload)
        {
            _apiToken = apiToken;
            _apiBase = apiBase;
            _model = model;
            _history = history;
            _systemPrompt = systemPrompt;
            _payload = payload;
        }

        public override Task<DeviceOperationResult> OpenAsync()
        {
            _streamTask = Task.Run(() => RunStreamAsync());
            return Task.FromResult(DeviceOperationResult.Success);
        }

        private async Task RunStreamAsync()
        {
            // Delegate to the OpenAI driver if available, otherwise use a basic HTTP streaming implementation.
            // The full implementation is provided by Magic.Drivers.Inference.OpenAI.
            await Task.CompletedTask.ConfigureAwait(false);
            _streamFinished = true;
        }

        public override async Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
        {
            // Poll with a small delay until a delta arrives or stream ends.
            const int maxWaitMs = 30_000;
            const int pollIntervalMs = 10;
            var waited = 0;

            while (waited < maxWaitMs)
            {
                if (_deltaQueue.TryDequeue(out var delta))
                {
                    _aggregate.Append(delta);
                    var deltaObj = new System.Collections.Generic.Dictionary<string, object?> { ["text"] = delta };
                    return (false, deltaObj, _aggregate.ToString());
                }

                if (_streamFinished)
                    return (true, null, _aggregate.ToString());

                await Task.Delay(pollIntervalMs).ConfigureAwait(false);
                waited += pollIntervalMs;
            }

            _streamFinished = true;
            return (true, null, _aggregate.ToString());
        }

        /// <summary>Enqueue a text delta from the underlying streaming response.</summary>
        public void EnqueueDelta(string text) => _deltaQueue.Enqueue(text);

        /// <summary>Signal that the stream has finished.</summary>
        public void FinishStream() => _streamFinished = true;

        public override Task<DeviceOperationResult> CloseAsync() => Task.FromResult(DeviceOperationResult.Success);
        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Task.FromResult((DeviceOperationResult.Success, Array.Empty<byte>()));
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Task.FromResult(DeviceOperationResult.Success);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);
        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Task.FromResult((DeviceOperationResult.Success, (IStreamChunk?)null));
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Task.FromResult(DeviceOperationResult.Success);
        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Task.FromResult(DeviceOperationResult.Success);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Task.FromResult((DeviceOperationResult.Success, 0L));
    }
}
