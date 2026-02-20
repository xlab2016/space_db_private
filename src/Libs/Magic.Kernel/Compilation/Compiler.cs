using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    public class Compiler
    {
        public async Task<List<CompilationResult>> CompileDirectoryAsync(string directoryPath)
        {
            var result = new List<CompilationResult>();
            var sourceFiles = Directory.GetFiles(directoryPath, "*.agi", SearchOption.AllDirectories);
            foreach (var file in sourceFiles)
            {
                var fileResult = await CompileFileAsync(file);
                result.Add(fileResult);
            }
            return result;
        }

        public async Task<CompilationResult> CompileFileAsync(string filePath)
        {
            string sourceCode = await File.ReadAllTextAsync(filePath);
            return await CompileAsync(sourceCode);
        }

        public async Task<CompilationResult> CompileAsync(string sourceCode)
        {
            try
            {
                var parser = new Parser();
                var semanticAnalyzer = new SemanticAnalyzer();
                var executableUnit = new ExecutableUnit();

                // Парсим структуру программы
                var programStructure = parser.ParseProgram(sourceCode);

                // Устанавливаем метаданные
                executableUnit.Version = programStructure.Version;
                executableUnit.Name = programStructure.ProgramName;
                executableUnit.Module = programStructure.Module;

                // Компилируем entrypoint
                if (programStructure.EntryPoint != null && programStructure.EntryPoint.Count > 0)
                {
                    foreach (var instruction in programStructure.EntryPoint)
                    {
                        var ast = parser.Parse(instruction);
                        var command = semanticAnalyzer.Analyze(ast);
                        executableUnit.EntryPoint.Add(command);
                    }
                }
                else
                {
                    // Fallback: если EntryPoint пуст, парсим как набор инструкций (обратная совместимость)
                    executableUnit.EntryPoint = CompileAsInstructionSet(sourceCode, parser, semanticAnalyzer);
                }

                // Компилируем процедуры
                foreach (var proc in programStructure.Procedures)
                {
                    var procedure = new Processor.Procedure { Name = proc.Key };
                    foreach (var instruction in proc.Value)
                    {
                        var ast = parser.Parse(instruction);
                        var command = semanticAnalyzer.Analyze(ast);
                        procedure.Body.Add(command);
                    }
                    executableUnit.Procedures[proc.Key] = procedure;
                }

                // Компилируем функции
                foreach (var func in programStructure.Functions)
                {
                    var function = new Processor.Function { Name = func.Key };
                    foreach (var instruction in func.Value)
                    {
                        var ast = parser.Parse(instruction);
                        var command = semanticAnalyzer.Analyze(ast);
                        function.Body.Add(command);
                    }
                    executableUnit.Functions[func.Key] = function;
                }

                return new CompilationResult
                {
                    Success = true,
                    Result = executableUnit
                };
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

        private ExecutionBlock CompileAsInstructionSet(string sourceCode, Parser parser, SemanticAnalyzer semanticAnalyzer)
        {
            var commands = new ExecutionBlock();
            var lines = sourceCode.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;

                var ast = parser.Parse(trimmedLine);
                var command = semanticAnalyzer.Analyze(ast);
                commands.Add(command);
            }
            return commands;
        }
    }
}
