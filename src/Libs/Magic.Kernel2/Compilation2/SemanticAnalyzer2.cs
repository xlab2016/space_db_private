using System.Collections.Generic;
using Magic.Kernel.Processor;
using Magic.Kernel2.Compilation2.Ast2;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Magic compiler 2.0 semantic analyzer.
    /// <para>
    /// Walks the fully-typed AST produced by <see cref="Parser2"/> and builds a
    /// <see cref="SymbolTable2"/> (type registrations, variable slot allocations).
    /// </para>
    /// <para>
    /// This class performs <strong>no bytecode generation</strong> and has <strong>no dependency
    /// on V1 infrastructure</strong> (<c>StatementLoweringCompiler</c>, <c>Assembler</c>, etc.).
    /// Bytecode is generated exclusively by <see cref="Assembler2"/>.
    /// </para>
    /// </summary>
    public class SemanticAnalyzer2
    {
        private readonly SymbolTable2 _symbolTable;

        public SemanticAnalyzer2()
        {
            _symbolTable = new SymbolTable2();
        }

        /// <summary>
        /// Analyze the fully-typed AST, register all declared types and allocate global slots
        /// for type instances in the entrypoint.  Returns the populated symbol table for use
        /// by <see cref="Assembler2"/>.
        /// </summary>
        public SymbolTable2 Analyze(ProgramNode2 program)
        {
            _symbolTable.Reset();

            // Phase 1: Register all type declarations so they are available for type resolution
            // in procedure/function bodies.
            foreach (var typeDecl in program.TypeDeclarations)
                _symbolTable.RegisterType(typeDecl);

            return _symbolTable;
        }
    }

    /// <summary>Symbol table for compiler 2.0 — tracks variable slots, types, and scopes.</summary>
    public sealed class SymbolTable2
    {
        internal int NextSlot;
        private readonly Dictionary<string, int> _globalSlots = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TypeDeclarationNode2> _types = new(System.StringComparer.OrdinalIgnoreCase);

        // Nested procedures/functions discovered during body assembly.
        private readonly List<NestedProcedureStatement2> _nestedProcedures = new();
        private readonly List<NestedFunctionStatement2> _nestedFunctions = new();

        public ScopeSymbols2 GlobalScope => new(this, _globalSlots, _types, isGlobal: true);

        public IReadOnlyList<NestedProcedureStatement2> NestedProcedures => _nestedProcedures;
        public IReadOnlyList<NestedFunctionStatement2> NestedFunctions => _nestedFunctions;

        public void Reset()
        {
            NextSlot = 0;
            _globalSlots.Clear();
            _types.Clear();
            _nestedProcedures.Clear();
            _nestedFunctions.Clear();
        }

        public void RegisterType(TypeDeclarationNode2 typeDecl)
        {
            _types[typeDecl.Name] = typeDecl;
        }

        public void AddNestedProcedure(NestedProcedureStatement2 proc)
            => _nestedProcedures.Add(proc);

        public void AddNestedFunction(NestedFunctionStatement2 func)
            => _nestedFunctions.Add(func);

        public ScopeSymbols2 CreateProcedureScope(string name, System.Collections.Generic.List<ParameterDeclaration2> parameters)
        {
            var scope = new ScopeSymbols2(this, _globalSlots, _types, isGlobal: false);
            foreach (var param in parameters)
                scope.AllocateLocal(param.Name);
            return scope;
        }

        public ScopeSymbols2 CreateFunctionScope(string name, System.Collections.Generic.List<ParameterDeclaration2> parameters)
        {
            var scope = new ScopeSymbols2(this, _globalSlots, _types, isGlobal: false);
            foreach (var param in parameters)
                scope.AllocateLocal(param.Name);
            return scope;
        }
    }

    /// <summary>Variable/slot scope for a procedure or function.</summary>
    public class ScopeSymbols2
    {
        private readonly SymbolTable2 _table;
        private readonly Dictionary<string, int> _globalSlots;
        private readonly Dictionary<string, TypeDeclarationNode2> _types;
        private readonly Dictionary<string, int> _localSlots = new(System.StringComparer.OrdinalIgnoreCase);
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

        /// <summary>Register an existing slot index under a new variable name (no new slot is allocated).</summary>
        public void RegisterLocalSlot(string name, int slot)
        {
            _localSlots[name] = slot;
            if (_isGlobal)
                _globalSlots[name] = slot;
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

    /// <summary>Result of semantic analysis: typed program ready for execution.</summary>
    public sealed class AnalyzedProgram2
    {
        public ExecutionBlock EntryPoint { get; } = new ExecutionBlock();
        public Dictionary<string, Magic.Kernel.Processor.Procedure> Procedures { get; } = new();
        public Dictionary<string, Magic.Kernel.Processor.Function> Functions { get; } = new();
    }
}
