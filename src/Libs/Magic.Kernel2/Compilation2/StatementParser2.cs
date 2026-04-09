using System;
using System.Collections.Generic;
using System.Globalization;
using Magic.Kernel.Compilation;
using Magic.Kernel2.Compilation2.Ast2;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Parses individual AGI statement text into fully-typed AST nodes.
    /// <para>
    /// This is the key component that eliminates the deferred lowering in v1.0.
    /// Instead of storing raw statement text in <c>StatementLineNode</c> for later
    /// processing by <c>StatementLoweringCompiler</c>, this parser immediately creates
    /// the appropriate typed <see cref="StatementNode2"/> subclass.
    /// </para>
    /// </summary>
    /// <summary>
    /// Parses individual AGI statement text into fully-typed AST nodes.
    /// Marked as internal but inheritable so tests can subclass it for white-box testing.
    /// </summary>
    internal class StatementParser2
    {
        /// <summary>
        /// Parse a single statement text into its typed AST node.
        /// Returns null if the text is empty or unrecognizable.
        /// </summary>
        protected internal StatementNode2? ParseStatement(string text, int sourceLine)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var trimmed = text.Trim().TrimEnd(';').Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            // Skip comment lines.
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                return null;

            StatementNode2? result = null;

            // Variable declaration: var x := expr  OR  var x: Type := expr
            if (TryParseVarDeclaration(trimmed, sourceLine, out var varDecl))
                result = varDecl;

            // Return statement: return expr
            else if (TryParseReturn(trimmed, sourceLine, out var ret))
                result = ret;

            // If statement
            else if (TryParseIf(trimmed, sourceLine, out var ifStmt))
                result = ifStmt;

            // Switch statement
            else if (TryParseSwitch(trimmed, sourceLine, out var switchStmt))
                result = switchStmt;

            // Stream wait for loop: for (var item in stream) { ... }
            else if (TryParseForLoop(trimmed, sourceLine, out var forLoop))
                result = forLoop;

            // Assignment: x := expr  OR  x.Field := expr
            else if (TryParseAssignment(trimmed, sourceLine, out var assignment))
                result = assignment;

            // Call statement: Foo(a, b) or await Foo or obj.Method(a, b)
            else if (TryParseCallStatement(trimmed, sourceLine, out var call))
                result = call;

            // Fallback: treat as a raw call/expression statement
            else
                result = new CallStatement2
                {
                    SourceLine = sourceLine,
                    Callee = new VariableExpression2 { Name = trimmed, SourceLine = sourceLine }
                };

            // Preserve original source text on every parsed node for V1-lowering fallback.
            if (result != null)
                result.SourceText = text;

            return result;
        }

        /// <summary>
        /// Parse a type declaration body (e.g., "Point: type { X: int; Y: int; }")
        /// </summary>
        public TypeDeclarationNode2? ParseTypeDeclaration(string text, int sourceLine)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var trimmed = text.Trim();

            // Format: Name: type { ... }  OR  Name: ClassName { ... }  OR  Name: class { ... }
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx <= 0)
                return null;

            var braceIdx = trimmed.IndexOf('{');
            if (braceIdx < 0)
                return null;

            var typeName = trimmed.Substring(0, colonIdx).Trim();
            var header = trimmed.Substring(colonIdx + 1, braceIdx - colonIdx - 1).Trim();
            var body = trimmed.Substring(braceIdx + 1);
            // Remove closing brace
            var closeBrace = body.LastIndexOf('}');
            if (closeBrace >= 0)
                body = body.Substring(0, closeBrace);

            var isType = string.Equals(header, "type", StringComparison.OrdinalIgnoreCase);
            var isClass = string.Equals(header, "class", StringComparison.OrdinalIgnoreCase);
            var isTable = string.Equals(header, "table", StringComparison.OrdinalIgnoreCase);
            var isDatabase = string.Equals(header, "database", StringComparison.OrdinalIgnoreCase);

            var decl = new TypeDeclarationNode2
            {
                Name = typeName,
                SourceLine = sourceLine,
                BaseType = isType || isClass ? null : header,
                IsClass = isClass || (!isType && !isTable && !isDatabase && !string.IsNullOrEmpty(header)),
                IsTable = isTable,
                IsDatabase = isDatabase,
                RawText = text  // Preserve raw text for V1-based lowering of table/database types
            };

            // Parse body: fields (X: int;) and methods/constructors
            ParseTypeBody(body, decl, sourceLine);

            return decl;
        }

        private void ParseTypeBody(string body, TypeDeclarationNode2 decl, int sourceLine)
        {
            if (string.IsNullOrWhiteSpace(body))
                return;

            // Split into member declarations by semicolon/newline, but be careful of nested braces
            var members = SplitTypeMembers(body);
            foreach (var member in members)
            {
                var m = member.Trim();
                if (string.IsNullOrWhiteSpace(m) || m == "{" || m == "}")
                    continue;

                // Constructor: constructor(params) { body }
                if (m.StartsWith("constructor", StringComparison.OrdinalIgnoreCase))
                {
                    var ctor = ParseConstructor(m, sourceLine);
                    if (ctor != null)
                        decl.Constructors.Add(ctor);
                    continue;
                }

                // Method: method Name(params) { body }
                if (m.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
                {
                    var method = ParseMethod(m, sourceLine);
                    if (method != null)
                        decl.Methods.Add(method);
                    continue;
                }

                // Field: Name: Type
                if (m.Contains(':'))
                {
                    var parts = m.Split(':', 2);
                    var fieldName = parts[0].Trim();
                    var fieldType = parts[1].Trim().TrimEnd(';').Trim();
                    if (!string.IsNullOrEmpty(fieldName))
                        decl.Fields.Add(new FieldDeclaration2 { Name = fieldName, TypeSpec = fieldType });
                }
                else if (decl.IsDatabase)
                {
                    // Database body items are table references: "Message<>;" (no colon)
                    var tableRef = m.TrimEnd(';').Trim();
                    if (!string.IsNullOrEmpty(tableRef))
                        decl.Fields.Add(new FieldDeclaration2 { Name = tableRef, TypeSpec = tableRef });
                }
            }
        }

        private List<string> SplitTypeMembers(string body)
        {
            var result = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if ((c == ';' || c == '\n') && depth == 0)
                {
                    var chunk = body.Substring(start, i - start).Trim();
                    if (!string.IsNullOrWhiteSpace(chunk))
                        result.Add(chunk);
                    start = i + 1;
                }
            }

            var last = body.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(last))
                result.Add(last);

            return result;
        }

        private ConstructorDeclaration2? ParseConstructor(string text, int sourceLine)
        {
            // constructor(params) { body }
            var parenStart = text.IndexOf('(');
            if (parenStart < 0)
                return new ConstructorDeclaration2 { SourceLine = sourceLine };

            var parenEnd = FindMatchingParen(text, parenStart);
            if (parenEnd < 0)
                return null;

            var paramText = text.Substring(parenStart + 1, parenEnd - parenStart - 1);
            var parameters = ParseFormalParameters(paramText);

            var braceStart = text.IndexOf('{', parenEnd);
            var ctorBody = new BlockNode2();
            if (braceStart >= 0)
            {
                var braceEnd = FindMatchingBrace(text, braceStart);
                if (braceEnd > braceStart)
                {
                    var bodyText = text.Substring(braceStart + 1, braceEnd - braceStart - 1);
                    ParseBlockStatements(bodyText, ctorBody, sourceLine);
                }
            }

            return new ConstructorDeclaration2
            {
                SourceLine = sourceLine,
                Parameters = parameters,
                Body = ctorBody
            };
        }

        private MethodDeclaration2? ParseMethod(string text, int sourceLine)
        {
            // method Name(params) { body }
            var rest = text.Substring("method".Length).TrimStart();
            var parenStart = rest.IndexOf('(');
            var name = parenStart >= 0 ? rest.Substring(0, parenStart).Trim() : rest.Trim();

            var parameters = new List<ParameterDeclaration2>();
            var methodBody = new BlockNode2();

            if (parenStart >= 0)
            {
                var parenEnd = FindMatchingParen(rest, parenStart);
                if (parenEnd >= 0)
                {
                    var paramText = rest.Substring(parenStart + 1, parenEnd - parenStart - 1);
                    parameters = ParseFormalParameters(paramText);

                    var braceStart = rest.IndexOf('{', parenEnd);
                    if (braceStart >= 0)
                    {
                        var braceEnd = FindMatchingBrace(rest, braceStart);
                        if (braceEnd > braceStart)
                        {
                            var bodyText = rest.Substring(braceStart + 1, braceEnd - braceStart - 1);
                            ParseBlockStatements(bodyText, methodBody, sourceLine);
                        }
                    }
                }
            }

            return new MethodDeclaration2
            {
                Name = name,
                SourceLine = sourceLine,
                Parameters = parameters,
                Body = methodBody
            };
        }

        private List<ParameterDeclaration2> ParseFormalParameters(string paramText)
        {
            var result = new List<ParameterDeclaration2>();
            if (string.IsNullOrWhiteSpace(paramText))
                return result;

            var parts = paramText.Split(',');
            foreach (var part in parts)
            {
                var p = part.Trim();
                if (string.IsNullOrEmpty(p)) continue;

                if (p.Contains(':'))
                {
                    var idx = p.IndexOf(':');
                    result.Add(new ParameterDeclaration2
                    {
                        Name = p.Substring(0, idx).Trim(),
                        TypeSpec = p.Substring(idx + 1).Trim()
                    });
                }
                else
                {
                    result.Add(new ParameterDeclaration2 { Name = p });
                }
            }

            return result;
        }

        private void ParseBlockStatements(string bodyText, BlockNode2 block, int sourceLine)
        {
            var lines = SplitStatements(bodyText);
            foreach (var line in lines)
            {
                var stmt = ParseStatement(line, sourceLine);
                if (stmt != null)
                    block.Statements.Add(stmt);
            }
        }

        private List<string> SplitStatements(string text)
        {
            var result = new List<string>();
            var depth = 0;
            var start = 0;
            var inString = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;

                if (inString) continue;

                if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == '}' || c == ')' || c == ']') depth--;
                else if ((c == ';' || c == '\n') && depth == 0)
                {
                    var chunk = text.Substring(start, i - start).Trim();
                    if (!string.IsNullOrWhiteSpace(chunk))
                        result.Add(chunk);
                    start = i + 1;
                }
            }

            var last = text.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(last))
                result.Add(last);

            return result;
        }

        // ─── Statement parsers ────────────────────────────────────────────────────

        private bool TryParseVarDeclaration(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;
            var t = text;

            // Strip optional "var" keyword
            var hasVar = false;
            if (t.StartsWith("var ", StringComparison.OrdinalIgnoreCase))
            {
                hasVar = true;
                t = t.Substring(4).TrimStart();
            }

            // Without "var", this is an assignment, not a declaration.
            if (!hasVar)
                return false;

            // Find `:=` or `=` assignment (`:=` takes priority to avoid matching `==`)
            var assignIdx = t.IndexOf(":=", StringComparison.Ordinal);
            var assignLen = 2;

            if (assignIdx < 0)
            {
                // Try plain `=` (but not `==`)
                assignIdx = FindPlainAssignment(t);
                assignLen = 1;
            }

            string varName;
            string? explicitType = null;

            if (assignIdx > 0)
            {
                var lhs = t.Substring(0, assignIdx).Trim();
                // Check for "name: Type" pattern
                var colonIdx = lhs.IndexOf(':');
                if (colonIdx > 0)
                {
                    varName = lhs.Substring(0, colonIdx).Trim();
                    explicitType = lhs.Substring(colonIdx + 1).Trim();
                }
                else
                {
                    varName = lhs;
                }

                // Validate variable name
                if (!IsValidIdentifier(varName))
                    return false;

                var rhs = t.Substring(assignIdx + assignLen).Trim();

                // streamwait expr as initializer is not (yet) supported as an expression —
                // match V1 behavior: allocate the variable slot but emit no assignment instructions.
                ExpressionNode2? initExpr = rhs.StartsWith("streamwait ", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ParseExpression(rhs, sourceLine);

                result = new VarDeclarationStatement2
                {
                    SourceLine = sourceLine,
                    VariableName = varName,
                    ExplicitType = explicitType,
                    Initializer = initExpr
                };
                return true;
            }
            else if (hasVar)
            {
                // "var x" without initializer — a simple declaration
                if (!IsValidIdentifier(t))
                    return false;

                result = new VarDeclarationStatement2
                {
                    SourceLine = sourceLine,
                    VariableName = t
                };
                return true;
            }

            return false;
        }

        private bool TryParseReturn(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;
            if (!text.StartsWith("return", StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = text.Substring(6).Trim();
            ExpressionNode2? value = null;
            if (!string.IsNullOrEmpty(rest))
                value = ParseExpression(rest, sourceLine);

            result = new ReturnStatement2 { SourceLine = sourceLine, Value = value };
            return true;
        }

        private bool TryParseIf(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;
            if (!text.StartsWith("if ", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("if(", StringComparison.OrdinalIgnoreCase))
                return false;

            // if (cond) { ... } [else { ... }]
            var rest = text.Substring(2).TrimStart();
            if (!rest.StartsWith("("))
                return false;

            var condEnd = FindMatchingParen(rest, 0);
            if (condEnd < 0)
                return false;

            var condText = rest.Substring(1, condEnd - 1);
            var cond = ParseExpression(condText, sourceLine);

            var afterCond = rest.Substring(condEnd + 1).TrimStart();
            var thenBlock = new BlockNode2();
            var elseBlock = (BlockNode2?)null;

            if (afterCond.StartsWith("{"))
            {
                var braceEnd = FindMatchingBrace(afterCond, 0);
                if (braceEnd >= 0)
                {
                    var bodyText = afterCond.Substring(1, braceEnd - 1);
                    ParseBlockStatements(bodyText, thenBlock, sourceLine);

                    var remaining = afterCond.Substring(braceEnd + 1).TrimStart();
                    if (remaining.StartsWith("else", StringComparison.OrdinalIgnoreCase))
                    {
                        var elseRest = remaining.Substring(4).TrimStart();
                        elseBlock = new BlockNode2();
                        if (elseRest.StartsWith("{"))
                        {
                            var elseBraceEnd = FindMatchingBrace(elseRest, 0);
                            if (elseBraceEnd >= 0)
                            {
                                var elseBodyText = elseRest.Substring(1, elseBraceEnd - 1);
                                ParseBlockStatements(elseBodyText, elseBlock, sourceLine);
                            }
                        }
                        else
                        {
                            var elseStmt = ParseStatement(elseRest, sourceLine);
                            if (elseStmt != null)
                                elseBlock.Statements.Add(elseStmt);
                        }
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(afterCond))
            {
                // Single-statement if without braces: if (cond) stmt;
                var thenStmt = ParseStatement(afterCond, sourceLine);
                if (thenStmt != null)
                    thenBlock.Statements.Add(thenStmt);
            }

            result = new IfStatement2
            {
                SourceLine = sourceLine,
                Condition = cond,
                ThenBlock = thenBlock,
                ElseBlock = elseBlock
            };
            return true;
        }

        private bool TryParseSwitch(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;
            if (!text.StartsWith("switch", StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = text.Substring(6).TrimStart();
            ExpressionNode2 expr;

            if (rest.StartsWith("("))
            {
                var end = FindMatchingParen(rest, 0);
                if (end < 0)
                    return false;
                expr = ParseExpression(rest.Substring(1, end - 1), sourceLine);
                rest = rest.Substring(end + 1).TrimStart();
            }
            else
            {
                // switch without parens — treat whole rest as expression up to {
                var braceIdx = rest.IndexOf('{');
                if (braceIdx < 0)
                    return false;
                expr = ParseExpression(rest.Substring(0, braceIdx).Trim(), sourceLine);
                rest = rest.Substring(braceIdx);
            }

            var switchStmt = new SwitchStatement2 { SourceLine = sourceLine, Expression = expr };

            if (rest.StartsWith("{"))
            {
                var bodyEnd = FindMatchingBrace(rest, 0);
                if (bodyEnd >= 0)
                {
                    var body = rest.Substring(1, bodyEnd - 1);
                    ParseSwitchBody(body, switchStmt, sourceLine);
                }
            }

            result = switchStmt;
            return true;
        }

        private void ParseSwitchBody(string body, SwitchStatement2 switchStmt, int sourceLine)
        {
            // Parse "case X: { ... }" and "default: { ... }"
            var i = 0;
            while (i < body.Length)
            {
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length) break;

                var remaining = body.Substring(i);

                if (remaining.StartsWith("default", StringComparison.OrdinalIgnoreCase))
                {
                    var colonPos = remaining.IndexOf(':');
                    if (colonPos >= 0)
                    {
                        var afterColon = remaining.Substring(colonPos + 1).TrimStart();
                        if (afterColon.StartsWith("{"))
                        {
                            var braceEnd = FindMatchingBrace(afterColon, 0);
                            if (braceEnd >= 0)
                            {
                                var defaultBlock = new BlockNode2();
                                ParseBlockStatements(afterColon.Substring(1, braceEnd - 1), defaultBlock, sourceLine);
                                switchStmt.DefaultBlock = defaultBlock;
                                i += (colonPos + 1) + (afterColon.Length - afterColon.Substring(braceEnd + 1).Length);
                                break;
                            }
                        }
                    }
                    break;
                }

                if (remaining.StartsWith("case ", StringComparison.OrdinalIgnoreCase))
                {
                    var colonPos = remaining.IndexOf(':', 5);
                    if (colonPos < 0) break;

                    var patternText = remaining.Substring(5, colonPos - 5).Trim();
                    var pattern = ParseExpression(patternText, sourceLine);

                    var afterColon = remaining.Substring(colonPos + 1).TrimStart();
                    var caseBody = new BlockNode2();

                    if (afterColon.StartsWith("{"))
                    {
                        var braceEnd = FindMatchingBrace(afterColon, 0);
                        if (braceEnd >= 0)
                        {
                            ParseBlockStatements(afterColon.Substring(1, braceEnd - 1), caseBody, sourceLine);
                            switchStmt.Cases.Add(new SwitchCaseNode2
                            {
                                SourceLine = sourceLine,
                                Pattern = pattern,
                                Body = caseBody
                            });
                            i += (colonPos + 1) + (afterColon.Length - afterColon.Substring(braceEnd + 1).Length);
                            continue;
                        }
                    }
                }

                break;
            }
        }

        private bool TryParseForLoop(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;
            if (!text.StartsWith("for ", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("for(", StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = text.Substring(3).TrimStart();

            // for streamwait [sync] by <waitType> (stream, delta [, aggregate]) { body }
            if (rest.StartsWith("streamwait", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseStreamWaitByDeltaLoop(rest, sourceLine, out result))
                    return true;
            }

            // for (var x in stream) { ... }
            if (!rest.StartsWith("("))
                return false;

            var parenEnd = FindMatchingParen(rest, 0);
            if (parenEnd < 0)
                return false;

            var header = rest.Substring(1, parenEnd - 1);
            // Parse: var x in stream
            var inIdx = header.LastIndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (inIdx < 0)
                return false;

            var varPart = header.Substring(0, inIdx).Trim();
            if (varPart.StartsWith("var ", StringComparison.OrdinalIgnoreCase))
                varPart = varPart.Substring(4).Trim();

            var streamPart = header.Substring(inIdx + 4).Trim();
            var stream = ParseExpression(streamPart, sourceLine);

            var body = new BlockNode2();
            var afterParen = rest.Substring(parenEnd + 1).TrimStart();
            if (afterParen.StartsWith("{"))
            {
                var braceEnd = FindMatchingBrace(afterParen, 0);
                if (braceEnd >= 0)
                    ParseBlockStatements(afterParen.Substring(1, braceEnd - 1), body, sourceLine);
            }

            result = new StreamWaitForLoop2
            {
                SourceLine = sourceLine,
                VariableName = varPart,
                Stream = stream,
                Body = body
            };
            return true;
        }

        /// <summary>
        /// Parses: streamwait [sync] by &lt;waitType&gt; (streamExpr, deltaVar [, aggregateVar]) { body }
        /// The caller has already consumed "for " and passes the rest starting with "streamwait".
        /// </summary>
        private bool TryParseStreamWaitByDeltaLoop(string rest, int sourceLine, out StatementNode2? result)
        {
            result = null;

            // Skip optional "sync" keyword after "streamwait"
            var after = rest.Substring("streamwait".Length).TrimStart();
            if (after.StartsWith("sync ", StringComparison.OrdinalIgnoreCase))
                after = after.Substring(4).TrimStart();

            // Expect "by"
            if (!after.StartsWith("by ", StringComparison.OrdinalIgnoreCase))
                return false;
            after = after.Substring(2).TrimStart();

            // Read waitType (identifier before '(')
            var parenIdx = after.IndexOf('(');
            if (parenIdx <= 0)
                return false;

            var waitType = after.Substring(0, parenIdx).Trim();
            if (string.IsNullOrEmpty(waitType))
                return false;

            var parenPart = after.Substring(parenIdx);
            var parenEnd = FindMatchingParen(parenPart, 0);
            if (parenEnd < 0)
                return false;

            // Parse (streamExpr, deltaVar [, aggregateVar])
            var argsText = parenPart.Substring(1, parenEnd - 1);
            var argParts = SplitByComma(argsText);
            if (argParts.Count < 2)
                return false;

            var streamExprText = argParts[0].Trim();
            var deltaVarName = argParts[1].Trim();
            var aggregateVarName = argParts.Count >= 3 ? argParts[2].Trim() : "";

            var streamExpr = ParseExpression(streamExprText, sourceLine);

            // Parse body { ... }
            var afterParen = parenPart.Substring(parenEnd + 1).TrimStart();
            var body = new BlockNode2();
            if (afterParen.StartsWith("{"))
            {
                var braceEnd = FindMatchingBrace(afterParen, 0);
                if (braceEnd >= 0)
                    ParseBlockStatements(afterParen.Substring(1, braceEnd - 1), body, sourceLine);
            }

            result = new StreamWaitByDeltaLoop2
            {
                SourceLine = sourceLine,
                Stream = streamExpr,
                WaitType = waitType,
                DeltaVarName = deltaVarName,
                AggregateVarName = aggregateVarName,
                Body = body
            };
            return true;
        }

        private bool TryParseAssignment(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;

            // Check for compound assignment: +=
            var plusEqIdx = FindOperatorOutsideStrings(text, "+=");
            if (plusEqIdx > 0)
            {
                var compoundLhs = text.Substring(0, plusEqIdx).Trim();
                if (!compoundLhs.StartsWith("var ", StringComparison.OrdinalIgnoreCase) && IsValidLValue(compoundLhs))
                {
                    var compoundRhs = text.Substring(plusEqIdx + 2).Trim();
                    result = new CompoundAssignmentStatement2
                    {
                        SourceLine = sourceLine,
                        Target = ParseExpression(compoundLhs, sourceLine),
                        Value = ParseExpression(compoundRhs, sourceLine),
                        Operator = "+"
                    };
                    return true;
                }
            }

            // Try `:=` first, then plain `=`
            var assignIdx = text.IndexOf(":=", StringComparison.Ordinal);
            var assignLen = 2;

            if (assignIdx <= 0)
            {
                assignIdx = FindPlainAssignment(text);
                assignLen = 1;
            }

            if (assignIdx <= 0)
                return false;

            var lhs = text.Substring(0, assignIdx).Trim();
            // Don't treat variable declarations as assignments
            if (lhs.StartsWith("var ", StringComparison.OrdinalIgnoreCase))
                return false;

            // Must be a valid expression target (identifier or member access)
            if (!IsValidLValue(lhs))
                return false;

            var rhs = text.Substring(assignIdx + assignLen).Trim();
            var target = ParseExpression(lhs, sourceLine);
            var value = ParseExpression(rhs, sourceLine);

            result = new AssignmentStatement2
            {
                SourceLine = sourceLine,
                Target = target,
                Value = value
            };
            return true;
        }

        private bool TryParseCallStatement(string text, int sourceLine, out StatementNode2? result)
        {
            result = null;

            // await expr
            if (text.StartsWith("await ", StringComparison.OrdinalIgnoreCase))
            {
                var operand = ParseExpression(text.Substring(6).Trim(), sourceLine);
                result = new CallStatement2
                {
                    SourceLine = sourceLine,
                    Callee = new VariableExpression2 { Name = "await", SourceLine = sourceLine },
                    Arguments = new List<ExpressionNode2> { operand }
                };
                return true;
            }

            // streamwait funcName(args) — pipeline result to function via streamwait opcode
            if (text.StartsWith("streamwait ", StringComparison.OrdinalIgnoreCase))
            {
                var callText = text.Substring("streamwait".Length).TrimStart();
                var swParenIdx = FindFirstParenOutsideStrings(callText);
                if (swParenIdx > 0)
                {
                    var funcName = callText.Substring(0, swParenIdx).Trim();
                    var swArgs = ParseArgumentList(callText.Substring(swParenIdx), sourceLine);
                    result = new StreamWaitCallStatement2
                    {
                        SourceLine = sourceLine,
                        FunctionName = funcName,
                        Arguments = swArgs
                    };
                    return true;
                }
            }

            // Identifier followed by '(' — function/procedure call
            var parenIdx = FindFirstParenOutsideStrings(text);
            if (parenIdx > 0)
            {
                var callee = text.Substring(0, parenIdx).Trim();
                var argsText = text.Substring(parenIdx);

                if (!IsValidCallTarget(callee))
                    return false;

                var calleeExpr = ParseExpression(callee, sourceLine);
                var args = ParseArgumentList(argsText, sourceLine);

                result = new CallStatement2
                {
                    SourceLine = sourceLine,
                    Callee = calleeExpr,
                    Arguments = args
                };
                return true;
            }

            // Plain identifier call (no parens) — zero-arg call
            if (IsValidIdentifier(text))
            {
                result = new CallStatement2
                {
                    SourceLine = sourceLine,
                    Callee = new VariableExpression2 { Name = text, SourceLine = sourceLine }
                };
                return true;
            }

            return false;
        }

        // ─── Expression parser ────────────────────────────────────────────────────

        /// <summary>Parse an expression text into an <see cref="ExpressionNode2"/>.</summary>
        protected internal ExpressionNode2 ParseExpression(string text, int sourceLine)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new LiteralExpression2 { Kind = LiteralKind2.Null, SourceLine = sourceLine };

            var t = text.Trim();

            // String literal
            if (t.StartsWith("\"") && t.EndsWith("\"") && t.Length >= 2)
            {
                return new LiteralExpression2
                {
                    SourceLine = sourceLine,
                    Kind = LiteralKind2.String,
                    Value = t.Substring(1, t.Length - 2)
                };
            }

            // Integer literal
            if (long.TryParse(t, out var longVal))
                return new LiteralExpression2 { SourceLine = sourceLine, Kind = LiteralKind2.Integer, Value = longVal };

            // Float literal
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
                return new LiteralExpression2 { SourceLine = sourceLine, Kind = LiteralKind2.Float, Value = dblVal };

            // Boolean
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase))
                return new LiteralExpression2 { SourceLine = sourceLine, Kind = LiteralKind2.Boolean, Value = true };
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase))
                return new LiteralExpression2 { SourceLine = sourceLine, Kind = LiteralKind2.Boolean, Value = false };

            // Null
            if (string.Equals(t, "null", StringComparison.OrdinalIgnoreCase))
                return new LiteralExpression2 { SourceLine = sourceLine, Kind = LiteralKind2.Null };

            // Memory slot: [N]
            if (t.StartsWith("[") && t.EndsWith("]"))
            {
                var inner = t.Substring(1, t.Length - 2).Trim();
                if (int.TryParse(inner, out var slotIdx))
                    return new MemorySlotExpression2 { SourceLine = sourceLine, SlotIndex = slotIdx };
            }

            // Symbolic variable: :time, :now etc.
            if (t.Length >= 2 && t[0] == ':')
            {
                var symName = t.Substring(1).Trim();
                if (!string.IsNullOrEmpty(symName) && IsSimpleIdentifier(symName))
                    return new SymbolicExpression2 { SourceLine = sourceLine, Name = symName };
            }

            // Object literal: { key: val, ... }
            if (t.StartsWith("{") && t.EndsWith("}"))
            {
                var braceEnd = FindMatchingBrace(t, 0);
                if (braceEnd == t.Length - 1)
                {
                    var objLit = ParseObjectLiteral(t.Substring(1, t.Length - 2), sourceLine);
                    if (objLit != null) return objLit;
                }
            }

            // Await expression: await expr
            if (t.StartsWith("await ", StringComparison.OrdinalIgnoreCase))
            {
                var operand = ParseExpression(t.Substring(6).Trim(), sourceLine);
                return new AwaitExpression2 { SourceLine = sourceLine, Operand = operand };
            }

            // Generic type: name<TypeArg> — but NOT member access expressions containing <>
            // Only match if there's no dot before the < and no dots in typeArgs that suggest member access
            if (t.Contains('<') && t.EndsWith(">"))
            {
                var ltIdx = t.IndexOf('<');
                var typeName = t.Substring(0, ltIdx).Trim();
                var typeArg = t.Substring(ltIdx + 1, t.Length - ltIdx - 2).Trim();
                if (IsSimpleIdentifier(typeName) && !typeName.Contains('.'))
                    return new GenericTypeExpression2 { SourceLine = sourceLine, TypeName = typeName, TypeArg = typeArg };
            }

            // Lambda expression: param => body_expr (check BEFORE member access to avoid misparse of "_ => _.Foo")
            // Must be checked before FindOutermostDot, otherwise "_ => _.Foo" splits at the dot in "_.Foo".
            {
                var earlyArrowIdx = FindArrowOutsideParens(t);
                if (earlyArrowIdx > 0)
                {
                    var earlyParam = t.Substring(0, earlyArrowIdx).Trim();
                    var earlyBody = t.Substring(earlyArrowIdx + 2).Trim();
                    if (IsSimpleIdentifier(earlyParam) && !string.IsNullOrEmpty(earlyBody))
                    {
                        var earlyBodyExpr = ParseExpression(earlyBody, sourceLine);
                        var earlyBlock = new BlockNode2
                        {
                            Statements = new System.Collections.Generic.List<StatementNode2>
                            {
                                new ExpressionStatement2 { Expression = earlyBodyExpr, SourceLine = sourceLine }
                            }
                        };
                        return new LambdaExpression2
                        {
                            SourceLine = sourceLine,
                            Parameters = new System.Collections.Generic.List<string> { earlyParam },
                            Body = earlyBlock
                        };
                    }
                }
            }

            // Member access + call chain: parse left-to-right.
            // Find the outermost dot (handles data!.id by treating '!' as part of left side).
            var dotIdx = FindOutermostDot(t);
            if (dotIdx > 0)
            {
                var leftPart = t.Substring(0, dotIdx).Trim();
                var rightPart = t.Substring(dotIdx + 1).Trim();

                // Handle null-assertion suffix: data! → NullAssertExpression2
                ExpressionNode2 leftExpr;
                if (leftPart.EndsWith("!") && leftPart.Length > 1)
                {
                    var baseExpr = ParseExpression(leftPart.Substring(0, leftPart.Length - 1).Trim(), sourceLine);
                    leftExpr = new NullAssertExpression2 { SourceLine = sourceLine, Operand = baseExpr };
                }
                else
                {
                    leftExpr = ParseExpression(leftPart, sourceLine);
                }

                // Right part might be a call: Method(args)  OR  Path.To.Method(args)
                var rParenIdx = FindFirstParenOutsideStrings(rightPart);
                if (rParenIdx > 0)
                {
                    var methodChain = rightPart.Substring(0, rParenIdx).Trim();
                    var argsText = rightPart.Substring(rParenIdx);
                    var args = ParseArgumentList(argsText, sourceLine);

                    // If methodChain contains dots, build intermediate member-access nodes.
                    // E.g. for "Message<>.find": receiver = MemberAccess(leftExpr, "Message<>"), method = "find"
                    ExpressionNode2 receiverExpr = leftExpr;
                    var actualMethodName = methodChain;
                    if (methodChain.Contains('.'))
                    {
                        var segments = SplitSimpleMemberChain(methodChain);
                        actualMethodName = segments[segments.Count - 1];
                        for (var si = 0; si < segments.Count - 1; si++)
                        {
                            receiverExpr = new MemberAccessExpression2
                            {
                                SourceLine = sourceLine,
                                Object = receiverExpr,
                                MemberName = segments[si]
                            };
                        }
                    }

                    return new CallExpression2
                    {
                        SourceLine = sourceLine,
                        Callee = new MemberAccessExpression2
                        {
                            SourceLine = sourceLine,
                            Object = receiverExpr,
                            MemberName = actualMethodName
                        },
                        Arguments = args,
                        IsObjectCall = true
                    };
                }

                return new MemberAccessExpression2
                {
                    SourceLine = sourceLine,
                    Object = leftExpr,
                    MemberName = rightPart
                };
            }

            // Binary operators: +, -, *, /, ==, !=, <, >, <=, >=, &&, ||
            foreach (var op in new[] { "||", "&&", "==", "!=", "<=", ">=", "+", "-", "*", "/" })
            {
                var opIdx = FindOperatorOutsideStrings(t, op);
                if (opIdx > 0)
                {
                    return new BinaryExpression2
                    {
                        SourceLine = sourceLine,
                        Left = ParseExpression(t.Substring(0, opIdx).Trim(), sourceLine),
                        Operator = op,
                        Right = ParseExpression(t.Substring(opIdx + op.Length).Trim(), sourceLine)
                    };
                }
            }

            // Postfix null-assertion: expr!  (standalone, no dot after)
            if (t.EndsWith("!") && t.Length > 1)
            {
                var inner2 = t.Substring(0, t.Length - 1).Trim();
                if (!string.IsNullOrEmpty(inner2))
                    return new NullAssertExpression2
                    {
                        SourceLine = sourceLine,
                        Operand = ParseExpression(inner2, sourceLine)
                    };
            }

            // Unary NOT: !expr
            if (t.StartsWith("!") && t.Length > 1)
                return new UnaryExpression2
                {
                    SourceLine = sourceLine,
                    Operator = "!",
                    Operand = ParseExpression(t.Substring(1).Trim(), sourceLine)
                };

            // Function call: Name(args)
            var fnParenIdx = FindFirstParenOutsideStrings(t);
            if (fnParenIdx > 0)
            {
                var callee = t.Substring(0, fnParenIdx).Trim();
                if (IsValidCallTarget(callee))
                {
                    var args = ParseArgumentList(t.Substring(fnParenIdx), sourceLine);
                    return new CallExpression2
                    {
                        SourceLine = sourceLine,
                        Callee = new VariableExpression2 { Name = callee, SourceLine = sourceLine },
                        Arguments = args
                    };
                }
            }

            // Parenthesized expression
            if (t.StartsWith("(") && t.EndsWith(")"))
            {
                var innerEnd = FindMatchingParen(t, 0);
                if (innerEnd == t.Length - 1)
                    return ParseExpression(t.Substring(1, t.Length - 2), sourceLine);
            }

            // Lambda expression: param => body_expr
            // Matches patterns like: _ => _.Member = value
            var arrowIdx = FindArrowOutsideParens(t);
            if (arrowIdx > 0)
            {
                var paramName = t.Substring(0, arrowIdx).Trim();
                var bodyText = t.Substring(arrowIdx + 2).Trim();
                if (IsSimpleIdentifier(paramName) && !string.IsNullOrEmpty(bodyText))
                {
                    // Parse body as an expression
                    var bodyExpr = ParseExpression(bodyText, sourceLine);
                    // Wrap in a block with a single expression statement
                    var body = new BlockNode2
                    {
                        Statements = new System.Collections.Generic.List<StatementNode2>
                        {
                            new ExpressionStatement2 { Expression = bodyExpr, SourceLine = sourceLine }
                        }
                    };
                    return new LambdaExpression2
                    {
                        SourceLine = sourceLine,
                        Parameters = new System.Collections.Generic.List<string> { paramName },
                        Body = body
                    };
                }
            }

            // Identifier or qualified name
            return new VariableExpression2 { SourceLine = sourceLine, Name = t };
        }

        /// <summary>Parse an object literal body: "key1: val1, key2: val2".</summary>
        private ObjectLiteralExpression2? ParseObjectLiteral(string body, int sourceLine)
        {
            if (string.IsNullOrWhiteSpace(body))
                return new ObjectLiteralExpression2 { SourceLine = sourceLine };

            var parts = SplitByComma(body);
            var props = new List<(string Key, ExpressionNode2 Value)>();
            foreach (var part in parts)
            {
                var p = part.Trim();
                if (string.IsNullOrEmpty(p)) continue;

                // Each part: "key: value" or just "key" (shorthand = variable with same name)
                var colonIdx = p.IndexOf(':');
                if (colonIdx <= 0)
                {
                    // Shorthand: { foo } → foo: foo
                    var key = p.TrimEnd(';').Trim();
                    if (!string.IsNullOrEmpty(key))
                        props.Add((key, new VariableExpression2 { SourceLine = sourceLine, Name = key }));
                    continue;
                }

                var propKey = p.Substring(0, colonIdx).Trim();
                var propVal = p.Substring(colonIdx + 1).Trim().TrimEnd(';').Trim();
                if (string.IsNullOrEmpty(propKey)) return null;

                props.Add((propKey, ParseExpression(propVal, sourceLine)));
            }

            return new ObjectLiteralExpression2 { SourceLine = sourceLine, Properties = props };
        }

        private List<ExpressionNode2> ParseArgumentList(string argsText, int sourceLine)
        {
            var result = new List<ExpressionNode2>();
            if (string.IsNullOrWhiteSpace(argsText))
                return result;

            var t = argsText.Trim();
            if (!t.StartsWith("("))
                return result;

            var parenEnd = FindMatchingParen(t, 0);
            if (parenEnd < 0)
                return result;

            var inner = t.Substring(1, parenEnd - 1);
            if (string.IsNullOrWhiteSpace(inner))
                return result;

            // Split by comma respecting nesting
            var args = SplitByComma(inner);
            foreach (var arg in args)
            {
                var trimmed = arg.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    result.Add(ParseExpression(trimmed, sourceLine));
            }

            return result;
        }

        private List<string> SplitByComma(string text)
        {
            var result = new List<string>();
            var depth = 0;
            var start = 0;
            var inString = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (inString) continue;

                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }

            result.Add(text.Substring(start));
            return result;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private static bool IsValidIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_' && s[0] != '@')
                return false;
            foreach (var c in s)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != ':')
                    return false;
            return true;
        }

        /// <summary>Simple identifier: letters, digits, underscore only (no dots/colons).</summary>
        private static bool IsSimpleIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_')
                return false;
            foreach (var c in s)
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            return true;
        }

        private static bool IsValidLValue(string s)
        {
            // An LValue can be: identifier, member access (a.b), or indexed (a[b])
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("\"")) return false;
            if (char.IsDigit(s[0])) return false;
            return true;
        }

        private static bool IsValidCallTarget(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("\"")) return false;
            return true;
        }

        /// <summary>
        /// Find the index of a plain '=' that is not part of ':=', '==', '!=', '&lt;=', '&gt;=' or '+='.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindPlainAssignment(string text)
        {
            var depth = 0;
            var inString = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (inString) continue;

                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == '=' && depth == 0)
                {
                    // Skip if part of :=, ==, !=, <=, >=, +=, -=
                    if (i > 0 && (text[i - 1] == ':' || text[i - 1] == '!' || text[i - 1] == '<' || text[i - 1] == '>' || text[i - 1] == '+' || text[i - 1] == '-'))
                        continue;
                    if (i + 1 < text.Length && text[i + 1] == '=')
                        continue;
                    return i;
                }
            }
            return -1;
        }

        private static int FindMatchingParen(string text, int openPos)
        {
            var depth = 0;
            for (var i = openPos; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindMatchingBrace(string text, int openPos)
        {
            var depth = 0;
            var inString = false;
            for (var i = openPos; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (inString) continue;

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindFirstParenOutsideStrings(string text)
        {
            var inString = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (!inString && c == '(')
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Split a simple member chain like "Message&lt;&gt;.find" or "a.b.c" into ["Message&lt;&gt;", "find"] or ["a","b","c"].
        /// Handles segments that contain &lt;&gt; (no nesting inside segments is expected here).
        /// </summary>
        private static List<string> SplitSimpleMemberChain(string chain)
        {
            var result = new List<string>();
            var start = 0;
            var depth = 0; // track < > nesting
            for (var i = 0; i < chain.Length; i++)
            {
                var c = chain[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == '.' && depth == 0)
                {
                    var seg = chain.Substring(start, i - start).Trim();
                    if (!string.IsNullOrEmpty(seg))
                        result.Add(seg);
                    start = i + 1;
                }
            }
            var last = chain.Substring(start).Trim();
            if (!string.IsNullOrEmpty(last))
                result.Add(last);
            return result;
        }

        private static int FindOutermostDot(string text)
        {
            // Find a dot NOT inside parens/brackets/strings
            var depth = 0;
            var inString = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (inString) continue;

                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == '.' && depth == 0)
                    return i;
            }
            return -1;
        }

        private static int FindOperatorOutsideStrings(string text, string op)
        {
            var depth = 0;
            var inString = false;
            for (var i = 0; i <= text.Length - op.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (inString) continue;

                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (depth == 0 && text.Substring(i, op.Length) == op)
                    return i;
            }
            return -1;
        }

        /// <summary>Find the index of '=>' outside parentheses, brackets, braces, and strings.</summary>
        private static int FindArrowOutsideParens(string text)
        {
            if (text.Length < 3) return -1;
            var depth = 0;
            var inString = false;
            for (var i = 0; i <= text.Length - 2; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                    inString = !inString;
                if (inString) continue;

                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (depth == 0 && c == '=' && i + 1 < text.Length && text[i + 1] == '>')
                {
                    // Make sure it's not '==' or other operator
                    if (i == 0 || (text[i - 1] != '<' && text[i - 1] != '>' && text[i - 1] != '!' && text[i - 1] != '='))
                        return i;
                }
            }
            return -1;
        }
    }
}
