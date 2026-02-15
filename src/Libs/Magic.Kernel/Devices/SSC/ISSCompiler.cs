using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Core;

namespace Magic.Kernel.Devices.SSC
{
    public interface ISSCompiler
    {
        public Task CompileAsync(IStreamDevice device);
    }
}
