using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel;

namespace Magic.Kernel.Compilation
{
    public class SemanticAnalyzer
    {
        private readonly Assembler _assembler;

        public SemanticAnalyzer()
        {
            _assembler = new Assembler();
        }

        public Command Analyze(InstructionNode instruction)
        {
            // Преобразуем opcode
            var opcode = MapOpcode(instruction.Opcode);

            // Используем Assembler для эмита команды
            return _assembler.Emit(opcode, instruction.Parameters);
        }

        private Opcodes MapOpcode(string opcode)
        {
            return opcode.ToLower() switch
            {
                "addvertex" => Opcodes.AddVertex,
                "addrelation" => Opcodes.AddRelation,
                "addshape" => Opcodes.AddShape,
                "call" => Opcodes.Call,
                "push" => Opcodes.Push,
                "pop" => Opcodes.Pop,
                "syscall" => Opcodes.SysCall,
                "ret" => Opcodes.Ret,
                "move" => Opcodes.Move,
                "getvertex" => Opcodes.GetVertex,
                "def" => Opcodes.Def,
                "defgen" => Opcodes.DefGen,
                "callobj" => Opcodes.CallObj,
                "awaitobj" => Opcodes.AwaitObj,
                _ => Opcodes.Nop
            };
        }
    }
}
