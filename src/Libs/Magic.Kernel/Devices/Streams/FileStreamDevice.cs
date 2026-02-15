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
    public class FileStreamDevice : IStreamDevice, IType
    {
        private readonly string _path;
        private FileStream? _stream;

        public long? Index { get; set; }
        public string Name { get; set; } = "";
        public List<IType> Generalizations { get; set; } = new List<IType>();

        public FileStreamDevice(string path)
        {
            _path = path ?? "";
        }

        public Task<DeviceOperationResult> Open()
        {
            try
            {
                if (File.Exists(_path))
                {
                    _stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                }
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }

        public Task<DeviceOperationResult> Read(out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            try
            {
                if (File.Exists(_path))
                    bytes = File.ReadAllBytes(_path);
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }

        public Task<DeviceOperationResult> Write(byte[] bytes)
        {
            try
            {
                if (bytes != null && bytes.Length > 0)
                    File.WriteAllBytes(_path, bytes);
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }

        public Task<DeviceOperationResult> Control(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> Close()
        {
            try
            {
                _stream?.Dispose();
                _stream = null;
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }

        public Task<DeviceOperationResult> ReadChunkAsync(out IStreamChunk chunk)
        {
            chunk = new StreamChunk();
            try
            {
                if (File.Exists(_path))
                    chunk.Data = File.ReadAllBytes(_path);
                else
                    chunk.Data = Array.Empty<byte>();
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                chunk.Data = Array.Empty<byte>();
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
        {
            try
            {
                if (chunk?.Data != null && chunk.Data.Length > 0)
                    File.WriteAllBytes(_path, chunk.Data);
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }

        public Task<DeviceOperationResult> MoveAsync(long offset) => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> LengthAsync(out long length)
        {
            length = 0;
            try
            {
                if (File.Exists(_path))
                    length = new FileInfo(_path).Length;
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch
            {
                return Task.FromResult(DeviceOperationResult.Failed);
            }
        }
    }
}
