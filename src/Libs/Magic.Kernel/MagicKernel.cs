using Magic.Kernel.Compilation;
using Magic.Kernel.Devices;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel
{
    public class MagicKernel
    {
        public KernelConfiguration Configuration { get; } = new KernelConfiguration();

        public KernelMemory Memory { get; } = new KernelMemory();
        public Compiler Compiler { get; } = new Compiler();
        public Interpreter Interpreter { get; } = new Interpreter { Configuration = null };

        public List<IDevice> Devices { get; } = new List<IDevice>();

        public async Task StartKernel()
        {
            Interpreter.Configuration = Configuration;

            foreach (var device in Devices)
            {
                if (device is ISpaceDisk spaceDisk)
                {
                    Configuration.DefaultDisk = spaceDisk;
                    spaceDisk.Configuration = new SpaceDiskConfiguration();

                    foreach (var item in Devices)
                    {
                        if (item is IProjectorDevice projector && await spaceDisk.IsCapableAsync(projector))
                            spaceDisk.Configuration.Projector = projector;
                    }
                    break;
                }
            }

            foreach (var device in Devices)
            {
                if (device is IInferenceDevice inferenceDevice)
                {
                    Configuration.DefaultInferenceDevice = inferenceDevice;
                    break;
                }
            }

            foreach (var device in Devices)
            {
                if (device is IProjectorDevice)
                {
                    Configuration.DefaultProjectorDevice = device as IProjectorDevice;
                    break;
                }
            }

            // Назначаем конфигурацию интерпретатору
            Interpreter.Configuration = Configuration;
        }

        public async Task<List<CompilationResult>> CompileDirectoryAsync(string directoryPath)
        {
            return await Compiler.CompileDirectoryAsync(directoryPath);
        }

        public async Task<CompilationResult> CompileAsync(string sourceCode)
        {
            return await Compiler.CompileAsync(sourceCode);
        }

        public async Task<InterpretationResult> InterpreteCompiledFileAsync(string compiledPath)
        {
            var executableUnit = await ExecutableUnit.LoadAsync(compiledPath);
            return await Interpreter.InterpreteAsync(executableUnit);
        }

        public async Task<List<InterpretationResult>> InterpreteCompiledDirectoryAsync(string directoryPath)
        {
            var results = new List<InterpretationResult>();
            var compiledFiles = Directory.GetFiles(directoryPath, "*.agic", SearchOption.AllDirectories);
            foreach (var file in compiledFiles)
            {
                var result = await InterpreteCompiledFileAsync(file);
                results.Add(result);
            }
            return results;
        }

        public async Task<InterpretationResult> InterpreteSourceCodeAsync(string sourceCode)
        {
            var compilationResult = await CompileAsync(sourceCode);
            if (!compilationResult.Success)
            {
                return new InterpretationResult
                {
                    Success = false
                };
            }
            return await Interpreter.InterpreteAsync(compilationResult.Result);
        }
    }
}