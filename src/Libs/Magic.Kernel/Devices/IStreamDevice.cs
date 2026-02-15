using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IStreamDevice : IIODevice
    {
        public Task<DeviceOperationResult> ReadChunkAsync(out IStreamChunk chunk);
        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk);
        public Task<DeviceOperationResult> MoveAsync(long offset);
        public Task<DeviceOperationResult> LengthAsync(out long length);
    }
}
