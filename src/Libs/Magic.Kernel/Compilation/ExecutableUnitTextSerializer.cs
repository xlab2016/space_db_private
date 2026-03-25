using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// AGI Assembly (agiasm) text format: one instruction per line, human-readable.
    /// </summary>
    internal static class ExecutableUnitTextSerializer
    {
        public const string FormatMarker = "@AGIASM";

        public static string Serialize(ExecutableUnit unit)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{FormatMarker} 1");
            sb.AppendLine($"@AGI {unit.Version?.TrimEnd(';') ?? "0.0.1"}");
            if (!string.IsNullOrEmpty(unit.Name))
                sb.AppendLine($"program {unit.Name}");
            if (!string.IsNullOrEmpty(unit.Module))
                sb.AppendLine($"module {unit.Module}");
            // Procedures and functions are emitted before the entrypoint body so that all definitions
            // appear before the entry-point call (matching the expected AGI assembly format).
            foreach (var kv in unit.Procedures ?? new Dictionary<string, Processor.Procedure>())
            {
                sb.AppendLine($"procedure {kv.Key}");
                foreach (var cmd in kv.Value.Body ?? new ExecutionBlock())
                    sb.AppendLine(CommandToInstruction(cmd));
            }
            foreach (var kv in unit.Functions ?? new Dictionary<string, Processor.Function>())
            {
                sb.AppendLine($"function {kv.Key}");
                foreach (var cmd in kv.Value.Body ?? new ExecutionBlock())
                    sb.AppendLine(CommandToInstruction(cmd));
            }
            sb.AppendLine("entrypoint");
            foreach (var cmd in unit.EntryPoint ?? new ExecutionBlock())
                sb.AppendLine(CommandToInstruction(cmd));
            return sb.ToString();
        }

        public static bool IsAgiasmFormat(byte[] data)
        {
            if (data == null || data.Length < 8) return false;
            var start = Encoding.UTF8.GetString(data, 0, Math.Min(64, data.Length));
            return start.StartsWith(FormatMarker, StringComparison.OrdinalIgnoreCase);
        }

        public static ExecutableUnit Deserialize(string text)
        {
            var parser = new InstructionParser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var unit = new ExecutableUnit();
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            string? currentProcedure = null;
            string? currentFunction = null;
            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i].Trim();
                i++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

                if (line.StartsWith(FormatMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("@AGI "))
                {
                    unit.Version = line.Substring(5).Trim();
                    continue;
                }
                if (line.StartsWith("program "))
                {
                    unit.Name = line.Substring(8).Trim();
                    continue;
                }
                if (line.StartsWith("module "))
                {
                    unit.Module = line.Substring(7).Trim();
                    continue;
                }
                if (line.Equals("entrypoint", StringComparison.OrdinalIgnoreCase))
                {
                    currentProcedure = null;
                    currentFunction = null;
                    continue;
                }
                if (line.StartsWith("procedure "))
                {
                    currentProcedure = line.Substring(10).Trim();
                    currentFunction = null;
                    continue;
                }
                if (line.StartsWith("function "))
                {
                    currentFunction = line.Substring(9).Trim();
                    currentProcedure = null;
                    continue;
                }

                var ast = parser.Parse(line);
                var command = semanticAnalyzer.Analyze(ast);
                if (currentProcedure != null)
                {
                    if (!unit.Procedures.ContainsKey(currentProcedure))
                        unit.Procedures[currentProcedure] = new Processor.Procedure { Name = currentProcedure };
                    unit.Procedures[currentProcedure].Body.Add(command);
                }
                else if (currentFunction != null)
                {
                    if (!unit.Functions.ContainsKey(currentFunction))
                        unit.Functions[currentFunction] = new Processor.Function { Name = currentFunction };
                    unit.Functions[currentFunction].Body.Add(command);
                }
                else
                {
                    unit.EntryPoint.Add(command);
                }
            }

            return unit;
        }

        public static string CommandToInstruction(Command cmd)
        {
            switch (cmd.Opcode)
            {
                case Opcodes.Nop:
                    return "nop";
                case Opcodes.Ret:
                    return "ret";
                case Opcodes.Def:
                    return "def";
                case Opcodes.DefGen:
                    return "defgen";
                case Opcodes.AwaitObj:
                    return "awaitobj";
                case Opcodes.Await:
                    return "await";
                case Opcodes.StreamWaitObj:
                    return "streamwaitobj";
                case Opcodes.GetObj:
                    return "getobj";
                case Opcodes.SetObj:
                    return "setobj";
                case Opcodes.StreamWait:
                    return "streamwait";
                case Opcodes.Label:
                    return "label " + (cmd.Operand1?.ToString() ?? "");
                case Opcodes.Je:
                    return "je " + (cmd.Operand1?.ToString() ?? "");
                case Opcodes.Jmp:
                    return "jmp " + (cmd.Operand1?.ToString() ?? "");
                case Opcodes.Cmp:
                    if (cmd.Operand1 is MemoryAddress cmpLeft && cmd.Operand2 is long cmpRight)
                        return $"cmp [{cmpLeft.Index ?? 0}], {cmpRight}";
                    if (cmd.Operand1 is Tuple<long, long> cmp)
                        return $"cmp [{cmp.Item1}], {cmp.Item2}";
                    return "cmp [0], 0";
                case Opcodes.Expr:
                    return "expr";
                case Opcodes.DefExpr:
                    return "defexpr";
                case Opcodes.Lambda:
                    return FormatLambda(cmd.Operand1);
                case Opcodes.Equals:
                    return "equals";
                case Opcodes.Not:
                    return "not";
                case Opcodes.Lt:
                    return "lt";
                case Opcodes.CallObj:
                    return FormatCallObj(cmd.Operand1 as string);
                case Opcodes.AddVertex:
                    return FormatAddVertex(cmd.Operand1 as Vertex);
                case Opcodes.AddRelation:
                    return FormatAddRelation(cmd.Operand1 as Relation);
                case Opcodes.AddShape:
                    return FormatAddShape(cmd.Operand1 as Shape);
                case Opcodes.Call:
                    return FormatCall("call", cmd.Operand1 as CallInfo);
                case Opcodes.ACall:
                    return FormatCall("acall", cmd.Operand1 as CallInfo);
                case Opcodes.SysCall:
                    return FormatCall("call", cmd.Operand1 as CallInfo);
                case Opcodes.Pop:
                    return FormatPop(cmd.Operand1 as MemoryAddress);
                case Opcodes.Push:
                    return FormatPush(cmd.Operand1);
                default:
                    return "nop";
            }
        }

        private static string FormatCallObj(string? methodName)
        {
            if (string.IsNullOrEmpty(methodName)) return "callobj \"\"";
            return "callobj \"" + EscapeString(methodName) + "\"";
        }

        private static string FormatLambda(object? operand)
        {
            if (operand is List<object> parameters && parameters.Count > 0)
            {
                var names = parameters
                    .Select(p => p?.ToString())
                    .Where(p => !string.IsNullOrWhiteSpace(p));
                var joined = string.Join(", ", names!);
                if (!string.IsNullOrWhiteSpace(joined))
                    return "lambda " + joined;
            }

            return "lambda";
        }

        private static string FormatAddVertex(Vertex? v)
        {
            if (v == null) return "nop";
            var parts = new List<string>();
            if (v.Index.HasValue) parts.Add($"index: {v.Index.Value}");
            if (v.Position?.Dimensions != null && v.Position.Dimensions.Count > 0)
                parts.Add($"dimensions: [{string.Join(", ", v.Position.Dimensions)}]");
            if (v.Weight.HasValue)
                parts.Add($"weight: {v.Weight.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (v.Data != null && !string.IsNullOrEmpty(v.Data.Data))
            {
                var types = v.Data.Type?.Types?.Select(t => t.ToString().ToLowerInvariant()).ToList() ?? new List<string>();
                if (types.Count == 0) types.Add("text");
                var typeStr = string.Join(": ", types);
                parts.Add($"data: {typeStr}: \"{EscapeString(v.Data.Data)}\"");
            }
            return "addvertex " + string.Join(", ", parts);
        }

        private static string FormatAddRelation(Relation? r)
        {
            if (r == null) return "nop";
            var parts = new List<string>();
            if (r.Index.HasValue) parts.Add($"index: {r.Index.Value}");
            var fromType = (r.FromType ?? EntityType.Vertex).ToString().ToLowerInvariant();
            var toType = (r.ToType ?? EntityType.Vertex).ToString().ToLowerInvariant();
            if (r.FromIndex.HasValue) parts.Add($"from: {fromType}: index: {r.FromIndex.Value}");
            if (r.ToIndex.HasValue) parts.Add($"to: {toType}: index: {r.ToIndex.Value}");
            parts.Add($"weight: {(r.Weight ?? 0.5f).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return "addrelation " + string.Join(", ", parts);
        }

        private static string FormatAddShape(Shape? s)
        {
            if (s == null) return "nop";
            var parts = new List<string>();
            if (s.Index.HasValue) parts.Add($"index: {s.Index.Value}");
            if (s.VertexIndices != null && s.VertexIndices.Count > 0)
                parts.Add($"vertices: indices: [{string.Join(", ", s.VertexIndices)}]");
            if (s.Weight.HasValue) parts.Add($"weight: {s.Weight.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (s.Origin?.Dimensions != null && s.Origin.Dimensions.Count > 0)
                parts.Add($"dimensions: [{string.Join(", ", s.Origin.Dimensions)}]");
            return "addshape " + string.Join(", ", parts);
        }

        private static string FormatCall(string opcode, CallInfo? c)
        {
            if (c == null) return "nop";
            var name = c.FunctionName.Contains(' ') ? $"\"{c.FunctionName}\"" : c.FunctionName;
            var paramParts = new List<string>();
            foreach (var kv in c.Parameters ?? new Dictionary<string, object>())
            {
                if (kv.Value is MemoryAddress ma && ma.Index.HasValue)
                    paramParts.Add($"[{ma.Index.Value}]");
                else if (kv.Value is string str)
                    paramParts.Add($"{kv.Key}: \"{EscapeString(str)}\"");
                else if (kv.Value is Dictionary<string, object> dict &&
                         dict.TryGetValue("type", out var typeObj) &&
                         dict.TryGetValue("index", out var idxObj) &&
                         idxObj is long idx)
                {
                    var et = typeObj is EntityType etVal ? etVal.ToString().ToLowerInvariant() : "vertex";
                    paramParts.Add($"{kv.Key}: {et}: index: {idx}");
                }
                else
                    paramParts.Add($"{kv.Key}: {kv.Value}");
            }
            var paramStr = paramParts.Count > 0 ? ", " + string.Join(", ", paramParts) : "";
            return opcode + " " + name + paramStr;
        }

        private static string FormatPop(MemoryAddress? ma)
        {
            // "pop" with no operand is represented in runtime as Pop(MemoryAddress.Index = null).
            // In previous versions we serialized it as "pop [0]" which made the disassembly ambiguous.
            if (ma?.Index == null) return "pop";
            if (ma.IsGlobal) return $"pop global: [{ma.Index.Value}]";
            return $"pop [{ma.Index.Value}]";
        }

        private static string FormatPush(object? operand)
        {
            if (operand is MemoryAddress ma && ma.Index.HasValue)
            {
                if (ma.IsGlobal)
                    return $"push global: [{ma.Index.Value}]";
                return $"push [{ma.Index.Value}]";
            }
            if (operand is PushOperand po)
            {
                return po.Kind switch
                {
                    "Type" => "push " + (po.Value as string ?? ""),
                    "IntLiteral" => "push " + (po.Value is long l ? l.ToString() : po.Value?.ToString() ?? "0"),
                    "StringLiteral" => BuildFormattedStringPush(po.Value as string ?? ""),
                    "LambdaArg" => "push lambda: arg" + (po.Value is int i ? i.ToString() : "0"),
                    _ => "push [0]"
                };
            }
            return "push [0]";
        }

        private static string BuildFormattedStringPush(string value)
        {
            // Compiler uses unary '&' for procedure references (e.g. &call).
            // In runtime it's still a plain string (the '&' may be stripped by device bindings),
            // but in text dumps we prefer making it explicit as an address-like literal.
            if (value.StartsWith("&", StringComparison.Ordinal) && value.Length > 1)
            {
                return "push address: \"" + EscapeString(value.Substring(1)) + "\"";
            }
            return "push string: \"" + EscapeString(value) + "\"";
        }

        private static string EscapeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
