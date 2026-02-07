using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Processor
{
    public class Command
    {
        public object? Operand1 { get; set; }
        public object? Operand2 { get; set; }
        public Opcodes Opcode { get; set; } = Opcodes.Nop;
    }
}
