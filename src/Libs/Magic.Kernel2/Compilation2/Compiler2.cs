using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;

namespace Magic.Kernel2.Compilation2
{
    /// <summary>
    /// Magic compiler 2.0 — the top-level compilation orchestrator.
    /// <para>
    /// Architecture improvements over v1.0 (<see cref="Magic.Kernel.Compilation.Compiler"/>):
    /// </para>
    /// <list type="bullet">
    /// <item>
    ///   <term>Full AST parsing</term>
    ///   <description>
    ///     <see cref="Parser2"/> produces a <strong>complete, fully-typed AST</strong> in one pass.
    ///     No raw statement text (<c>StatementLineNode</c>) is stored for deferred lowering.
    ///   </description>
    /// </item>
    /// <item>
    ///   <term>Semantic analysis on typed AST</term>
    ///   <description>
    ///     <see cref="SemanticAnalyzer2"/> walks typed AST nodes directly, building the symbol table
    ///     (variable slots, type registrations, method signatures) without text re-parsing.
    ///   </description>
    /// </item>
    /// <item>
    ///   <term>No lowering phase</term>
    ///   <description>
    ///     <see cref="Assembler2"/> generates executable <see cref="Command"/> bytecode by walking
    ///     typed AST nodes directly. There is no <c>StatementLoweringCompiler</c> text-lowering step.
    ///   </description>
    /// </item>
    /// </list>
    /// </summary>
    public class Compiler2
    {
        private readonly Parser2 _parser;
        private readonly SemanticAnalyzer2 _semanticAnalyzer;

        public Compiler2()
        {
            _parser = new Parser2();
            _semanticAnalyzer = new SemanticAnalyzer2();
        }

        /// <summary>Compile source code from a file.</summary>
        public async Task<CompilationResult> CompileFileAsync(string filePath)
        {
            try
            {
                var sourceCode = await File.ReadAllTextAsync(filePath);
                return await CompileAsync(sourceCode, filePath);
            }
            catch (Exception ex)
            {
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>Compile source code string.</summary>
        public Task<CompilationResult> CompileAsync(string sourceCode)
            => CompileAsync(sourceCode, null);

        /// <summary>
        /// Compile source code string with optional source path context.
        /// Pipeline: Parse → SemanticAnalysis → (no lowering needed, AST is fully typed) → Assemble.
        /// </summary>
        public Task<CompilationResult> CompileAsync(string sourceCode, string? sourcePath)
        {
            try
            {
                // Step 1: Parse into a complete, fully-typed AST.
                // Unlike v1.0, no StatementLineNode raw text is produced — the AST is complete.
                var programAst = _parser.ParseProgram(sourceCode);

                // Step 2: Semantic analysis — walks typed AST, builds symbol table, resolves types.
                // Unlike v1.0, this does NOT call StatementLoweringCompiler on raw text.
                var analyzed = _semanticAnalyzer.Analyze(programAst);

                // Step 3: Build the ExecutableUnit from the analyzed program.
                var unit = new ExecutableUnit
                {
                    Version = programAst.Version,
                    Name = programAst.ProgramName,
                    Module = programAst.Module,
                    System = programAst.System,
                    EntryPoint = analyzed.EntryPoint
                };

                foreach (var (name, proc) in analyzed.Procedures)
                    unit.Procedures[name] = proc;

                foreach (var (name, func) in analyzed.Functions)
                    unit.Functions[name] = func;

                if (sourcePath != null)
                    unit.AttachSourcePath = Path.GetFullPath(sourcePath);

                unit.StampListingLineNumbers();

                return Task.FromResult(new CompilationResult
                {
                    Success = true,
                    Result = unit
                });
            }
            catch (CompilationException cex)
            {
                var (line, column) = GetLineAndColumn(sourceCode, cex.Position);
                var posSuffix = cex.Position >= 0
                    ? $" (line {line}, column {column})"
                    : string.Empty;

                return Task.FromResult(new CompilationResult
                {
                    Success = false,
                    ErrorMessage = cex.Message + posSuffix
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new CompilationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        private static (int Line, int Column) GetLineAndColumn(string source, int position)
        {
            if (position < 0 || position > source.Length)
                return (0, 0);

            var line = 1;
            var col = 1;
            for (var i = 0; i < position; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
            return (line, col);
        }
    }
}
