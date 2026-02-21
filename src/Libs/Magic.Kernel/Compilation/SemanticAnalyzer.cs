using System.Collections.Generic;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel;

namespace Magic.Kernel.Compilation
{
    /// <summary>Результат анализа программы: скомпилированные entrypoint, процедуры и функции.</summary>
    public class AnalyzedProgram
    {
        public ExecutionBlock EntryPoint { get; set; } = new ExecutionBlock();
        public Dictionary<string, Processor.Procedure> Procedures { get; set; } = new Dictionary<string, Processor.Procedure>();
        public Dictionary<string, Processor.Function> Functions { get; set; } = new Dictionary<string, Processor.Function>();
    }

    public class SemanticAnalyzer
    {
        private readonly Assembler _assembler;

        public SemanticAnalyzer()
        {
            _assembler = new Assembler();
        }

        public Command Analyze(InstructionNode instruction)
        {
            var opcode = MapOpcode(instruction.Opcode);
            return _assembler.Emit(opcode, instruction.Parameters);
        }

        /// <summary>Компилирует полную структуру программы (AST) в команды. Если entrypoint пуст — парсит sourceCode как плоский набор инструкций (fallback).</summary>
        public AnalyzedProgram AnalyzeProgram(ProgramStructure programStructure, Parser parser, string sourceCode)
        {
            var result = new AnalyzedProgram();
            if (programStructure.EntryPoint != null && programStructure.EntryPoint.Count > 0)
            {
                foreach (var astNode in programStructure.EntryPoint)
                    result.EntryPoint.Add(Analyze(astNode));
            }
            else
            {
                result.EntryPoint = AnalyzeInstructions(parser.ParseAsInstructionSet(sourceCode));
            }
            foreach (var proc in programStructure.Procedures)
            {
                var procedure = new Processor.Procedure { Name = proc.Key };
                foreach (var astNode in proc.Value)
                    procedure.Body.Add(Analyze(astNode));
                result.Procedures[proc.Key] = procedure;
            }
            foreach (var func in programStructure.Functions)
            {
                var function = new Processor.Function { Name = func.Key };
                foreach (var astNode in func.Value)
                    function.Body.Add(Analyze(astNode));
                result.Functions[func.Key] = function;
            }
            return result;
        }

        /// <summary>Компилирует список AST-инструкций в блок команд (для fallback без структуры программы).</summary>
        public ExecutionBlock AnalyzeInstructions(IEnumerable<InstructionNode> nodes)
        {
            var block = new ExecutionBlock();
            foreach (var node in nodes)
                block.Add(Analyze(node));
            return block;
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
