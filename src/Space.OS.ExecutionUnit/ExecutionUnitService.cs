using Magic.Kernel;
using Magic.Kernel.Compilation;
using Magic.Kernel.Interpretation;
using SpaceDb.Services;

namespace Space.OS.ExecutionUnit;

public class ExecutionUnitService : IExecutionUnitService
{
    private readonly MagicKernel _kernel;
    private readonly RocksDbSpaceDisk _disk;
    private readonly string _codeDir;
    private readonly string _programFile;
    private readonly string _vaultFile;

    public ExecutionUnitService(
        MagicKernel kernel,
        RocksDbSpaceDisk disk,
        IConfiguration configuration)
    {
        _kernel = kernel;
        _disk = disk;
        _codeDir = configuration["Code:Directory"] ?? "Code";
        _programFile = configuration["Code:ProgramFile"] ?? "program.agic";
        _vaultFile = configuration["Code:VaultFile"] ?? "vault.json";
    }

    public async Task<ExecutionResult> RunFromCodeFolderAsync(string? programCode = null)
    {
        var baseDir = Path.IsPathRooted(_codeDir) ? _codeDir : Path.Combine(Directory.GetCurrentDirectory(), _codeDir);

        if (!string.IsNullOrEmpty(programCode))
        {
            _kernel.Configuration.VaultReader = ResolveVault(baseDir);
            var comp = await _kernel.CompileAsync(programCode);
            if (!comp.Success)
                return new ExecutionResult { Success = false, ErrorMessage = comp.ErrorMessage ?? "Compilation failed." };
                var result = await _kernel.InterpreteRootAsync(comp.Result!);
            return new ExecutionResult { Success = result.Success, ErrorMessage = result.Success ? null : "Execution failed." };
        }

        var programPath = ResolveProgramPath(baseDir);
        if (programPath == null)
            return new ExecutionResult { Success = false, ErrorMessage = $"No program file found in {baseDir}. Add program.agic or program.agi." };

        _kernel.Configuration.VaultReader = ResolveVault(baseDir);

        InterpretationResult result2;
        var ext = Path.GetExtension(programPath).ToLowerInvariant();
        if (ext == ".agic" || ext == ".agiasm")
            result2 = await _kernel.InterpreteCompiledRootFileAsync(programPath);
        else
        {
            var comp = await _kernel.CompileAsync(await File.ReadAllTextAsync(programPath));
            if (!comp.Success)
                return new ExecutionResult { Success = false, ErrorMessage = comp.ErrorMessage ?? "Compilation failed." };
            result2 = await _kernel.InterpreteRootAsync(comp.Result!);
        }

        return new ExecutionResult { Success = result2.Success, ErrorMessage = result2.Success ? null : "Execution failed." };
    }

    private string? ResolveProgramPath(string baseDir)
    {
        var agic = Path.Combine(baseDir, "program.agic");
        if (File.Exists(agic)) return agic;
        var agi = Path.Combine(baseDir, "program.agi");
        if (File.Exists(agi)) return agi;
        var fromConfig = Path.Combine(baseDir, _programFile);
        if (File.Exists(fromConfig)) return fromConfig;
        return null;
    }

    private IVaultReader ResolveVault(string baseDir)
    {
        var vaultPath = Path.Combine(baseDir, _vaultFile);
        if (File.Exists(vaultPath))
            return new JsonFileVaultReader(vaultPath);
        return new EnvironmentVaultReader();
    }
}
