using Magic.Kernel.Compilation;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel
{
    public class KernelConfiguration
    {
        /// <summary>Currently running unit (set by Interpreter). Used to read SpaceName for disk ops.</summary>
        public ExecutableUnit? CurrentExecutableUnit { get; set; }

        public ISpaceDisk? DefaultDisk { get; set; }
        public ISSCompiler? DefaultSSCCompiler { get; set; }
        public IInferenceDevice? DefaultInferenceDevice { get; set; }
        public IProjectorDevice? DefaultProjectorDevice { get; set; }
    }
}
