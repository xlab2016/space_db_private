using System;
using System.Collections.Generic;
using System.Linq;
using Magic.Kernel.Compilation;
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
    /// No <c>StatementLoweringCompiler</c> is used — all lowering was already
    /// done by the parser in 2.0.
    /// </para>
    /// <para>
    /// After analysis, the result is passed to <see cref="Assembler2"/> which
    /// walks the same typed AST to generate executable commands.
    /// </para>
    /// </summary>
    public class SemanticAnalyzer2
    {
        private readonly Assembler2 _assembler;
        private readonly SymbolTable2 _symbolTable;

        public SemanticAnalyzer2()
        {
            _assembler = new Assembler2();
            _symbolTable = new SymbolTable2();
        }

        /// <summary>
        /// Analyze the fully-typed AST and produce an <see cref="AnalyzedProgram2"/>.
        /// Unlike v1.0, this walks typed AST nodes — no text-based lowering occurs here.
        /// </summary>
        public AnalyzedProgram2 Analyze(ProgramNode2 program)
        {
            var result = new AnalyzedProgram2();
            _symbolTable.Reset();

            // Phase 1: Register all type declarations so they are available for type resolution.
            foreach (var typeDecl in program.TypeDeclarations)
                _symbolTable.RegisterType(typeDecl);

            // Phase 2: Analyze type declarations — emit def/defobj instructions into entrypoint.
            foreach (var typeDecl in program.TypeDeclarations)
            {
                var typeCommands = _assembler.EmitTypeDeclaration(typeDecl, _symbolTable);
                result.EntryPoint.AddRange(typeCommands);
            }

            // Phase 3: Analyze procedures.
            foreach (var proc in program.Procedures)
            {
                var procSymbols = _symbolTable.CreateProcedureScope(proc.Name, proc.Parameters);
                var body = _assembler.EmitBlock(proc.Body, procSymbols, isProcedure: true);
                EnsureReturnAtEnd(body);
                result.Procedures[proc.Name] = new Magic.Kernel.Processor.Procedure { Name = proc.Name, Body = body };
            }

            // Phase 4: Analyze functions.
            foreach (var func in program.Functions)
            {
                var funcSymbols = _symbolTable.CreateFunctionScope(func.Name, func.Parameters);
                var body = _assembler.EmitBlock(func.Body, funcSymbols, isProcedure: false);
                EnsureReturnAtEnd(body);
                result.Functions[func.Name] = new Magic.Kernel.Processor.Function { Name = func.Name, Body = body };
            }

            // Phase 5: Analyze entrypoint.
            if (program.EntryPoint != null)
            {
                var entryCommands = _assembler.EmitBlock(program.EntryPoint, _symbolTable.GlobalScope, isProcedure: false);
                result.EntryPoint.AddRange(entryCommands);
            }

            return result;
        }

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
