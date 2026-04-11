using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>
    /// Site device: an HTTP server that listens on a configurable port and returns an empty HTML page for any request.
    /// Usage in AGI:
    /// <code>
    /// Site1} : site { }
    ///
    /// procedure Main() {
    ///   var frontend := stream&lt;site, Site1&gt;;
    ///   frontend.open({ port: 6000 });
    ///   await frontend;
    /// }
    /// </code>
    /// </summary>
    public class SiteStreamDevice : DefStream
    {
        private SiteDriver? _driver;

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";

            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
                return await HandleOpenAsync(args).ConfigureAwait(false);

            throw new CallUnknownMethodException(name, this);
        }

        private async Task<DeviceOperationResult> HandleOpenAsync(object?[]? args)
        {
            _driver ??= new SiteDriver();
            _driver.SetServerName(Name);
            if (args != null && args.Length > 0)
                _driver.ParseAndApplyConfig(args[0]);

            return await _driver.OpenAsync().ConfigureAwait(false);
        }

        private IStreamDevice Driver => _driver ?? throw new InvalidOperationException("Device not opened. Call site.open first.");

        public override Task<DeviceOperationResult> OpenAsync() => Driver.OpenAsync();

        public override async Task<object?> AwaitObjAsync()
        {
            if (_driver != null)
                await _driver.AwaitUntilStoppedAsync().ConfigureAwait(false);
            return this;
        }

        public override Task<object?> Await() => AwaitObjAsync();

        public override async Task<DeviceOperationResult> CloseAsync()
        {
            try
            {
                if (_driver != null)
                    await _driver.CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                UnregisterFromStreamRegistry();
            }

            return DeviceOperationResult.Success;
        }

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Driver.ReadAsync();
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Driver.WriteAsync(bytes);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Driver.ControlAsync(deviceControl);
        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Driver.ReadChunkAsync();
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Driver.WriteChunkAsync(chunk);
        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Driver.MoveAsync(position);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Driver.LengthAsync();
    }
}
