using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IStreamChunk
    {
        public byte[] Data { get; set; }
    }
}
