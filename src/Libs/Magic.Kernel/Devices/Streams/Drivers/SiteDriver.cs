using System.Net;
using System.Text;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>
    /// HTTP Site server: listens on a configurable port and returns an empty HTML page for any request.
    /// Suitable for use as a simple site device in the AGI stream system.
    /// </summary>
    public sealed class SiteDriver : IStreamDevice
    {
        private int _port;
        private string _serverName = "site";

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static readonly byte[] EmptyHtmlBytes = Encoding.UTF8.GetBytes("<html></html>");

        public int Port => _port;

        public bool IsListening => _listener?.IsListening == true && _serverTask != null && !_serverTask.IsCompleted;

        public void SetServerName(string name)
        {
            _serverName = string.IsNullOrWhiteSpace(name) ? "site" : name.Trim();
        }

        public void ParseAndApplyConfig(object? config)
        {
            if (config == null) return;

            if (config is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("port", out var portObj))
                    _port = ParseInt(portObj, 8080);
            }

            if (_port <= 0) _port = 8080;
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            if (_port <= 0) _port = 8080;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _listener?.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{_serverName}] [site] Failed to start listener on port {_port}: {ex.Message}");
                return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.IOError, ex.Message));
            }

            Console.WriteLine($"[{_serverName}] [site] Listening on port {_port}");

            _serverTask = Task.Run(() => ServeLoopAsync(token), CancellationToken.None);

            return Task.FromResult(DeviceOperationResult.Success);
        }

        private async Task ServeLoopAsync(CancellationToken token)
        {
            var listener = _listener;
            if (listener == null) return;

            try
            {
                while (!token.IsCancellationRequested && listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) when (token.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleRequestAsync(ctx), CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[{_serverName}] [site] Server loop error: {ex.Message}");
            }
            finally
            {
                _stoppedTcs.TrySetResult();
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var response = ctx.Response;
                response.StatusCode = 200;
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = EmptyHtmlBytes.Length;

                await response.OutputStream.WriteAsync(EmptyHtmlBytes, 0, EmptyHtmlBytes.Length).ConfigureAwait(false);
                response.OutputStream.Close();
            }
            catch
            {
                // ignore per-request errors
            }
        }

        /// <summary>Waits until the server stops (listener closed or cancelled).</summary>
        public Task AwaitUntilStoppedAsync() => _stoppedTcs.Task;

        public async Task<DeviceOperationResult> CloseAsync()
        {
            _cts?.Cancel();

            try
            {
                _listener?.Close();
            }
            catch { }

            if (_serverTask != null)
            {
                try
                {
                    await _serverTask.ConfigureAwait(false);
                }
                catch { }
            }

            _stoppedTcs.TrySetResult();

            Console.WriteLine($"[{_serverName}] [site] Stopped.");
            return DeviceOperationResult.Success;
        }

        public Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.NotSupported("SiteDriver does not support Read"), Array.Empty<byte>()));

        public Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support Write"));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support Control"));

        public Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => Task.FromResult<(DeviceOperationResult, IStreamChunk?)>((DeviceOperationResult.NotSupported("SiteDriver does not support ReadChunk"), null));

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support WriteChunk"));

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support Move"));

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.NotSupported("SiteDriver does not support Length"), 0L));

        private static int ParseInt(object? obj, int defaultVal)
        {
            if (obj is long l) return (int)l;
            if (obj is int i) return i;
            if (obj is string s && int.TryParse(s, out var v)) return v;
            return defaultVal;
        }
    }
}
