using System;
using System.Collections.Generic;
using System.Linq;
using Magic.Kernel.Compilation;
using Magic.Kernel2.Compilation2.Ast2;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Magic compiler 2.0 parser.
    /// <para>
    /// Key difference from v1.0: this parser builds a <strong>complete, fully-typed AST</strong>
    /// immediately during parsing. No <c>StatementLineNode</c> raw-text is stored for later
    /// lowering. Every statement is parsed into its typed AST representation during this phase,
    /// so the <see cref="SemanticAnalyzer2"/> and <see cref="Assembler2"/> can walk the tree
    /// directly — no lowering pass is needed.
    /// </para>
    /// </summary>
    public class Parser2
    {
        private readonly Magic.Kernel.Compilation.Parser _v1Parser;
        private readonly StatementParser2 _statementParser;

        public Parser2()
        {
            _v1Parser = new Magic.Kernel.Compilation.Parser();
            _statementParser = new StatementParser2();
        }

        /// <summary>
        /// Parse the given AGI source code into a complete AST.
        /// Unlike v1.0, the body of every procedure, function, and entrypoint is
        /// fully parsed into typed statement nodes — not deferred as raw text.
        /// </summary>
        public ProgramNode2 ParseProgram(string sourceCode)
        {
            // Use v1 parser to get the high-level structure (headers, procedure/function boundaries).
            // Then we lift each body's StatementLineNode raw-text into proper typed AST nodes.
            var v1Structure = _v1Parser.ParseProgram(sourceCode);

            var program = new ProgramNode2
            {
                Version = v1Structure.Version,
                ProgramName = v1Structure.ProgramName,
                Module = v1Structure.Module,
                System = v1Structure.System,
            };

            // Parse type declarations.
            foreach (var typeNode in v1Structure.Types)
            {
                var decl = _statementParser.ParseTypeDeclaration(typeNode.Text, typeNode.SourceLine);
                if (decl != null)
                    program.TypeDeclarations.Add(decl);
            }

            // Parse use directives.
            foreach (var use in v1Structure.UseDirectives)
            {
                program.UseDirectives.Add(new UseDirectiveNode2
                {
                    ModulePath = use.ModulePath ?? "",
                    SourceLine = 0,
                    Imports = use.Signatures?.Select(i => new UseImport2
                    {
                        Kind = i.Kind ?? "",
                        Name = i.Name ?? ""
                    }).ToList() ?? new List<UseImport2>()
                });
            }

            // Parse procedure bodies into fully-typed AST.
            foreach (var (name, procNode) in v1Structure.Procedures)
            {
                var decl = new ProcedureDeclarationNode2
                {
                    Name = name,
                    SourceLine = 0,
                    Parameters = procNode.Parameters.Select(p => new ParameterDeclaration2 { Name = p }).ToList()
                };
                decl.Body = ParseBodyNode(procNode.Body);
                program.Procedures.Add(decl);
            }

            // Parse function bodies into fully-typed AST.
            foreach (var (name, funcNode) in v1Structure.Functions)
            {
                var decl = new FunctionDeclarationNode2
                {
                    Name = name,
                    SourceLine = 0,
                    Parameters = funcNode.Parameters.Select(p => new ParameterDeclaration2 { Name = p }).ToList()
                };
                decl.Body = ParseBodyNode(funcNode.Body);
                program.Functions.Add(decl);
            }

            // Parse entrypoint body into fully-typed AST.
            if (v1Structure.EntryPoint != null && v1Structure.EntryPoint.Count > 0)
            {
                program.EntryPoint = new BlockNode2();
                foreach (var stmt in v1Structure.EntryPoint)
                    ParseAndAddStatement(stmt, program.EntryPoint.Statements);
            }

            return program;
        }

        private BlockNode2 ParseBodyNode(Magic.Kernel.Compilation.Ast.BodyNode body)
        {
            var block = new BlockNode2();
            foreach (var stmt in body.Statements)
                ParseAndAddStatement(stmt, block.Statements);
            return block;
        }

        private void ParseAndAddStatement(
            Magic.Kernel.Compilation.Ast.StatementNode v1Stmt,
            List<StatementNode2> output)
        {
            switch (v1Stmt)
            {
                case Magic.Kernel.Compilation.Ast.StatementLineNode rawLine:
                {
                    // v1.0 stored raw text here for deferred lowering.
                    // v2.0: parse it immediately into typed AST.
                    var parsed = _statementParser.ParseStatement(rawLine.Text, rawLine.SourceLine);
                    if (parsed != null)
                        output.Add(parsed);
                    break;
                }

                case Magic.Kernel.Compilation.Ast.InstructionNode instrNode:
                {
                    // Already a typed instruction (from asm {} block).
                    var instr = new InstructionStatement2
                    {
                        Opcode = instrNode.Opcode,
                        SourceLine = instrNode.SourceLine
                    };
                    foreach (var param in instrNode.Parameters)
                        instr.Parameters.Add(ConvertParam(param));
                    output.Add(instr);
                    break;
                }

                case Magic.Kernel.Compilation.Ast.NestedFunctionNode nested:
                {
                    // Nested function/procedure — will be collected by SemanticAnalyzer2.
                    var nestedDecl = nested.IsProcedure
                        ? (StatementNode2)new NestedProcedureStatement2
                        {
                            Name = nested.Name,
                            Parameters = nested.Parameters.Select(p => new ParameterDeclaration2 { Name = p }).ToList(),
                            Body = ParseBodyNode(nested.Body)
                        }
                        : new NestedFunctionStatement2
                        {
                            Name = nested.Name,
                            Parameters = nested.Parameters.Select(p => new ParameterDeclaration2 { Name = p }).ToList(),
                            Body = ParseBodyNode(nested.Body)
                        };
                    output.Add(nestedDecl);
                    break;
                }

                default:
                    // Fallback: emit a raw text statement for anything else.
                    break;
            }
        }

        private static InstructionParam2 ConvertParam(Magic.Kernel.Compilation.Ast.ParameterNode param)
        {
            return param switch
            {
                Magic.Kernel.Compilation.Ast.StringParameterNode s =>
                    new InstructionParam2 { Name = s.Name, Value = s.Value, ValueType = "string" },
                Magic.Kernel.Compilation.Ast.IndexParameterNode idx =>
                    new InstructionParam2 { Name = idx.Name, Value = idx.Value, ValueType = "index" },
                Magic.Kernel.Compilation.Ast.WeightParameterNode w =>
                    new InstructionParam2 { Name = w.Name, Value = w.Value, ValueType = "weight" },
                Magic.Kernel.Compilation.Ast.DimensionsParameterNode d =>
                    new InstructionParam2 { Name = d.Name, Value = d.Values, ValueType = "dimensions" },
                Magic.Kernel.Compilation.Ast.DataParameterNode data =>
                    new InstructionParam2 { Name = data.Name, Value = data.Value, ValueType = $"data:{data.Type}" },
                Magic.Kernel.Compilation.Ast.MemoryParameterNode mem =>
                    new InstructionParam2 { Name = mem.Name, Value = mem.Index, ValueType = "memory" },
                Magic.Kernel.Compilation.Ast.FunctionNameParameterNode fn =>
                    new InstructionParam2 { Name = fn.Name, Value = fn.FunctionName, ValueType = "function" },
                _ => new InstructionParam2 { Name = param.Name, Value = param.ToString(), ValueType = "unknown" }
            };
        }
    }
}
