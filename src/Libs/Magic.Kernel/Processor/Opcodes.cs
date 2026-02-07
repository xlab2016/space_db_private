using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Processor
{
    public enum Opcodes : int
    {
        Nop = 0,
        AddVertex = 1,
        AddRelation = 2,
        AddShape = 3,
        Call = 4,
        Push = 5,
        Pop = 6,
        SysCall = 7,
        Ret = 8,
        Move = 9,
        GetVertex = 10
    }
}
