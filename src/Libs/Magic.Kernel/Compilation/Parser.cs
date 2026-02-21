using Magic.Kernel.Compilation.Ast;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Magic.Kernel.Compilation
{
    public class Parser
    {
        private readonly InstructionParser _instructionParser = new InstructionParser();

        private static void SkipNewlines(Scanner s)
        {
            while (s.Current.Kind == TokenKind.Newline)
                s.Scan();
        }

        private static void AddRangeAsStatement(string source, int start, int end, List<string> output)
        {
            if (start < 0 || end <= start) return;
            var text = source.Substring(start, end - start).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                output.Add(text);
        }

        private static string NormalizeInstructionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            var inString = false;
            var escaped = false;
            var pendingSpace = false;

            foreach (var ch in text)
            {
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
                    if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                        sb.Append(' ');
                    pendingSpace = false;
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }

            return sb.ToString().Trim();
        }

        /// <summary>Потребляет обычный блок (после "{") и возвращает список statement-строк до парной "}".</summary>
        private static List<string> ConsumeStatementBlockLines(Scanner s, string source)
        {
            var result = new List<string>();
            var depth = 1;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = s.Scan();
                if (tok.Kind == TokenKind.EndOfInput)
                {
                    AddRangeAsStatement(source, currentStart, currentEnd, result);
                    break;
                }

                if (tok.Kind == TokenKind.LBrace)
                {
                    depth++;
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (tok.Kind == TokenKind.RBrace)
                {
                    depth--;
                    if (depth == 0)
                    {
                        AddRangeAsStatement(source, currentStart, currentEnd, result);
                        break;
                    }
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (depth == 1 && (tok.Kind == TokenKind.Semicolon || tok.Kind == TokenKind.Newline))
                {
                    AddRangeAsStatement(source, currentStart, currentEnd, result);
                    currentStart = -1;
                    currentEnd = -1;
                    continue;
                }

                if (currentStart < 0) currentStart = tok.Start;
                currentEnd = tok.End;
            }

            return result;
        }

        /// <summary>Потребляет asm-блок (после "asm {") и возвращает инструкции до парной "}".</summary>
        private static List<string> ConsumeAsmBlockInstructions(Scanner s, string source)
        {
            var result = new List<string>();
            var braceDepth = 1;
            var bracketDepth = 0;
            var parenDepth = 0;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = s.Scan();
                switch (tok.Kind)
                {
                    case TokenKind.EndOfInput:
                        AddRangeAsStatement(source, currentStart, currentEnd, result);
                        return result;
                    case TokenKind.LBrace:
                        braceDepth++;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.RBrace:
                        braceDepth--;
                        if (braceDepth == 0)
                        {
                            AddRangeAsStatement(source, currentStart, currentEnd, result);
                            return result;
                        }
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.LBracket:
                        bracketDepth++;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.RBracket:
                        if (bracketDepth > 0) bracketDepth--;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.LParen:
                        parenDepth++;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.RParen:
                        if (parenDepth > 0) parenDepth--;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.Semicolon:
                        if (braceDepth == 1 && bracketDepth == 0 && parenDepth == 0)
                        {
                            AddRangeAsStatement(source, currentStart, currentEnd, result);
                            currentStart = -1;
                            currentEnd = -1;
                        }
                        else
                        {
                            if (currentStart < 0) currentStart = tok.Start;
                            currentEnd = tok.End;
                        }
                        break;
                    case TokenKind.Newline:
                        if (braceDepth == 1 && bracketDepth == 0 && parenDepth == 0 && NextAsmTokenStartsOpcode(s))
                        {
                            AddRangeAsStatement(source, currentStart, currentEnd, result);
                            currentStart = -1;
                            currentEnd = -1;
                        }
                        break;
                    default:
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                }
            }
        }

        /// <summary>BNF: block = "{" (asm-block | statement-block) "}"</summary>
        private bool TryParseBodyBlock(Scanner s, string source, out List<InstructionNode> instructions)
        {
            instructions = new List<InstructionNode>();
            if (s.Current.Kind != TokenKind.LBrace) return false;
            s.Scan(); // consume outer "{"
            SkipNewlines(s);

            // asm-block = "asm" "{" asm-instructions "}"
            if (s.Current.Kind == TokenKind.Identifier && string.Equals(s.Current.Value, "asm", StringComparison.OrdinalIgnoreCase))
            {
                s.Scan();
                SkipNewlines(s);
                if (s.Current.Kind != TokenKind.LBrace)
                    throw new CompilationException($"Expected '{{' after asm at position {s.Current.Start}.", s.Current.Start);
                s.Scan(); // consume asm "{"
                var asmList = ConsumeAsmBlockInstructions(s, source); // consumes asm "}"
                instructions = asmList.Select(NormalizeInstructionText).Select(ParseWithContext).ToList();
                SkipNewlines(s);
                if (s.Current.Kind != TokenKind.RBrace)
                    throw new CompilationException($"Expected '}}' to close block at position {s.Current.Start}.", s.Current.Start);
                s.Scan(); // consume outer "}"
                return true;
            }

            // statement-block = { statements }
            var statementLines = ConsumeStatementBlockLines(s, source); // consumes outer "}"
            instructions = CompileStatementLines(statementLines).Select(NormalizeInstructionText).Select(ParseWithContext).ToList();
            return true;
        }

        private static bool IsProgramKeyword(string id) =>
            id == "@" || string.Equals(id, "AGI", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "program", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "module", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "procedure", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "function", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "entrypoint", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "asm", StringComparison.OrdinalIgnoreCase);

        private static Token ExpectToken(Scanner s, TokenKind kind, string? value = null)
        {
            var tok = s.Scan();
            if (tok.Kind != kind)
                throw new CompilationException($"Expected {kind}, got {tok.Kind} at position {tok.Start}.", tok.Start);
            if (value != null && !string.Equals(tok.Value, value, StringComparison.OrdinalIgnoreCase))
                throw new CompilationException($"Expected '{value}', got '{tok.Value}' at position {tok.Start}.", tok.Start);
            return tok;
        }

        private static string ExpectNonEmptyHeaderValue(Scanner s, string source, string headerName)
        {
            var start = s.Current.Start;
            var end = start;
            var hasTokens = false;
            while (!s.Current.IsEndOfInput && s.Current.Kind != TokenKind.Semicolon && s.Current.Kind != TokenKind.Newline)
            {
                hasTokens = true;
                end = s.Current.End;
                s.Scan();
            }

            if (!hasTokens)
                throw new CompilationException($"Expected {headerName} at position {s.Current.Start}.", s.Current.Start);

            var value = source.Substring(start, end - start).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new CompilationException($"Expected {headerName} at position {start}.", start);

            return value;
        }

        private bool TryParseAgi(Scanner s, string source, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || s.Current.Value != "@") return false;
            ExpectToken(s, TokenKind.Identifier, "@");
            ExpectToken(s, TokenKind.Identifier, "AGI");
            var versionStart = s.Current.Start;
            var versionEnd = versionStart;
            while (!s.Current.IsEndOfInput && s.Current.Kind != TokenKind.Newline)
            {
                versionEnd = s.Current.End;
                s.Scan();
            }
            structure.Version = versionEnd > versionStart
                ? source.Substring(versionStart, versionEnd - versionStart).Trim()
                : "";
            if (s.Current.Kind == TokenKind.Newline) s.Scan();
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseProgram(Scanner s, string source, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || !string.Equals(s.Current.Value, "program", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(s, TokenKind.Identifier, "program");
            var name = ExpectNonEmptyHeaderValue(s, source, "program name");
            if (s.Current.Kind == TokenKind.Semicolon) s.Scan();
            SkipNewlines(s);
            structure.ProgramName = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseModule(Scanner s, string source, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || !string.Equals(s.Current.Value, "module", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(s, TokenKind.Identifier, "module");
            var name = ExpectNonEmptyHeaderValue(s, source, "module name");
            if (s.Current.Kind == TokenKind.Semicolon) s.Scan();
            SkipNewlines(s);
            structure.Module = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseProcedure(Scanner s, string source, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || !string.Equals(s.Current.Value, "procedure", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(s, TokenKind.Identifier, "procedure");
            var name = ExpectToken(s, TokenKind.Identifier).Value ?? "";
            SkipNewlines(s);
            if (!TryParseBodyBlock(s, source, out var body))
                throw new CompilationException($"Expected body block for procedure '{name}' at position {s.Current.Start}.", s.Current.Start);
            structure.Procedures[name] = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseFunction(Scanner s, string source, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || !string.Equals(s.Current.Value, "function", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(s, TokenKind.Identifier, "function");
            var name = ExpectToken(s, TokenKind.Identifier).Value ?? "";
            SkipNewlines(s);
            if (!TryParseBodyBlock(s, source, out var body))
                throw new CompilationException($"Expected body block for function '{name}' at position {s.Current.Start}.", s.Current.Start);
            structure.Functions[name] = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseEntrypoint(Scanner s, string source, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || !string.Equals(s.Current.Value, "entrypoint", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(s, TokenKind.Identifier, "entrypoint");
            SkipNewlines(s);
            if (!TryParseBodyBlock(s, source, out var body))
                throw new CompilationException($"Expected body block for entrypoint at position {s.Current.Start}.", s.Current.Start);
            structure.EntryPoint = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseTopLevelCall(Scanner s, ProgramStructure structure)
        {
            if (s.Current.Kind != TokenKind.Identifier || IsProgramKeyword(s.Current.Value ?? "")) return false;
            var name = s.Current.Value ?? "";
            if (s.Watch(1)?.Kind != TokenKind.Semicolon) return false;
            s.Scan();
            s.Scan();
            structure.EntryPoint ??= new List<InstructionNode>();
            structure.EntryPoint.Add(new InstructionNode
            {
                Opcode = "call",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode
                    {
                        Name = "function",
                        FunctionName = name
                    }
                }
            });
            structure.IsProgramStructure = true;
            SkipNewlines(s);
            return true;
        }

        private static string ConsumeLineToUnprocessed(Scanner s, string source)
        {
            if (s.Current.IsEndOfInput) return "";
            var start = s.Current.Start;
            while (!s.Current.IsEndOfInput && s.Current.Kind != TokenKind.Newline)
                s.Scan();
            var end = s.Current.IsEndOfInput ? source.Length : s.Current.Start;
            if (s.Current.Kind == TokenKind.Newline) s.Scan();
            return source.Substring(start, end - start).Trim();
        }

        private InstructionNode ParseWithContext(string instructionText)
        {
            try
            {
                return _instructionParser.Parse(instructionText);
            }
            catch (CompilationException ex)
            {
                throw new CompilationException($"{ex.Message} Instruction: '{instructionText}'.", ex.Position);
            }
        }

        public ProgramStructure ParseProgram(string sourceCode)
        {
            var structure = new ProgramStructure();
            var source = sourceCode ?? "";
            if (string.IsNullOrEmpty(source))
            {
                return structure;
            }
            var scanner = new Scanner(source);
            var unprocessedLines = new List<string>();

            while (!scanner.Current.IsEndOfInput)
            {
                SkipNewlines(scanner);
                if (scanner.Current.IsEndOfInput) break;

                if (TryParseAgi(scanner, source, structure)) continue;
                if (TryParseProgram(scanner, source, structure)) continue;
                if (TryParseModule(scanner, source, structure)) continue;
                if (TryParseProcedure(scanner, source, structure)) continue;
                if (TryParseFunction(scanner, source, structure)) continue;
                if (TryParseEntrypoint(scanner, source, structure)) continue;
                if (TryParseTopLevelCall(scanner, structure)) continue;

                var line = ConsumeLineToUnprocessed(scanner, source);
                if (!string.IsNullOrWhiteSpace(line) && !structure.IsProgramStructure)
                    unprocessedLines.Add(line);
            }

            if (!structure.IsProgramStructure)
            {
                if (unprocessedLines.Count == 0)
                    structure.EntryPoint = ParseAsInstructionSet(source);
                else
                    structure.EntryPoint = unprocessedLines.Select(ParseWithContext).ToList();
            }

            return structure;
        }

        /// <summary>Парсит исходный код как плоский набор инструкций (по строкам). Для обратной совместимости, когда нет program/entrypoint.</summary>
        public List<InstructionNode> ParseAsInstructionSet(string sourceCode)
        {
            var nodes = new List<InstructionNode>();
            var reader = new LineReader(sourceCode ?? "");
            for (var i = 0; i < reader.Count; i++)
            {
                var (_, trimmedLine) = reader.GetLine(i);
                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;
                nodes.Add(_instructionParser.Parse(trimmedLine));
            }
            return nodes;
        }

        private static readonly HashSet<string> AsmOpcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "addvertex",
            "addrelation",
            "addshape",
            "call",
            "pop",
            "push",
            "nop",
            "def",
            "defgen",
            "callobj",
            "awaitobj"
        };

        private static List<string> SplitAsmInstructions(string asmText)
        {
            // Делит asm-текст на инструкции по ';' и по newline, когда следующая строка начинается с opcode.
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(asmText))
                return result;

            var scanner = new Scanner(asmText);
            var current = new List<string>();
            var braceDepth = 0;
            var bracketDepth = 0;
            var parenDepth = 0;

            void FlushInstruction()
            {
                if (current.Count == 0) return;
                var instruction = string.Join(" ", current).Trim();
                if (!string.IsNullOrWhiteSpace(instruction))
                    result.Add(instruction);
                current.Clear();
            }

            while (!scanner.Current.IsEndOfInput)
            {
                var tok = scanner.Scan();
                switch (tok.Kind)
                {
                    case TokenKind.LBrace:
                        braceDepth++;
                        current.Add("{");
                        break;
                    case TokenKind.RBrace:
                        if (braceDepth > 0) braceDepth--;
                        current.Add("}");
                        break;
                    case TokenKind.LBracket:
                        bracketDepth++;
                        current.Add("[");
                        break;
                    case TokenKind.RBracket:
                        if (bracketDepth > 0) bracketDepth--;
                        current.Add("]");
                        break;
                    case TokenKind.LParen:
                        parenDepth++;
                        current.Add("(");
                        break;
                    case TokenKind.RParen:
                        if (parenDepth > 0) parenDepth--;
                        current.Add(")");
                        break;
                    case TokenKind.Semicolon:
                        if (braceDepth == 0 && bracketDepth == 0 && parenDepth == 0)
                            FlushInstruction();
                        else
                            current.Add(";");
                        break;
                    case TokenKind.Newline:
                        if (braceDepth == 0 && bracketDepth == 0 && parenDepth == 0 && NextAsmTokenStartsOpcode(scanner))
                            FlushInstruction();
                        break;
                    case TokenKind.EndOfInput:
                        break;
                    default:
                        current.Add(RenderAsmToken(tok));
                        break;
                }
            }

            FlushInstruction();
            return result;
        }

        private static bool NextAsmTokenStartsOpcode(Scanner scanner)
        {
            var offset = 0;
            while (true)
            {
                var tok = scanner.Watch(offset);
                if (tok == null || tok.Value.Kind == TokenKind.EndOfInput)
                    return false;
                if (tok.Value.Kind == TokenKind.Newline)
                {
                    offset++;
                    continue;
                }
                return tok.Value.Kind == TokenKind.Identifier && AsmOpcodes.Contains(tok.Value.Value ?? "");
            }
        }

        private static string RenderAsmToken(Token tok)
        {
            return tok.Kind switch
            {
                TokenKind.Colon => ":",
                TokenKind.Comma => ",",
                TokenKind.Dot => ".",
                TokenKind.Assign => "=",
                TokenKind.LessThan => "<",
                TokenKind.GreaterThan => ">",
                TokenKind.StringLiteral => "\"" + EscapeStringLiteral(tok.Value ?? "") + "\"",
                _ => tok.Value ?? ""
            };
        }

        private static string EscapeStringLiteral(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private List<string> CompileStatementLines(IEnumerable<string> sourceLines)
        {
            var asmInstructions = new List<string>();
            var vars = new Dictionary<string, (string Kind, int Index)>(StringComparer.Ordinal);
            var vertexCounter = 1;
            var relationCounter = 1;
            var shapeCounter = 1;
            var memorySlotCounter = 0;
            var inVarBlock = false;
            var varBlockLines = new List<string>();

            foreach (var rawLine in sourceLines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line == "{" || line == "}")
                    continue;

                if (TryParseVarKeywordOnly(line))
                {
                    inVarBlock = true;
                    varBlockLines.Clear();
                    continue;
                }

                if (TryStripInlineVarPrefix(line, out var inlineDecl) && IsDeclarationStatement(inlineDecl))
                {
                    inVarBlock = true;
                    varBlockLines.Clear();
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
                    asmInstructions.AddRange(CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));
                }

                if (IsDeclarationStatement(line))
                {
                    asmInstructions.AddRange(CompileVarBlock(new List<string> { line }, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));
                    continue;
                }

                if (TryCompileMethodCall(line, vars, asmInstructions))
                    continue;

                if (TryCompileAssignment(line, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, asmInstructions))
                    continue;

                if (TryCompileFunctionCall(line, vars, asmInstructions))
                    continue;

                if (TryParseProcedureName(line, out var procName))
                    asmInstructions.Add($"call {procName}");
            }

            if (inVarBlock && varBlockLines.Count > 0)
                asmInstructions.AddRange(CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));

            return asmInstructions;
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

        private List<string> CompileVarBlock(List<string> varLines, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter, ref int relationCounter, ref int shapeCounter, ref int memorySlotCounter)
        {
            var asm = new List<string>();

            foreach (var line in varLines)
            {
                var scanner = new Scanner(line);
                if (TryParseStreamDeclaration(scanner, out var streamName, out var elementType))
                {
                    EmitStreamDeclaration(streamName, elementType, vars, ref memorySlotCounter, asm);
                    continue;
                }

                scanner = new Scanner(line);
                if (!TryParseEntityDeclaration(line, scanner, out var varName, out var varType, out var initText))
                    continue;

                if (varType == "vertex")
                {
                    var index = vertexCounter++;
                    vars[varName] = ("vertex", index);
                    asm.AddRange(CompileVertexInit(index, initText));
                }
                else if (varType == "relation")
                {
                    var index = relationCounter++;
                    vars[varName] = ("relation", index);
                    asm.AddRange(CompileRelationInit(index, initText, vars));
                }
                else if (varType == "shape")
                {
                    var index = shapeCounter++;
                    vars[varName] = ("shape", index);
                    asm.AddRange(CompileShapeInit(index, initText, vars, ref vertexCounter));
                }
            }

            return asm;
        }

        private List<string> CompileVertexInit(int index, string initValue)
        {
            var asm = new List<string>();
            var scanner = new Scanner(initValue);
            if (!TryParseVertexObject(scanner, out var spec))
                return asm;

            var dimsStr = string.Join(", ", spec.Dimensions);
            var asmLine = $"addvertex index: {index}, dimensions: [{dimsStr}], weight: {spec.Weight.ToString(CultureInfo.InvariantCulture)}";

            if (!string.IsNullOrEmpty(spec.BinaryData))
                asmLine += $", data: binary: base64: \"{EscapeStringLiteral(spec.BinaryData)}\"";
            else if (!string.IsNullOrEmpty(spec.TextData))
                asmLine += $", data: text: \"{EscapeStringLiteral(spec.TextData)}\"";

            asm.Add(asmLine);
            return asm;
        }

        private List<string> CompileRelationInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars)
        {
            var asm = new List<string>();
            var scanner = new Scanner(initValue);
            if (!TryParseRelationObject(scanner, out var spec) || string.IsNullOrEmpty(spec.From) || string.IsNullOrEmpty(spec.To))
                return asm;

            if (!vars.TryGetValue(spec.From, out var fromVar) || !vars.TryGetValue(spec.To, out var toVar))
                return asm;

            var fromType = fromVar.Kind == "relation" ? "relation" : "vertex";
            var toType = toVar.Kind == "relation" ? "relation" : "vertex";

            asm.Add($"addrelation index: {index}, from: {fromType}: index: {fromVar.Index}, to: {toType}: index: {toVar.Index}, weight: {spec.Weight.ToString(CultureInfo.InvariantCulture)}");
            return asm;
        }

        private List<string> CompileShapeInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter)
        {
            var asm = new List<string>();
            var scanner = new Scanner(initValue);
            if (!TryParseShapeObject(scanner, out var spec))
                return asm;

            var indices = new List<int>();
            if (spec.UsesInlineVertices)
            {
                foreach (var vertex in spec.InlineVertices)
                {
                    var tempIndex = vertexCounter++;
                    var dimsStr = string.Join(", ", vertex.Dimensions);
                    var line = $"addvertex index: {tempIndex}, dimensions: [{dimsStr}]";
                    if (vertex.Weight.HasValue)
                        line += $", weight: {vertex.Weight.Value.ToString(CultureInfo.InvariantCulture)}";
                    asm.Add(line);
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
                asm.Add($"addshape index: {index}, vertices: indices: [{string.Join(", ", indices)}]");

            return asm;
        }

        private bool TryCompileAssignment(string line, Dictionary<string, (string Kind, int Index)> vars, ref int shapeCounter, ref int vertexCounter, ref int memorySlotCounter, List<string> asm)
        {
            var scanner = new Scanner(line);
            if (IsIdentifier(scanner.Current, "var"))
            {
                scanner.Scan();
            }

            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var targetName = scanner.Scan().Value;

            var hasAssign = scanner.Current.Kind == TokenKind.Assign;
            var hasDefineAssign = scanner.Current.Kind == TokenKind.Colon && scanner.Watch(1)?.Kind == TokenKind.Assign;
            if (!hasAssign && !hasDefineAssign)
                return false;
            if (hasDefineAssign)
            {
                scanner.Scan();
                scanner.Scan();
            }
            else
            {
                scanner.Scan();
            }

            SkipSemicolon(scanner);
            if (scanner.Current.IsEndOfInput)
                return true;

            if (IsIdentifier(scanner.Current, "vault"))
            {
                var vaultSlot = memorySlotCounter++;
                vars[targetName] = ("vault", vaultSlot);
                return true;
            }

            if (TryParseVaultReadExpression(scanner, out var vaultVarName, out var tokenKey) &&
                vars.TryGetValue(vaultVarName, out var vaultVar) &&
                vaultVar.Kind == "vault")
            {
                var resultSlot = memorySlotCounter++;
                vars[targetName] = ("memory", resultSlot);
                asm.Add($"push string: \"{EscapeStringLiteral(tokenKey)}\"");
                asm.Add("push 1");
                asm.Add("call vault_read");
                asm.Add($"pop [{resultSlot}]");
                return true;
            }

            if (TryParseAwaitExpression(scanner, out var awaitVarName) &&
                vars.TryGetValue(awaitVarName, out var streamVar) &&
                streamVar.Kind == "stream")
            {
                var dataSlot = memorySlotCounter++;
                vars[targetName] = ("memory", dataSlot);
                asm.Add($"push [{streamVar.Index}]");
                asm.Add("awaitobj");
                asm.Add($"pop [{dataSlot}]");
                return true;
            }

            if (TryParseCompileExpression(scanner, out var compileArgName) &&
                vars.TryGetValue(compileArgName, out var dataVar) &&
                (dataVar.Kind == "memory" || dataVar.Kind == "stream"))
            {
                var resultSlot = memorySlotCounter++;
                vars[targetName] = ("memory", resultSlot);
                asm.Add($"push [{dataVar.Index}]");
                asm.Add("push 1");
                asm.Add("call compile");
                asm.Add($"pop [{resultSlot}]");
                return true;
            }

            if (TryParseOriginExpression(scanner, out var shapeVarName) &&
                vars.TryGetValue(shapeVarName, out var shapeVar) &&
                shapeVar.Kind == "shape")
            {
                asm.Add($"call \"origin\", shape: index: {shapeVar.Index}");
                asm.Add("pop [0]");
                vars[targetName] = ("memory", 0);
                return true;
            }

            if (TryParseIntersectionExpression(scanner, line, out var shapeAName, out var shapeBText))
            {
                if (!vars.TryGetValue(shapeAName, out var shapeA) || shapeA.Kind != "shape")
                    return true;

                int? shapeBIndex = null;
                if (vars.TryGetValue(shapeBText, out var shapeBVar) && shapeBVar.Kind == "shape")
                {
                    shapeBIndex = shapeBVar.Index;
                }
                else
                {
                    var tempShapeIndex = shapeCounter++;
                    asm.AddRange(CompileShapeInit(tempShapeIndex, shapeBText, vars, ref vertexCounter));
                    shapeBIndex = tempShapeIndex;
                }

                if (shapeBIndex.HasValue)
                {
                    asm.Add($"call \"intersect\", shapeA: shape: index: {shapeA.Index}, shapeB: shape: index: {shapeBIndex.Value}");
                    asm.Add("pop [1]");
                    vars[targetName] = ("memory", 1);
                }
                return true;
            }

            return true;
        }

        private bool TryCompileFunctionCall(string line, Dictionary<string, (string Kind, int Index)> vars, List<string> asm)
        {
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var functionName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var argName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            SkipSemicolon(scanner);
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!string.Equals(functionName, "print", StringComparison.OrdinalIgnoreCase))
                return true;

            if (vars.TryGetValue(argName, out var argVar) && argVar.Kind == "memory")
            {
                asm.Add($"push [{argVar.Index}]");
                asm.Add("push 1");
                asm.Add("call print");
            }
            return true;
        }

        private bool TryCompileMethodCall(string line, Dictionary<string, (string Kind, int Index)> vars, List<string> asm)
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
            if (scanner.Current.Kind == TokenKind.StringLiteral)
            {
                argText = $"\"{EscapeStringLiteral(scanner.Scan().Value)}\"";
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
                argText = $"\"{EscapeStringLiteral(argText)}\"";
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            SkipSemicolon(scanner);
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(objectName, out var objVar))
                return true;

            asm.Add($"push [{objVar.Index}]");
            asm.Add($"push string: {argText}");
            asm.Add("push 1");
            asm.Add($"callobj \"{methodName}\"");
            return true;
        }

        private void EmitStreamDeclaration(string streamName, string elementType, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<string> asm)
        {
            var baseSlot = memorySlotCounter++;
            var streamSlot = memorySlotCounter++;
            vars[streamName] = ("stream", streamSlot);
            asm.Add("push stream");
            asm.Add("def");
            asm.Add($"pop [{baseSlot}]");
            asm.Add($"push [{baseSlot}]");
            asm.Add($"push {elementType}");
            asm.Add("push 1");
            asm.Add("defgen");
            asm.Add($"pop [{streamSlot}]");
        }

        private bool IsDeclarationStatement(string line)
        {
            var scanner = new Scanner(line);
            return TryParseStreamDeclaration(scanner, out _, out _) ||
                   TryParseEntityDeclaration(line, new Scanner(line), out _, out _, out _);
        }

        private bool TryParseVarKeywordOnly(string line)
        {
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "var"))
                return false;
            scanner.Scan();
            SkipSemicolon(scanner);
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

        private bool TryParseStreamDeclaration(Scanner scanner, out string streamName, out string elementType)
        {
            streamName = "";
            elementType = "";

            if (IsIdentifier(scanner.Current, "var"))
                scanner.Scan();

            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            streamName = scanner.Scan().Value;

            if (scanner.Current.Kind != TokenKind.Colon || scanner.Watch(1)?.Kind != TokenKind.Assign)
                return false;
            scanner.Scan();
            scanner.Scan();

            if (!IsIdentifier(scanner.Current, "stream"))
                return false;
            scanner.Scan();

            if (scanner.Current.Kind != TokenKind.LessThan)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            elementType = scanner.Scan().Value.ToLowerInvariant();

            while (scanner.Current.Kind == TokenKind.Comma)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                scanner.Scan();
            }

            if (scanner.Current.Kind != TokenKind.GreaterThan)
                return false;
            scanner.Scan();
            SkipSemicolon(scanner);
            return scanner.Current.IsEndOfInput;
        }

        private bool TryParseEntityDeclaration(string sourceLine, Scanner scanner, out string varName, out string varType, out string initText)
        {
            varName = "";
            varType = "";
            initText = "";

            if (IsIdentifier(scanner.Current, "var"))
                scanner.Scan();

            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            varName = scanner.Scan().Value;

            if (scanner.Current.Kind != TokenKind.Colon)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            varType = scanner.Scan().Value.ToLowerInvariant();
            if (varType != "vertex" && varType != "relation" && varType != "shape")
                return false;
            if (scanner.Current.Kind != TokenKind.Assign)
                return false;
            scanner.Scan();

            if (scanner.Current.IsEndOfInput)
                return false;
            var start = scanner.Current.Start;
            var end = start;
            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.Semicolon)
            {
                end = scanner.Current.End;
                scanner.Scan();
            }
            initText = start < end ? sourceLine.Substring(start, end - start).Trim() : "";
            if (string.IsNullOrWhiteSpace(initText))
                return false;

            SkipSemicolon(scanner);
            return scanner.Current.IsEndOfInput;
        }

        private bool TryParseVertexObject(Scanner scanner, out VertexInitSpec spec)
        {
            spec = new VertexInitSpec();
            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;
            scanner.Scan();

            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBrace)
            {
                if (scanner.Current.Kind == TokenKind.Comma)
                {
                    scanner.Scan();
                    continue;
                }

                if (IsIdentifier(scanner.Current, "DIM"))
                {
                    scanner.Scan();
                    TryConsumeColon(scanner);
                    var dims = ParseLongArray(scanner);
                    if (dims.Count > 0)
                    {
                        spec.Dimensions.Clear();
                        spec.Dimensions.AddRange(dims);
                    }
                    continue;
                }

                if (IsIdentifier(scanner.Current, "W"))
                {
                    scanner.Scan();
                    TryConsumeColon(scanner);
                    if (TryParseDoubleToken(scanner, out var weight))
                        spec.Weight = weight;
                    continue;
                }

                if (IsIdentifier(scanner.Current, "DATA"))
                {
                    scanner.Scan();
                    TryConsumeColon(scanner);
                    if (IsIdentifier(scanner.Current, "BIN"))
                    {
                        scanner.Scan();
                        TryConsumeColon(scanner);
                        if (scanner.Current.Kind == TokenKind.StringLiteral)
                            spec.BinaryData = scanner.Scan().Value;
                    }
                    else if (scanner.Current.Kind == TokenKind.StringLiteral)
                    {
                        spec.TextData = scanner.Scan().Value;
                    }
                    continue;
                }

                scanner.Scan();
            }

            if (scanner.Current.Kind != TokenKind.RBrace)
                return false;
            scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        private bool TryParseRelationObject(Scanner scanner, out RelationInitSpec spec)
        {
            spec = new RelationInitSpec();
            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;
            scanner.Scan();

            if (scanner.Current.Kind == TokenKind.Identifier)
                spec.From = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.Assign || scanner.Watch(1)?.Kind != TokenKind.GreaterThan)
                return false;
            scanner.Scan();
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            spec.To = scanner.Scan().Value;

            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBrace)
            {
                if (scanner.Current.Kind == TokenKind.Comma)
                {
                    scanner.Scan();
                    continue;
                }
                if (IsIdentifier(scanner.Current, "W"))
                {
                    scanner.Scan();
                    TryConsumeColon(scanner);
                    if (TryParseDoubleToken(scanner, out var weight))
                        spec.Weight = weight;
                    continue;
                }
                scanner.Scan();
            }

            if (scanner.Current.Kind != TokenKind.RBrace)
                return false;
            scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        private bool TryParseShapeObject(Scanner scanner, out ShapeInitSpec spec)
        {
            spec = new ShapeInitSpec();
            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;
            scanner.Scan();

            if (IsIdentifier(scanner.Current, "VERT"))
            {
                scanner.Scan();
                TryConsumeColon(scanner);
                if (scanner.Current.Kind != TokenKind.LBracket)
                    return false;
                scanner.Scan();
                while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBracket)
                {
                    if (scanner.Current.Kind == TokenKind.Comma)
                    {
                        scanner.Scan();
                        continue;
                    }
                    if (scanner.Current.Kind == TokenKind.LBrace && TryParseInlineVertex(scanner, out var inlineVertex))
                    {
                        spec.InlineVertices.Add(inlineVertex);
                        continue;
                    }
                    scanner.Scan();
                }
                if (scanner.Current.Kind != TokenKind.RBracket)
                    return false;
                scanner.Scan();
            }
            else if (scanner.Current.Kind == TokenKind.LBracket)
            {
                scanner.Scan();
                while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBracket)
                {
                    if (scanner.Current.Kind == TokenKind.Comma)
                    {
                        scanner.Scan();
                        continue;
                    }
                    if (scanner.Current.Kind == TokenKind.Identifier)
                        spec.VertexNames.Add(scanner.Scan().Value);
                    else
                        scanner.Scan();
                }
                if (scanner.Current.Kind != TokenKind.RBracket)
                    return false;
                scanner.Scan();
            }
            else
            {
                return false;
            }

            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBrace)
                scanner.Scan();
            if (scanner.Current.Kind != TokenKind.RBrace)
                return false;
            scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        private bool TryParseInlineVertex(Scanner scanner, out InlineVertexSpec spec)
        {
            spec = new InlineVertexSpec();
            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;
            scanner.Scan();

            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBrace)
            {
                if (scanner.Current.Kind == TokenKind.Comma)
                {
                    scanner.Scan();
                    continue;
                }
                if (IsIdentifier(scanner.Current, "DIM"))
                {
                    scanner.Scan();
                    TryConsumeColon(scanner);
                    var dims = ParseLongArray(scanner);
                    if (dims.Count > 0)
                    {
                        spec.Dimensions.Clear();
                        spec.Dimensions.AddRange(dims);
                    }
                    continue;
                }
                if (IsIdentifier(scanner.Current, "W"))
                {
                    scanner.Scan();
                    TryConsumeColon(scanner);
                    if (TryParseDoubleToken(scanner, out var weight))
                        spec.Weight = weight;
                    continue;
                }
                scanner.Scan();
            }

            if (scanner.Current.Kind != TokenKind.RBrace)
                return false;
            scanner.Scan();
            return true;
        }

        private static List<long> ParseLongArray(Scanner scanner)
        {
            var values = new List<long>();
            if (scanner.Current.Kind != TokenKind.LBracket)
                return values;
            scanner.Scan();
            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBracket)
            {
                if (scanner.Current.Kind == TokenKind.Comma)
                {
                    scanner.Scan();
                    continue;
                }
                if ((scanner.Current.Kind == TokenKind.Number || scanner.Current.Kind == TokenKind.Float) &&
                    long.TryParse(scanner.Current.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                {
                    values.Add(num);
                }
                scanner.Scan();
            }
            if (scanner.Current.Kind == TokenKind.RBracket)
                scanner.Scan();
            return values;
        }

        private static bool TryParseDoubleToken(Scanner scanner, out double value)
        {
            value = 0d;
            if (scanner.Current.Kind != TokenKind.Number && scanner.Current.Kind != TokenKind.Float)
                return false;
            var ok = double.TryParse(scanner.Current.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            scanner.Scan();
            return ok;
        }

        private static bool TryParseVaultReadExpression(Scanner scanner, out string vaultVarName, out string tokenKey)
        {
            vaultVarName = "";
            tokenKey = "";
            var pos = scanner.Save();
            if (scanner.Current.Kind != TokenKind.Identifier) return false;
            vaultVarName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.Dot || !IsIdentifier(scanner.Watch(1), "read"))
            {
                scanner.Restore(pos);
                return false;
            }
            scanner.Scan();
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.LParen)
            {
                scanner.Restore(pos);
                return false;
            }
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.StringLiteral)
            {
                scanner.Restore(pos);
                return false;
            }
            tokenKey = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.RParen)
            {
                scanner.Restore(pos);
                return false;
            }
            scanner.Scan();
            SkipSemicolon(scanner);
            var success = scanner.Current.IsEndOfInput;
            if (!success) scanner.Restore(pos);
            return success;
        }

        private static bool TryParseAwaitExpression(Scanner scanner, out string variableName)
        {
            variableName = "";
            var pos = scanner.Save();
            if (!IsIdentifier(scanner.Current, "await"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
            {
                scanner.Restore(pos);
                return false;
            }
            variableName = scanner.Scan().Value;
            SkipSemicolon(scanner);
            var success = scanner.Current.IsEndOfInput;
            if (!success) scanner.Restore(pos);
            return success;
        }

        private static bool TryParseCompileExpression(Scanner scanner, out string variableName)
        {
            variableName = "";
            var pos = scanner.Save();
            if (!IsIdentifier(scanner.Current, "compile"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.LParen)
            {
                scanner.Restore(pos);
                return false;
            }
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
            {
                scanner.Restore(pos);
                return false;
            }
            variableName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.RParen)
            {
                scanner.Restore(pos);
                return false;
            }
            scanner.Scan();
            SkipSemicolon(scanner);
            var success = scanner.Current.IsEndOfInput;
            if (!success) scanner.Restore(pos);
            return success;
        }

        private static bool TryParseOriginExpression(Scanner scanner, out string shapeVarName)
        {
            shapeVarName = "";
            var pos = scanner.Save();
            if (!(scanner.Current.Kind == TokenKind.RBracket || scanner.Current.Value == "]"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
            {
                scanner.Restore(pos);
                return false;
            }
            shapeVarName = scanner.Scan().Value;
            SkipSemicolon(scanner);
            var success = scanner.Current.IsEndOfInput;
            if (!success) scanner.Restore(pos);
            return success;
        }

        private static bool TryParseIntersectionExpression(Scanner scanner, string sourceLine, out string shapeAName, out string shapeBText)
        {
            shapeAName = "";
            shapeBText = "";
            var pos = scanner.Save();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            shapeAName = scanner.Scan().Value;
            if (scanner.Current.Value != "|")
            {
                scanner.Restore(pos);
                return false;
            }
            scanner.Scan();
            if (scanner.Current.IsEndOfInput)
            {
                scanner.Restore(pos);
                return false;
            }
            var start = scanner.Current.Start;
            var end = start;
            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.Semicolon)
            {
                end = scanner.Current.End;
                scanner.Scan();
            }
            shapeBText = start < end ? sourceLine.Substring(start, end - start).Trim() : "";
            var success = !string.IsNullOrWhiteSpace(shapeAName) && !string.IsNullOrWhiteSpace(shapeBText);
            if (!success)
                scanner.Restore(pos);
            return success;
        }

        private static bool TryParseProcedureName(string line, out string procedureName)
        {
            procedureName = "";
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            procedureName = scanner.Scan().Value;
            SkipSemicolon(scanner);
            return scanner.Current.IsEndOfInput;
        }

        private static bool IsIdentifier(Token token, string value) =>
            token.Kind == TokenKind.Identifier && string.Equals(token.Value, value, StringComparison.OrdinalIgnoreCase);

        private static bool IsIdentifier(Token? token, string value) =>
            token.HasValue && token.Value.Kind == TokenKind.Identifier && string.Equals(token.Value.Value, value, StringComparison.OrdinalIgnoreCase);

        private static void SkipSemicolon(Scanner scanner)
        {
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
        }

        private static void TryConsumeColon(Scanner scanner)
        {
            if (scanner.Current.Kind == TokenKind.Colon)
                scanner.Scan();
        }
    }
}
