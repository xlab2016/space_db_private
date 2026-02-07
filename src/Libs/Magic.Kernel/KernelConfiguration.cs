using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel
{
    public class KernelConfiguration
    {
        public ISpaceDisk? DefaultDisk { get; set; }
        public IInferenceDevice? DefaultInferenceDevice { get; set; }
        public IProjectorDevice? DefaultProjectorDevice { get; set; }
    }
}
