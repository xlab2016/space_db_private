using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>
    /// HTTP stream device: wraps an HTTP client (<see cref="HttpDriver"/>) as a stream device.
    /// Supports <c>get</c>, <c>post</c>, <c>put</c>, and <c>delete</c> method calls.
    /// <para>
    /// Usage in AGI:
    /// <code>
    /// var api := stream&lt;http&gt;;
    ///
    /// // POST request with JSON body, awaiting a JSON response.
    /// var response := json: await api.post("/api/v1/authenticate", {
    ///   username, password
    /// });
    /// return response.success;
    ///
    /// // GET request.
    /// var data := await api.get("/api/v1/users");
    ///
    /// // With base URL configured.
    /// api.config({ baseUrl: "https://api.example.com" });
    /// </code>
    /// </para>
    /// </summary>
    public class HttpStreamDevice : DefStream
    {
        private HttpDriver? _driver;

        private HttpDriver Driver => _driver ??= new HttpDriver();

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = (methodName ?? "").Trim().ToLowerInvariant();

            switch (name)
            {
                case "get":
                {
                    var path = ExtractPath(args);
                    return await Driver.GetAsync(path).ConfigureAwait(false);
                }

                case "post":
                {
                    var path = ExtractPath(args);
                    var body = args.Length > 1 ? args[1] : null;
                    return await Driver.PostAsync(path, body).ConfigureAwait(false);
                }

                case "put":
                {
                    var path = ExtractPath(args);
                    var body = args.Length > 1 ? args[1] : null;
                    return await Driver.PutAsync(path, body).ConfigureAwait(false);
                }

                case "delete":
                {
                    var path = ExtractPath(args);
                    return await Driver.DeleteAsync(path).ConfigureAwait(false);
                }

                case "config":
                case "configure":
                case "open":
                {
                    if (args.Length > 0)
                        Driver.ParseAndApplyConfig(args[0]);
                    return DeviceOperationResult.Success;
                }

                default:
                    throw new CallUnknownMethodException(name, this);
            }
        }

        private static string ExtractPath(object?[] args)
        {
            if (args.Length > 0 && args[0] is string s)
                return s;
            return "";
        }

        public override Task<DeviceOperationResult> OpenAsync()
            => Task.FromResult(DeviceOperationResult.Success);

        public override Task<object?> AwaitObjAsync()
            => Task.FromResult<object?>(this);

        public override Task<object?> Await()
            => Task.FromResult<object?>(this);

        public override Task<DeviceOperationResult> CloseAsync()
        {
            _driver?.Dispose();
            _driver = null;
            UnregisterFromStreamRegistry();
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.NotSupported("HttpStreamDevice does not support Read"), Array.Empty<byte>()));

        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.NotSupported("HttpStreamDevice does not support Write"));

        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.NotSupported("HttpStreamDevice does not support Control"));

        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => Task.FromResult<(DeviceOperationResult, IStreamChunk?)>((DeviceOperationResult.NotSupported("HttpStreamDevice does not support ReadChunk"), null));

        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.NotSupported("HttpStreamDevice does not support WriteChunk"));

        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.NotSupported("HttpStreamDevice does not support Move"));

        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.NotSupported("HttpStreamDevice does not support Length"), 0L));
    }
}
