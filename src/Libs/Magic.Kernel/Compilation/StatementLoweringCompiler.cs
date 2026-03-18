using Magic.Kernel.Compilation.Ast;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Lowers high-level statement lines into instruction AST nodes.
    /// </summary>
    internal sealed class StatementLoweringCompiler
    {
        private Scanner? _scanner;
        private readonly Dictionary<string, int> _globalSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _nextGlobalSlot;

        private Scanner CurrentScanner =>
            _scanner ?? throw new InvalidOperationException("Scanner is not initialized.");

        public List<InstructionNode> Lower(IEnumerable<string> sourceLines, bool registerGlobals = false)
        {
            var vars = new Dictionary<string, (string Kind, int Index)>(StringComparer.OrdinalIgnoreCase);
            foreach (var global in _globalSlots)
                vars[global.Key] = ("global", global.Value);
            var vertexCounter = 1;
            var relationCounter = 1;
            var shapeCounter = 1;
            var memorySlotCounter = _nextGlobalSlot;
            var streamLoopCounter = 0;
            var instructions = CompileStatementLines(sourceLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter);

            if (memorySlotCounter > _nextGlobalSlot)
                _nextGlobalSlot = memorySlotCounter;

            if (registerGlobals)
            {
                foreach (var kv in vars)
                {
                    if (kv.Value.Kind == "memory" || kv.Value.Kind == "global")
                        _globalSlots[kv.Key] = kv.Value.Index;
                }
            }

            return instructions;
        }

        private List<InstructionNode> CompileStatementLines(
            IEnumerable<string> sourceLines,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter)
        {
            var instructions = new List<InstructionNode>();
            var inVarBlock = false;
            var varBlockLines = new List<string>();

            foreach (var rawLine in CoalesceMultilineStatements(sourceLines))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line == "{" || line == "}")
                    continue;

                if (TryCompileSchemaDeclaration(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryParseVarKeywordOnly(line))
                {
                    inVarBlock = true;
                    varBlockLines.Clear();
                    continue;
                }

                if (TryStripInlineVarPrefix(line, out var inlineDecl) && IsDeclarationStatement(inlineDecl))
                {
                    if (!inVarBlock)
                        varBlockLines.Clear();
                    inVarBlock = true;
                    varBlockLines.Add(inlineDecl);
                    continue;
                }

                if (inVarBlock)
                {
                    if (IsDeclarationStatement(line))
                    {
                        varBlockLines.Add(line);
                        continue;
                    }

                    inVarBlock = false;
                    instructions.AddRange(CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));
                }

                if (IsDeclarationStatement(line))
                {
                    instructions.AddRange(CompileVarBlock(new List<string> { line }, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));
                    continue;
                }

                if (TryCompileIfStatement(line, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions))
                    continue;

                if (TryCompileStreamWaitForLoop(line, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions))
                    continue;

                if (TryCompilePlusAssign(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileMultiplyAssign(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileMemberAssignment(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileMethodCall(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileAssignment(line, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileAwaitStatement(line, vars, instructions))
                    continue;

                if (TryCompileFunctionCall(line, vars, instructions))
                    continue;

                if (TryParseProcedureName(line, out var procName))
                    instructions.Add(CreateCallInstruction(procName));
            }

            if (inVarBlock && varBlockLines.Count > 0)
                instructions.AddRange(CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));

            return instructions;
        }

        private static IEnumerable<string> CoalesceMultilineStatements(IEnumerable<string> sourceLines)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var collecting = false;
            var parenDepth = 0;
            var braceDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var escape = false;

            foreach (var raw in sourceLines)
            {
                var line = raw ?? "";
                var trimmed = line.Trim();

                if (!collecting)
                {
                    if (trimmed.Length == 0)
                    {
                        result.Add(line);
                        continue;
                    }

                    var (parenDelta, braceDelta, bracketDelta) = ComputeDepthDelta(trimmed, ref inString, ref escape);
                    parenDepth = Math.Max(0, parenDelta);
                    braceDepth = Math.Max(0, braceDelta);
                    bracketDepth = Math.Max(0, bracketDelta);
                    if (parenDepth > 0 || braceDepth > 0 || bracketDepth > 0)
                    {
                        collecting = true;
                        sb.Clear();
                        sb.Append(trimmed);
                        continue;
                    }

                    result.Add(line);
                    continue;
                }

                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(trimmed);

                var (parenDeltaAcc, braceDeltaAcc, bracketDeltaAcc) = ComputeDepthDelta(trimmed, ref inString, ref escape);
                parenDepth = Math.Max(0, parenDepth + parenDeltaAcc);
                braceDepth = Math.Max(0, braceDepth + braceDeltaAcc);
                bracketDepth = Math.Max(0, bracketDepth + bracketDeltaAcc);
                if (parenDepth <= 0 && braceDepth <= 0 && bracketDepth <= 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    collecting = false;
                    parenDepth = 0;
                    braceDepth = 0;
                    bracketDepth = 0;
                }
            }

            if (collecting && sb.Length > 0)
                result.Add(sb.ToString());

            return result;
        }

        private static (int ParenDelta, int BraceDelta, int BracketDelta) ComputeDepthDelta(string text, ref bool inString, ref bool escape)
        {
            var parenDelta = 0;
            var braceDelta = 0;
            var bracketDelta = 0;

            foreach (var ch in text)
            {
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (ch == '"')
                        inString = false;

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '(') parenDelta++;
                else if (ch == ')') parenDelta--;
                else if (ch == '{') braceDelta++;
                else if (ch == '}') braceDelta--;
                else if (ch == '[') bracketDelta++;
                else if (ch == ']') bracketDelta--;
            }

            return (parenDelta, braceDelta, bracketDelta);
        }

        private sealed class VertexInitSpec
        {
            public List<long> Dimensions { get; } = new List<long> { 1, 0, 0, 0 };
            public double Weight { get; set; } = 0.5d;
            public string? TextData { get; set; }
            public string? BinaryData { get; set; }
        }

        private sealed class RelationInitSpec
        {
            public string? From { get; set; }
            public string? To { get; set; }
            public double Weight { get; set; } = 0.5d;
        }

        private sealed class InlineVertexSpec
        {
            public List<long> Dimensions { get; } = new List<long> { 1, 0, 0, 0 };
            public double? Weight { get; set; }
        }

        private sealed class ShapeInitSpec
        {
            public List<string> VertexNames { get; } = new List<string>();
            public List<InlineVertexSpec> InlineVertices { get; } = new List<InlineVertexSpec>();
            public bool UsesInlineVertices => InlineVertices.Count > 0;
        }

        private List<InstructionNode> CompileVarBlock(List<string> varLines, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter, ref int relationCounter, ref int shapeCounter, ref int memorySlotCounter)
        {
            var instructions = new List<InstructionNode>();

            foreach (var line in varLines)
            {
                if (TryParseStreamDeclaration(line, out var streamName, out var elementTypes))
                {
                    EmitStreamDeclaration(streamName, elementTypes, vars, ref memorySlotCounter, instructions);
                    continue;
                }

                if (!TryParseEntityDeclaration(line, out var varName, out var varType, out var initText))
                    continue;

                if (varType == "vertex")
                {
                    var index = vertexCounter++;
                    vars[varName] = ("vertex", index);
                    instructions.AddRange(CompileVertexInit(index, initText));
                }
                else if (varType == "relation")
                {
                    var index = relationCounter++;
                    vars[varName] = ("relation", index);
                    instructions.AddRange(CompileRelationInit(index, initText, vars));
                }
                else if (varType == "shape")
                {
                    var index = shapeCounter++;
                    vars[varName] = ("shape", index);
                    instructions.AddRange(CompileShapeInit(index, initText, vars, ref vertexCounter));
                }
            }

            return instructions;
        }

        private List<InstructionNode> CompileVertexInit(int index, string initValue)
        {
            var instructions = new List<InstructionNode>();
            if (!TryParseVertexObject(initValue, out var spec))
                return instructions;

            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = index },
                new DimensionsParameterNode { Name = "dimensions", Values = spec.Dimensions.Select(v => (float)v).ToList() },
                new WeightParameterNode { Name = "weight", Value = (float)spec.Weight }
            };

            if (!string.IsNullOrEmpty(spec.BinaryData))
            {
                parameters.Add(new DataParameterNode
                {
                    Name = "data",
                    Type = "binary:base64",
                    Types = new List<string> { "binary", "base64" },
                    Value = spec.BinaryData,
                    HasColon = true
                });
            }
            else if (!string.IsNullOrEmpty(spec.TextData))
            {
                parameters.Add(new DataParameterNode
                {
                    Name = "data",
                    Type = "text",
                    Types = new List<string> { "text" },
                    Value = spec.TextData,
                    HasColon = true
                });
            }

            instructions.Add(new InstructionNode { Opcode = "addvertex", Parameters = parameters });
            return instructions;
        }

        private List<InstructionNode> CompileRelationInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars)
        {
            var instructions = new List<InstructionNode>();
            if (!TryParseRelationObject(initValue, out var spec) || string.IsNullOrEmpty(spec.From) || string.IsNullOrEmpty(spec.To))
                return instructions;

            if (!vars.TryGetValue(spec.From, out var fromVar))
                throw new UndeclaredVariableException(spec.From);
            if (!vars.TryGetValue(spec.To, out var toVar))
                throw new UndeclaredVariableException(spec.To);

            var fromType = fromVar.Kind == "relation" ? "relation" : "vertex";
            var toType = toVar.Kind == "relation" ? "relation" : "vertex";

            instructions.Add(new InstructionNode
            {
                Opcode = "addrelation",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = index },
                    new FromParameterNode { Name = "from", EntityType = fromType, Index = fromVar.Index },
                    new ToParameterNode { Name = "to", EntityType = toType, Index = toVar.Index },
                    new WeightParameterNode { Name = "weight", Value = (float)spec.Weight }
                }
            });
            return instructions;
        }

        private List<InstructionNode> CompileShapeInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter)
        {
            var instructions = new List<InstructionNode>();
            if (!TryParseShapeObject(initValue, out var spec))
                return instructions;

            var indices = new List<long>();
            if (spec.UsesInlineVertices)
            {
                foreach (var vertex in spec.InlineVertices)
                {
                    var tempIndex = vertexCounter++;
                    var parameters = new List<ParameterNode>
                    {
                        new IndexParameterNode { Name = "index", Value = tempIndex },
                        new DimensionsParameterNode { Name = "dimensions", Values = vertex.Dimensions.Select(v => (float)v).ToList() }
                    };
                    if (vertex.Weight.HasValue)
                        parameters.Add(new WeightParameterNode { Name = "weight", Value = (float)vertex.Weight.Value });
                    instructions.Add(new InstructionNode { Opcode = "addvertex", Parameters = parameters });
                    indices.Add(tempIndex);
                }
            }
            else
            {
                foreach (var name in spec.VertexNames)
                {
                    if (vars.TryGetValue(name, out var vertexVar) && vertexVar.Kind == "vertex")
                        indices.Add(vertexVar.Index);
                }
            }

            if (indices.Count > 0)
            {
                instructions.Add(new InstructionNode
                {
                    Opcode = "addshape",
                    Parameters = new List<ParameterNode>
                    {
                        new IndexParameterNode { Name = "index", Value = index },
                        new VerticesParameterNode { Name = "vertices", Indices = indices }
                    }
                });
            }

            return instructions;
        }

        private bool TryCompileStreamWaitForLoop(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            if (!TryParseStreamWaitForLoop(line, out var isSync, out var streamName, out var waitType, out var deltaVarName, out var aggregateVarName, out var bodyText))
                return false;

            if (!vars.TryGetValue(streamName, out var streamVar))
                throw new UndeclaredVariableException(streamName);
            if (streamVar.Kind != "stream" && streamVar.Kind != "def" && streamVar.Kind != "memory" && streamVar.Kind != "history")
                return true;

            var loopCounter = ++streamLoopCounter;
            var loopStartLabel = $"streamwait_loop_{loopCounter}";
            var loopEndLabel = $"streamwait_loop_{loopCounter}_end";
            var loopBodyLabel = $"streamwait_loop_{loopCounter}_delta";

            var endSlot = memorySlotCounter++;
            var deltaSlot = memorySlotCounter++;
            var aggregateSlot = memorySlotCounter++;
            vars[deltaVarName] = ("memory", deltaSlot);
            if (!string.IsNullOrWhiteSpace(aggregateVarName))
                vars[aggregateVarName] = ("memory", aggregateSlot);

            instructions.Add(CreateLabelInstruction(loopStartLabel));
            instructions.Add(CreatePushMemoryInstruction(streamVar.Index));
            instructions.Add(CreatePushStringInstruction(waitType));
            instructions.Add(new InstructionNode { Opcode = "streamwaitobj" });
            instructions.Add(CreatePopMemoryInstruction(endSlot));
            instructions.Add(CreatePopMemoryInstruction(deltaSlot));
            instructions.Add(CreatePopMemoryInstruction(aggregateSlot));
            instructions.Add(CreateCmpInstruction(endSlot, 1));
            instructions.Add(CreateJumpIfEqualInstruction(loopEndLabel));

            var bodyLines = SplitStatementLines(bodyText);
            var bodyInstructions = CompileStatementLines(bodyLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter);
            var useSyncLoop = isSync || string.Equals(streamVar.Kind, "history", StringComparison.OrdinalIgnoreCase);
            instructions.Add(useSyncLoop
                ? CreateCallInstruction(loopBodyLabel)
                : CreateAsyncCallInstruction(loopBodyLabel));
            instructions.Add(CreateJumpInstruction(loopStartLabel));
            instructions.Add(CreateLabelInstruction(loopBodyLabel));
            instructions.AddRange(bodyInstructions);
            instructions.Add(new InstructionNode { Opcode = "ret" });
            instructions.Add(CreateLabelInstruction(loopEndLabel));
            return true;
        }

        private bool TryCompileIfStatement(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            if (!TryParseIfStatement(line, out var condText, out var negated, out var bodyText, out var elseText, out var elseIsBlock))
                return false;

            var ifCounter = ++streamLoopCounter + 1000;
            var elseLabel = $"if_else_{ifCounter}";
            var endLabel = $"if_end_{ifCounter}";
            var condSlot = memorySlotCounter++;

            if (!TryCompileExpressionToSlot(condText, vars, condSlot, ref memorySlotCounter, instructions))
                throw new CompilationException($"Cannot compile if condition: '{condText?.Trim()}'", -1);

            var slotToCompare = condSlot;
            if (negated)
            {
                var notSlot = memorySlotCounter++;
                instructions.Add(CreatePushMemoryInstruction(condSlot));
                instructions.Add(new InstructionNode { Opcode = "not" });
                instructions.Add(CreatePopMemoryInstruction(notSlot));
                slotToCompare = notSlot;
            }

            // Jump to else/end when the final condition value is false.
            instructions.Add(CreateCmpInstruction(slotToCompare, 0L));
            instructions.Add(CreateJumpIfEqualInstruction(string.IsNullOrWhiteSpace(elseText) ? endLabel : elseLabel));

            var bodyLines = SplitStatementLines(bodyText);
            var bodyInstructions = CompileStatementLines(bodyLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter);
            instructions.AddRange(bodyInstructions);

            if (!string.IsNullOrWhiteSpace(elseText))
            {
                instructions.Add(CreateJumpInstruction(endLabel));
                instructions.Add(CreateLabelInstruction(elseLabel));

                var elseLines = elseIsBlock
                    ? SplitStatementLines(elseText)
                    : new List<string> { elseText };
                var elseInstructions = CompileStatementLines(elseLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter);
                instructions.AddRange(elseInstructions);
            }

            instructions.Add(CreateLabelInstruction(endLabel));
            return true;
        }

        /// <summary>Compile expression to a slot (push instructions that leave one value on stack, then pop to slot).</summary>
        private bool TryCompileExpressionToSlot(string exprText, Dictionary<string, (string Kind, int Index)> vars, long targetSlot, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var trimmed = TrimBalancedOuterParentheses(exprText?.Trim() ?? "");
            if (string.IsNullOrEmpty(trimmed)) return false;

            if (TryParseAwaitExpressionText(trimmed, out var awaitedExpression))
            {
                var awaitSourceSlot = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(awaitedExpression, vars, awaitSourceSlot, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(awaitSourceSlot));
                instructions.Add(new InstructionNode { Opcode = "awaitobj" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            if (trimmed.StartsWith("!", StringComparison.Ordinal) && trimmed.Length > 1)
            {
                var inner = TrimBalancedOuterParentheses(trimmed.Substring(1).Trim());
                if (inner.Length > 0)
                {
                    var notSlot = memorySlotCounter++;
                    if (TryCompileExpressionToSlot(inner, vars, notSlot, ref memorySlotCounter, instructions))
                    {
                        instructions.Add(CreatePushMemoryInstruction(notSlot));
                        instructions.Add(new InstructionNode { Opcode = "not" });
                        instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        return true;
                    }
                }
            }

            if (TrySplitBinaryComparison(trimmed, out var leftStr, out var op, out var rightStr))
            {
                var slotLeft = memorySlotCounter++;
                var slotRight = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(leftStr, vars, slotLeft, ref memorySlotCounter, instructions))
                    return false;
                if (!TryCompileExpressionToSlot(rightStr, vars, slotRight, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(slotLeft));
                instructions.Add(CreatePushMemoryInstruction(slotRight));
                if (op == "<")
                    instructions.Add(new InstructionNode { Opcode = "lt" });
                else if (op == "==")
                    instructions.Add(new InstructionNode { Opcode = "equals" });
                else
                    return false;
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            if (TryEmitBareCallExpressionToSlot(trimmed, vars, targetSlot, ref memorySlotCounter, instructions))
                return true;

            if (vars.TryGetValue(trimmed, out var singleVar))
            {
                if (singleVar.Kind == "memory" || singleVar.Kind == "stream" || singleVar.Kind == "def" || singleVar.Kind == "global")
                {
                    instructions.Add(singleVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(singleVar.Index) : CreatePushMemoryInstruction(singleVar.Index));
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }

                if (singleVar.Kind == "vertex" || singleVar.Kind == "relation" || singleVar.Kind == "shape")
                {
                    instructions.Add(CreatePushIntInstruction(singleVar.Index));
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (TryEmitMethodCallExpression(trimmed, vars, targetSlot, ref memorySlotCounter, instructions, out _))
                return true;

            if (TryParseMemberAccessFromString(trimmed, out var memberRoot, out var memberPath) && vars.TryGetValue(memberRoot, out var memberVar))
            {
                instructions.Add(memberVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(memberVar.Index) : CreatePushMemoryInstruction(memberVar.Index));
                instructions.Add(CreatePushStringInstruction(memberPath));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            return false;
        }

        /// <summary>Splits expression at top-level &lt; or == (outside parentheses/strings).</summary>
        private static bool TrySplitBinaryComparison(string expr, out string left, out string op, out string right)
        {
            left = "";
            op = "";
            right = "";
            var depth = 0;
            var inString = false;
            var quote = '\0';
            var escaped = false;
            for (var i = 0; i < expr.Length; i++)
            {
                var ch = expr[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == quote) { inString = false; continue; }
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth--; continue; }
                if (depth != 0) continue;
                if (ch == '<')
                {
                    var j = i + 1;
                    while (j < expr.Length && char.IsWhiteSpace(expr[j])) j++;
                    if (j < expr.Length && expr[j] == '>')
                        continue;
                    if ((i == 0 || expr[i - 1] != '=') && (i + 1 >= expr.Length || expr[i + 1] != '='))
                    {
                        left = expr.Substring(0, i).Trim();
                        op = "<";
                        right = expr.Substring(i + 1).Trim();
                        return left.Length > 0 && right.Length > 0;
                    }
                }
                if (i + 1 < expr.Length && ch == '=' && expr[i + 1] == '=')
                {
                    left = expr.Substring(0, i).Trim();
                    op = "==";
                    right = expr.Substring(i + 2).Trim();
                    return left.Length > 0 && right.Length > 0;
                }
            }
            return false;
        }

        /// <summary>Compiles bare function call like unit(20, "mb") to slot: emit call with literal params, result on stack, then pop to slot.</summary>
        private bool TryEmitBareCallExpressionToSlot(string expr, Dictionary<string, (string Kind, int Index)> vars, long targetSlot, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            if (!TryParseBareCall(expr, out var funcName, out var argExprs))
                return false;
            var parameters = new List<ParameterNode> { new FunctionNameParameterNode { Name = "function", FunctionName = funcName } };
            for (var i = 0; i < argExprs.Count; i++)
            {
                var arg = argExprs[i].Trim();
                if (long.TryParse(arg, out var numVal))
                    parameters.Add(new IndexParameterNode { Name = i.ToString(), Value = numVal });
                else if (arg.Length >= 2 && (arg.StartsWith("\"", StringComparison.Ordinal) && arg.EndsWith("\"", StringComparison.Ordinal) || arg.StartsWith("'", StringComparison.Ordinal) && arg.EndsWith("'", StringComparison.Ordinal)))
                    parameters.Add(new StringParameterNode { Name = i.ToString(), Value = arg.Substring(1, arg.Length - 2).Replace("\\\"", "\"", StringComparison.Ordinal) });
                else if (TryParseTypeLiteralArgument(arg, out var typeLiteral))
                    parameters.Add(new StringParameterNode { Name = i.ToString(), Value = typeLiteral });
                else if (vars.TryGetValue(arg, out var argVar) && (argVar.Kind == "memory" || argVar.Kind == "global"))
                    parameters.Add(new FunctionParameterNode { Name = i.ToString(), ParameterName = i.ToString(), EntityType = "memory", Index = argVar.Index });
                else
                    return false;
            }
            instructions.Add(new InstructionNode { Opcode = "call", Parameters = parameters });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));
            return true;
        }

        private static bool TryParseTypeLiteralArgument(string text, out string typeLiteral)
        {
            typeLiteral = "";
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return false;

            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var baseType = scanner.Scan().Value.ToLowerInvariant();
            if (scanner.Current.Kind != TokenKind.LessThan)
                return false;

            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier && scanner.Current.Kind != TokenKind.StringLiteral)
                return false;

            var genericType = scanner.Scan().Value.ToLowerInvariant();
            if (scanner.Current.Kind != TokenKind.GreaterThan)
                return false;

            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();

            if (!scanner.Current.IsEndOfInput)
                return false;

            typeLiteral = $"{baseType}<{genericType}>";
            return true;
        }

        private static bool TryParseBareCall(string expr, out string funcName, out List<string> argExprs)
        {
            funcName = "";
            argExprs = new List<string>();
            var trimmed = expr.Trim();
            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            funcName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            var openParenStart = scanner.Current.Start;
            scanner.Scan();
            if (scanner.Current.Kind == TokenKind.RParen)
            {
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                return scanner.Current.IsEndOfInput;
            }
            var start = scanner.Current.Start;
            var depth = 1;
            var inString = false;
            var quote = '\0';
            var escaped = false;
            var segmentStart = start;
            var closeParenIndex = -1;
            for (var i = openParenStart + 1; i < trimmed.Length; i++)
            {
                var ch = trimmed[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == quote) { inString = false; continue; }
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (segmentStart < i)
                            argExprs.Add(trimmed.Substring(segmentStart, i - segmentStart).Trim());
                        closeParenIndex = i;
                        break;
                    }
                    continue;
                }
                if (depth == 1 && ch == ',')
                {
                    argExprs.Add(trimmed.Substring(segmentStart, i - segmentStart).Trim());
                    segmentStart = i + 1;
                }
            }
            if (closeParenIndex < 0)
                return false;
            for (var i = closeParenIndex + 1; i < trimmed.Length; i++)
            {
                if (!char.IsWhiteSpace(trimmed[i]) && trimmed[i] != ';')
                    return false;
            }
            return true;
        }

        private static bool TryParseMemberAccessFromString(string expr, out string rootVariableName, out string path)
        {
            rootVariableName = "";
            path = "";
            var scanner = new Scanner(expr.Trim());
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            rootVariableName = scanner.Scan().Value;
            var segments = new List<string>();
            while (scanner.Current.Kind == TokenKind.Dot)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var segment = scanner.Scan().Value;
                if (scanner.Current.Kind == TokenKind.LessThan && scanner.Watch(1)?.Kind == TokenKind.GreaterThan)
                {
                    scanner.Scan();
                    scanner.Scan();
                    segment += "<>";
                }
                segments.Add(segment);
            }
            if (segments.Count == 0)
                return false;
            path = string.Join(".", segments);
            return scanner.Current.IsEndOfInput || scanner.Current.Kind == TokenKind.Semicolon;
        }

        private static string TrimBalancedOuterParentheses(string text)
        {
            var trimmed = text?.Trim() ?? "";
            while (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')' && IsWrappedBySingleBalancedParentheses(trimmed))
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return trimmed;
        }

        private static bool IsWrappedBySingleBalancedParentheses(string text)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            var quote = '\0';

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    continue;
                }

                if (ch != ')')
                    continue;

                depth--;
                if (depth == 0 && i < text.Length - 1)
                    return false;
            }

            return depth == 0;
        }

        private static bool TryParseIfStatement(string line, out string condText, out bool negated, out string bodyText, out string elseText, out bool elseIsBlock)
        {
            condText = "";
            bodyText = "";
            elseText = "";
            negated = false;
            elseIsBlock = false;
            var t = line?.Trim() ?? "";
            if (t.Length < 3 || !t.StartsWith("if", StringComparison.OrdinalIgnoreCase))
                return false;
            var i = 2;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i >= t.Length) return false;
            if (t[i] == '!')
            {
                negated = true;
                i++;
                while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            }
            if (i >= t.Length) return false;

            var condStart = i;
            var parenDepth = 0;
            var inString = false;
            var quote = '\0';
            var braceStart = -1;
            while (i < t.Length)
            {
                var ch = t[i];
                if (inString)
                {
                    if (ch == '\\' && i + 1 < t.Length) { i += 2; continue; }
                    if (ch == quote) { inString = false; i++; continue; }
                    i++;
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; i++; continue; }
                if (ch == '(') { parenDepth++; i++; continue; }
                if (ch == ')') { parenDepth--; i++; continue; }
                if (ch == '{' && parenDepth == 0) { braceStart = i; break; }
                i++;
            }
            if (braceStart < 0) return false;
            condText = t.Substring(condStart, braceStart - condStart).Trim();
            if (string.IsNullOrEmpty(condText)) return false;

            var bodyDepth = 1;
            var j = braceStart + 1;
            while (j < t.Length && bodyDepth > 0)
            {
                var c = t[j];
                if (c == '"' || c == '\'')
                {
                    var q = c;
                    j++;
                    while (j < t.Length && (t[j] != q || (j > 0 && t[j - 1] == '\\'))) j++;
                    if (j < t.Length) j++;
                    continue;
                }
                if (c == '{') { bodyDepth++; j++; continue; }
                if (c == '}') { bodyDepth--; if (bodyDepth == 0) break; j++; continue; }
                j++;
            }
            if (bodyDepth != 0) return false;
            bodyText = t.Substring(braceStart + 1, j - braceStart - 1);

            var tailIndex = j + 1;
            while (tailIndex < t.Length && char.IsWhiteSpace(t[tailIndex]))
                tailIndex++;
            while (tailIndex < t.Length && t[tailIndex] == ';')
            {
                tailIndex++;
                while (tailIndex < t.Length && char.IsWhiteSpace(t[tailIndex]))
                    tailIndex++;
            }

            if (tailIndex >= t.Length)
                return true;

            const string elseKeyword = "else";
            if (tailIndex + elseKeyword.Length > t.Length ||
                !t.AsSpan(tailIndex, elseKeyword.Length).Equals(elseKeyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;

            var elseValueIndex = tailIndex + elseKeyword.Length;
            if (elseValueIndex < t.Length && !char.IsWhiteSpace(t[elseValueIndex]) && t[elseValueIndex] != '{')
                return false;

            while (elseValueIndex < t.Length && char.IsWhiteSpace(t[elseValueIndex]))
                elseValueIndex++;
            if (elseValueIndex >= t.Length)
                return false;

            if (t[elseValueIndex] == '{')
            {
                var elseBraceStart = elseValueIndex;
                var elseDepth = 1;
                var k = elseBraceStart + 1;
                while (k < t.Length && elseDepth > 0)
                {
                    var c = t[k];
                    if (c == '"' || c == '\'')
                    {
                        var q = c;
                        k++;
                        while (k < t.Length && (t[k] != q || (k > 0 && t[k - 1] == '\\'))) k++;
                        if (k < t.Length) k++;
                        continue;
                    }
                    if (c == '{') { elseDepth++; k++; continue; }
                    if (c == '}') { elseDepth--; if (elseDepth == 0) break; k++; continue; }
                    k++;
                }
                if (elseDepth != 0)
                    return false;

                elseText = t.Substring(elseBraceStart + 1, k - elseBraceStart - 1);
                elseIsBlock = true;

                var tailAfterElse = k + 1;
                while (tailAfterElse < t.Length && char.IsWhiteSpace(t[tailAfterElse]))
                    tailAfterElse++;
                while (tailAfterElse < t.Length && t[tailAfterElse] == ';')
                {
                    tailAfterElse++;
                    while (tailAfterElse < t.Length && char.IsWhiteSpace(t[tailAfterElse]))
                        tailAfterElse++;
                }

                return tailAfterElse >= t.Length;
            }

            elseText = t.Substring(elseValueIndex).Trim();
            return !string.IsNullOrWhiteSpace(elseText);
        }

        private bool TryCompileAssignment(string line, Dictionary<string, (string Kind, int Index)> vars, ref int shapeCounter, ref int vertexCounter, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var previous = _scanner;
            _scanner = new Scanner(line);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                {
                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var targetName = CurrentScanner.Scan().Value;

                var hasAssign = CurrentScanner.Current.Kind == TokenKind.Assign;
                var hasDefineAssign = CurrentScanner.Current.Kind == TokenKind.Colon && CurrentScanner.Watch(1)?.Kind == TokenKind.Assign;
                if (!hasAssign && !hasDefineAssign)
                    return false;
                if (hasDefineAssign)
                {
                    CurrentScanner.Scan();
                    CurrentScanner.Scan();
                }
                else
                {
                    CurrentScanner.Scan();
                }

                SkipSemicolon();
                if (CurrentScanner.Current.IsEndOfInput)
                    return true;

                var rhsText = ExtractRemainingExpressionText(line, CurrentScanner.Current.Start);
                if (LooksLikeJsonLiteral(rhsText))
                {
                    // RHS of := : allow object literal with ; (e.g. a := { key: x; }). Only forbid ; in strict JSON.
                    if (TryParseObjectLiteralWithVarRefs(rhsText, out var keyVars) && keyVars != null && keyVars.Count > 0)
                    {
                        var targetSlot = memorySlotCounter++;
                        vars[targetName] = ("memory", targetSlot);
                        instructions.Add(CreatePushStringInstruction("{}"));
                        instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        foreach (var (key, varName) in keyVars)
                        {
                            if (!vars.TryGetValue(varName, out var valVar) || (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def"))
                                return false;
                            EmitOpJsonCall(
                                targetSlot,
                                "set",
                                key,
                                instructions,
                                dataRef: new FunctionParameterNode
                                {
                                    Name = "data",
                                    ParameterName = "data",
                                    EntityType = "memory",
                                    Index = valVar.Index
                                });
                        }
                        return true;
                    }
                    if (HasSemicolonInsideJsonLiteral(rhsText))
                        throw new CompilationException("JSON literal cannot contain ';' inside object or array.", CurrentScanner.Current.Start);
                    if (TryParseJsonArgument(rhsText, out var jsonArg))
                    {
                        var targetSlot = memorySlotCounter++;
                        vars[targetName] = ("memory", targetSlot);
                        var initJson = jsonArg is JsonArrayNode ? "[]" : "{}";
                        instructions.Add(CreatePushStringInstruction(initJson));
                        instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        EmitBuildJsonNode(jsonArg, targetSlot, "", vars, instructions);
                        return true;
                    }
                }

                if (TryParseSymbolicVariableExpression(out var symbolicVariableName))
                {
                    var resultSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", resultSlot);
                    instructions.Add(CreatePushStringInstruction(":" + symbolicVariableName));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(CreateCallInstruction("get"));
                    instructions.Add(CreatePopMemoryInstruction(resultSlot));
                    return true;
                }

                if (IsIdentifier(CurrentScanner.Current, "vault"))
                {
                    var vaultSlot = memorySlotCounter++;
                    vars[targetName] = ("vault", vaultSlot);
                    instructions.Add(CreatePushTypeInstruction("vault"));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode { Opcode = "def" });
                    instructions.Add(CreatePopMemoryInstruction(vaultSlot));
                    return true;
                }

                if (TryParseVaultReadExpression(out var vaultVarName, out var readArgs))
                {
                    if (!vars.TryGetValue(vaultVarName, out var vaultVar))
                        throw new UndeclaredVariableException(vaultVarName);
                    if (vaultVar.Kind != "vault")
                        return true;
                    var resultSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", resultSlot);
                    instructions.Add(CreatePushMemoryInstruction(vaultVar.Index));
                    foreach (var arg in readArgs)
                        instructions.Add(CreatePushStringInstruction(arg));
                    instructions.Add(CreatePushIntInstruction(readArgs.Count));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = "read" } }
                    });
                    instructions.Add(CreatePopMemoryInstruction(resultSlot));
                    return true;
                }

                if (TryParseGenericDefExpression(out var defTypeName, out var genericTypeNames))
                {
                    var baseSlot = memorySlotCounter++;
                    var targetSlot = memorySlotCounter++;
                    vars[targetName] = ("def", targetSlot);

                    instructions.Add(CreatePushTypeInstruction(defTypeName));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode { Opcode = "def" });
                    instructions.Add(CreatePopMemoryInstruction(baseSlot));

                    instructions.Add(CreatePushMemoryInstruction(baseSlot));
                    foreach (var genericType in genericTypeNames)
                    {
                        if (_globalSlots.TryGetValue(genericType, out var globalIndex))
                            instructions.Add(CreatePushGlobalMemoryInstruction(globalIndex));
                        else
                            instructions.Add(CreatePushTypeInstruction(genericType));
                    }
                    instructions.Add(CreatePushIntInstruction(genericTypeNames.Count));
                    instructions.Add(new InstructionNode { Opcode = "defgen" });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }

                if (TryParseAwaitExpressionText(rhsText, out var awaitedExpression))
                {
                    var dataSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", dataSlot);
                    var awaitSourceSlot = memorySlotCounter++;
                    if (!TryCompileExpressionToSlot(awaitedExpression, vars, awaitSourceSlot, ref memorySlotCounter, instructions))
                        return false;
                    instructions.Add(CreatePushMemoryInstruction(awaitSourceSlot));
                    instructions.Add(new InstructionNode { Opcode = "awaitobj" });
                    instructions.Add(CreatePopMemoryInstruction(dataSlot));
                    return true;
                }

                // Специализированный кейс: db.Table<>.where(_ => _.Col = x).max(_ => _.OtherCol)
                // для var offsetId := ...;
                {
                    var whereMaxCounterSnapshot = memorySlotCounter;
                    var whereMaxResultSlot = memorySlotCounter++;
                    if (TryEmitWhereMaxExpression(rhsText, vars, whereMaxResultSlot, ref memorySlotCounter, instructions, out _))
                    {
                        vars[targetName] = ("memory", whereMaxResultSlot);
                        return true;
                    }
                    memorySlotCounter = whereMaxCounterSnapshot;
                }

                if (TryParseStreamWaitExpression(out var streamWaitVarName))
                {
                    if (!vars.TryGetValue(streamWaitVarName, out var streamVar))
                        throw new UndeclaredVariableException(streamWaitVarName);
                    if (streamVar.Kind != "stream" && streamVar.Kind != "def")
                        return true;
                    var dataSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", dataSlot);
                    instructions.Add(CreatePushMemoryInstruction(streamVar.Index));
                    instructions.Add(CreatePushStringInstruction("data"));
                    instructions.Add(new InstructionNode { Opcode = "streamwaitobj" });
                    instructions.Add(CreatePopMemoryInstruction(dataSlot));
                    return true;
                }

                var methodCallCounterSnapshot = memorySlotCounter;
                var methodCallResultSlot = memorySlotCounter++;
                if (TryEmitMethodCallExpression(rhsText, vars, methodCallResultSlot, ref memorySlotCounter, instructions, out var methodCallKind))
                {
                    vars[targetName] = (methodCallKind, methodCallResultSlot);
                    return true;
                }
                memorySlotCounter = methodCallCounterSnapshot;

                if (TryParseCompileExpression(out var compileArgName))
                {
                    if (!vars.TryGetValue(compileArgName, out var dataVar))
                        throw new UndeclaredVariableException(compileArgName);
                    if (dataVar.Kind != "memory" && dataVar.Kind != "stream")
                        return true;
                    var resultSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", resultSlot);
                    instructions.Add(CreatePushMemoryInstruction(dataVar.Index));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(CreateCallInstruction("compile"));
                    instructions.Add(CreatePopMemoryInstruction(resultSlot));
                    return true;
                }

                if (TryParseAwaitCompileExpression(out var awaitCompileArgName))
                {
                    if (!vars.TryGetValue(awaitCompileArgName, out var awaitCompileDataVar))
                        throw new UndeclaredVariableException(awaitCompileArgName);
                    if (awaitCompileDataVar.Kind != "memory" && awaitCompileDataVar.Kind != "stream")
                        return true;
                    var resultSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", resultSlot);
                    instructions.Add(CreatePushMemoryInstruction(awaitCompileDataVar.Index));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(CreateCallInstruction("compile"));
                    instructions.Add(new InstructionNode { Opcode = "await" });
                    instructions.Add(CreatePopMemoryInstruction(resultSlot));
                    return true;
                }

                if (TryParseMemberAccessExpression(out var memberAccessBaseName, out var memberAccessPath))
                {
                    if (!vars.TryGetValue(memberAccessBaseName, out var memberAccessVar))
                        throw new UndeclaredVariableException(memberAccessBaseName);
                    if (memberAccessVar.Kind != "memory" && memberAccessVar.Kind != "stream" && memberAccessVar.Kind != "def")
                        return true;
                    var resultSlot = memorySlotCounter++;
                    vars[targetName] = ("memory", resultSlot);
                    instructions.Add(CreatePushMemoryInstruction(memberAccessVar.Index));
                    var segments = memberAccessPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var segment in segments)
                    {
                        instructions.Add(CreatePushStringInstruction(segment));
                        instructions.Add(new InstructionNode { Opcode = "getobj" });
                    }
                    instructions.Add(CreatePopMemoryInstruction(resultSlot));
                    return true;
                }

                if (TryParseOriginExpression(out var shapeVarName))
                {
                    if (!vars.TryGetValue(shapeVarName, out var shapeVar))
                        throw new UndeclaredVariableException(shapeVarName);
                    if (shapeVar.Kind != "shape")
                        return true;
                    instructions.Add(CreateCallInstruction("origin", CreateEntityCallParameter("shape", "shape", shapeVar.Index)));
                    instructions.Add(CreatePopMemoryInstruction(0));
                    vars[targetName] = ("memory", 0);
                    return true;
                }

                var savedScanner = _scanner;
                _scanner = new Scanner(rhsText);
                var intersectionMatched = TryParseIntersectionExpression(rhsText, out var shapeAName, out var shapeBText);
                _scanner = savedScanner;
                if (intersectionMatched)
                {
                    if (!vars.TryGetValue(shapeAName, out var shapeA))
                        throw new UndeclaredVariableException(shapeAName);
                    if (shapeA.Kind != "shape")
                        return true;

                    int? shapeBIndex = null;
                    if (vars.TryGetValue(shapeBText.Trim(), out var shapeBVar) && shapeBVar.Kind == "shape")
                    {
                        shapeBIndex = shapeBVar.Index;
                    }
                    else
                    {
                        var tempShapeIndex = shapeCounter++;
                        instructions.AddRange(CompileShapeInit(tempShapeIndex, shapeBText, vars, ref vertexCounter));
                        shapeBIndex = tempShapeIndex;
                    }

                    if (shapeBIndex.HasValue)
                    {
                        instructions.Add(CreateCallInstruction(
                            "intersect",
                            CreateEntityCallParameter("shapeA", "shape", shapeA.Index),
                            CreateEntityCallParameter("shapeB", "shape", shapeBIndex.Value)));
                        instructions.Add(CreatePopMemoryInstruction(1));
                        vars[targetName] = ("memory", 1);
                    }
                    return true;
                }

                var valueTargetSlot = vars.TryGetValue(targetName, out var existingTargetVar) && existingTargetVar.Kind == "memory"
                    ? existingTargetVar.Index
                    : memorySlotCounter++;

                if (TryEmitValuePush(rhsText, vars, ref memorySlotCounter, instructions))
                {
                    vars[targetName] = ("memory", valueTargetSlot);
                    instructions.Add(CreatePopMemoryInstruction(valueTargetSlot));
                }

                return true;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryCompileMemberAssignment(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (IsIdentifier(scanner.Current, "var"))
                return false;
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var rootName = scanner.Scan().Value;
            var pathSegments = new List<string>();
            while (true)
            {
                if (scanner.Current.Kind == TokenKind.Identifier && scanner.Current.Value == "!")
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Dot)
                    break;
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                pathSegments.Add(scanner.Scan().Value);
            }

            if (pathSegments.Count == 0)
                return false;

            var hasAssign = scanner.Current.Kind == TokenKind.Assign;
            var hasDefineAssign = scanner.Current.Kind == TokenKind.Colon && scanner.Watch(1)?.Kind == TokenKind.Assign;
            if (!hasAssign && !hasDefineAssign)
                return false;

            scanner.Scan();
            if (hasDefineAssign)
                scanner.Scan();

            var rhsStart = scanner.Current.Start;
            var rhsEnd = rhsStart;
            if (scanner.Current.Kind == TokenKind.LBrace)
            {
                var depth = 1;
                scanner.Scan();
                while (!scanner.Current.IsEndOfInput && depth > 0)
                {
                    if (scanner.Current.Kind == TokenKind.LBrace) depth++;
                    else if (scanner.Current.Kind == TokenKind.RBrace) depth--;
                    rhsEnd = scanner.Current.End;
                    scanner.Scan();
                }
            }
            else
            {
                while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.Semicolon)
                {
                    rhsEnd = scanner.Current.End;
                    scanner.Scan();
                }
            }
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(rootName, out var rootVar))
                throw new UndeclaredVariableException(rootName);
            if (rootVar.Kind != "memory" && rootVar.Kind != "stream" && rootVar.Kind != "def")
                return true;

            if (rhsEnd <= rhsStart || rhsStart < 0 || rhsEnd > line.Length)
                return true;

            var rhsText = line.Substring(rhsStart, rhsEnd - rhsStart).Trim();
            if (string.IsNullOrWhiteSpace(rhsText))
                return true;

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            for (var i = 0; i < pathSegments.Count - 1; i++)
            {
                instructions.Add(CreatePushStringInstruction(pathSegments[i]));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }
            instructions.Add(CreatePushStringInstruction(pathSegments[pathSegments.Count - 1]));
            if (!TryEmitValuePush(rhsText, vars, ref memorySlotCounter, instructions))
                return true;
            instructions.Add(new InstructionNode { Opcode = "setobj" });
            instructions.Add(CreatePopMemoryInstruction(memorySlotCounter++));
            return true;
        }

        private bool TryEmitValuePush(string valueText, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var trimmed = valueText.Trim();

            if (TryParseFormatStringLiteral(trimmed, out var formatTemplate, out var formatExpressions))
            {
                var parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { Name = "function", FunctionName = "format" },
                    new StringParameterNode { Name = "0", Value = formatTemplate }
                };
                for (var i = 0; i < formatExpressions.Count; i++)
                {
                    var argumentSlot = memorySlotCounter++;
                    if (!TryCompileExpressionToSlot(formatExpressions[i], vars, argumentSlot, ref memorySlotCounter, instructions))
                        return false;

                    parameters.Add(new FunctionParameterNode
                    {
                        Name = (i + 1).ToString(),
                        ParameterName = (i + 1).ToString(),
                        EntityType = "memory",
                        Index = argumentSlot
                    });
                }
                instructions.Add(new InstructionNode { Opcode = "call", Parameters = parameters });
                return true;
            }

            // JSON only in valid context: RHS of := or inside (). Object literal with ; (e.g. { data: photoData; }) is allowed.
            if (LooksLikeJsonLiteral(trimmed))
            {
                // Prefer object literal with var refs (allows ; as separator). Only forbid ; in strict JSON literal.
                if (TryParseObjectLiteralWithVarRefs(trimmed, out var keyVars) && keyVars != null && keyVars.Count > 0)
                {
                    var objSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(objSlot));
                    foreach (var (key, varName) in keyVars)
                    {
                        if (!vars.TryGetValue(varName, out var valVar) || (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def"))
                            return false;
                        EmitOpJsonCall(
                            objSlot,
                            "set",
                            key,
                            instructions,
                            dataRef: new FunctionParameterNode
                            {
                                Name = "data",
                                ParameterName = "data",
                                EntityType = "memory",
                                Index = valVar.Index
                            });
                    }
                    instructions.Add(CreatePushMemoryInstruction(objSlot));
                    return true;
                }

                if (HasSemicolonInsideJsonLiteral(trimmed))
                    throw new CompilationException("JSON literal cannot contain ';' inside object or array.", -1);

                if (TryParseJsonArgument(trimmed, out var jsonArg))
                {
                    var targetSlot = memorySlotCounter++;
                    var initJson = jsonArg is JsonArrayNode ? "[]" : "{}";
                    instructions.Add(CreatePushStringInstruction(initJson));
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    EmitBuildJsonNode(jsonArg, targetSlot, "", vars, instructions);
                    instructions.Add(CreatePushMemoryInstruction(targetSlot));
                    return true;
                }
            }

            var scanner = new Scanner(valueText);
            if (scanner.Current.Kind == TokenKind.StringLiteral)
            {
                var literal = scanner.Scan().Value;
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (!scanner.Current.IsEndOfInput)
                    return false;
                instructions.Add(CreatePushStringInstruction(literal));
                return true;
            }

            if (scanner.Current.Kind == TokenKind.Number && long.TryParse(scanner.Current.Value, out var intLiteral))
            {
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (!scanner.Current.IsEndOfInput)
                    return false;
                instructions.Add(CreatePushIntInstruction(intLiteral));
                return true;
            }

            if (scanner.Current.Kind == TokenKind.Identifier)
            {
                var baseName = scanner.Scan().Value;
                var segments = new List<string>();
                while (true)
                {
                    if (scanner.Current.Kind == TokenKind.Identifier && scanner.Current.Value == "!")
                        scanner.Scan();
                    if (scanner.Current.Kind != TokenKind.Dot)
                        break;
                    scanner.Scan();
                    if (scanner.Current.Kind != TokenKind.Identifier)
                        return false;
                    segments.Add(scanner.Scan().Value);
                }
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (!scanner.Current.IsEndOfInput)
                    return false;

                if (!vars.TryGetValue(baseName, out var baseVar))
                    throw new UndeclaredVariableException(baseName);
                if (baseVar.Kind != "memory" && baseVar.Kind != "stream" && baseVar.Kind != "def" && baseVar.Kind != "global")
                    return false;

                instructions.Add(baseVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(baseVar.Index) : CreatePushMemoryInstruction(baseVar.Index));
                foreach (var segment in segments)
                {
                    instructions.Add(CreatePushStringInstruction(segment));
                    instructions.Add(new InstructionNode { Opcode = "getobj" });
                }
                return true;
            }

            return false;
        }

        private bool TryCompileSchemaDeclaration(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var tableMatch = Regex.Match(
                line,
                @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:<>|>)?)\s*:\s*table\s*\{(?<body>.*)\}\s*;?\s*$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (tableMatch.Success)
            {
                var tableName = tableMatch.Groups["name"].Value.Trim();
                var body = tableMatch.Groups["body"].Value;
                var tableSlot = memorySlotCounter++;
                vars[tableName] = ("memory", tableSlot);

                instructions.Add(CreatePushStringInstruction(tableName));
                instructions.Add(CreatePushStringInstruction("table"));
                instructions.Add(CreatePushIntInstruction(2));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(tableSlot));

                foreach (var columnRaw in SplitTopLevelBySemicolon(body))
                {
                    if (!TryParseColumnSpec(columnRaw, out var columnName, out var columnType, out var modifiers))
                        continue;

                    instructions.Add(CreatePushMemoryInstruction(tableSlot));
                    instructions.Add(CreatePushStringInstruction(columnName));
                    instructions.Add(CreatePushStringInstruction(columnType));
                    foreach (var modifier in modifiers)
                        instructions.Add(CreatePushStringInstruction(modifier));
                    instructions.Add(CreatePushStringInstruction("column"));
                    instructions.Add(CreatePushIntInstruction(4 + modifiers.Count));
                    instructions.Add(new InstructionNode { Opcode = "def" });
                    instructions.Add(CreatePopMemoryInstruction(tableSlot));
                }

                return true;
            }

            var dbMatch = Regex.Match(
                line,
                @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:<>|>)?)\s*:\s*database\s*\{(?<body>.*)\}\s*;?\s*$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!dbMatch.Success)
                return false;

            var dbName = dbMatch.Groups["name"].Value.Trim();
            var dbBody = dbMatch.Groups["body"].Value;
            var dbSlot = memorySlotCounter++;
            vars[dbName] = ("memory", dbSlot);

            instructions.Add(CreatePushStringInstruction(dbName));
            instructions.Add(CreatePushStringInstruction("database"));
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(dbSlot));

            foreach (var tableRefRaw in SplitTopLevelBySemicolon(dbBody))
            {
                var tableRef = tableRefRaw.Trim();
                if (string.IsNullOrWhiteSpace(tableRef))
                    continue;
                if (!vars.TryGetValue(tableRef, out var tableVar))
                    throw new UndeclaredVariableException(tableRef);

                instructions.Add(CreatePushMemoryInstruction(dbSlot));
                instructions.Add(CreatePushMemoryInstruction(tableVar.Index));
                instructions.Add(CreatePushStringInstruction("table"));
                instructions.Add(CreatePushIntInstruction(3));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(dbSlot));
            }

            return true;
        }

        private static List<string> SplitTopLevelBySemicolon(string source)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
                return result;

            var sb = new StringBuilder();
            var parenDepth = 0;
            var inString = false;
            var escaped = false;

            for (var index = 0; index < source.Length; index++)
            {
                var ch = source[index];
                if (inString)
                {
                    sb.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (ch == '(') parenDepth++;
                if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);

                if (ch == ';' && parenDepth == 0)
                {
                    var item = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(item))
                        result.Add(item);
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);
            }

            var last = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(last))
                result.Add(last);
            return result;
        }

        private static bool TryParseColumnSpec(string columnRaw, out string columnName, out string columnType, out List<string> modifiers)
        {
            columnName = "";
            columnType = "";
            modifiers = new List<string>();

            var idx = columnRaw.IndexOf(':');
            if (idx <= 0)
                return false;

            columnName = columnRaw.Substring(0, idx).Trim();
            var spec = columnRaw.Substring(idx + 1).Trim();
            if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(spec))
                return false;

            var nullable = spec.EndsWith("?", StringComparison.Ordinal);
            if (nullable)
                spec = spec.Substring(0, spec.Length - 1).Trim();

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

        private bool TryCompileFunctionCall(string line, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var functionName = scanner.Scan().Value;
            var isStreamWait = false;

            // streamwait <handler> [ (args) ];
            if (string.Equals(functionName, "streamwait", StringComparison.OrdinalIgnoreCase))
            {
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                functionName = scanner.Scan().Value; // handler name, e.g. debug/print
                isStreamWait = true;

                // streamwait <handler>;  (no args)
                if (scanner.Current.Kind != TokenKind.LParen)
                {
                    while (scanner.Current.Kind == TokenKind.Semicolon)
                        scanner.Scan();
                    if (!scanner.Current.IsEndOfInput)
                        return false;

                    instructions.Add(CreatePushStringInstruction(functionName));
                    instructions.Add(CreatePushIntInstruction(0));
                    instructions.Add(new InstructionNode { Opcode = "streamwait" });
                    return true;
                }
            }

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();

            if (!string.Equals(functionName, "print", StringComparison.OrdinalIgnoreCase))
            {
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.RParen)
                    return false;
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                return scanner.Current.IsEndOfInput;
            }

            var printArgs = new List<Action>();
            if (scanner.Current.Kind != TokenKind.RParen)
            {
                while (true)
                {
                    if (scanner.Current.Kind == TokenKind.Colon)
                    {
                        scanner.Scan();
                        if (scanner.Current.Kind != TokenKind.Identifier)
                            return false;
                        var symbolicName = scanner.Scan().Value;
                        printArgs.Add(() =>
                        {
                            instructions.Add(CreatePushStringInstruction(":" + symbolicName));
                            instructions.Add(CreatePushIntInstruction(1));
                            instructions.Add(CreateCallInstruction("get"));
                        });
                    }
                    else if (scanner.Current.Kind == TokenKind.Identifier)
                    {
                        var identifier = scanner.Scan().Value;
                        if (vars.TryGetValue(identifier, out var argVar))
                        {
                            if (argVar.Kind == "global")
                                printArgs.Add(() => instructions.Add(CreatePushGlobalMemoryInstruction(argVar.Index)));
                            else
                                printArgs.Add(() => instructions.Add(CreatePushMemoryInstruction(argVar.Index)));
                        }
                        else
                            throw new UndeclaredVariableException(identifier);
                    }
                    else if (scanner.Current.Kind == TokenKind.StringLiteral)
                    {
                        var stringLiteral = scanner.Scan().Value;
                        printArgs.Add(() => instructions.Add(CreatePushStringInstruction(stringLiteral)));
                    }
                    else if (scanner.Current.Kind == TokenKind.Number)
                    {
                        var raw = scanner.Scan().Value;
                        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                            return false;
                        printArgs.Add(() => instructions.Add(CreatePushIntInstruction(number)));
                    }
                    else if (scanner.Current.Kind == TokenKind.Float)
                    {
                        var raw = scanner.Scan().Value;
                        // Push does not support float literal yet; keep user-visible value lossless.
                        printArgs.Add(() => instructions.Add(CreatePushStringInstruction(raw)));
                    }
                    else
                    {
                        return false;
                    }

                    if (scanner.Current.Kind == TokenKind.Comma)
                    {
                        scanner.Scan();
                        continue;
                    }

                    break;
                }
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (isStreamWait)
            {
                // streamwait print(...): push function name, then args, then arity, then streamwait.
                instructions.Add(CreatePushStringInstruction(functionName));
                foreach (var emit in printArgs)
                    emit();
                instructions.Add(CreatePushIntInstruction(printArgs.Count));
                instructions.Add(new InstructionNode { Opcode = "streamwait" });
                return true;
            }

            foreach (var emit in printArgs)
                emit();
            instructions.Add(CreatePushIntInstruction(printArgs.Count));
            instructions.Add(CreateCallInstruction("print"));
            // Результат print в виде statement не используется — очищаем стек.
            instructions.Add(CreatePopInstruction());
            return true;
        }

        private bool TryCompileAwaitStatement(string line, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "await"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var target = scanner.Scan().Value;
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(target, out var targetVar))
                throw new UndeclaredVariableException(target);
            if (targetVar.Kind != "stream" && targetVar.Kind != "def" && targetVar.Kind != "memory")
                return true;

            instructions.Add(CreatePushMemoryInstruction(targetVar.Index));
            instructions.Add(new InstructionNode { Opcode = "awaitobj" });
            // await как отдельный statement — результат не используется.
            instructions.Add(CreatePopInstruction());
            return true;
        }

        private sealed class ObjectLiteralArgumentSpec
        {
            public List<(string Key, string ValueIdentifier)> Properties { get; } = new List<(string Key, string ValueIdentifier)>();
        }

        private abstract class JsonArgumentNode { }

        private sealed class JsonObjectNode : JsonArgumentNode
        {
            public List<(string Key, JsonArgumentNode Value)> Properties { get; } = new List<(string Key, JsonArgumentNode Value)>();
        }

        private sealed class JsonArrayNode : JsonArgumentNode
        {
            public List<JsonArgumentNode> Items { get; } = new List<JsonArgumentNode>();
        }

        private sealed class JsonIdentifierNode : JsonArgumentNode
        {
            public string Name { get; set; } = "";
        }

        private sealed class JsonPrimitiveNode : JsonArgumentNode
        {
            public object? Value { get; set; }
        }

        private bool TryCompileMethodCall(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var objectName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.Dot)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var methodName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();

            string argText;
            var argIsStringLiteral = false;
            if (scanner.Current.Kind == TokenKind.StringLiteral)
            {
                argText = scanner.Scan().Value;
                argIsStringLiteral = true;
            }
            else
            {
                var start = scanner.Current.Start;
                var end = start;
                while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RParen)
                {
                    end = scanner.Current.End;
                    scanner.Scan();
                }
                argText = start < end ? line.Substring(start, end - start).Trim() : "";
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(objectName, out var objVar))
                throw new UndeclaredVariableException(objectName);

            if (LooksLikeJsonLiteral(argText))
            {
                // Argument inside (): allow object literal with ; (e.g. obj.method({ key: x; })). Only forbid ; in strict JSON.
                if (TryParseObjectLiteralWithVarRefs(argText, out var keyVars) && keyVars != null && keyVars.Count > 0)
                {
                    var objectSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(objectSlot));
                    foreach (var (key, varName) in keyVars)
                    {
                        if (!vars.TryGetValue(varName, out var valVar))
                            throw new UndeclaredVariableException(varName);
                        if (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def")
                            return false;
                        EmitOpJsonCall(
                            objectSlot,
                            "set",
                            key,
                            instructions,
                            dataRef: new FunctionParameterNode
                            {
                                Name = "data",
                                ParameterName = "data",
                                EntityType = "memory",
                                Index = valVar.Index
                            });
                    }
                    instructions.Add(CreatePushMemoryInstruction(objVar.Index));
                    instructions.Add(CreatePushMemoryInstruction(objectSlot));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = methodName }
                        }
                    });
                    // Вызов метода как statement — результат не используется.
                    instructions.Add(CreatePopInstruction());
                    return true;
                }
                if (HasSemicolonInsideJsonLiteral(argText))
                    throw new CompilationException("JSON literal cannot contain ';' inside object or array.", -1);
                if (TryParseJsonArgument(argText, out var jsonArg))
                {
                    var objectSlot = memorySlotCounter++;
                    var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                    instructions.Add(CreatePushStringInstruction(init));
                    instructions.Add(CreatePopMemoryInstruction(objectSlot));

                    EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions);

                    instructions.Add(CreatePushMemoryInstruction(objVar.Index));
                    instructions.Add(CreatePushMemoryInstruction(objectSlot));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = methodName }
                        }
                    });
                    // Вызов метода как statement — результат не используется.
                    instructions.Add(CreatePopInstruction());
                    return true;
                }
            }

            instructions.Add(CreatePushMemoryInstruction(objVar.Index));
            if (argIsStringLiteral)
            {
                instructions.Add(CreatePushStringInstruction(argText));
            }
            else if (vars.TryGetValue(argText, out var argVar))
                instructions.Add(CreatePushMemoryInstruction(argVar.Index));
            else
                instructions.Add(CreatePushStringInstruction(argText));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = methodName }
                }
            });
            // Вызов метода как statement — результат не используется.
            instructions.Add(CreatePopInstruction());
            return true;
        }

        private bool TryEmitMethodCallExpression(
            string expressionText,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            out string resultKind)
        {
            resultKind = "memory";

            // Специализированный разбор db.Table<>.where(_ => _.Col = var).max(_ => _.OtherCol)
            if (TryEmitWhereMaxExpression(expressionText, vars, targetSlot, ref memorySlotCounter, instructions, out var whereMaxKind))
            {
                resultKind = whereMaxKind;
                return true;
            }
            var scanner = new Scanner(expressionText);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var objectName = scanner.Scan().Value;
            var pathSegments = new List<string>();
            var methodName = "";
            while (scanner.Current.Kind == TokenKind.Dot)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var segment = scanner.Scan().Value;
                if (scanner.Current.Kind == TokenKind.LessThan)
                {
                    scanner.Scan();
                    if (scanner.Current.Kind == TokenKind.GreaterThan)
                        scanner.Scan();
                    segment += "<>";
                }
                if (scanner.Current.Kind == TokenKind.Dot)
                    pathSegments.Add(segment);
                else
                {
                    methodName = segment;
                    break;
                }
            }
            if (string.IsNullOrEmpty(methodName))
                return false;
            if (string.Equals(methodName, "history", StringComparison.OrdinalIgnoreCase))
                resultKind = "history";

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();

            var argIsStringLiteral = false;
            var hasArgument = scanner.Current.Kind != TokenKind.RParen;
            var argText = "";

            if (hasArgument)
            {
                if (scanner.Current.Kind == TokenKind.StringLiteral)
                {
                    argText = scanner.Scan().Value;
                    argIsStringLiteral = true;
                }
                else
                {
                    var start = scanner.Current.Start;
                    var end = start;
                    while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RParen)
                    {
                        end = scanner.Current.End;
                        scanner.Scan();
                    }
                    argText = start < end ? expressionText.Substring(start, end - start).Trim() : "";
                }
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(objectName, out var objVar))
                throw new UndeclaredVariableException(objectName);

            instructions.Add(objVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(objVar.Index) : CreatePushMemoryInstruction(objVar.Index));
            foreach (var segment in pathSegments)
            {
                instructions.Add(CreatePushStringInstruction(segment));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }

            if (!hasArgument)
            {
                instructions.Add(CreatePushIntInstruction(0));
                instructions.Add(new InstructionNode
                {
                    Opcode = "callobj",
                    Parameters = new List<ParameterNode>
                    {
                        new FunctionNameParameterNode { FunctionName = methodName }
                    }
                });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            if ((string.Equals(methodName, "any", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, "where", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, "find", StringComparison.OrdinalIgnoreCase)) &&
                TryParseLambdaExpression(argText, out var lambdaParameters, out var lambdaBody))
            {
                if (TryEmitLambdaBodyEqualsMember(lambdaParameters, lambdaBody, vars, ref memorySlotCounter, instructions))
                {
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = methodName }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (string.Equals(methodName, "max", StringComparison.OrdinalIgnoreCase) &&
                TryParseLambdaExpression(argText, out var maxLambdaParameters, out var maxLambdaBody))
            {
                if (TryEmitLambdaBodyMemberAccess(maxLambdaParameters, maxLambdaBody, instructions))
                {
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = "max" }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (LooksLikeJsonLiteral(argText))
            {
                if (TryParseObjectLiteralWithVarRefs(argText, out var keyVars) && keyVars != null && keyVars.Count > 0)
                {
                    var objectSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(objectSlot));
                    foreach (var (key, varName) in keyVars)
                    {
                        if (!vars.TryGetValue(varName, out var valVar))
                            throw new UndeclaredVariableException(varName);
                        if (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def")
                            return false;
                        EmitOpJsonCall(
                            objectSlot,
                            "set",
                            key,
                            instructions,
                            dataRef: new FunctionParameterNode
                            {
                                Name = "data",
                                ParameterName = "data",
                                EntityType = "memory",
                                Index = valVar.Index
                            });
                    }
                    instructions.Add(CreatePushMemoryInstruction(objectSlot));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = methodName }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }

                if (HasSemicolonInsideJsonLiteral(argText))
                    throw new CompilationException("JSON literal cannot contain ';' inside object or array.", -1);

                if (TryParseJsonArgument(argText, out var jsonArg))
                {
                    var objectSlot = memorySlotCounter++;
                    var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                    instructions.Add(CreatePushStringInstruction(init));
                    instructions.Add(CreatePopMemoryInstruction(objectSlot));
                    EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions);
                    instructions.Add(CreatePushMemoryInstruction(objectSlot));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = methodName }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (argIsStringLiteral)
                instructions.Add(CreatePushStringInstruction(argText));
            else if (vars.TryGetValue(argText, out var argVar))
                instructions.Add(CreatePushMemoryInstruction(argVar.Index));
            else
                instructions.Add(CreatePushStringInstruction(argText));

            instructions.Add(CreatePushIntInstruction(0));
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = methodName }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));
            return true;
        }

        /// <summary>
        /// Специализированная поддержка конструкций:
        /// db1.Message<>.where(_ => _.ChatId = channelTitle).max(_ => _.MessageId)
        /// Компилируется в отдельные вызовы Table.where(whereLambda) и Table.max(selectorLambda).
        /// </summary>
        private bool TryEmitWhereMaxExpression(
            string expressionText,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            out string resultKind)
        {
            resultKind = "memory";
            if (string.IsNullOrWhiteSpace(expressionText))
                return false;

            var trimmed = expressionText.Trim();
            while (trimmed.EndsWith(";", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();

            if (!trimmed.Contains(".where", StringComparison.OrdinalIgnoreCase) ||
                !trimmed.Contains(".max", StringComparison.OrdinalIgnoreCase))
                return false;

            var whereSplit = trimmed.Split(new[] { ".where", ".Where" }, 2, StringSplitOptions.None);
            if (whereSplit.Length != 2)
                return false;
            var head = whereSplit[0].Trim();
            var rest = whereSplit[1];

            var maxSplit = rest.Split(new[] { ".max", ".Max" }, 2, StringSplitOptions.None);
            if (maxSplit.Length != 2)
                return false;

            var whereCall = maxSplit[0].Trim();
            var maxCall = maxSplit[1].Trim();

            if (!whereCall.StartsWith("(", StringComparison.Ordinal))
                whereCall = "(" + whereCall;
            if (!whereCall.EndsWith(")", StringComparison.Ordinal))
                whereCall += ")";
            if (!maxCall.StartsWith("(", StringComparison.Ordinal))
                maxCall = "(" + maxCall;
            if (!maxCall.EndsWith(")", StringComparison.Ordinal))
                maxCall += ")";

            var headScanner = new Scanner(head);
            if (headScanner.Current.Kind != TokenKind.Identifier)
                return false;
            var objectName = headScanner.Scan().Value;
            var pathSegments = new List<string>();
            while (headScanner.Current.Kind == TokenKind.Dot)
            {
                headScanner.Scan();
                if (headScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var segment = headScanner.Scan().Value;
                if (headScanner.Current.Kind == TokenKind.LessThan)
                {
                    headScanner.Scan();
                    if (headScanner.Current.Kind == TokenKind.GreaterThan)
                        headScanner.Scan();
                    segment += "<>";
                }
                pathSegments.Add(segment);
            }

            if (!vars.TryGetValue(objectName, out var objVar))
                throw new UndeclaredVariableException(objectName);

            if (!TryParseLambdaExpression(whereCall.Trim(' ', '\t', '\r', '\n', '(', ')'), out var whereParams, out var whereBody))
                return false;

            // table, затем where-лямбда
            instructions.Add(objVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(objVar.Index) : CreatePushMemoryInstruction(objVar.Index));
            foreach (var segment in pathSegments)
            {
                instructions.Add(CreatePushStringInstruction(segment));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }

            if (!TryEmitLambdaBodyEqualsMember(whereParams, whereBody, vars, ref memorySlotCounter, instructions))
                return false;

            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "where" }
                }
            });

            if (!TryParseLambdaExpression(maxCall.Trim(' ', '\t', '\r', '\n', '(', ')'), out var maxParams, out var maxBody))
                return false;
            if (!TryEmitLambdaBodyMemberAccess(maxParams, maxBody, instructions))
                return false;

            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "max" }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));

            resultKind = "memory";
            return true;
        }

        private static bool TryParseLambdaExpression(string text, out List<string> parameters, out string body)
        {
            parameters = new List<string>();
            body = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var parts = text.Split(new[] { "=>" }, 2, StringSplitOptions.None);
            if (parts.Length != 2)
                return false;

            var left = parts[0].Trim();
            body = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(body))
                return false;

            if (left.StartsWith("(", StringComparison.Ordinal) && left.EndsWith(")", StringComparison.Ordinal) && left.Length >= 2)
                left = left.Substring(1, left.Length - 2).Trim();

            foreach (var rawParameter in left.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Regex.IsMatch(rawParameter, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    return false;
                parameters.Add(rawParameter);
            }

            return parameters.Count > 0;
        }

        /// <summary>Emits lambda body for pattern param.Member = varName: expr, duplicate lambda arg0, getobj Member, pop temp, push temp, push varSlot, equals, lambda, defexpr.</summary>
        private static bool TryEmitLambdaBodyEqualsMember(
            List<string> lambdaParameters,
            string body,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            if (string.IsNullOrWhiteSpace(body) || lambdaParameters.Count != 1)
                return false;

            var parameterName = Regex.Escape(lambdaParameters[0]);
            var m = Regex.Match(
                body.Trim(),
                $@"{parameterName}\s*\.\s*(\w+)\s*=\s*(\w+)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            var memberName = m.Groups[1].Value;
            var rightVarName = m.Groups[2].Value;
            if (!vars.TryGetValue(rightVarName, out var rightVar))
                return false;
            var tempSlot = memorySlotCounter++;

            instructions.Add(new InstructionNode { Opcode = "expr" });
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushStringInstruction(memberName));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(tempSlot));
            instructions.Add(CreatePushMemoryInstruction(tempSlot));
            if (rightVar.Kind == "global")
                instructions.Add(CreatePushGlobalMemoryInstruction(rightVar.Index));
            else
                instructions.Add(CreatePushMemoryInstruction(rightVar.Index));
            instructions.Add(new InstructionNode { Opcode = "equals" });
            instructions.Add(new InstructionNode { Opcode = "lambda" });
            instructions.Add(new InstructionNode { Opcode = "defexpr" });

            return true;
        }

        private static bool TryEmitLambdaBodyMemberAccess(
            List<string> lambdaParameters,
            string body,
            List<InstructionNode> instructions)
        {
            if (string.IsNullOrWhiteSpace(body) || lambdaParameters.Count != 1)
                return false;

            var parameterName = Regex.Escape(lambdaParameters[0]);
            var m = Regex.Match(
                body.Trim(),
                $@"^{parameterName}\s*\.\s*(\w+)\s*$",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                return false;

            var memberName = m.Groups[1].Value;
            instructions.Add(new InstructionNode { Opcode = "expr" });
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushStringInstruction(memberName));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(new InstructionNode { Opcode = "lambda" });
            instructions.Add(new InstructionNode { Opcode = "defexpr" });
            return true;
        }

        /// <summary>Parses #"text {expr1} more {expr2}" into template "text {0} more {1}" and list [expr1, expr2].</summary>
        private static bool TryParseFormatStringLiteral(string trimmed, out string formatTemplate, out List<string> expressions)
        {
            formatTemplate = "";
            expressions = new List<string>();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 4)
                return false;
            if (!trimmed.StartsWith("#\"", StringComparison.Ordinal) || trimmed[trimmed.Length - 1] != '"')
                return false;

            var inner = trimmed.Substring(2, trimmed.Length - 3);
            var templateBuilder = new StringBuilder();
            var expressionBuilder = new StringBuilder();
            var inExpression = false;
            var parenDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var quote = '\0';
            var escaped = false;

            for (var i = 0; i < inner.Length; i++)
            {
                var ch = inner[i];
                if (!inExpression)
                {
                    if (ch == '{')
                    {
                        inExpression = true;
                        expressionBuilder.Clear();
                        parenDepth = 0;
                        bracketDepth = 0;
                        inString = false;
                        quote = '\0';
                        escaped = false;
                        continue;
                    }

                    templateBuilder.Append(ch);
                    continue;
                }

                if (inString)
                {
                    expressionBuilder.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == '(')
                {
                    parenDepth++;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == ')')
                {
                    if (parenDepth == 0)
                        return false;

                    parenDepth--;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == ']')
                {
                    if (bracketDepth == 0)
                        return false;

                    bracketDepth--;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == '}' && parenDepth == 0 && bracketDepth == 0)
                {
                    var expression = expressionBuilder.ToString().Trim();
                    if (string.IsNullOrEmpty(expression))
                        return false;

                    expressions.Add(expression);
                    templateBuilder.Append('{').Append(expressions.Count - 1).Append('}');
                    inExpression = false;
                    continue;
                }

                expressionBuilder.Append(ch);
            }

            if (inExpression || inString || parenDepth != 0 || bracketDepth != 0)
                return false;

            formatTemplate = templateBuilder.ToString();
            return true;
        }

        private static bool LooksLikeJsonLiteral(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var trimmed = text.TrimStart();
            return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
        }

        /// <summary>Returns true if there is a semicolon inside the top-level JSON object or array body (between matching { } or [ ]).</summary>
        private static bool HasSemicolonInsideJsonLiteral(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var t = text.Trim();
            if (t.Length < 2 || (t[0] != '{' && t[0] != '['))
                return false;
            var depth = 1;
            var inString = false;
            var escape = false;
            var quote = '\0';
            for (var i = 1; i < t.Length && depth > 0; i++)
            {
                var ch = t[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (ch == '\\') { escape = true; continue; }
                    if (ch == quote) { inString = false; continue; }
                    continue;
                }
                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }
                if (ch == '{' || ch == '[') { depth++; continue; }
                if (ch == '}' || ch == ']') { depth--; continue; }
                if (ch == ';' && depth >= 1)
                    return true;
            }
            return false;
        }

        private static string ExtractRemainingExpressionText(string sourceLine, int startIndex)
        {
            if (string.IsNullOrEmpty(sourceLine) || startIndex < 0 || startIndex >= sourceLine.Length)
                return "";

            var rhs = sourceLine.Substring(startIndex).Trim();
            while (rhs.EndsWith(";", StringComparison.Ordinal))
                rhs = rhs.Substring(0, rhs.Length - 1).TrimEnd();
            return rhs;
        }

        private static string AppendPath(string basePath, string segment)
        {
            if (string.IsNullOrEmpty(basePath))
                return segment;
            if (segment.StartsWith("[", StringComparison.Ordinal))
                return basePath + segment;
            return basePath + "." + segment;
        }

        private static string JsonLiteralToRaw(object? value)
        {
            if (value == null)
                return "null";
            if (value is bool b)
                return b ? "true" : "false";
            if (value is long l)
                return l.ToString(CultureInfo.InvariantCulture);
            if (value is double d)
                return d.ToString(CultureInfo.InvariantCulture);
            if (value is string s)
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return "\"" + value.ToString()?.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void EmitOpJsonCall(long sourceIndex, string operation, string path, List<InstructionNode> instructions, FunctionParameterNode? dataRef = null, string? dataJson = null)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "opjson" },
                new FunctionParameterNode { Name = "source", ParameterName = "source", EntityType = "memory", Index = sourceIndex },
                new StringParameterNode { Name = "operation", Value = operation },
                new StringParameterNode { Name = "path", Value = path }
            };

            if (dataRef != null)
                parameters.Add(dataRef);
            if (dataJson != null)
                parameters.Add(new StringParameterNode { Name = "dataJson", Value = dataJson });

            instructions.Add(new InstructionNode { Opcode = "call", Parameters = parameters });
            // opjson возвращает корень JSON на вершину стека.
            // Везде, где мы его эмитим из компилятора, он используется только по side‑effect'у,
            // поэтому сразу очищаем стек от результата.
            instructions.Add(CreatePopInstruction());
        }

        private static void EmitBuildJsonNode(JsonArgumentNode node, long sourceIndex, string path, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            if (node is JsonObjectNode objNode)
            {
                if (!string.IsNullOrEmpty(path))
                    EmitOpJsonCall(sourceIndex, "ensureObject", path, instructions);

                foreach (var (key, value) in objNode.Properties)
                {
                    var childPath = AppendPath(path, key);
                    EmitBuildJsonNode(value, sourceIndex, childPath, vars, instructions);
                }
                return;
            }

            if (node is JsonArrayNode arrNode)
            {
                if (!string.IsNullOrEmpty(path))
                    EmitOpJsonCall(sourceIndex, "ensureArray", path, instructions);

                for (var i = 0; i < arrNode.Items.Count; i++)
                {
                    var item = arrNode.Items[i];
                    var indexPath = AppendPath(path, $"[{i}]");

                    if (item is JsonObjectNode)
                    {
                        EmitOpJsonCall(sourceIndex, "append", path, instructions, dataJson: "{}");
                        EmitBuildJsonNode(item, sourceIndex, indexPath, vars, instructions);
                    }
                    else if (item is JsonArrayNode)
                    {
                        EmitOpJsonCall(sourceIndex, "append", path, instructions, dataJson: "[]");
                        EmitBuildJsonNode(item, sourceIndex, indexPath, vars, instructions);
                    }
                    else if (item is JsonIdentifierNode idNode && vars.TryGetValue(idNode.Name, out var valueVar))
                    {
                        EmitOpJsonCall(
                            sourceIndex,
                            "append",
                            path,
                            instructions,
                            dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = valueVar.Index });
                    }
                    else if (item is JsonPrimitiveNode primitiveItem)
                    {
                        EmitOpJsonCall(sourceIndex, "append", path, instructions, dataJson: JsonLiteralToRaw(primitiveItem.Value));
                    }
                }
                return;
            }

            if (node is JsonIdentifierNode id && vars.TryGetValue(id.Name, out var dataVar))
            {
                EmitOpJsonCall(
                    sourceIndex,
                    "set",
                    path,
                    instructions,
                    dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = dataVar.Index });
                return;
            }

            if (node is JsonPrimitiveNode primitive)
            {
                EmitOpJsonCall(sourceIndex, "set", path, instructions, dataJson: JsonLiteralToRaw(primitive.Value));
            }
        }

        private static bool TryParseJsonArgument(string text, out JsonArgumentNode root)
        {
            root = new JsonPrimitiveNode { Value = null };
            var i = 0;
            SkipWs(text, ref i);
            if (!TryParseJsonValue(text, ref i, out root))
                return false;
            SkipWs(text, ref i);
            return i == text.Length;
        }

        private static bool TryParseJsonValue(string text, ref int i, out JsonArgumentNode node)
        {
            node = new JsonPrimitiveNode { Value = null };
            SkipWs(text, ref i);
            if (i >= text.Length)
                return false;

            var ch = text[i];
            if (ch == '{')
                return TryParseJsonObject(text, ref i, out node);
            if (ch == '[')
                return TryParseJsonArray(text, ref i, out node);
            if (ch == '"')
            {
                if (!TryParseQuotedString(text, ref i, out var str))
                    return false;
                node = new JsonPrimitiveNode { Value = str };
                return true;
            }
            if (ch == '-' || char.IsDigit(ch))
            {
                if (!TryParseNumber(text, ref i, out var num))
                    return false;
                node = new JsonPrimitiveNode { Value = num };
                return true;
            }

            if (!TryParseIdentifier(text, ref i, out var ident))
                return false;

            if (string.Equals(ident, "true", StringComparison.OrdinalIgnoreCase))
            {
                node = new JsonPrimitiveNode { Value = true };
                return true;
            }
            if (string.Equals(ident, "false", StringComparison.OrdinalIgnoreCase))
            {
                node = new JsonPrimitiveNode { Value = false };
                return true;
            }
            if (string.Equals(ident, "null", StringComparison.OrdinalIgnoreCase))
            {
                node = new JsonPrimitiveNode { Value = null };
                return true;
            }

            node = new JsonIdentifierNode { Name = ident };
            return true;
        }

        private static bool TryParseJsonObject(string text, ref int i, out JsonArgumentNode node)
        {
            node = new JsonPrimitiveNode { Value = null };
            if (i >= text.Length || text[i] != '{')
                return false;
            i++;
            SkipWs(text, ref i);

            var obj = new JsonObjectNode();
            if (i < text.Length && text[i] == '}')
            {
                i++;
                node = obj;
                return true;
            }

            while (i < text.Length)
            {
                string key;
                if (i < text.Length && text[i] == '"')
                {
                    if (!TryParseQuotedString(text, ref i, out key))
                        return false;
                }
                else
                {
                    if (!TryParseIdentifier(text, ref i, out key))
                        return false;
                }

                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != ':')
                    return false;
                i++;

                if (!TryParseJsonValue(text, ref i, out var valueNode))
                    return false;
                obj.Properties.Add((key, valueNode));

                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipWs(text, ref i);
                    continue;
                }
                if (i < text.Length && text[i] == '}')
                {
                    i++;
                    node = obj;
                    return true;
                }
                return false;
            }

            return false;
        }

        private static bool TryParseJsonArray(string text, ref int i, out JsonArgumentNode node)
        {
            node = new JsonPrimitiveNode { Value = null };
            if (i >= text.Length || text[i] != '[')
                return false;
            i++;
            SkipWs(text, ref i);

            var arr = new JsonArrayNode();
            if (i < text.Length && text[i] == ']')
            {
                i++;
                node = arr;
                return true;
            }

            while (i < text.Length)
            {
                if (!TryParseJsonValue(text, ref i, out var valueNode))
                    return false;
                arr.Items.Add(valueNode);

                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipWs(text, ref i);
                    continue;
                }
                if (i < text.Length && text[i] == ']')
                {
                    i++;
                    node = arr;
                    return true;
                }
                return false;
            }

            return false;
        }

        private static void SkipWs(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
        }

        private static bool TryParseIdentifier(string text, ref int i, out string ident)
        {
            ident = "";
            if (i >= text.Length || !(char.IsLetter(text[i]) || text[i] == '_'))
                return false;
            var start = i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;
            ident = text.Substring(start, i - start);
            return true;
        }

        private static bool TryParseQuotedString(string text, ref int i, out string value)
        {
            value = "";
            if (i >= text.Length || text[i] != '"')
                return false;
            i++;
            var sb = new StringBuilder();
            while (i < text.Length)
            {
                var ch = text[i++];
                if (ch == '\\' && i < text.Length)
                {
                    var esc = text[i++];
                    sb.Append(esc switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => esc
                    });
                    continue;
                }
                if (ch == '"')
                {
                    value = sb.ToString();
                    return true;
                }
                sb.Append(ch);
            }
            return false;
        }

        private static bool TryParseNumber(string text, ref int i, out object number)
        {
            number = 0L;
            var start = i;
            if (text[i] == '-') i++;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && char.IsDigit(text[i])) i++;
            }
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                i++;
                if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                while (i < text.Length && char.IsDigit(text[i])) i++;
            }

            var raw = text.Substring(start, i - start);
            if (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0)
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    number = d;
                    return true;
                }
                return false;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                number = l;
                return true;
            }
            return false;
        }

        private static bool TryParseObjectLiteralArgument(string text, out ObjectLiteralArgumentSpec spec)
        {
            spec = new ObjectLiteralArgumentSpec();
            var trimmed = (text ?? "").Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return false;

            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (inner.Length == 0)
                return true;

            var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length != 2)
                    return false;
                var key = kv[0].Trim();
                var valueIdentifier = kv[1].Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(valueIdentifier))
                    return false;
                spec.Properties.Add((key, valueIdentifier));
            }

            return true;
        }

        private void EmitStreamDeclaration(string streamName, IReadOnlyList<string> elementTypes, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var baseSlot = memorySlotCounter++;
            var streamSlot = memorySlotCounter++;
            vars[streamName] = ("stream", streamSlot);
            instructions.Add(CreatePushTypeInstruction("stream"));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(baseSlot));
            instructions.Add(CreatePushMemoryInstruction(baseSlot));
            foreach (var elementType in elementTypes)
                instructions.Add(CreatePushTypeInstruction(elementType));
            instructions.Add(CreatePushIntInstruction(elementTypes.Count));
            instructions.Add(new InstructionNode { Opcode = "defgen" });
            instructions.Add(CreatePopMemoryInstruction(streamSlot));
        }

        private static bool TryParseStreamWaitForLoop(
            string line,
            out bool isSync,
            out string streamName,
            out string waitType,
            out string deltaVarName,
            out string aggregateVarName,
            out string bodyText)
        {
            isSync = false;
            streamName = "";
            waitType = "";
            deltaVarName = "";
            aggregateVarName = "";
            bodyText = "";

            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "for"))
                return false;
            scanner.Scan();
            if (IsIdentifier(scanner.Current, "sync"))
            {
                isSync = true;
                scanner.Scan();
            }
            if (!IsIdentifier(scanner.Current, "streamwait"))
                return false;
            scanner.Scan();
            if (!IsIdentifier(scanner.Current, "by"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            waitType = scanner.Scan().Value;

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            streamName = scanner.Scan().Value;

            if (scanner.Current.Kind != TokenKind.Comma)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            deltaVarName = scanner.Scan().Value;

            if (scanner.Current.Kind == TokenKind.Comma)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                aggregateVarName = scanner.Scan().Value;
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();

            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;

            var braceStart = scanner.Current.Start;
            var braceDepth = 0;
            var bodyStart = -1;
            var bodyEnd = -1;
            var inString = false;
            var escaped = false;
            var quote = '\0';

            for (var i = braceStart; i < line.Length; i++)
            {
                var ch = line[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '{')
                {
                    braceDepth++;
                    if (braceDepth == 1)
                        bodyStart = i + 1;
                    continue;
                }

                if (ch != '}')
                    continue;

                braceDepth--;
                if (braceDepth == 0)
                {
                    bodyEnd = i;
                    break;
                }
            }

            if (bodyStart < 0 || bodyEnd < bodyStart)
                return false;

            for (var i = bodyEnd + 1; i < line.Length; i++)
            {
                if (!char.IsWhiteSpace(line[i]) && line[i] != ';')
                    return false;
            }

            bodyText = line.Substring(bodyStart, bodyEnd - bodyStart);
            return true;
        }

        private static List<string> SplitStatementLines(string source)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
                return lines;

            var sb = new StringBuilder();
            var inString = false;
            var escaped = false;
            var parenDepth = 0;
            var braceDepth = 0;
            var bracketDepth = 0;

            for (var index = 0; index < source.Length; index++)
            {
                var ch = source[index];
                if (inString)
                {
                    sb.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (ch == '(') parenDepth++;
                if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);
                if (ch == '{') braceDepth++;
                if (ch == '}') braceDepth = Math.Max(0, braceDepth - 1);
                if (ch == '[') bracketDepth++;
                if (ch == ']') bracketDepth = Math.Max(0, bracketDepth - 1);

                var topLevel = parenDepth == 0 && braceDepth == 0 && bracketDepth == 0;
                if ((ch == ';' || ch == '\n' || ch == '\r') && topLevel)
                {
                    var statement = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(statement))
                        lines.Add(statement);
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);

                if (ch == '}' && topLevel)
                {
                    var nextIndex = index + 1;
                    while (nextIndex < source.Length && char.IsWhiteSpace(source[nextIndex]))
                        nextIndex++;

                    if (nextIndex < source.Length && source[nextIndex] != ';')
                    {
                        var tail = source.Substring(nextIndex);
                        if (!tail.StartsWith("else", StringComparison.OrdinalIgnoreCase))
                        {
                            var statement = sb.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(statement))
                                lines.Add(statement);
                            sb.Clear();
                        }
                    }
                }
            }

            var last = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(last))
                lines.Add(last);

            return lines;
        }

        private static InstructionNode CreateCallInstruction(string functionName, params FunctionParameterNode[] args)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = functionName }
            };
            foreach (var arg in args)
                parameters.Add(arg);
            return new InstructionNode { Opcode = "call", Parameters = parameters };
        }

        private static InstructionNode CreateAsyncCallInstruction(string functionName, params FunctionParameterNode[] args)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = functionName }
            };
            foreach (var arg in args)
                parameters.Add(arg);
            return new InstructionNode { Opcode = "acall", Parameters = parameters };
        }

        private static InstructionNode CreateLabelInstruction(string label)
        {
            return new InstructionNode
            {
                Opcode = "label",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = label }
                }
            };
        }

        private static InstructionNode CreateCmpInstruction(long leftMemoryIndex, long rightLiteral)
        {
            return new InstructionNode
            {
                Opcode = "cmp",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "left", Index = leftMemoryIndex },
                    new IndexParameterNode { Name = "int", Value = rightLiteral }
                }
            };
        }

        private static InstructionNode CreateJumpIfEqualInstruction(string label)
        {
            return new InstructionNode
            {
                Opcode = "je",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = label }
                }
            };
        }

        private static InstructionNode CreateJumpInstruction(string label)
        {
            return new InstructionNode
            {
                Opcode = "jmp",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = label }
                }
            };
        }

        private static FunctionParameterNode CreateEntityCallParameter(string paramName, string entityType, long index)
        {
            return new FunctionParameterNode
            {
                Name = paramName,
                ParameterName = paramName,
                EntityType = entityType,
                Index = index
            };
        }

        private static InstructionNode CreatePushMemoryInstruction(long index)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "index", Index = index }
                }
            };
        }

        private static InstructionNode CreatePushGlobalMemoryInstruction(long index)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "global", Index = index }
                }
            };
        }

        private static InstructionNode CreatePushStringInstruction(string value)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new StringParameterNode { Name = "string", Value = value }
                }
            };
        }

        private static InstructionNode CreatePushIntInstruction(long value)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "int", Value = value }
                }
            };
        }

        private static InstructionNode CreatePushTypeInstruction(string typeName)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new TypeLiteralParameterNode { TypeName = typeName }
                }
            };
        }

        private static InstructionNode CreatePushLambdaArgInstruction(int argIndex)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new LambdaArgParameterNode { Name = "lambda", Index = argIndex }
                }
            };
        }

        private static InstructionNode CreatePopInstruction()
        {
            return new InstructionNode
            {
                Opcode = "pop",
                Parameters = new List<ParameterNode>()
            };
        }

        private static InstructionNode CreatePopMemoryInstruction(long index)
        {
            return new InstructionNode
            {
                Opcode = "pop",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "index", Index = index }
                }
            };
        }

        private bool IsDeclarationStatement(string line)
        {
            return TryParseStreamDeclaration(line, out _, out _) ||
                   TryParseEntityDeclaration(line, out _, out _, out _);
        }

        private bool TryParseVarKeywordOnly(string line)
        {
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "var"))
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        private bool TryStripInlineVarPrefix(string line, out string declarationLine)
        {
            declarationLine = "";
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "var"))
                return false;
            scanner.Scan();
            if (scanner.Current.IsEndOfInput)
                return false;
            var start = scanner.Current.Start;
            var end = start;
            while (!scanner.Current.IsEndOfInput)
            {
                end = scanner.Current.End;
                scanner.Scan();
            }
            if (end <= start || start >= line.Length)
                return false;
            declarationLine = line.Substring(start, end - start).Trim();
            return !string.IsNullOrEmpty(declarationLine);
        }

        private bool TryParseStreamDeclaration(string line, out string streamName, out List<string> elementTypes)
        {
            streamName = string.Empty;
            elementTypes = new List<string>();
            var previous = _scanner;
            _scanner = new Scanner(line);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                    CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                streamName = CurrentScanner.Scan().Value;

                if (CurrentScanner.Current.Kind != TokenKind.Colon || CurrentScanner.Watch(1)?.Kind != TokenKind.Assign)
                    return false;
                CurrentScanner.Scan();
                CurrentScanner.Scan();

                if (!IsIdentifier(CurrentScanner.Current, "stream"))
                    return false;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.LessThan)
                    return false;
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                elementTypes.Add(CurrentScanner.Scan().Value.ToLowerInvariant());

                while (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                        return false;
                    elementTypes.Add(CurrentScanner.Scan().Value.ToLowerInvariant());
                }

                if (CurrentScanner.Current.Kind != TokenKind.GreaterThan)
                    return false;
                CurrentScanner.Scan();
                SkipSemicolon();
                return CurrentScanner.Current.IsEndOfInput && elementTypes.Count > 0;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseEntityDeclaration(string sourceLine, out string varName, out string varType, out string initText)
        {
            varName = "";
            varType = "";
            initText = "";
            var previous = _scanner;
            _scanner = new Scanner(sourceLine);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                    CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                varName = CurrentScanner.Scan().Value;

                if (CurrentScanner.Current.Kind != TokenKind.Colon)
                    return false;
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                varType = CurrentScanner.Scan().Value.ToLowerInvariant();
                if (varType != "vertex" && varType != "relation" && varType != "shape")
                    return false;
                if (CurrentScanner.Current.Kind != TokenKind.Assign)
                    return false;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.IsEndOfInput)
                    return false;
                var start = CurrentScanner.Current.Start;
                var end = start;
                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Semicolon)
                {
                    end = CurrentScanner.Current.End;
                    CurrentScanner.Scan();
                }
                initText = start < end ? sourceLine.Substring(start, end - start).Trim() : "";
                if (string.IsNullOrWhiteSpace(initText))
                    return false;

                SkipSemicolon();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseVertexObject(string initValue, out VertexInitSpec spec)
        {
            spec = new VertexInitSpec();
            var previous = _scanner;
            _scanner = new Scanner(initValue);
            try
            {
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    return false;
                CurrentScanner.Scan();

                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
                {
                    if (CurrentScanner.Current.Kind == TokenKind.Comma)
                    {
                        CurrentScanner.Scan();
                        continue;
                    }

                    if (IsIdentifier(CurrentScanner.Current, "DIM"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        var dims = ParseLongArray();
                        if (dims.Count > 0)
                        {
                            spec.Dimensions.Clear();
                            spec.Dimensions.AddRange(dims);
                        }
                        continue;
                    }

                    if (IsIdentifier(CurrentScanner.Current, "W"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        if (TryParseDoubleToken(out var weight))
                            spec.Weight = weight;
                        continue;
                    }

                    if (IsIdentifier(CurrentScanner.Current, "DATA"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        if (IsIdentifier(CurrentScanner.Current, "BIN"))
                        {
                            CurrentScanner.Scan();
                            TryConsumeColon();
                            if (CurrentScanner.Current.Kind == TokenKind.StringLiteral)
                                spec.BinaryData = CurrentScanner.Scan().Value;
                        }
                        else if (CurrentScanner.Current.Kind == TokenKind.StringLiteral)
                        {
                            spec.TextData = CurrentScanner.Scan().Value;
                        }
                        continue;
                    }

                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    return false;
                CurrentScanner.Scan();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseRelationObject(string initValue, out RelationInitSpec spec)
        {
            spec = new RelationInitSpec();
            var previous = _scanner;
            _scanner = new Scanner(initValue);
            try
            {
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    return false;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                    spec.From = CurrentScanner.Scan().Value;
                if (CurrentScanner.Current.Kind != TokenKind.Assign || CurrentScanner.Watch(1)?.Kind != TokenKind.GreaterThan)
                    return false;
                CurrentScanner.Scan();
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                spec.To = CurrentScanner.Scan().Value;

                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
                {
                    if (CurrentScanner.Current.Kind == TokenKind.Comma)
                    {
                        CurrentScanner.Scan();
                        continue;
                    }
                    if (IsIdentifier(CurrentScanner.Current, "W"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        if (TryParseDoubleToken(out var weight))
                            spec.Weight = weight;
                        continue;
                    }
                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    return false;
                CurrentScanner.Scan();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseShapeObject(string initValue, out ShapeInitSpec spec)
        {
            spec = new ShapeInitSpec();
            var previous = _scanner;
            _scanner = new Scanner(initValue);
            try
            {
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    return false;
                CurrentScanner.Scan();

                if (IsIdentifier(CurrentScanner.Current, "VERT"))
                {
                    CurrentScanner.Scan();
                    TryConsumeColon();
                    if (CurrentScanner.Current.Kind != TokenKind.LBracket)
                        return false;
                    CurrentScanner.Scan();
                    while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBracket)
                    {
                        if (CurrentScanner.Current.Kind == TokenKind.Comma)
                        {
                            CurrentScanner.Scan();
                            continue;
                        }
                        if (CurrentScanner.Current.Kind == TokenKind.LBrace && TryParseInlineVertex(out var inlineVertex))
                        {
                            spec.InlineVertices.Add(inlineVertex);
                            continue;
                        }
                        CurrentScanner.Scan();
                    }
                    if (CurrentScanner.Current.Kind != TokenKind.RBracket)
                        return false;
                    CurrentScanner.Scan();
                }
                else if (CurrentScanner.Current.Kind == TokenKind.LBracket)
                {
                    CurrentScanner.Scan();
                    while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBracket)
                    {
                        if (CurrentScanner.Current.Kind == TokenKind.Comma)
                        {
                            CurrentScanner.Scan();
                            continue;
                        }
                        if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                            spec.VertexNames.Add(CurrentScanner.Scan().Value);
                        else
                            CurrentScanner.Scan();
                    }
                    if (CurrentScanner.Current.Kind != TokenKind.RBracket)
                        return false;
                    CurrentScanner.Scan();
                }
                else
                {
                    return false;
                }

                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
                    CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    return false;
                CurrentScanner.Scan();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseInlineVertex(out InlineVertexSpec spec)
        {
            spec = new InlineVertexSpec();
            if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                return false;
            CurrentScanner.Scan();

            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    continue;
                }
                if (IsIdentifier(CurrentScanner.Current, "DIM"))
                {
                    CurrentScanner.Scan();
                    TryConsumeColon();
                    var dims = ParseLongArray();
                    if (dims.Count > 0)
                    {
                        spec.Dimensions.Clear();
                        spec.Dimensions.AddRange(dims);
                    }
                    continue;
                }
                if (IsIdentifier(CurrentScanner.Current, "W"))
                {
                    CurrentScanner.Scan();
                    TryConsumeColon();
                    if (TryParseDoubleToken(out var weight))
                        spec.Weight = weight;
                    continue;
                }
                CurrentScanner.Scan();
            }

            if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                return false;
            CurrentScanner.Scan();
            return true;
        }

        private List<long> ParseLongArray()
        {
            var values = new List<long>();
            if (CurrentScanner.Current.Kind != TokenKind.LBracket)
                return values;
            CurrentScanner.Scan();
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBracket)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    continue;
                }
                if ((CurrentScanner.Current.Kind == TokenKind.Number || CurrentScanner.Current.Kind == TokenKind.Float) &&
                    long.TryParse(CurrentScanner.Current.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                {
                    values.Add(num);
                }
                CurrentScanner.Scan();
            }
            if (CurrentScanner.Current.Kind == TokenKind.RBracket)
                CurrentScanner.Scan();
            return values;
        }

        private bool TryParseDoubleToken(out double value)
        {
            value = 0d;
            if (CurrentScanner.Current.Kind != TokenKind.Number && CurrentScanner.Current.Kind != TokenKind.Float)
                return false;
            var ok = double.TryParse(CurrentScanner.Current.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            CurrentScanner.Scan();
            return ok;
        }

        private bool TryParseVaultReadExpression(out string vaultVarName, out List<string> readArgs)
        {
            vaultVarName = "";
            readArgs = new List<string>();
            var pos = CurrentScanner.Save();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier) return false;
            vaultVarName = CurrentScanner.Scan().Value;
            if (CurrentScanner.Current.Kind != TokenKind.Dot || !IsIdentifier(CurrentScanner.Watch(1), "read"))
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.LParen)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.StringLiteral)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            readArgs.Add(CurrentScanner.Scan().Value);
            if (CurrentScanner.Current.Kind == TokenKind.Comma)
            {
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.StringLiteral) { CurrentScanner.Restore(pos); return false; }
                readArgs.Add(CurrentScanner.Scan().Value);
                if (CurrentScanner.Current.Kind != TokenKind.Comma) { CurrentScanner.Restore(pos); return false; }
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.StringLiteral) { CurrentScanner.Restore(pos); return false; }
                readArgs.Add(CurrentScanner.Scan().Value);
            }
            if (readArgs.Count != 1 && readArgs.Count != 3) { CurrentScanner.Restore(pos); return false; }
            if (CurrentScanner.Current.Kind != TokenKind.RParen)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success) CurrentScanner.Restore(pos);
            return success;
        }

        private bool TryParseGenericDefExpression(out string defTypeName, out List<string> genericTypeNames)
        {
            defTypeName = "";
            genericTypeNames = new List<string>();
            var pos = CurrentScanner.Save();

            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                return false;
            defTypeName = CurrentScanner.Scan().Value.ToLowerInvariant();

            if (CurrentScanner.Current.Kind != TokenKind.LessThan)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();

            if (CurrentScanner.Current.Kind != TokenKind.Identifier && CurrentScanner.Current.Kind != TokenKind.StringLiteral)
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            genericTypeNames.Add(CurrentScanner.Scan().Value.ToLowerInvariant());

            while (CurrentScanner.Current.Kind == TokenKind.Comma)
            {
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier && CurrentScanner.Current.Kind != TokenKind.StringLiteral)
                {
                    CurrentScanner.Restore(pos);
                    return false;
                }
                genericTypeNames.Add(CurrentScanner.Scan().Value.ToLowerInvariant());
            }

            if (CurrentScanner.Current.Kind != TokenKind.GreaterThan)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();

            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success)
                CurrentScanner.Restore(pos);
            return success;
        }

        private bool TryParseAwaitExpression(out string variableName)
        {
            variableName = "";
            var pos = CurrentScanner.Save();
            if (!IsIdentifier(CurrentScanner.Current, "await"))
                return false;
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            variableName = CurrentScanner.Scan().Value;
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success) CurrentScanner.Restore(pos);
            return success;
        }

        private static bool TryParseAwaitExpressionText(string text, out string expressionText)
        {
            expressionText = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.Trim();
            if (!trimmed.StartsWith("await", StringComparison.OrdinalIgnoreCase))
                return false;
            if (trimmed.Length <= 5 || !char.IsWhiteSpace(trimmed[5]))
                return false;

            expressionText = trimmed.Substring(5).Trim();
            while (expressionText.EndsWith(";", StringComparison.Ordinal))
                expressionText = expressionText.Substring(0, expressionText.Length - 1).TrimEnd();
            return expressionText.Length > 0;
        }

        private bool TryParseStreamWaitExpression(out string streamVarName)
        {
            streamVarName = "";
            var pos = CurrentScanner.Save();
            if (!IsIdentifier(CurrentScanner.Current, "streamwait"))
                return false;
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            streamVarName = CurrentScanner.Scan().Value;
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success) CurrentScanner.Restore(pos);
            return success;
        }

        private static bool TryParseObjectLiteralWithVarRefs(string valueText, out List<(string Key, string VarName)>? keyVars)
        {
            keyVars = null;
            var trimmed = valueText.Trim();
            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;
            scanner.Scan();
            var list = new List<(string, string)>();
            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBrace)
            {
                while (scanner.Current.Kind == TokenKind.Semicolon || scanner.Current.Kind == TokenKind.Comma)
                    scanner.Scan();
                if (scanner.Current.Kind == TokenKind.RBrace)
                    break;
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var key = scanner.Scan().Value ?? "";
                if (scanner.Current.Kind != TokenKind.Colon)
                    return false;
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var varName = scanner.Scan().Value ?? "";
                list.Add((key, varName));
                while (scanner.Current.Kind == TokenKind.Semicolon || scanner.Current.Kind == TokenKind.Comma)
                    scanner.Scan();
            }
            if (scanner.Current.Kind != TokenKind.RBrace)
                return false;
            keyVars = list;
            return true;
        }

        private bool TryParseCompileExpression(out string variableName)
        {
            variableName = "";
            var pos = CurrentScanner.Save();
            if (!IsIdentifier(CurrentScanner.Current, "compile"))
                return false;
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.LParen)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            variableName = CurrentScanner.Scan().Value;
            if (CurrentScanner.Current.Kind != TokenKind.RParen)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success) CurrentScanner.Restore(pos);
            return success;
        }

        private bool TryParseAwaitCompileExpression(out string variableName)
        {
            variableName = "";
            var pos = CurrentScanner.Save();
            if (!IsIdentifier(CurrentScanner.Current, "await"))
                return false;
            CurrentScanner.Scan();
            if (!IsIdentifier(CurrentScanner.Current, "compile"))
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.LParen)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            variableName = CurrentScanner.Scan().Value;
            if (CurrentScanner.Current.Kind != TokenKind.RParen)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success) CurrentScanner.Restore(pos);
            return success;
        }

        private bool TryParseSymbolicVariableExpression(out string symbolicVariableName)
        {
            symbolicVariableName = "";
            var pos = CurrentScanner.Save();
            if (CurrentScanner.Current.Kind != TokenKind.Colon)
                return false;
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            symbolicVariableName = CurrentScanner.Scan().Value;
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success)
                CurrentScanner.Restore(pos);
            return success;
        }

        private bool TryCompilePlusAssign(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var opIdx = line.IndexOf("+=", StringComparison.Ordinal);
            if (opIdx <= 0)
                return false;

            var left = line.Substring(0, opIdx).Trim();
            var right = line.Substring(opIdx + 2).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            if (right.EndsWith(";", StringComparison.Ordinal))
                right = right.Substring(0, right.Length - 1).Trim();

            if (!TryParseJsonArgument(right, out var jsonArg))
                return false;

            var dotIdx = left.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx <= 0 || dotIdx >= left.Length - 1)
                return false;

            var rootName = left.Substring(0, dotIdx).Trim();
            var memberPath = left.Substring(dotIdx + 1).Trim();
            if (!vars.TryGetValue(rootName, out var rootVar))
                throw new UndeclaredVariableException(rootName);
            if (rootVar.Kind != "memory" && rootVar.Kind != "stream" && rootVar.Kind != "def")
                return true;

            long objectSlot;
            var useExistingValueSlot = false;
            if (jsonArg is JsonIdentifierNode idNode &&
                vars.TryGetValue(idNode.Name, out var existingVar) &&
                (existingVar.Kind == "memory" || existingVar.Kind == "stream" || existingVar.Kind == "def"))
            {
                objectSlot = existingVar.Index;
                useExistingValueSlot = true;
            }
            else
            {
                objectSlot = memorySlotCounter++;
            }
            var collectionSlot = memorySlotCounter++;
            var discardSlot = memorySlotCounter++;

            if (!useExistingValueSlot)
            {
                var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                instructions.Add(CreatePushStringInstruction(init));
                instructions.Add(CreatePopMemoryInstruction(objectSlot));
                EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions);
            }

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(CreatePushMemoryInstruction(objectSlot));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "add" }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(new InstructionNode { Opcode = "setobj" });
            instructions.Add(CreatePopMemoryInstruction(discardSlot));
            return true;
        }

        private bool TryCompileMultiplyAssign(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var opIdx = line.IndexOf("*=", StringComparison.Ordinal);
            if (opIdx <= 0)
                return false;

            var left = line.Substring(0, opIdx).Trim();
            var right = line.Substring(opIdx + 2).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            if (right.EndsWith(";", StringComparison.Ordinal))
                right = right.Substring(0, right.Length - 1).Trim();

            if (!TryParseJsonArgument(right, out var jsonArg))
                return false;

            var dotIdx = left.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx <= 0 || dotIdx >= left.Length - 1)
                return false;

            var rootName = left.Substring(0, dotIdx).Trim();
            var memberPath = left.Substring(dotIdx + 1).Trim();
            if (!vars.TryGetValue(rootName, out var rootVar))
                throw new UndeclaredVariableException(rootName);
            if (rootVar.Kind != "memory" && rootVar.Kind != "stream" && rootVar.Kind != "def")
                return true;

            long objectSlot;
            var useExistingValueSlot = false;
            if (jsonArg is JsonIdentifierNode idNode &&
                vars.TryGetValue(idNode.Name, out var existingVar) &&
                (existingVar.Kind == "memory" || existingVar.Kind == "stream" || existingVar.Kind == "def"))
            {
                objectSlot = existingVar.Index;
                useExistingValueSlot = true;
            }
            else
            {
                objectSlot = memorySlotCounter++;
            }
            var collectionSlot = memorySlotCounter++;
            var discardSlot = memorySlotCounter++;

            if (!useExistingValueSlot)
            {
                var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                instructions.Add(CreatePushStringInstruction(init));
                instructions.Add(CreatePopMemoryInstruction(objectSlot));
                EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions);
            }

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(CreatePushMemoryInstruction(objectSlot));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "mul" }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(new InstructionNode { Opcode = "setobj" });
            instructions.Add(CreatePopMemoryInstruction(discardSlot));
            return true;
        }

        private bool TryParseMemberAccessExpression(out string rootVariableName, out string path)
        {
            rootVariableName = "";
            path = "";
            var pos = CurrentScanner.Save();

            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                return false;

            rootVariableName = CurrentScanner.Scan().Value;
            var segments = new List<string>();

            while (true)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Identifier && CurrentScanner.Current.Value == "!")
                    CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Dot)
                    break;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                {
                    CurrentScanner.Restore(pos);
                    return false;
                }

                segments.Add(CurrentScanner.Scan().Value);
            }

            SkipSemicolon();
            if (segments.Count == 0 || !CurrentScanner.Current.IsEndOfInput)
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            path = string.Join(".", segments);
            return true;
        }

        private bool TryParseOriginExpression(out string shapeVarName)
        {
            shapeVarName = "";
            var pos = CurrentScanner.Save();
            if (!(CurrentScanner.Current.Kind == TokenKind.RBracket || CurrentScanner.Current.Value == "]"))
                return false;
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            shapeVarName = CurrentScanner.Scan().Value;
            SkipSemicolon();
            var success = CurrentScanner.Current.IsEndOfInput;
            if (!success) CurrentScanner.Restore(pos);
            return success;
        }

        private bool TryParseIntersectionExpression(string sourceLine, out string shapeAName, out string shapeBText)
        {
            shapeAName = "";
            shapeBText = "";
            var pos = CurrentScanner.Save();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                return false;
            shapeAName = CurrentScanner.Scan().Value;
            if (CurrentScanner.Current.Value != "|")
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            if (CurrentScanner.Current.IsEndOfInput)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            var start = CurrentScanner.Current.Start;
            var end = start;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Semicolon)
            {
                end = CurrentScanner.Current.End;
                CurrentScanner.Scan();
            }
            shapeBText = start < end ? sourceLine.Substring(start, end - start).Trim() : "";
            var success = !string.IsNullOrWhiteSpace(shapeAName) && !string.IsNullOrWhiteSpace(shapeBText);
            if (!success)
                CurrentScanner.Restore(pos);
            return success;
        }

        private static bool TryParseProcedureName(string line, out string procedureName)
        {
            procedureName = "";
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            procedureName = scanner.Scan().Value;
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        private static bool IsIdentifier(Token token, string value) =>
            token.Kind == TokenKind.Identifier && string.Equals(token.Value, value, StringComparison.OrdinalIgnoreCase);

        private static bool IsIdentifier(Token? token, string value) =>
            token.HasValue && token.Value.Kind == TokenKind.Identifier && string.Equals(token.Value.Value, value, StringComparison.OrdinalIgnoreCase);

        private void SkipSemicolon()
        {
            while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                CurrentScanner.Scan();
        }

        private void TryConsumeColon()
        {
            if (CurrentScanner.Current.Kind == TokenKind.Colon)
                CurrentScanner.Scan();
        }
    }
}
