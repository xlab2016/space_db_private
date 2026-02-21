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
        private Scanner? _scanner;

        private Scanner CurrentScanner =>
            _scanner ?? throw new InvalidOperationException("Scanner is not initialized.");

        private void SkipNewlines()
        {
            while (CurrentScanner.Current.Kind == TokenKind.Newline)
                CurrentScanner.Scan();
        }

        private void AddRangeAsStatement(int start, int end, List<string> output)
        {
            if (start < 0 || end <= start) return;
            var text = CurrentScanner.Slice(start, end).Trim();
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
        private List<string> ConsumeStatementBlockLines()
        {
            var result = new List<string>();
            var depth = 1;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = CurrentScanner.Scan();
                if (tok.Kind == TokenKind.EndOfInput)
                {
                    AddRangeAsStatement(currentStart, currentEnd, result);
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
                        AddRangeAsStatement(currentStart, currentEnd, result);
                        break;
                    }
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (depth == 1 && (tok.Kind == TokenKind.Semicolon || tok.Kind == TokenKind.Newline))
                {
                    AddRangeAsStatement(currentStart, currentEnd, result);
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
        private List<string> ConsumeAsmBlockInstructions()
        {
            var result = new List<string>();
            var braceDepth = 1;
            var bracketDepth = 0;
            var parenDepth = 0;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = CurrentScanner.Scan();
                switch (tok.Kind)
                {
                    case TokenKind.EndOfInput:
                        AddRangeAsStatement(currentStart, currentEnd, result);
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
                            AddRangeAsStatement(currentStart, currentEnd, result);
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
                            AddRangeAsStatement(currentStart, currentEnd, result);
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
                        if (braceDepth == 1 && bracketDepth == 0 && parenDepth == 0 && NextAsmTokenStartsOpcode())
                        {
                            AddRangeAsStatement(currentStart, currentEnd, result);
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
        private bool TryParseBodyBlock(out List<InstructionNode> instructions)
        {
            instructions = new List<InstructionNode>();
            if (CurrentScanner.Current.Kind != TokenKind.LBrace) return false;
            CurrentScanner.Scan(); // consume outer "{"
            SkipNewlines();

            // asm-block = "asm" "{" asm-instructions "}"
            if (CurrentScanner.Current.Kind == TokenKind.Identifier && string.Equals(CurrentScanner.Current.Value, "asm", StringComparison.OrdinalIgnoreCase))
            {
                CurrentScanner.Scan();
                SkipNewlines();
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    throw new CompilationException($"Expected '{{' after asm at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
                CurrentScanner.Scan(); // consume asm "{"
                var asmList = ConsumeAsmBlockInstructions(); // consumes asm "}"
                instructions = asmList.Select(NormalizeInstructionText).Select(ParseWithContext).ToList();
                SkipNewlines();
                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    throw new CompilationException($"Expected '}}' to close block at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
                CurrentScanner.Scan(); // consume outer "}"
                return true;
            }

            // statement-block = { statements }
            var statementLines = ConsumeStatementBlockLines(); // consumes outer "}"
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

        private Token ExpectToken(TokenKind kind, string? value = null)
        {
            var tok = CurrentScanner.Scan();
            if (tok.Kind != kind)
                throw new CompilationException($"Expected {kind}, got {tok.Kind} at position {tok.Start}.", tok.Start);
            if (value != null && !string.Equals(tok.Value, value, StringComparison.OrdinalIgnoreCase))
                throw new CompilationException($"Expected '{value}', got '{tok.Value}' at position {tok.Start}.", tok.Start);
            return tok;
        }

        private string ExpectNonEmptyHeaderValue(string headerName)
        {
            var start = CurrentScanner.Current.Start;
            var end = start;
            var hasTokens = false;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Semicolon && CurrentScanner.Current.Kind != TokenKind.Newline)
            {
                hasTokens = true;
                end = CurrentScanner.Current.End;
                CurrentScanner.Scan();
            }

            if (!hasTokens)
                throw new CompilationException($"Expected {headerName} at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);

            var value = CurrentScanner.Slice(start, end).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new CompilationException($"Expected {headerName} at position {start}.", start);

            return value;
        }

        private bool TryParseAgi(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || CurrentScanner.Current.Value != "@") return false;
            ExpectToken(TokenKind.Identifier, "@");
            ExpectToken(TokenKind.Identifier, "AGI");
            var versionStart = CurrentScanner.Current.Start;
            var versionEnd = versionStart;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Newline)
            {
                versionEnd = CurrentScanner.Current.End;
                CurrentScanner.Scan();
            }
            structure.Version = versionEnd > versionStart
                ? CurrentScanner.Slice(versionStart, versionEnd).Trim()
                : "";
            if (CurrentScanner.Current.Kind == TokenKind.Newline) CurrentScanner.Scan();
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseProgram(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "program", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "program");
            var name = ExpectNonEmptyHeaderValue("program name");
            if (CurrentScanner.Current.Kind == TokenKind.Semicolon) CurrentScanner.Scan();
            SkipNewlines();
            structure.ProgramName = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseModule(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "module", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "module");
            var name = ExpectNonEmptyHeaderValue("module name");
            if (CurrentScanner.Current.Kind == TokenKind.Semicolon) CurrentScanner.Scan();
            SkipNewlines();
            structure.Module = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseProcedure(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "procedure", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "procedure");
            var name = ExpectToken(TokenKind.Identifier).Value ?? "";
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException($"Expected body block for procedure '{name}' at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
            structure.Procedures[name] = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseFunction(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "function", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "function");
            var name = ExpectToken(TokenKind.Identifier).Value ?? "";
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException($"Expected body block for function '{name}' at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
            structure.Functions[name] = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseEntrypoint(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "entrypoint", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "entrypoint");
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException($"Expected body block for entrypoint at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
            structure.EntryPoint = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseTopLevelCall(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || IsProgramKeyword(CurrentScanner.Current.Value ?? "")) return false;
            var name = CurrentScanner.Current.Value ?? "";
            if (CurrentScanner.Watch(1)?.Kind != TokenKind.Semicolon) return false;
            CurrentScanner.Scan();
            CurrentScanner.Scan();
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
            SkipNewlines();
            return true;
        }

        private string ConsumeLineToUnprocessed()
        {
            if (CurrentScanner.Current.IsEndOfInput) return "";
            var start = CurrentScanner.Current.Start;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Newline)
                CurrentScanner.Scan();
            var end = CurrentScanner.Current.IsEndOfInput ? CurrentScanner.SourceLength : CurrentScanner.Current.Start;
            if (CurrentScanner.Current.Kind == TokenKind.Newline) CurrentScanner.Scan();
            return CurrentScanner.Slice(start, end).Trim();
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
            _scanner = new Scanner(source);
            var unprocessedLines = new List<string>();

            while (!CurrentScanner.Current.IsEndOfInput)
            {
                SkipNewlines();
                if (CurrentScanner.Current.IsEndOfInput) break;

                if (TryParseAgi(structure)) continue;
                if (TryParseProgram(structure)) continue;
                if (TryParseModule(structure)) continue;
                if (TryParseProcedure(structure)) continue;
                if (TryParseFunction(structure)) continue;
                if (TryParseEntrypoint(structure)) continue;
                if (TryParseTopLevelCall(structure)) continue;

                var line = ConsumeLineToUnprocessed();
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

        private bool NextAsmTokenStartsOpcode()
        {
            var offset = 0;
            while (true)
            {
                var tok = CurrentScanner.Watch(offset);
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
                if (TryParseStreamDeclaration(line, out var streamName, out var elementType))
                {
                    EmitStreamDeclaration(streamName, elementType, vars, ref memorySlotCounter, asm);
                    continue;
                }

                if (!TryParseEntityDeclaration(line, out var varName, out var varType, out var initText))
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
            if (!TryParseVertexObject(initValue, out var spec))
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
            if (!TryParseRelationObject(initValue, out var spec) || string.IsNullOrEmpty(spec.From) || string.IsNullOrEmpty(spec.To))
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
            if (!TryParseShapeObject(initValue, out var spec))
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

                if (IsIdentifier(CurrentScanner.Current, "vault"))
                {
                    var vaultSlot = memorySlotCounter++;
                    vars[targetName] = ("vault", vaultSlot);
                    return true;
                }

                if (TryParseVaultReadExpression(out var vaultVarName, out var tokenKey) &&
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

                if (TryParseAwaitExpression(out var awaitVarName) &&
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

                if (TryParseCompileExpression(out var compileArgName) &&
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

                if (TryParseOriginExpression(out var shapeVarName) &&
                    vars.TryGetValue(shapeVarName, out var shapeVar) &&
                    shapeVar.Kind == "shape")
                {
                    asm.Add($"call \"origin\", shape: index: {shapeVar.Index}");
                    asm.Add("pop [0]");
                    vars[targetName] = ("memory", 0);
                    return true;
                }

                if (TryParseIntersectionExpression(line, out var shapeAName, out var shapeBText))
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
            finally
            {
                _scanner = previous;
            }
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
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
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
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
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

        private bool TryParseStreamDeclaration(string line, out string streamName, out string elementType)
        {
            streamName = string.Empty;
            elementType = string.Empty;
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
                elementType = CurrentScanner.Scan().Value.ToLowerInvariant();

                while (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                        return false;
                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.GreaterThan)
                    return false;
                CurrentScanner.Scan();
                SkipSemicolon();
                return CurrentScanner.Current.IsEndOfInput;
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

        private bool TryParseVaultReadExpression(out string vaultVarName, out string tokenKey)
        {
            vaultVarName = "";
            tokenKey = "";
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
            tokenKey = CurrentScanner.Scan().Value;
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
