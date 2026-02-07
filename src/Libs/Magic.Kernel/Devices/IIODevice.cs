using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IIODevice
    {
        public Task<DeviceOperationResult> Open();
        public Task<DeviceOperationResult> Read(out byte[] bytes);
        public Task<DeviceOperationResult> Write(byte[] bytes);
        public Task<DeviceOperationResult> Control(DeviceControlBase deviceControl);
        public Task<DeviceOperationResult> Close();
    }
}
