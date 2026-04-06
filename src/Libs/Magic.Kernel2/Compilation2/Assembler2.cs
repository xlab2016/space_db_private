using System;
using System.Collections.Generic;
using System.Linq;
using Magic.Kernel;
using Magic.Kernel.Processor;
using Magic.Kernel2.Compilation2.Ast2;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Magic compiler 2.0 assembler.
    /// <para>
    /// Key difference from v1.0: this assembler walks the <strong>fully-typed AST</strong>
    /// produced by <see cref="Parser2"/> and analyzed by <see cref="SemanticAnalyzer2"/>.
    /// It generates <see cref="Command"/> bytecode directly from typed AST nodes —
    /// no <c>StatementLoweringCompiler</c>, no re-parsing of raw text, no deferred lowering.
    /// </para>
    /// </summary>
    public class Assembler2
    {
        /// <summary>Emit bytecode for a type declaration (def/defobj preamble).</summary>
        public List<Command> EmitTypeDeclaration(TypeDeclarationNode2 typeDecl, SymbolTable2 symbolTable)
        {
            var commands = new List<Command>();
            var scope = symbolTable.GlobalScope;

            // Emit: push typeName; def
            commands.Add(PushString(typeDecl.Name, typeDecl.SourceLine));
            commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));

            // Emit field definitions
            foreach (var field in typeDecl.Fields)
            {
                commands.Add(PushString(field.Name, typeDecl.SourceLine));
                commands.Add(PushString(field.TypeSpec ?? "any", typeDecl.SourceLine));
                commands.Add(Emit(Opcodes.Def, typeDecl.SourceLine));
            }

            return commands;
        }

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

                case CallStatement2 call:
                    EmitCallStatement(call, scope, result);
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

                case StreamWaitForLoop2 forLoop:
                    EmitStreamWaitForLoop(forLoop, scope, result, isProcedure);
                    break;

                case InstructionStatement2 instr:
                    EmitInstructionStatement(instr, scope, result);
                    break;

                case NestedProcedureStatement2 nested:
                    // Nested procedures are handled at the SemanticAnalyzer2 level.
                    // Here we emit a placeholder comment (nop).
                    result.Add(Emit(Opcodes.Nop, nested.SourceLine));
                    break;

                case NestedFunctionStatement2 nested:
                    result.Add(Emit(Opcodes.Nop, nested.SourceLine));
                    break;
            }
        }

        // ─── Variable declaration ─────────────────────────────────────────────────

        private void EmitVarDeclaration(VarDeclarationStatement2 varDecl, ScopeSymbols2 scope, ExecutionBlock result)
        {
            var slot = scope.AllocateLocal(varDecl.VariableName);

            if (varDecl.Initializer != null)
            {
                // Emit initializer expression, then pop into slot.
                EmitExpression(varDecl.Initializer, scope, result);
                result.Add(PopSlot(slot, varDecl.SourceLine));
            }
            else if (!string.IsNullOrEmpty(varDecl.ExplicitType))
            {
                // var x: Type — emit type def instruction.
                result.Add(PushString(varDecl.ExplicitType, varDecl.SourceLine));
                result.Add(Emit(Opcodes.Def, varDecl.SourceLine));
                result.Add(PopSlot(slot, varDecl.SourceLine));
            }
        }

        // ─── Assignment ───────────────────────────────────────────────────────────

        private void EmitAssignment(AssignmentStatement2 assign, ScopeSymbols2 scope, ExecutionBlock result)
        {
            // Evaluate RHS.
            EmitExpression(assign.Value, scope, result);

            // Store into target.
            if (assign.Target is VariableExpression2 varExpr)
            {
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
                // obj.Field := value
                // Already have value on stack, need to push obj and field name, then setobj.
                EmitExpression(memberAccess.Object, scope, result);
                result.Add(PushString(memberAccess.MemberName, assign.SourceLine));
                // Stack is now: [value, obj, fieldName] but setobj expects [obj, fieldName, value]
                // We need to handle this ordering — for now we emit a setobj.
                result.Add(Emit(Opcodes.SetObj, assign.SourceLine));
                result.Add(Emit(Opcodes.Pop, assign.SourceLine));
            }
        }

        // ─── Call statement ───────────────────────────────────────────────────────

        private void EmitCallStatement(CallStatement2 call, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (call.Callee is VariableExpression2 varExpr)
            {
                if (string.Equals(varExpr.Name, "await", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
                {
                    // await obj
                    EmitExpression(call.Arguments[0], scope, result);
                    result.Add(Emit(Opcodes.AwaitObj, call.SourceLine));
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
            }
            else if (call.Callee is MemberAccessExpression2 memberAccess)
            {
                // obj.Method(a, b)
                EmitExpression(memberAccess.Object, scope, result);
                EmitCallArguments(call.Arguments, scope, result, call.SourceLine);

                var callCmd = new Command
                {
                    Opcode = Opcodes.CallObj,
                    Operand1 = memberAccess.MemberName,
                    SourceLine = call.SourceLine
                };
                result.Add(callCmd);
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
            // Push arity, then each argument.
            result.Add(PushInt(args.Count, sourceLine));
            foreach (var arg in args)
                EmitExpression(arg, scope, result);
        }

        // ─── Return ───────────────────────────────────────────────────────────────

        private void EmitReturnStatement(ReturnStatement2 ret, ScopeSymbols2 scope, ExecutionBlock result)
        {
            if (ret.Value != null)
                EmitExpression(ret.Value, scope, result);
            result.Add(Emit(Opcodes.Ret, ret.SourceLine));
        }

        // ─── If statement ─────────────────────────────────────────────────────────

        private static int _labelCounter;

        private void EmitIfStatement(IfStatement2 ifStmt, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            var id = System.Threading.Interlocked.Increment(ref _labelCounter);
            var elseLabel = $"__else_{id}";
            var endLabel = $"__endif_{id}";

            // Evaluate condition.
            EmitExpression(ifStmt.Condition, scope, result);

            // Jump to else if condition is false.
            result.Add(new Command
            {
                Opcode = Opcodes.Je,
                Operand1 = elseLabel,
                SourceLine = ifStmt.SourceLine
            });

            // Then block.
            var thenBlock = EmitBlock(ifStmt.ThenBlock, scope, isProcedure);
            result.AddRange(thenBlock);

            if (ifStmt.ElseBlock != null)
            {
                // Jump past else block.
                result.Add(new Command { Opcode = Opcodes.Jmp, Operand1 = endLabel, SourceLine = ifStmt.SourceLine });
            }

            // Else label.
            result.Add(new Command { Opcode = Opcodes.Label, Operand1 = elseLabel, SourceLine = ifStmt.SourceLine });

            if (ifStmt.ElseBlock != null)
            {
                var elseBlock = EmitBlock(ifStmt.ElseBlock, scope, isProcedure);
                result.AddRange(elseBlock);

                // End label.
                result.Add(new Command { Opcode = Opcodes.Label, Operand1 = endLabel, SourceLine = ifStmt.SourceLine });
            }
        }

        // ─── Switch statement ─────────────────────────────────────────────────────

        private void EmitSwitchStatement(SwitchStatement2 switchStmt, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            var id = System.Threading.Interlocked.Increment(ref _labelCounter);
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

        // ─── Stream wait for loop ─────────────────────────────────────────────────

        private void EmitStreamWaitForLoop(StreamWaitForLoop2 forLoop, ScopeSymbols2 scope, ExecutionBlock result, bool isProcedure)
        {
            var id = System.Threading.Interlocked.Increment(ref _labelCounter);
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
                    EmitGenericType(generic, result);
                    break;

                case MemorySlotExpression2 slot:
                    result.Add(PushSlot(slot.SlotIndex, slot.SourceLine));
                    break;

                case LambdaExpression2 lambda:
                    EmitLambda(lambda, scope, result);
                    break;

                default:
                    result.Add(Emit(Opcodes.Nop, expr.SourceLine));
                    break;
            }
        }

        private void EmitLiteral(LiteralExpression2 lit, ExecutionBlock result)
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

        private void EmitVariableRef(VariableExpression2 varExpr, ScopeSymbols2 scope, ExecutionBlock result)
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
                // Unresolved — treat as string literal identifier (type name, etc.)
                result.Add(new Command
                {
                    Opcode = Opcodes.Push,
                    Operand1 = new PushOperand { Kind = "StringLiteral", Value = varExpr.Name },
                    SourceLine = varExpr.SourceLine
                });
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
            result.Add(Emit(awaitExpr.IsObjectAwait ? Opcodes.AwaitObj : Opcodes.Await, awaitExpr.SourceLine));
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

        private static void EmitGenericType(GenericTypeExpression2 generic, ExecutionBlock result)
        {
            // Push type name, then type arg — for def/defgen patterns.
            result.Add(new Command
            {
                Opcode = Opcodes.Push,
                Operand1 = new PushOperand { Kind = "StringLiteral", Value = generic.TypeName },
                SourceLine = generic.SourceLine
            });
            result.Add(new Command
            {
                Opcode = Opcodes.Def,
                SourceLine = generic.SourceLine
            });
            result.Add(new Command
            {
                Opcode = Opcodes.Push,
                Operand1 = new PushOperand { Kind = "StringLiteral", Value = generic.TypeArg },
                SourceLine = generic.SourceLine
            });
            result.Add(new Command
            {
                Opcode = Opcodes.DefGen,
                SourceLine = generic.SourceLine
            });
        }

        private void EmitLambda(LambdaExpression2 lambda, ScopeSymbols2 scope, ExecutionBlock result)
        {
            // Lambda: emit Lambda opcode followed by body, then DefExpr.
            result.Add(new Command { Opcode = Opcodes.Lambda, SourceLine = lambda.SourceLine });
            var lambdaBody = EmitBlock(lambda.Body, scope, isProcedure: false);
            result.AddRange(lambdaBody);
            result.Add(Emit(Opcodes.DefExpr, lambda.SourceLine));
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

}
