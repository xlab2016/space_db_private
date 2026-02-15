using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Core
{
    public interface IType
    {
        public long? Index { get; set; }
        public string Name { get; set; }

        public List<IType> Generalizations { get; set; }
    }
}
