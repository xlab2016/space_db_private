using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Magic.Kernel;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Magic.Kernel2.Compilation2.Ast2;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Magic compiler 2.0 assembler.
    /// <para>
    /// Walks the <strong>fully-typed AST</strong> produced by <see cref="Parser2"/> and
    /// generates <see cref="Command"/> bytecode directly from typed AST nodes.
    /// No <c>StatementLoweringCompiler</c>, no re-parsing of raw text, no deferred lowering.
    /// </para>
    /// </summary>
    public class Assembler2
    {
        /// <summary>
        /// Assemble a complete program from its typed AST and symbol table.
        /// This is the main entry point called by <see cref="Compiler2"/>.
        /// Pipeline: typed AST → <see cref="ExecutableUnit"/> with all procedures, functions, and entrypoint.
        /// </summary>
        public ExecutableUnit Assemble(ProgramNode2 program, SymbolTable2 symbolTable)
        {
            var unit = new ExecutableUnit
            {
                Version = program.Version,
                Name = program.ProgramName,
                Module = program.Module,
                System = program.System
            };

            // Phase 1: Pre-allocate global slots for type declarations into the global scope.
            // This must happen BEFORE procedures are assembled so that procedure-local variables
            // get slot numbers that don't collide with type slots.
            // V1 compiles the entrypoint (including types) first, then procedures inherit the
            // slot counter — so the first user variable in a procedure gets slot N (where N = number of types).
            var entryScope = symbolTable.GlobalScope;
            var entryBlock = new ExecutionBlock();
            foreach (var typeDecl in program.TypeDeclarations)
            {
                var typeCommands = EmitTypeDeclaration(typeDecl, entryScope);
                entryBlock.AddRange(typeCommands);
            }

            // Emit entrypoint body statements (e.g. "call Main").
            if (program.EntryPoint != null)
            {
                foreach (var stmt in program.EntryPoint.Statements)
                    EmitStatement(stmt, entryScope, entryBlock, isProcedure: false);
            }

            unit.EntryPoint = entryBlock;

            // Phase 2: Assemble procedures and functions.
            // At this point the global slot counter has been advanced past the type slots,
            // so procedure-local variables start at the correct slot index.
            foreach (var proc in program.Procedures)
            {
                var procedure = AssembleProcedure(proc, symbolTable);
                unit.Procedures[proc.Name] = procedure;
            }

            foreach (var func in program.Functions)
            {
                var function = AssembleFunction(func, symbolTable);
                unit.Functions[func.Name] = function;
            }

            // Phase 3: Assemble nested procedures/functions discovered during body assembly.
            foreach (var nested in symbolTable.NestedProcedures)
            {
                var nestedDecl = new ProcedureDeclarationNode2
                {
                    Name = nested.Name,
                    Parameters = nested.Parameters,
                    Body = nested.Body
                };
                var nestedProc = AssembleProcedure(nestedDecl, symbolTable);
                unit.Procedures[nested.Name] = nestedProc;
            }
            foreach (var nested in symbolTable.NestedFunctions)
            {
                var nestedDecl = new FunctionDeclarationNode2
                {
                    Name = nested.Name,
                    Parameters = nested.Parameters,
                    Body = nested.Body
                };
                var nestedFunc = AssembleFunction(nestedDecl, symbolTable);
                unit.Functions[nested.Name] = nestedFunc;
            }

            return unit;
        }

        // ─── Procedure / Function assembly ───────────────────────────────────────

        private Magic.Kernel.Processor.Procedure AssembleProcedure(
            ProcedureDeclarationNode2 proc,
            SymbolTable2 symbolTable)
        {
            var procedure = new Magic.Kernel.Processor.Procedure { Name = proc.Name };

            // Create a fresh scope with access to global slots.
            // Use CreateProcedureScope so global type slots (Db>, Message<>, etc.) are resolvable.
            var scope = symbolTable.CreateProcedureScope(proc.Name, new System.Collections.Generic.List<ParameterDeclaration2>());

            // Bind parameters: V1 convention is Pop (arity), then Pop [slot] for each param in reverse.
            if (proc.Parameters.Count > 0)
            {
                procedure.Body.Add(Emit(Opcodes.Pop, 0));
                for (var pi = proc.Parameters.Count - 1; pi >= 0; pi--)
                {
                    var slot = scope.AllocateLocal(proc.Parameters[pi].Name);
                    procedure.Body.Add(PopSlot(slot, 0));
                }
            }

            // Emit procedure body — collect nested procedures/functions first.
            EmitBodyWithNested(proc.Body, scope, procedure.Body, isProcedure: true, symbolTable, procedure.Name);

            // Ensure Ret at end.
            if (procedure.Body.Count == 0 || procedure.Body[^1].Opcode != Opcodes.Ret)
                procedure.Body.Add(Emit(Opcodes.Ret, 0));

            return procedure;
        }

        private Magic.Kernel.Processor.Function AssembleFunction(
            FunctionDeclarationNode2 func,
            SymbolTable2 symbolTable)
        {
            var function = new Magic.Kernel.Processor.Function { Name = func.Name };
            var scope = symbolTable.CreateFunctionScope(func.Name, new System.Collections.Generic.List<ParameterDeclaration2>());

            if (func.Parameters.Count > 0)
            {
                function.Body.Add(Emit(Opcodes.Pop, 0));
                for (var pi = func.Parameters.Count - 1; pi >= 0; pi--)
                {
                    var slot = scope.AllocateLocal(func.Parameters[pi].Name);
                    function.Body.Add(PopSlot(slot, 0));
                }
            }

            EmitBodyWithNested(func.Body, scope, function.Body, isProcedure: false, symbolTable, func.Name);

            if (function.Body.Count == 0 || function.Body[^1].Opcode != Opcodes.Ret)
                function.Body.Add(Emit(Opcodes.Ret, 0));

            return function;
        }

        /// <summary>
        /// Emit body statements, extracting nested procedures/functions into the symbol table's
        /// extra collection (they are emitted as procedures/functions at the program level by V1).
        /// </summary>
        private void EmitBodyWithNested(
            BlockNode2 body,
            ScopeSymbols2 scope,
            ExecutionBlock target,
            bool isProcedure,
            SymbolTable2 symbolTable,
            string parentName)
        {
            foreach (var stmt in body.Statements)
            {
                if (stmt is NestedProcedureStatement2 nestedProc)
                {
                    // Nested procedures become top-level procedures in the unit.
                    symbolTable.AddNestedProcedure(nestedProc);
                    // No bytecode emitted inline for the declaration itself.
                    continue;
                }
                if (stmt is NestedFunctionStatement2 nestedFunc)
                {
                    symbolTable.AddNestedFunction(nestedFunc);
                    continue;
                }

                EmitStatement(stmt, scope, target, isProcedure);
            }
        }

        // ─── Type declaration ─────────────────────────────────────────────────────

        /// <summary>Emit bytecode for a type declaration into the given scope.</summary>
        public List<Command> EmitTypeDeclaration(TypeDeclarationNode2 typeDecl, ScopeSymbols2 scope)
        {
            var commands = new List<Command>();
            EmitTypeDeclarationInto(typeDecl, scope, commands);
            return commands;
        }

        private void EmitTypeDeclarationInto(TypeDeclarationNode2 typeDecl, ScopeSymbols2 scope, List<Command> commands)
        {
            if (typeDecl.IsTable)
                EmitTableDeclaration(typeDecl, scope, commands);
            else if (typeDecl.IsDatabase)
                EmitDatabaseDeclaration(typeDecl, scope, commands);
            else
                EmitSimpleTypeDeclaration(typeDecl, scope, commands);
        }

        private static void EmitSimpleTypeDeclaration(TypeDeclarationNode2 typeDecl, ScopeSymbols2 scope, List<Command> commands)
        {
            // push typeName; push 1 (or base count); def; pop [slot]
            // For simple types: push typeName; def; pop [slot]
            commands.Add(PushString(typeDecl.Name, typeDecl.SourceLine));
            commands.Add(PushInt(1L, typeDecl.SourceLine));
            commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));
            var slot = scope.AllocateLocal(typeDecl.Name);
            commands.Add(PopSlot(slot, typeDecl.SourceLine));
        }

        private static void EmitTableDeclaration(TypeDeclarationNode2 typeDecl, ScopeSymbols2 scope, List<Command> commands)
        {
            var slot = scope.AllocateLocal(typeDecl.Name);

            // push tableName; push "table"; push 2; def; pop [slot]
            commands.Add(PushString(typeDecl.Name, typeDecl.SourceLine));
            commands.Add(PushString("table", typeDecl.SourceLine));
            commands.Add(PushInt(2L, typeDecl.SourceLine));
            commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));
            commands.Add(PopSlot(slot, typeDecl.SourceLine));

            // Each field becomes: push [slot]; push colName; push colType; push modifiers...; push "column"; push arity; def; pop [slot]
            foreach (var field in typeDecl.Fields)
            {
                if (!TryParseColumnSpec(field.Name, field.TypeSpec ?? "", out var colName, out var colType, out var modifiers, out var isRelation, out var isArray))
                    continue;

                commands.Add(PushSlot(slot, typeDecl.SourceLine));
                commands.Add(PushString(colName, typeDecl.SourceLine));
                commands.Add(PushString(colType, typeDecl.SourceLine));

                if (isRelation)
                {
                    commands.Add(PushString(isArray ? "array" : "single", typeDecl.SourceLine));
                    commands.Add(PushString("relation", typeDecl.SourceLine));
                    commands.Add(PushInt(5L, typeDecl.SourceLine));
                }
                else
                {
                    foreach (var mod in modifiers)
                        commands.Add(PushString(mod, typeDecl.SourceLine));
                    commands.Add(PushString("column", typeDecl.SourceLine));
                    commands.Add(PushInt(4L + modifiers.Count, typeDecl.SourceLine));
                }

                commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));
                commands.Add(PopSlot(slot, typeDecl.SourceLine));
            }
        }

        private static void EmitDatabaseDeclaration(TypeDeclarationNode2 typeDecl, ScopeSymbols2 scope, List<Command> commands)
        {
            var slot = scope.AllocateLocal(typeDecl.Name);

            // push dbName; push "database"; push 2; def; pop [slot]
            commands.Add(PushString(typeDecl.Name, typeDecl.SourceLine));
            commands.Add(PushString("database", typeDecl.SourceLine));
            commands.Add(PushInt(2L, typeDecl.SourceLine));
            commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));
            commands.Add(PopSlot(slot, typeDecl.SourceLine));

            // Each referenced table: push [dbSlot]; push [tableSlot]; push "table"; push 3; def; pop [dbSlot]
            foreach (var field in typeDecl.Fields)
            {
                var tableRef = (field.TypeSpec ?? field.Name).Trim();
                if (string.IsNullOrWhiteSpace(tableRef))
                    tableRef = field.Name;

                if (!scope.TryResolveSlot(tableRef, out var tableSlot, out _))
                    continue;

                commands.Add(PushSlot(slot, typeDecl.SourceLine));
                commands.Add(PushSlot(tableSlot, typeDecl.SourceLine));
                commands.Add(PushString("table", typeDecl.SourceLine));
                commands.Add(PushInt(3L, typeDecl.SourceLine));
                commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));
                commands.Add(PopSlot(slot, typeDecl.SourceLine));
            }
        }

        // ─── Column spec parsing (table column modifiers) ─────────────────────────

        private static bool TryParseColumnSpec(
            string fieldName,
            string typeSpec,
            out string columnName,
            out string columnType,
            out List<string> modifiers,
            out bool isRelation,
            out bool isArray)
        {
            columnName = fieldName.Trim();
            columnType = "";
            modifiers = new List<string>();
            isRelation = false;
            isArray = false;

            var spec = typeSpec.Trim();
            if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(spec))
                return false;

            // Try relation spec: single identifier that references another table type, or T[]
            if (TryParseRelationSpec(spec, out var relationType, out var relationIsArray))
            {
                columnType = relationType;
                isRelation = true;
                isArray = relationIsArray;
                return true;
            }

            // nullable?
            var nullable = spec.EndsWith("?", StringComparison.Ordinal);
            if (nullable)
                spec = spec.Substring(0, spec.Length - 1).Trim();

            // length modifier: nvarchar(250)
            var lengthMatch = Regex.Match(spec, @"^(?<type>[A-Za-z_][A-Za-z0-9_]*)\((?<len>\d+)\)\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
            if (lengthMatch.Success)
            {
                columnType = lengthMatch.Groups["type"].Value.Trim().ToLowerInvariant();
                modifiers.Add($"length:{lengthMatch.Groups["len"].Value.Trim()}");
                spec = lengthMatch.Groups["rest"].Value.Trim();
            }
            else
            {
                var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return false;
                columnType = parts[0].Trim().ToLowerInvariant();
                spec = string.Join(" ", parts.Skip(1)).Trim();
            }

            // Support nullable marker directly on type token, e.g. "int? index".
            if (columnType.EndsWith("?", StringComparison.Ordinal))
            {
                nullable = true;
                columnType = columnType.Substring(0, columnType.Length - 1).Trim();
            }

            if (string.IsNullOrWhiteSpace(columnType))
                return false;

            if (Regex.IsMatch(spec, @"\bprimary\s+key\b", RegexOptions.IgnoreCase))
            {
                modifiers.Add("primary key");
                spec = Regex.Replace(spec, @"\bprimary\s+key\b", "", RegexOptions.IgnoreCase).Trim();
            }
            if (Regex.IsMatch(spec, @"\bidentity\b", RegexOptions.IgnoreCase))
            {
                modifiers.Add("identity");
                spec = Regex.Replace(spec, @"\bidentity\b", "", RegexOptions.IgnoreCase).Trim();
            }
            if (!string.IsNullOrWhiteSpace(spec))
                modifiers.Add(spec);

            modifiers.Add(nullable ? "nullable:1" : "nullable:0");
            return true;
        }

        private static readonly HashSet<string> BuiltinSqlTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bigint", "int", "integer", "smallint", "datetime", "timestamp", "date", "time",
            "bool", "boolean", "decimal", "double", "float", "uuid", "json", "jsonb", "text",
            "varchar", "nvarchar", "string", "any", "void", "object"
        };

        private static bool TryParseRelationSpec(string spec, out string referencedTableType, out bool isArray)
        {
            referencedTableType = "";
            isArray = false;

            var s = (spec ?? "").Trim();
            if (s.Length == 0)
                return false;

            if (s.EndsWith("?", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1).Trim();

            if (s.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                s = s.Substring(0, s.Length - 2).Trim();
            }

            if (!Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*(?:<>|>)?$"))
                return false;

            var normalizedType = s.Trim().ToLowerInvariant();
            if (BuiltinSqlTypeNames.Contains(normalizedType))
                return false;

            referencedTableType = s;
            return true;
        }

        // ─── Block / Statement emission ───────────────────────────────────────────

        /// <summary>
        /// Emit bytecode for a block of statements by walking each typed AST node directly.
        /// This is the v2.0 equivalent of v1.0's <c>StatementLoweringCompiler.Lower()</c>,
        /// but operates on fully-typed AST nodes instead of raw text strings.
        /// </summary>
        public ExecutionBlock EmitBlock(BlockNode2 block, ScopeSymbols2 scope, bool isProcedure)
        {
            var result = new ExecutionBlock();
            foreach (var stmt in block.Statements)
                EmitStatement(stmt, scope, result, isProcedure);
            return result;
        }

        private void EmitStatement(StatementNode2 stmt, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            switch (stmt)
            {
                case VarDeclarationStatement2 varDecl:
                    EmitVarDeclaration(varDecl, scope, result);
                    break;

                case AssignmentStatement2 assign:
                    EmitAssignment(assign, scope, result);
                    break;

                case CompoundAssignmentStatement2 compound:
                    EmitCompoundAssignment(compound, scope, result);
                    break;

                case CallStatement2 call:
                    EmitCallStatement(call, scope, result, isProcedure);
                    break;

                case StreamWaitCallStatement2 swCall:
                    EmitStreamWaitCallStatement(swCall, scope, result);
                    break;

                case ReturnStatement2 ret:
                    EmitReturnStatement(ret, scope, result);
                    break;

                case IfStatement2 ifStmt:
                    EmitIfStatement(ifStmt, scope, result, isProcedure);
                    break;

                case SwitchStatement2 switchStmt:
                    EmitSwitchStatement(switchStmt, scope, result, isProcedure);
                    break;

                case StreamWaitByDeltaLoop2 swDelta:
                    EmitStreamWaitByDeltaLoop(swDelta, scope, result, isProcedure);
                    break;

                case StreamWaitForLoop2 forLoop:
                    EmitStreamWaitForLoop(forLoop, scope, result, isProcedure);
                    break;

                case InstructionStatement2 instr:
                    EmitInstructionStatement(instr, scope, result);
                    break;

                case NestedProcedureStatement2 nested:
                    // Nested procedures are handled at the Assembler2 level — no inline bytecode.
                    result.Add(Emit(Opcodes.Nop, nested.SourceLine));
                    break;

                case NestedFunctionStatement2 nested:
                    result.Add(Emit(Opcodes.Nop, nested.SourceLine));
                    break;

                case ExpressionStatement2 exprStmt:
                    // Expression used as statement — emit into a temp slot (result discarded).
                    EmitExpression(exprStmt.Expression, scope, result);
                    break;
            }
        }

        // ─── Variable declaration ─────────────────────────────────────────────────

        private void EmitVarDeclaration(VarDeclarationStatement2 varDecl, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (varDecl.Initializer != null)
            {
                // V1 pattern for generic types: emit expression FIRST (allocating internal temps),
                // then allocate the local slot AFTER. This preserves V1's slot ordering.
                if (varDecl.Initializer is GenericTypeExpression2)
                {
                    // For generic types: emit expression (which allocs internal temps), then alloc local.
                    EmitExpression(varDecl.Initializer, scope, result);
                    var slot = scope.AllocateLocal(varDecl.VariableName);
                    result.Add(PopSlot(slot, varDecl.SourceLine));
                }
                else if (varDecl.Initializer is VariableExpression2 ve && IsBuiltinTypeName(ve.Name))
                {
                    // Simple builtin type (vault, stream, database, etc.) — V1 pattern:
                    // push <type>; push 1; def; pop [slot]
                    result.Add(PushIdentifier(ve.Name, varDecl.SourceLine));
                    result.Add(PushInt(1L, varDecl.SourceLine));
                    result.Add(Emit(Opcodes.Def, varDecl.SourceLine));
                    var slot = scope.AllocateLocal(varDecl.VariableName);
                    result.Add(PopSlot(slot, varDecl.SourceLine));
                }
                else if (varDecl.Initializer is ObjectLiteralExpression2)
                {
                    // For object literals: the literal's internal objSlot becomes the variable slot.
                    // V1 pattern: emit the literal (allocates objSlot), then register that slot as the var.
                    // This avoids allocating a separate var slot + copying from objSlot.
                    var objSlot = scope.NextSlot; // will be the first AllocateTemp() in EmitObjectLiteral
                    EmitExpression(varDecl.Initializer, scope, result);
                    // EmitObjectLiteral ends with "push [objSlot]" — pop it and register objSlot as the var.
                    result.RemoveAt(result.Count - 1); // remove trailing push [objSlot]
                    scope.RegisterLocalSlot(varDecl.VariableName, objSlot);
                }
                else if (varDecl.Initializer is AwaitExpression2 awaitInit &&
                         awaitInit.Operand is CallExpression2 awaitCallInit &&
                         awaitCallInit.Callee is MemberAccessExpression2 awaitMemberInit)
                {
                    // Special V1 pattern for: var x := await obj.method(args)
                    // V1 allocates: varSlot (x), awaitSourceSlot (intermediate), then any lambda temps.
                    // This matches V1's TryCompileExpressionToSlot "await" branch which pre-allocates awaitSourceSlot.
                    var slot = scope.AllocateLocal(varDecl.VariableName);      // slot N (x)
                    var awaitSourceSlot = scope.AllocateTemp();                 // slot N+1 (intermediate)

                    // Pre-push receiver (first push) into awaitSourceSlot — V1 double-push pattern.
                    var receiver = TryGetFirstPushReceiver(awaitInit.Operand);
                    if (receiver != null)
                    {
                        EmitExpression(receiver, scope, result);
                        result.Add(PopSlot(awaitSourceSlot, varDecl.SourceLine));
                    }

                    // Emit the call expression — result left on stack.
                    EmitCallExpression(awaitCallInit, scope, result);

                    // Store call result to awaitSourceSlot, then await it.
                    result.Add(PopSlot(awaitSourceSlot, varDecl.SourceLine));
                    result.Add(PushSlot(awaitSourceSlot, varDecl.SourceLine));
                    result.Add(Emit(Opcodes.AwaitObj, varDecl.SourceLine));
                    result.Add(PopSlot(slot, varDecl.SourceLine));
                }
                else
                {
                    // Allocate local first (standard pattern).
                    var slot = scope.AllocateLocal(varDecl.VariableName);
                    // V1 double-push: if expr starts by pushing a receiver (member access / method call),
                    // pre-push that receiver into the slot as a placeholder before evaluating the full expr.
                    var receiver = TryGetFirstPushReceiver(varDecl.Initializer);
                    if (receiver != null)
                    {
                        EmitExpression(receiver, scope, result);
                        result.Add(PopSlot(slot, varDecl.SourceLine));
                    }
                    EmitExpression(varDecl.Initializer, scope, result);
                    result.Add(PopSlot(slot, varDecl.SourceLine));
                }
            }
            else if (!string.IsNullOrEmpty(varDecl.ExplicitType))
            {
                // var x: Type — emit type def instruction.
                var slot = scope.AllocateLocal(varDecl.VariableName);
                result.Add(PushString(varDecl.ExplicitType, varDecl.SourceLine));
                result.Add(Emit(Opcodes.Def, varDecl.SourceLine));
                result.Add(PopSlot(slot, varDecl.SourceLine));
            }
            else
            {
                // No initializer, no explicit type — just allocate the slot.
                scope.AllocateLocal(varDecl.VariableName);
            }
        }

        private static readonly HashSet<string> BuiltinTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "vault", "stream", "database", "messenger", "network", "file", "telegram", "postgres"
        };

        private static bool IsBuiltinTypeName(string name) => BuiltinTypeNames.Contains(name);

        /// <summary>
        /// Returns the "first push receiver" of an expression — the innermost object that would
        /// be pushed first when evaluating the expression. Used to replicate V1's double-push
        /// pattern where the receiver is pre-pushed into the destination slot before full evaluation.
        ///
        /// Returns null if the expression starts with a non-slot push (literal, builtin type, etc.)
        /// or if it is a simple variable reference with no receiver.
        /// </summary>
        private static ExpressionNode2? TryGetFirstPushReceiver(ExpressionNode2 expr)
        {
            switch (expr)
            {
                case MemberAccessExpression2 ma:
                    // Recursively find the deepest receiver; if it resolves to something
                    // (i.e., the object itself has a receiver), return it; otherwise return ma.Object.
                    return TryGetFirstPushReceiver(ma.Object) ?? ma.Object;
                case CallExpression2 call when call.Callee is MemberAccessExpression2 ma2:
                    return TryGetFirstPushReceiver(ma2.Object) ?? ma2.Object;
                case NullAssertExpression2 na:
                    return TryGetFirstPushReceiver(na.Operand);
                case AwaitExpression2 aw:
                    return TryGetFirstPushReceiver(aw.Operand);
                default:
                    return null;
            }
        }

        // ─── Assignment ───────────────────────────────────────────────────────────

        private void EmitAssignment(AssignmentStatement2 assign, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (assign.Target is VariableExpression2 varExpr)
            {
                // Simple variable assignment: eval value, pop to slot.
                EmitExpression(assign.Value, scope, result);
                if (scope.TryResolveSlot(varExpr.Name, out var slot, out _))
                {
                    result.Add(PopSlot(slot, assign.SourceLine));
                }
                else
                {
                    // New variable — allocate slot.
                    var newSlot = scope.AllocateLocal(varExpr.Name);
                    result.Add(PopSlot(newSlot, assign.SourceLine));
                }
            }
            else if (assign.Target is MemberAccessExpression2 memberAccess)
            {
                // obj.Field := value  OR  obj.Field = value
                // V1 setobj convention: push obj; push fieldName; push value; setobj; pop [resultSlot]
                EmitExpression(memberAccess.Object, scope, result);
                result.Add(PushString(memberAccess.MemberName, assign.SourceLine));
                EmitExpression(assign.Value, scope, result);
                result.Add(Emit(Opcodes.SetObj, assign.SourceLine));
                var setObjResultSlot = scope.AllocateTemp();
                result.Add(PopSlot(setObjResultSlot, assign.SourceLine));
            }
        }

        // ─── Compound assignment ──────────────────────────────────────────────────

        private void EmitCompoundAssignment(CompoundAssignmentStatement2 compound, ScopeSymbols2 scope, ExecutionBlock result)
        {
            // db1.Message<> += value  →
            //   push [db1]; push string: "Message<>"; getobj; pop [tmp]
            //   push [tmp]; push [value]; push 1; callobj "add"; pop [tmp]
            //   push [db1]; push string: "Message<>"; push [tmp]; setobj; pop [slot]
            if (compound.Target is MemberAccessExpression2 memberAccess)
            {
                var tmpSlot = scope.AllocateTemp();
                var resultSlot = scope.AllocateTemp();

                // Get current value: obj.Member
                EmitExpression(memberAccess.Object, scope, result);
                result.Add(PushString(memberAccess.MemberName, compound.SourceLine));
                result.Add(Emit(Opcodes.GetObj, compound.SourceLine));
                result.Add(PopSlot(tmpSlot, compound.SourceLine));

                // Call "add" on it with the RHS
                result.Add(PushSlot(tmpSlot, compound.SourceLine));
                EmitExpression(compound.Value, scope, result);
                result.Add(PushInt(1L, compound.SourceLine));
                result.Add(new Command { Opcode = Opcodes.CallObj, Operand1 = "add", SourceLine = compound.SourceLine });
                result.Add(PopSlot(tmpSlot, compound.SourceLine));

                // Store back: obj.Member = tmp
                EmitExpression(memberAccess.Object, scope, result);
                result.Add(PushString(memberAccess.MemberName, compound.SourceLine));
                result.Add(PushSlot(tmpSlot, compound.SourceLine));
                result.Add(Emit(Opcodes.SetObj, compound.SourceLine));
                result.Add(PopSlot(resultSlot, compound.SourceLine));
            }
        }

        // ─── streamwait call statement ────────────────────────────────────────────

        private void EmitStreamWaitCallStatement(StreamWaitCallStatement2 swCall, ScopeSymbols2 scope, ExecutionBlock result)
        {
            // streamwait print(message) →
            //   push string: "print"; push [message]; push 1; streamwait
            result.Add(PushString(swCall.FunctionName, swCall.SourceLine));
            EmitCallArguments(swCall.Arguments, scope, result, swCall.SourceLine);
            result.Add(Emit(Opcodes.StreamWait, swCall.SourceLine));
        }

        // ─── Call statement ───────────────────────────────────────────────────────

        private void EmitCallStatement(CallStatement2 call, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure = true)
        {
            if (call.Callee is VariableExpression2 varExpr)
            {
                if (string.Equals(varExpr.Name, "await", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
                {
                    // await obj — push obj, awaitobj, pop (discard result in statement context)
                    EmitExpression(call.Arguments[0], scope, result);
                    result.Add(Emit(Opcodes.AwaitObj, call.SourceLine));
                    result.Add(Emit(Opcodes.Pop, call.SourceLine));
                    return;
                }

                // Simple function/procedure call: Foo(a, b)
                EmitCallArguments(call.Arguments, scope, result, call.SourceLine);
                var callCmd = new Command
                {
                    Opcode = call.IsAsync ? Opcodes.ACall : Opcodes.Call,
                    Operand1 = new CallInfo { FunctionName = varExpr.Name },
                    SourceLine = call.SourceLine
                };
                result.Add(callCmd);
                // V1 emits pop after procedure calls in statement context to discard return value.
                // But in entrypoint context (isProcedure=false), no pop is emitted after call Main.
                if (isProcedure)
                    result.Add(Emit(Opcodes.Pop, call.SourceLine));
            }
            else if (call.Callee is MemberAccessExpression2 memberAccess)
            {
                // obj.Method(a, b) — as statement, result is discarded.
                // V1 pattern: if any arg is an object literal, pre-evaluate all args into temp slots
                // BEFORE pushing the receiver, then push receiver, then push pre-evaluated args.
                // This matches V1's output ordering: arg-setup, receiver, arg-push, arity, callobj.
                bool hasObjectLiteralArg = call.Arguments.Any(a => a is ObjectLiteralExpression2);
                if (hasObjectLiteralArg)
                {
                    // V1 pattern: evaluate complex args first (object literals → temp slots),
                    // then push receiver, then push the pre-evaluated slots, then arity.
                    // This ordering matches V1's output: arg-setup code, push receiver, push args.
                    var argSlots = new List<int>();
                    foreach (var arg in call.Arguments)
                    {
                        if (arg is VariableExpression2 ve2 && scope.TryResolveSlot(ve2.Name, out var argSlotResolved, out _))
                        {
                            // Simple variable — don't emit, just track slot.
                            argSlots.Add(argSlotResolved);
                        }
                        else if (arg is ObjectLiteralExpression2)
                        {
                            // Object literal: EmitObjectLiteral allocates its own objSlot and
                            // ends with "push [objSlot]". We intercept by removing that last push
                            // and tracking objSlot directly (= scope.NextSlot before emission).
                            var objSlot = scope.NextSlot; // will be the first AllocateTemp() in EmitObjectLiteral
                            EmitExpression(arg, scope, result);
                            // Remove the trailing "push [objSlot]" that EmitObjectLiteral adds.
                            result.RemoveAt(result.Count - 1);
                            argSlots.Add(objSlot);
                        }
                        else
                        {
                            // Other complex expression — emit to stack, pop into temp.
                            EmitExpression(arg, scope, result);
                            var tempSlot = scope.AllocateTemp();
                            result.Add(PopSlot(tempSlot, call.SourceLine));
                            argSlots.Add(tempSlot);
                        }
                    }
                    // Push receiver after args are prepared.
                    EmitExpression(memberAccess.Object, scope, result);
                    // Push pre-evaluated arg slots, then arity.
                    foreach (var argSlot in argSlots)
                        result.Add(PushSlot(argSlot, call.SourceLine));
                    result.Add(PushInt(call.Arguments.Count, call.SourceLine));
                }
                else
                {
                    EmitExpression(memberAccess.Object, scope, result);
                    EmitCallArguments(call.Arguments, scope, result, call.SourceLine);
                }

                var callCmd = new Command
                {
                    Opcode = Opcodes.CallObj,
                    Operand1 = memberAccess.MemberName,
                    SourceLine = call.SourceLine
                };
                result.Add(callCmd);
                // V1 always emits pop after callobj statements (result discarded)
                result.Add(Emit(Opcodes.Pop, call.SourceLine));
            }
            else
            {
                // Generic expression call.
                EmitCallArguments(call.Arguments, scope, result, call.SourceLine);
                EmitExpression(call.Callee, scope, result);
            }
        }

        private void EmitCallArguments(List<ExpressionNode2> args, ScopeSymbols2 scope, ExecutionBlock result, int sourceLine)
        {
            // Push each argument first, then arity — matching V1 calling convention.
            foreach (var arg in args)
                EmitExpression(arg, scope, result);
            result.Add(PushInt(args.Count, sourceLine));
        }

        // ─── Return ───────────────────────────────────────────────────────────────

        private void EmitReturnStatement(ReturnStatement2 ret, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (ret.Value != null)
                EmitExpression(ret.Value, scope, result);
            result.Add(Emit(Opcodes.Ret, ret.SourceLine));
        }

        // ─── If statement ─────────────────────────────────────────────────────────

        // Shared counter for label generation — matches V1 where both streamwait loops and
        // if/switch labels share a single counter (StreamLoopCounter) incremented sequentially.
        private int _sharedLabelCounter;

        private void EmitIfStatement(IfStatement2 ifStmt, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            // Use V1-compatible label names: if_end_N where N = ++counter + 1000
            var raw = ++_sharedLabelCounter;
            var ifId = raw + 1000;
            var elseLabel = $"if_else_{ifId}";
            var endLabel  = ifStmt.ElseBlock != null ? $"if_else_end_{ifId}" : $"if_end_{ifId}";
            // V1 uses "if_end_N" for simple if (no else), "if_else_N"/"if_else_end_N" for if/else.
            // For the golden reference we need: je if_end_N (no else), or je if_else_N; jmp if_else_end_N.
            var jumpTarget = ifStmt.ElseBlock != null ? elseLabel : endLabel;

            // Evaluate condition into a temp slot, then cmp [slot], 0; je label.
            // V1 double-push: if condition starts by pushing a receiver (member access),
            // pre-push that receiver into condSlot before full evaluation.
            var condSlot = scope.AllocateTemp();
            var condReceiver = TryGetFirstPushReceiver(ifStmt.Condition);
            if (condReceiver != null)
            {
                EmitExpression(condReceiver, scope, result);
                result.Add(PopSlot(condSlot, ifStmt.SourceLine));
            }
            EmitExpression(ifStmt.Condition, scope, result);
            result.Add(PopSlot(condSlot, ifStmt.SourceLine));

            result.Add(new Command
            {
                Opcode = Opcodes.Cmp,
                Operand1 = new MemoryAddress { Index = condSlot },
                Operand2 = 0L,
                SourceLine = ifStmt.SourceLine
            });
            result.Add(new Command { Opcode = Opcodes.Je, Operand1 = jumpTarget, SourceLine = ifStmt.SourceLine });

            // Then block.
            var thenBlock = EmitBlock(ifStmt.ThenBlock, scope, isProcedure);
            result.AddRange(thenBlock);

            if (ifStmt.ElseBlock != null)
            {
                // Jump past else block.
                result.Add(new Command { Opcode = Opcodes.Jmp, Operand1 = endLabel, SourceLine = ifStmt.SourceLine });
                // Else label.
                result.Add(new Command { Opcode = Opcodes.Label, Operand1 = elseLabel, SourceLine = ifStmt.SourceLine });

                var elseBlock = EmitBlock(ifStmt.ElseBlock, scope, isProcedure);
                result.AddRange(elseBlock);
            }

            // End label.
            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = endLabel, SourceLine = ifStmt.SourceLine });
        }

        // ─── Switch statement ─────────────────────────────────────────────────────

        private void EmitSwitchStatement(SwitchStatement2 switchStmt, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            var id = ++_sharedLabelCounter;
            var endLabel = $"__endswitch_{id}";

            // Evaluate the switch expression once into a temp slot.
            var tempSlot = scope.AllocateLocal($"__switch_{id}");
            EmitExpression(switchStmt.Expression, scope, result);
            result.Add(PopSlot(tempSlot, switchStmt.SourceLine));

            for (var i = 0; i < switchStmt.Cases.Count; i++)
            {
                var caseNode = switchStmt.Cases[i];
                var caseEnd = i + 1 < switchStmt.Cases.Count
                    ? $"__case_{id}_{i + 1}"
                    : (switchStmt.DefaultBlock != null ? $"__default_{id}" : endLabel);
                var caseStart = $"__case_{id}_{i}";

                // Label for this case.
                result.Add(new Command { Opcode = Opcodes.Label, Operand1 = caseStart, SourceLine = caseNode.SourceLine });

                // Compare switch value with case pattern.
                result.Add(PushSlot(tempSlot, caseNode.SourceLine));
                EmitExpression(caseNode.Pattern, scope, result);
                result.Add(Emit(Opcodes.Cmp, caseNode.SourceLine));
                result.Add(new Command { Opcode = Opcodes.Je, Operand1 = caseEnd, SourceLine = caseNode.SourceLine });

                // Case body.
                var caseBody = EmitBlock(caseNode.Body, scope, isProcedure);
                result.AddRange(caseBody);
                result.Add(new Command { Opcode = Opcodes.Jmp, Operand1 = endLabel, SourceLine = caseNode.SourceLine });
            }

            // Default block.
            if (switchStmt.DefaultBlock != null)
            {
                result.Add(new Command { Opcode = Opcodes.Label, Operand1 = $"__default_{id}", SourceLine = switchStmt.SourceLine });
                var defaultBody = EmitBlock(switchStmt.DefaultBlock, scope, isProcedure);
                result.AddRange(defaultBody);
            }

            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = endLabel, SourceLine = switchStmt.SourceLine });
        }

        // ─── Stream wait by delta loop ────────────────────────────────────────────

        /// <summary>
        /// Emits the V1-compatible inline label-based pattern for:
        ///   for streamwait by delta (streamExpr, deltaVar [, aggregateVar]) { body }
        ///
        /// Output structure (all inline, no separate procedure):
        ///   label streamwait_loop_N
        ///   push [streamSlot]; push string: "delta"; streamwaitobj; pop [endSlot]
        ///   cmp [endSlot], 1; je streamwait_loop_N_end
        ///   push [captured...]; push arity; acall streamwait_loop_N_delta
        ///   jmp streamwait_loop_N
        ///   label streamwait_loop_N_delta
        ///   [body instructions]
        ///   ret
        ///   label streamwait_loop_N_end
        ///   pop; pop; ret
        /// </summary>
        private void EmitStreamWaitByDeltaLoop(StreamWaitByDeltaLoop2 loop, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            var n = ++_sharedLabelCounter;
            var loopLabel = $"streamwait_loop_{n}";
            var bodyLabel = $"streamwait_loop_{n}_delta";
            var endLabel  = $"streamwait_loop_{n}_end";

            // Allocate slots in V1 order: endSlot, deltaSlot, aggregateSlot
            var endSlot       = scope.AllocateTemp();
            var deltaSlot     = scope.AllocateLocal(loop.DeltaVarName);
            var aggregateSlot = string.IsNullOrEmpty(loop.AggregateVarName)
                ? scope.AllocateTemp()
                : scope.AllocateLocal(loop.AggregateVarName);

            // Track the first slot index of body locals (everything allocated before body is a parent slot).
            var bodySlotStart = scope.NextSlot;

            // ── Loop header ──────────────────────────────────────────────────────
            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = loopLabel, SourceLine = loop.SourceLine });
            EmitExpression(loop.Stream, scope, result);
            result.Add(PushString(loop.WaitType, loop.SourceLine));
            result.Add(Emit(Opcodes.StreamWaitObj, loop.SourceLine));
            result.Add(PopSlot(endSlot, loop.SourceLine));
            result.Add(new Command
            {
                Opcode = Opcodes.Cmp,
                Operand1 = new MemoryAddress { Index = endSlot },
                Operand2 = 1L,
                SourceLine = loop.SourceLine
            });
            result.Add(new Command { Opcode = Opcodes.Je, Operand1 = endLabel, SourceLine = loop.SourceLine });

            // ── Compile body first to discover captured parent slots ─────────────
            var bodyBlock = EmitBlock(loop.Body, scope, isProcedure);

            // Determine which parent slots are referenced in body (slots < bodySlotStart, excluding delta/aggregate).
            // Also always include endSlot (V1 always captures it as the delta value holder).
            var captureSlots = CollectCaptureSlots(bodyBlock, bodySlotStart, deltaSlot, aggregateSlot, endSlot);

            // ── acall: push captured, push arity, acall bodyLabel ────────────────
            foreach (var capSlot in captureSlots)
                result.Add(PushSlot(capSlot, loop.SourceLine));
            result.Add(PushInt(2L + captureSlots.Count, loop.SourceLine));
            result.Add(new Command
            {
                Opcode = Opcodes.ACall,
                Operand1 = new CallInfo { FunctionName = bodyLabel },
                SourceLine = loop.SourceLine
            });
            result.Add(new Command { Opcode = Opcodes.Jmp, Operand1 = loopLabel, SourceLine = loop.SourceLine });

            // ── Body ─────────────────────────────────────────────────────────────
            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = bodyLabel, SourceLine = loop.SourceLine });
            result.AddRange(bodyBlock);
            result.Add(Emit(Opcodes.Ret, loop.SourceLine));

            // ── End ──────────────────────────────────────────────────────────────
            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = endLabel, SourceLine = loop.SourceLine });
            result.Add(Emit(Opcodes.Pop, loop.SourceLine));
            result.Add(Emit(Opcodes.Pop, loop.SourceLine));
        }

        /// <summary>
        /// Collect all local memory slot indices referenced in <paramref name="block"/>
        /// that are &lt; <paramref name="bodySlotStart"/> (parent scope), excluding the delta and aggregate slots.
        /// Also always includes <paramref name="endSlot"/> (V1 always captures it as a delta value holder).
        /// These are the slots that need to be captured (pushed before acall).
        /// </summary>
        private static List<int> CollectCaptureSlots(ExecutionBlock block, int bodySlotStart, int deltaSlot, int aggregateSlot, int endSlot)
        {
            var set = new System.Collections.Generic.SortedSet<int>();

            void ConsiderSlot(int idx)
            {
                if (idx < bodySlotStart && idx != deltaSlot && idx != aggregateSlot)
                    set.Add(idx);
            }

            foreach (var cmd in block)
            {
                // Scan push [N] instructions.
                if (cmd.Opcode == Opcodes.Push && cmd.Operand1 is PushOperand po && po.Kind == "Memory")
                    ConsiderSlot((int)(long)po.Value!);

                // Scan call/callobj instructions — opjson calls reference slots via CallInfo.Parameters.
                if (cmd.Opcode == Opcodes.Call && cmd.Operand1 is CallInfo ci && ci.Parameters != null)
                {
                    foreach (var kv in ci.Parameters)
                    {
                        if (kv.Value is MemoryAddress ma)
                            ConsiderSlot((int)ma.Index);
                    }
                }

                // Scan setobj — Operand1 can be a memory address.
                if (cmd.Opcode == Opcodes.SetObj && cmd.Operand1 is MemoryAddress setMa)
                    ConsiderSlot((int)setMa.Index);
            }

            // V1 always captures endSlot (it holds the streamwait delta value).
            ConsiderSlot(endSlot);

            return new List<int>(set);
        }

        // ─── Stream wait for loop ─────────────────────────────────────────────────

        private void EmitStreamWaitForLoop(StreamWaitForLoop2 forLoop, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            var id = ++_sharedLabelCounter;
            var loopLabel = $"__streamfor_{id}";
            var endLabel = $"__endstreamfor_{id}";

            var itemSlot = scope.AllocateLocal(forLoop.VariableName);

            // Evaluate the stream expression.
            EmitExpression(forLoop.Stream, scope, result);

            // Stream wait loop — emit streamwait/streamwaitobj.
            result.Add(new Command
            {
                Opcode = Opcodes.StreamWait,
                Operand1 = loopLabel,
                SourceLine = forLoop.SourceLine
            });

            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = loopLabel, SourceLine = forLoop.SourceLine });
            result.Add(PopSlot(itemSlot, forLoop.SourceLine));

            var bodyBlock = EmitBlock(forLoop.Body, scope, isProcedure);
            result.AddRange(bodyBlock);

            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = endLabel, SourceLine = forLoop.SourceLine });
        }

        // ─── Inline instruction statement ────────────────────────────────────────

        private void EmitInstructionStatement(InstructionStatement2 instr, ScopeSymbols2 scope, ExecutionBlock result)
        {
            var opcode = MapOpcode(instr.Opcode);
            var cmd = new Command { Opcode = opcode, SourceLine = instr.SourceLine };

            // For simple opcodes, map parameters directly.
            switch (opcode)
            {
                case Opcodes.Call:
                case Opcodes.ACall:
                {
                    var fnName = instr.Parameters.FirstOrDefault(p => p.ValueType == "function")?.Value as string ?? "";
                    cmd.Operand1 = new CallInfo { FunctionName = fnName };
                    break;
                }
                case Opcodes.Push:
                {
                    var param = instr.Parameters.FirstOrDefault();
                    if (param != null)
                    {
                        cmd.Operand1 = param.ValueType switch
                        {
                            "string" => new PushOperand { Kind = "StringLiteral", Value = param.Value?.ToString() },
                            "index" or "int" => new PushOperand { Kind = "IntLiteral", Value = param.Value },
                            "memory" => new PushOperand { Kind = "Memory", Value = param.Value },
                            _ => new PushOperand { Kind = "StringLiteral", Value = param.Value?.ToString() }
                        };
                    }
                    break;
                }
                case Opcodes.Pop:
                {
                    var param = instr.Parameters.FirstOrDefault();
                    if (param?.Value is int slotIdx)
                        cmd.Operand1 = new MemoryAddress { Index = slotIdx };
                    break;
                }
                default:
                    // For other opcodes use the raw param list as Operand1.
                    if (instr.Parameters.Count > 0)
                        cmd.Operand1 = instr.Parameters.FirstOrDefault()?.Value;
                    break;
            }

            result.Add(cmd);
        }

        // ─── Expression emission ──────────────────────────────────────────────────

        /// <summary>
        /// Emit bytecode for an expression node by directly walking the typed AST.
        /// This is the v2.0 replacement for the text-parsing in v1.0's expression lowering.
        /// </summary>
        public void EmitExpression(ExpressionNode2 expr, ScopeSymbols2 scope, ExecutionBlock result)
        {
            switch (expr)
            {
                case LiteralExpression2 lit:
                    EmitLiteral(lit, result);
                    break;

                case VariableExpression2 varExpr:
                    EmitVariableRef(varExpr, scope, result);
                    break;

                case MemberAccessExpression2 memberAccess:
                    EmitMemberAccess(memberAccess, scope, result);
                    break;

                case CallExpression2 call:
                    EmitCallExpression(call, scope, result);
                    break;

                case BinaryExpression2 binary:
                    EmitBinaryExpression(binary, scope, result);
                    break;

                case UnaryExpression2 unary:
                    EmitUnaryExpression(unary, scope, result);
                    break;

                case AwaitExpression2 awaitExpr:
                    EmitAwaitExpression(awaitExpr, scope, result);
                    break;

                case ObjectCreationExpression2 objCreate:
                    EmitObjectCreation(objCreate, scope, result);
                    break;

                case GenericTypeExpression2 generic:
                    EmitGenericType(generic, scope, result);
                    break;

                case MemorySlotExpression2 slot:
                    result.Add(PushSlot(slot.SlotIndex, slot.SourceLine));
                    break;

                case LambdaExpression2 lambda:
                    EmitLambda(lambda, scope, result);
                    break;

                case ObjectLiteralExpression2 objLit:
                    EmitObjectLiteral(objLit, scope, result);
                    break;

                case SymbolicExpression2 symbolic:
                    EmitSymbolicExpression(symbolic, result);
                    break;

                case NullAssertExpression2 nullAssert:
                    // Null-assertion is transparent — just emit the inner operand.
                    EmitExpression(nullAssert.Operand, scope, result);
                    break;

                default:
                    result.Add(Emit(Opcodes.Nop, expr.SourceLine));
                    break;
            }
        }

        private static void EmitLiteral(LiteralExpression2 lit, ExecutionBlock result)
        {
            result.Add(lit.Kind switch
            {
                LiteralKind2.String => new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand { Kind = "StringLiteral", Value = lit.Value?.ToString() ?? "" },
                    SourceLine = lit.SourceLine
                },
                LiteralKind2.Integer => new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand { Kind = "IntLiteral", Value = lit.Value },
                    SourceLine = lit.SourceLine
                },
                LiteralKind2.Float => new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand { Kind = "FloatLiteral", Value = lit.Value },
                    SourceLine = lit.SourceLine
                },
                LiteralKind2.Boolean => new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand { Kind = "IntLiteral", Value = (bool)(lit.Value ?? false) ? 1L : 0L },
                    SourceLine = lit.SourceLine
                },
                _ => new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand { Kind = "IntLiteral", Value = 0L },
                    SourceLine = lit.SourceLine
                }
            });
        }

        private static void EmitVariableRef(VariableExpression2 varExpr, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (scope.TryResolveSlot(varExpr.Name, out var slot, out var kind))
            {
                result.Add(new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand
                    {
                        Kind = kind == "global" ? "Global" : "Memory",
                        Value = (long)slot
                    },
                    SourceLine = varExpr.SourceLine
                });
            }
            else
            {
                // Unresolved — bare identifier (type name like vault, stream, database, etc.)
                result.Add(PushIdentifier(varExpr.Name, varExpr.SourceLine));
            }
        }

        private void EmitMemberAccess(MemberAccessExpression2 memberAccess, ScopeSymbols2 scope, ExecutionBlock result)
        {
            EmitExpression(memberAccess.Object, scope, result);
            result.Add(new Command
            {
                Opcode = Opcodes.Push,
                Operand1 = new PushOperand { Kind = "StringLiteral", Value = memberAccess.MemberName },
                SourceLine = memberAccess.SourceLine
            });
            result.Add(Emit(Opcodes.GetObj, memberAccess.SourceLine));
        }

        private void EmitCallExpression(CallExpression2 call, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (call.Callee is MemberAccessExpression2 memberAccess)
            {
                // obj.Method(args) — emit as callobj.
                EmitExpression(memberAccess.Object, scope, result);
                EmitCallArguments(call.Arguments, scope, result, call.SourceLine);

                result.Add(new Command
                {
                    Opcode = Opcodes.CallObj,
                    Operand1 = memberAccess.MemberName,
                    SourceLine = call.SourceLine
                });
            }
            else if (call.Callee is VariableExpression2 varExpr)
            {
                // Regular function call: push args then call.
                EmitCallArguments(call.Arguments, scope, result, call.SourceLine);
                result.Add(new Command
                {
                    Opcode = call.IsAsync ? Opcodes.ACall : Opcodes.Call,
                    Operand1 = new CallInfo { FunctionName = varExpr.Name },
                    SourceLine = call.SourceLine
                });
            }
        }

        private void EmitBinaryExpression(BinaryExpression2 binary, ScopeSymbols2 scope, ExecutionBlock result)
        {
            EmitExpression(binary.Left, scope, result);
            EmitExpression(binary.Right, scope, result);

            var opCode = binary.Operator switch
            {
                "+" => Opcodes.Add,
                "-" => Opcodes.Sub,
                "*" => Opcodes.Mul,
                "/" => Opcodes.Div,
                "==" => Opcodes.Equals,
                "!=" => Opcodes.Equals, // followed by Not
                "<" => Opcodes.Lt,
                _ => Opcodes.Cmp
            };

            result.Add(Emit(opCode, binary.SourceLine));

            if (binary.Operator == "!=")
                result.Add(Emit(Opcodes.Not, binary.SourceLine));
        }

        private void EmitUnaryExpression(UnaryExpression2 unary, ScopeSymbols2 scope, ExecutionBlock result)
        {
            EmitExpression(unary.Operand, scope, result);
            if (unary.Operator == "!")
                result.Add(Emit(Opcodes.Not, unary.SourceLine));
        }

        private void EmitAwaitExpression(AwaitExpression2 awaitExpr, ScopeSymbols2 scope, ExecutionBlock result)
        {
            EmitExpression(awaitExpr.Operand, scope, result);
            // AGI always uses awaitobj (awaiting an object/promise); 'await' opcode is not used in standard patterns.
            result.Add(Emit(Opcodes.AwaitObj, awaitExpr.SourceLine));
        }

        private void EmitObjectCreation(ObjectCreationExpression2 objCreate, ScopeSymbols2 scope, ExecutionBlock result)
        {
            result.Add(PushString(objCreate.TypeName, objCreate.SourceLine));

            if (objCreate.PositionalArgs.Count > 0)
            {
                result.Add(PushInt(objCreate.PositionalArgs.Count, objCreate.SourceLine));
                foreach (var arg in objCreate.PositionalArgs)
                    EmitExpression(arg, scope, result);

                result.Add(new Command
                {
                    Opcode = Opcodes.Call,
                    Operand1 = new CallInfo
                    {
                        FunctionName = $"{objCreate.TypeName}_ctor_{objCreate.PositionalArgs.Count}"
                    },
                    SourceLine = objCreate.SourceLine
                });
            }
            else
            {
                result.Add(Emit(Opcodes.Def, objCreate.SourceLine));
            }
        }

        private static void EmitGenericType(GenericTypeExpression2 generic, ScopeSymbols2 scope, ExecutionBlock result)
        {
            // V1 pattern for stream<A, B>:
            //   push typeName   ← bare identifier (not string literal)
            //   push 1          ← arity for def
            //   def             ← create base type instance
            //   pop [tempSlot]  ← store base instance
            //   push [tempSlot] ← push base instance for defgen
            //   push A          ← type arg(s) as bare identifiers
            //   push B
            //   push N          ← arg count for defgen
            //   defgen          ← specialize
            // (caller stores result via PopSlot)
            var tempSlot = scope.AllocateTemp();

            // Push type name as bare identifier (push stream, not push string: "stream")
            result.Add(PushIdentifier(generic.TypeName, generic.SourceLine));
            result.Add(PushInt(1L, generic.SourceLine));
            result.Add(new Command { Opcode = Opcodes.Def, SourceLine = generic.SourceLine });
            result.Add(PopSlot(tempSlot, generic.SourceLine));
            result.Add(PushSlot(tempSlot, generic.SourceLine));

            // Push each type argument as bare identifier or global slot reference.
            var typeArgs = generic.TypeArg.Split(',');
            foreach (var arg in typeArgs)
            {
                var a = arg.Trim();
                if (string.IsNullOrEmpty(a)) continue;

                // Check if this type arg is a known global type (e.g. Db> → global: [1])
                if (scope.TryResolveSlot(a, out var argSlot, out var argKind))
                {
                    // It's a known variable/type — push as global or memory slot
                    result.Add(new Command
                    {
                        Opcode = Opcodes.Push,
                        Operand1 = new PushOperand { Kind = argKind == "global" ? "Global" : "Memory", Value = (long)argSlot },
                        SourceLine = generic.SourceLine
                    });
                }
                else
                {
                    // Unknown identifier — push as bare identifier (not string literal)
                    result.Add(PushIdentifier(a, generic.SourceLine));
                }
            }

            var argCount = typeArgs.Count(a => !string.IsNullOrWhiteSpace(a));
            result.Add(PushInt((long)argCount, generic.SourceLine));
            result.Add(new Command { Opcode = Opcodes.DefGen, SourceLine = generic.SourceLine });
        }

        private static Command PushLambdaArg(int argIndex, int sourceLine) => new()
        {
            Opcode = Opcodes.Push,
            Operand1 = new PushOperand { Kind = "LambdaArg", Value = argIndex },
            SourceLine = sourceLine
        };

        private void EmitLambda(LambdaExpression2 lambda, ScopeSymbols2 scope, ExecutionBlock result)
        {
            // V1-compatible lambda emission for: param => param.Member = capturedExpr
            // Check for the specific pattern: param => param.Member = rhs
            // This is parsed by ParseExpression as MemberAccess(param, "Member = rhs")
            // because the parser greedily takes everything after the dot as the member name.
            if (lambda.Parameters.Count == 1 &&
                lambda.Body.Statements.Count == 1 &&
                lambda.Body.Statements[0] is ExpressionStatement2 bodyExprStmt)
            {
                var paramName = lambda.Parameters[0];
                var bodyExpr = bodyExprStmt.Expression;

                // Pattern 1: param.Member == rhs (parsed as BinaryExpression)
                if (bodyExpr is BinaryExpression2 binExpr && binExpr.Operator == "==" &&
                    binExpr.Left is MemberAccessExpression2 lhsMa1 &&
                    lhsMa1.Object is VariableExpression2 lhsVar1 && lhsVar1.Name == paramName)
                {
                    EmitLambdaMemberEquality(paramName, lhsMa1.MemberName, binExpr.Right, scope, result, lambda.SourceLine);
                    return;
                }

                // Pattern 2: parsed as MemberAccess(param, "Member = rhs")
                // because parser takes all after dot as member name
                if (bodyExpr is MemberAccessExpression2 ma &&
                    ma.Object is VariableExpression2 maVar && maVar.Name == paramName)
                {
                    // The member name may contain "= rhs" if '=' was included in member name parsing
                    var memberText = ma.MemberName;
                    var eqIdx = memberText.IndexOf('=');
                    if (eqIdx > 0 && (eqIdx + 1 >= memberText.Length || memberText[eqIdx + 1] != '='))
                    {
                        var actualMember = memberText.Substring(0, eqIdx).Trim();
                        var rhsText = memberText.Substring(eqIdx + 1).Trim();
                        var rhsExpr = new StatementParser2().ParseExpression(rhsText, lambda.SourceLine);
                        EmitLambdaMemberEquality(paramName, actualMember, rhsExpr, scope, result, lambda.SourceLine);
                        return;
                    }
                }
            }

            // Fallback: generic lambda emission (expr + body + lambda + defexpr)
            result.Add(Emit(Opcodes.Expr, lambda.SourceLine));
            var lambdaBody = EmitBlock(lambda.Body, scope, isProcedure: false);
            result.AddRange(lambdaBody);
            result.Add(Emit(Opcodes.Lambda, lambda.SourceLine));
            result.Add(Emit(Opcodes.DefExpr, lambda.SourceLine));
        }

        /// <summary>
        /// Emit V1-compatible lambda for pattern: param => param.Member = rhs
        ///
        /// Output:
        ///   [pre-compute rhs into rightSlot]
        ///   expr
        ///   push lambda: arg0   ← double-push placeholder
        ///   push lambda: arg0   ← actual for getobj
        ///   push string: "Member"
        ///   getobj
        ///   pop [tempSlot]
        ///   push [tempSlot]
        ///   push [rightSlot]
        ///   equals
        ///   lambda
        ///   defexpr
        /// </summary>
        private void EmitLambdaMemberEquality(
            string paramName, string memberName, ExpressionNode2 rhsExpr,
            ScopeSymbols2 scope, ExecutionBlock result, int sourceLine)
        {
            // Pre-compute RHS into rightSlot (before expr opcode)
            var tempSlot = scope.AllocateTemp();   // for _.Member result
            var rightSlot = scope.AllocateTemp();  // for rhs value

            // Pre-compute rhs (captured from outer scope)
            // V1 also uses double-push for rhs if it's a member access:
            var rhsReceiver = TryGetFirstPushReceiver(rhsExpr);
            if (rhsReceiver != null)
            {
                EmitExpression(rhsReceiver, scope, result);
                result.Add(PopSlot(rightSlot, sourceLine));
            }
            EmitExpression(rhsExpr, scope, result);
            result.Add(PopSlot(rightSlot, sourceLine));

            // Start lambda
            result.Add(Emit(Opcodes.Expr, sourceLine));

            // Double-push lambda arg0 (the parameter)
            result.Add(PushLambdaArg(0, sourceLine));
            result.Add(PushLambdaArg(0, sourceLine));
            result.Add(PushString(memberName, sourceLine));
            result.Add(Emit(Opcodes.GetObj, sourceLine));
            result.Add(PopSlot(tempSlot, sourceLine));

            // Compare LHS with RHS
            result.Add(PushSlot(tempSlot, sourceLine));
            result.Add(PushSlot(rightSlot, sourceLine));
            result.Add(Emit(Opcodes.Equals, sourceLine));

            result.Add(Emit(Opcodes.Lambda, sourceLine));
            result.Add(Emit(Opcodes.DefExpr, sourceLine));
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private static Command Emit(Opcodes opcode, int sourceLine) =>
            new() { Opcode = opcode, SourceLine = sourceLine };

        private static Command PushString(string value, int sourceLine) => new()
        {
            Opcode = Opcodes.Push,
            Operand1 = new PushOperand { Kind = "StringLiteral", Value = value },
            SourceLine = sourceLine
        };

        private static Command PushInt(long value, int sourceLine) => new()
        {
            Opcode = Opcodes.Push,
            Operand1 = new PushOperand { Kind = "IntLiteral", Value = value },
            SourceLine = sourceLine
        };

        private static Command PushSlot(int slot, int sourceLine) => new()
        {
            Opcode = Opcodes.Push,
            Operand1 = new PushOperand { Kind = "Memory", Value = (long)slot },
            SourceLine = sourceLine
        };

        private static Command PopSlot(int slot, int sourceLine) => new()
        {
            Opcode = Opcodes.Pop,
            Operand1 = new MemoryAddress { Index = slot },
            SourceLine = sourceLine
        };

        /// <summary>
        /// Push a bare identifier (type name, keyword) — serializes as "push stream" not "push string: "stream"".
        /// In the Command model, this is a Push with Kind="StringLiteral" but the AGIASM printer
        /// uses "Identifier" kind for bare names.  We use a dedicated kind "Identifier" here.
        /// </summary>
        private static Command PushIdentifier(string name, int sourceLine) => new()
        {
            Opcode = Opcodes.Push,
            Operand1 = new PushOperand { Kind = "Type", Value = name },
            SourceLine = sourceLine
        };

        // ─── Object literal emission ──────────────────────────────────────────────

        /// <summary>
        /// Emit an object literal { key1: val1, key2: val2 } as:
        ///   push string: "{}"; pop [slot];
        ///   call opjson, [slot], operation: "set", path: "key1", [val1slot]; pop
        ///   call opjson, [slot], operation: "set", path: "key2", [val2slot]; pop
        ///   push [slot]  ← leaves result on stack for caller
        ///
        /// For simple variable expressions that resolve to a known slot, the slot is used directly
        /// (no extra temp). For complex expressions, a temp slot is allocated.
        /// </summary>
        private void EmitObjectLiteral(ObjectLiteralExpression2 objLit, ScopeSymbols2 scope, ExecutionBlock result)
        {
            var objSlot = scope.AllocateTemp();
            result.Add(PushString("{}", objLit.SourceLine));
            result.Add(PopSlot(objSlot, objLit.SourceLine));

            foreach (var (key, valExpr) in objLit.Properties)
            {
                // Resolve value expression to a slot without emitting extra instructions for simple var refs.
                int valSlot;
                if (valExpr is VariableExpression2 ve && scope.TryResolveSlot(ve.Name, out var resolvedSlot, out _))
                {
                    // Direct slot reference — no temp needed.
                    valSlot = resolvedSlot;
                }
                else if (valExpr is MemberAccessExpression2 ma)
                {
                    // Complex expression — emit into temp.
                    valSlot = scope.AllocateTemp();
                    EmitExpression(valExpr, scope, result);
                    result.Add(PopSlot(valSlot, objLit.SourceLine));
                }
                else
                {
                    // Complex expression — emit into temp.
                    valSlot = scope.AllocateTemp();
                    EmitExpression(valExpr, scope, result);
                    result.Add(PopSlot(valSlot, objLit.SourceLine));
                }
                EmitOpJsonSet(objSlot, key, valSlot, objLit.SourceLine, result);
            }

            result.Add(PushSlot(objSlot, objLit.SourceLine));
        }

        /// <summary>Emit: call opjson, [objSlot], operation: "set", path: "key", [valSlot]; pop</summary>
        private static void EmitOpJsonSet(int objSlot, string key, int valSlot, int sourceLine, ExecutionBlock result)
        {
            result.Add(new Command
            {
                Opcode = Opcodes.Call,
                Operand1 = new CallInfo
                {
                    FunctionName = "opjson",
                    Parameters = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "source", new MemoryAddress { Index = objSlot } },
                        { "operation", "set" },
                        { "path", key },
                        { "data", new MemoryAddress { Index = valSlot } }
                    }
                },
                SourceLine = sourceLine
            });
            result.Add(Emit(Opcodes.Pop, sourceLine));
        }

        // ─── Symbolic expression emission ─────────────────────────────────────────

        /// <summary>
        /// Emit: push string: ":name"; push 1; call get
        /// </summary>
        private static void EmitSymbolicExpression(SymbolicExpression2 symbolic, ExecutionBlock result)
        {
            result.Add(PushString(":" + symbolic.Name, symbolic.SourceLine));
            result.Add(PushInt(1L, symbolic.SourceLine));
            result.Add(new Command
            {
                Opcode = Opcodes.Call,
                Operand1 = new CallInfo { FunctionName = "get" },
                SourceLine = symbolic.SourceLine
            });
        }

        private static Opcodes MapOpcode(string opcode)
        {
            return opcode.ToLowerInvariant() switch
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
        }
    }

    /// <summary>
    /// Private scope implementation used by <see cref="Assembler2"/> for procedure/function assembly.
    /// Inherits the global slot counter from the symbol table so procedure slots continue after global ones.
    /// </summary>
    internal sealed class ScopeSymbols2Private : ScopeSymbols2
    {
        public ScopeSymbols2Private(SymbolTable2 table)
            : base(table, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, TypeDeclarationNode2>(StringComparer.OrdinalIgnoreCase), isGlobal: false)
        {
        }
    }
}
