using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Generic stream device wrapper; override or use for in-memory / test streams.</summary>
    public class StreamDevice : IStreamDevice, IType
    {
        private readonly Func<byte[]?>? _readAll;
        private readonly Action<byte[]?>? _writeAll;
        private long _length;
        private long _position;

        public long? Index { get; set; }
        public string Name { get; set; } = "";
        public List<IType> Generalizations { get; set; } = new List<IType>();

        public StreamDevice(Func<byte[]?>? readAll = null, Action<byte[]?>? writeAll = null, long length = 0)
        {
            _readAll = readAll;
            _writeAll = writeAll;
            _length = length;
        }

        public Task<DeviceOperationResult> Open() => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> Read(out byte[] bytes)
        {
            bytes = _readAll?.Invoke() ?? Array.Empty<byte>();
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> Write(byte[] bytes)
        {
            _writeAll?.Invoke(bytes);
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> Control(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> Close() => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> ReadChunkAsync(out IStreamChunk chunk)
        {
            chunk = new StreamChunk { Data = _readAll?.Invoke() ?? Array.Empty<byte>() };
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
        {
            _writeAll?.Invoke(chunk?.Data);
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> MoveAsync(long offset)
        {
            _position = Math.Max(0, offset);
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> LengthAsync(out long length)
        {
            length = _length;
            return Task.FromResult(DeviceOperationResult.Success);
        }
    }
}
