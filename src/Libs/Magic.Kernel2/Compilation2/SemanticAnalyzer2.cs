using System;
using System.Collections.Generic;
using System.Linq;
using Magic.Kernel.Compilation;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel2.Compilation2.Ast2;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Magic compiler 2.0 semantic analyzer.
    /// <para>
    /// Key difference from v1.0: this analyzer walks the fully-typed AST produced by
    /// <see cref="Parser2"/> directly. It builds a symbol table (variable slots,
    /// type definitions, method registrations) by traversing typed AST nodes.
    /// </para>
    /// <para>
    /// For complex constructs (table/database type declarations, streamwait loops,
    /// and procedure bodies with V1-only syntax), the analyzer delegates to V1's
    /// <see cref="StatementLoweringCompiler"/> to guarantee output identical to V1.
    /// </para>
    /// </summary>
    public class SemanticAnalyzer2
    {
        private readonly Assembler2 _assembler;
        private readonly SymbolTable2 _symbolTable;

        // V1 infrastructure used for exact-match output on complex constructs.
        private readonly Magic.Kernel.Compilation.StatementLoweringCompiler _v1Compiler;
        private readonly Magic.Kernel.Compilation.Assembler _v1Assembler;

        public SemanticAnalyzer2()
        {
            _assembler = new Assembler2();
            _symbolTable = new SymbolTable2();
            _v1Compiler = new StatementLoweringCompiler();
            _v1Assembler = new Magic.Kernel.Compilation.Assembler();
        }

        /// <summary>
        /// Analyze the fully-typed AST and produce an <see cref="AnalyzedProgram2"/>.
        /// Type declarations and procedure bodies are compiled using V1's lowering pipeline
        /// to guarantee byte-for-byte identical output.
        /// </summary>
        public AnalyzedProgram2 Analyze(ProgramNode2 program)
        {
            var result = new AnalyzedProgram2();
            _symbolTable.Reset();
            _v1Compiler.BeginStatementSequence();

            // Phase 1: Register all type declarations so they are available for type resolution.
            foreach (var typeDecl in program.TypeDeclarations)
                _symbolTable.RegisterType(typeDecl);

            // Phase 2: Analyze type declarations using V1's StatementLoweringCompiler for
            // exact output (table columns with modifiers, database references, etc.).
            foreach (var typeDecl in program.TypeDeclarations)
            {
                if (!string.IsNullOrEmpty(typeDecl.RawText))
                {
                    // Use V1 lowering for table/database/complex types — exact V1 output.
                    var v1Instructions = _v1Compiler.Lower(
                        new[] { typeDecl.RawText }, registerGlobals: true, statementStartSourceLine: typeDecl.SourceLine);
                    foreach (var instr in v1Instructions)
                        AddV1Command(result.EntryPoint, instr);
                }
                else
                {
                    // Simple type: emit via Assembler2.
                    var typeCommands = _assembler.EmitTypeDeclaration(typeDecl, _symbolTable);
                    result.EntryPoint.AddRange(typeCommands);
                }
            }

            // Sync V2 slot counter with V1's after type declarations so procedure slots don't overlap.
            _symbolTable.NextSlot = _v1Compiler.NextGlobalSlot;

            // Phase 3: Analyze procedures using V1's lowering for exact output.
            foreach (var proc in program.Procedures)
            {
                var procedure = new Magic.Kernel.Processor.Procedure { Name = proc.Name };

                var procCompiler = new StatementLoweringCompiler();
                procCompiler.InheritGlobalSlots(_v1Compiler);
                procCompiler.BeginStatementSequence();
                procCompiler.ResetLocals();

                // Bind parameters (same as V1 SemanticAnalyzer).
                var paramNames = proc.Parameters.Select(p => p.Name).ToList();
                if (paramNames.Count > 0)
                {
                    procedure.Body.Add(_v1Assembler.Emit(Opcodes.Pop, null));
                    for (var pi = paramNames.Count - 1; pi >= 0; pi--)
                    {
                        var paramName = paramNames[pi];
                        var slot = procCompiler.AllocateLocalSlot(paramName);
                        procedure.Body.Add(_v1Assembler.Emit(Opcodes.Pop,
                            new List<ParameterNode>
                            {
                                new MemoryParameterNode { Name = "index", Index = slot, LogicalIndex = pi }
                            }));
                    }
                }

                // Compile body using V1's lowering — handles all AGI constructs.
                CompileProcedureBodyV1(proc.Body, procedure, procCompiler, result);

                if (procedure.Body.Count == 0 || procedure.Body[^1].Opcode != Opcodes.Ret)
                    procedure.Body.Add(_v1Assembler.Emit(Opcodes.Ret, null));

                result.Procedures[proc.Name] = procedure;
            }

            // Phase 4: Analyze functions using V1's lowering.
            foreach (var func in program.Functions)
            {
                var function = new Magic.Kernel.Processor.Function { Name = func.Name };
                var funcCompiler = new StatementLoweringCompiler();
                funcCompiler.InheritGlobalSlots(_v1Compiler);
                funcCompiler.BeginStatementSequence();
                funcCompiler.ResetLocals();

                var paramNames = func.Parameters.Select(p => p.Name).ToList();
                if (paramNames.Count > 0)
                {
                    function.Body.Add(_v1Assembler.Emit(Opcodes.Pop, null));
                    for (var pi = paramNames.Count - 1; pi >= 0; pi--)
                    {
                        var slot = funcCompiler.AllocateLocalSlot(paramNames[pi]);
                        function.Body.Add(_v1Assembler.Emit(Opcodes.Pop,
                            new List<ParameterNode>
                            {
                                new MemoryParameterNode { Name = "index", Index = slot, LogicalIndex = pi }
                            }));
                    }
                }

                LowerBodyV1(func.Body, funcCompiler, function.Body);

                if (function.Body.Count == 0 || function.Body[^1].Opcode != Opcodes.Ret)
                    function.Body.Add(_v1Assembler.Emit(Opcodes.Ret, null));

                result.Functions[func.Name] = function;
            }

            // Phase 5: Analyze entrypoint body using V1's lowering.
            if (program.EntryPoint != null)
            {
                _v1Compiler.BeginStatementSequence();
                LowerBodyV1(program.EntryPoint, _v1Compiler, result.EntryPoint);
            }

            return result;
        }

        /// <summary>Compile a procedure body using V1's StatementLoweringCompiler.
        /// Nested procedures/functions from the body are extracted and compiled separately.</summary>
        private void CompileProcedureBodyV1(
            BlockNode2 body,
            Magic.Kernel.Processor.Procedure procedure,
            StatementLoweringCompiler procCompiler,
            AnalyzedProgram2 result)
        {
            // Extract nested functions/procedures and compile them separately.
            foreach (var stmt in body.Statements)
            {
                if (stmt is NestedProcedureStatement2 nestedProc)
                {
                    var nested = new Magic.Kernel.Processor.Procedure { Name = nestedProc.Name };
                    var nestedCompiler = new StatementLoweringCompiler();
                    nestedCompiler.InheritGlobalSlots(procCompiler);
                    nestedCompiler.BeginStatementSequence();
                    nestedCompiler.ResetLocals();
                    LowerBodyV1(nestedProc.Body, nestedCompiler, nested.Body);
                    if (nested.Body.Count == 0 || nested.Body[^1].Opcode != Opcodes.Ret)
                        nested.Body.Add(_v1Assembler.Emit(Opcodes.Ret, null));
                    result.Procedures[nestedProc.Name] = nested;
                }
                else if (stmt is NestedFunctionStatement2 nestedFunc)
                {
                    var nested = new Magic.Kernel.Processor.Function { Name = nestedFunc.Name };
                    var nestedCompiler = new StatementLoweringCompiler();
                    nestedCompiler.InheritGlobalSlots(procCompiler);
                    nestedCompiler.BeginStatementSequence();
                    nestedCompiler.ResetLocals();
                    LowerBodyV1(nestedFunc.Body, nestedCompiler, nested.Body);
                    if (nested.Body.Count == 0 || nested.Body[^1].Opcode != Opcodes.Ret)
                        nested.Body.Add(_v1Assembler.Emit(Opcodes.Ret, null));
                    result.Functions[nestedFunc.Name] = nested;
                }
            }

            LowerBodyV1(body, procCompiler, procedure.Body);
        }

        /// <summary>Lower a V2 block's statements using V1's StatementLoweringCompiler.</summary>
        private void LowerBodyV1(BlockNode2 body, StatementLoweringCompiler compiler, ExecutionBlock target)
        {
            foreach (var stmt in body.Statements)
            {
                if (stmt is NestedProcedureStatement2 || stmt is NestedFunctionStatement2)
                    continue; // Already handled by the caller

                if (stmt is InstructionStatement2 instr)
                {
                    // Low-level ASM instruction — emit directly without re-lowering.
                    var instrNode = new InstructionNode
                    {
                        Opcode = instr.Opcode,
                        SourceLine = instr.SourceLine
                    };
                    foreach (var p in instr.Parameters)
                        instrNode.Parameters.Add(ConvertParam2ToV1(p));
                    AddV1Command(target, instrNode);
                    continue;
                }

                var text = stmt.SourceText;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var lowered = compiler.Lower(new[] { text }, registerGlobals: false, stmt.SourceLine);
                foreach (var v1Instr in lowered)
                    AddV1Command(target, v1Instr);
            }
        }

        private static Magic.Kernel.Compilation.Ast.ParameterNode ConvertParam2ToV1(InstructionParam2 p)
        {
            return p.ValueType switch
            {
                "string" => new StringParameterNode { Name = p.Name ?? "", Value = p.Value?.ToString() ?? "" },
                "index" or "int" => new IndexParameterNode { Name = p.Name ?? "", Value = Convert.ToInt64(p.Value ?? 0L) },
                "memory" => new MemoryParameterNode { Name = p.Name ?? "", Index = Convert.ToInt32(p.Value ?? 0) },
                "function" => new FunctionNameParameterNode { Name = p.Name ?? "", FunctionName = p.Value?.ToString() ?? "" },
                _ => new StringParameterNode { Name = p.Name ?? "", Value = p.Value?.ToString() ?? "" }
            };
        }

        private void AddV1Command(ExecutionBlock target, InstructionNode instr)
        {
            var opcode = MapV1Opcode(instr.Opcode);
            var cmd = _v1Assembler.Emit(opcode, instr.Parameters);
            if (instr.SourceLine > 0)
                cmd.SourceLine = instr.SourceLine;

            // Auto-inject arity=0 for bare Call instructions (matching V1 SemanticAnalyzer behavior).
            if (opcode == Opcodes.Call && (instr.Parameters == null || instr.Parameters.TrueForAll(p => p is FunctionNameParameterNode)))
            {
                if (target.Count == 0 || !(target[^1].Operand1 is PushOperand { Kind: "IntLiteral" }))
                    target.Add(_v1Assembler.EmitPushIntLiteral(0));
            }

            target.Add(cmd);
        }

        private static Opcodes MapV1Opcode(string opcode) =>
            opcode.ToLowerInvariant() switch
            {
                "addvertex" => Opcodes.AddVertex,
                "addrelation" => Opcodes.AddRelation,
                "addshape" => Opcodes.AddShape,
                "call" => Opcodes.Call,
                "acall" => Opcodes.ACall,
                "callobj" => Opcodes.CallObj,
                "push" => Opcodes.Push,
                "pop" => Opcodes.Pop,
                "def" => Opcodes.Def,
                "defgen" => Opcodes.DefGen,
                "defobj" => Opcodes.DefObj,
                "defexpr" => Opcodes.DefExpr,
                "await" => Opcodes.Await,
                "awaitobj" => Opcodes.AwaitObj,
                "streamwait" => Opcodes.StreamWait,
                "streamwaitobj" => Opcodes.StreamWaitObj,
                "lambda" => Opcodes.Lambda,
                "expr" => Opcodes.Expr,
                "label" => Opcodes.Label,
                "je" => Opcodes.Je,
                "jmp" => Opcodes.Jmp,
                "cmp" => Opcodes.Cmp,
                "equals" => Opcodes.Equals,
                "not" => Opcodes.Not,
                "lt" => Opcodes.Lt,
                "add" => Opcodes.Add,
                "sub" => Opcodes.Sub,
                "mul" => Opcodes.Mul,
                "div" => Opcodes.Div,
                "pow" => Opcodes.Pow,
                "ret" => Opcodes.Ret,
                "getobj" => Opcodes.GetObj,
                "setobj" => Opcodes.SetObj,
                "getvertex" => Opcodes.GetVertex,
                "syscall" => Opcodes.SysCall,
                "nop" => Opcodes.Nop,
                _ => Opcodes.Nop
            };

        private static void EnsureReturnAtEnd(ExecutionBlock body)
        {
            if (body.Count == 0 || body[body.Count - 1].Opcode != Opcodes.Ret)
                body.Add(new Command { Opcode = Opcodes.Ret });
        }
    }

    /// <summary>Result of semantic analysis: typed program ready for execution.</summary>
    public sealed class AnalyzedProgram2
    {
        public ExecutionBlock EntryPoint { get; } = new ExecutionBlock();
        public Dictionary<string, Magic.Kernel.Processor.Procedure> Procedures { get; } = new();
        public Dictionary<string, Magic.Kernel.Processor.Function> Functions { get; } = new();
    }

    /// <summary>Symbol table for compiler 2.0 — tracks variable slots, types, and scopes.</summary>
    public sealed class SymbolTable2
    {
        internal int NextSlot;
        private readonly Dictionary<string, int> _globalSlots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TypeDeclarationNode2> _types = new(StringComparer.OrdinalIgnoreCase);

        public ScopeSymbols2 GlobalScope => new(this, _globalSlots, _types, isGlobal: true);

        public void Reset()
        {
            NextSlot = 0;
            _globalSlots.Clear();
            _types.Clear();
        }

        public void RegisterType(TypeDeclarationNode2 typeDecl)
        {
            _types[typeDecl.Name] = typeDecl;
        }

        public ScopeSymbols2 CreateProcedureScope(string name, List<ParameterDeclaration2> parameters)
        {
            var scope = new ScopeSymbols2(this, _globalSlots, _types, isGlobal: false);
            foreach (var param in parameters)
                scope.AllocateLocal(param.Name);
            return scope;
        }

        public ScopeSymbols2 CreateFunctionScope(string name, List<ParameterDeclaration2> parameters)
        {
            var scope = new ScopeSymbols2(this, _globalSlots, _types, isGlobal: false);
            foreach (var param in parameters)
                scope.AllocateLocal(param.Name);
            return scope;
        }
    }

    /// <summary>Variable/slot scope for a procedure or function.</summary>
    public sealed class ScopeSymbols2
    {
        private readonly SymbolTable2 _table;
        private readonly Dictionary<string, int> _globalSlots;
        private readonly Dictionary<string, TypeDeclarationNode2> _types;
        private readonly Dictionary<string, int> _localSlots = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _isGlobal;

        internal ScopeSymbols2(
            SymbolTable2 table,
            Dictionary<string, int> globalSlots,
            Dictionary<string, TypeDeclarationNode2> types,
            bool isGlobal)
        {
            _table = table;
            _globalSlots = globalSlots;
            _types = types;
            _isGlobal = isGlobal;
        }

        public int AllocateLocal(string name)
        {
            if (_localSlots.TryGetValue(name, out var existing))
                return existing;
            var slot = _table.NextSlot++;
            _localSlots[name] = slot;
            if (_isGlobal)
                _globalSlots[name] = slot;
            return slot;
        }

        /// <summary>Allocate an anonymous temporary slot (not named, not tracked in symbol dict).</summary>
        public int AllocateTemp()
        {
            return _table.NextSlot++;
        }

        public bool TryResolveSlot(string name, out int slot, out string kind)
        {
            if (_localSlots.TryGetValue(name, out slot))
            {
                kind = "memory";
                return true;
            }
            if (_globalSlots.TryGetValue(name, out slot))
            {
                kind = "global";
                return true;
            }
            slot = -1;
            kind = "";
            return false;
        }

        public bool TryResolveType(string name, out TypeDeclarationNode2? typeDecl)
            => _types.TryGetValue(name, out typeDecl);

        public int NextSlot => _table.NextSlot;
    }
}
