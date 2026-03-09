using Magic.Kernel;
using Magic.Kernel.Compilation;
using Magic.Kernel.Interpretation;
using Microsoft.Extensions.Logging;
using SpaceDb.Services;

namespace Space.OS.ExecutionUnit;

/// <summary>Runs AGI program with given code and config (library mode).</summary>
public static class ExecutionUnitRunner
{
    /// <summary>Runs program from request: ProgramPath or ProgramCode, vault from VaultPath/VaultData/VaultReader, RocksDB and Space from request or config.</summary>
    public static async Task<ExecutionResult> RunAsync(
        ExecutionRequest request,
        IConfiguration? configuration = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var loggerFact = loggerFactory ?? LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFact.CreateLogger("Space.OS.ExecutionUnit");

        var rocksDbPath = request.RocksDbPath
            ?? configuration?["RocksDb:Path"]
            ?? Environment.GetEnvironmentVariable("ROCKSDB_PATH")
            ?? "rocksdb";

        var rocksDb = new RocksDbService(rocksDbPath, logger);
        var disk = new RocksDbSpaceDisk(rocksDb);

        var kernel = new MagicKernel();
        kernel.Devices.Add(disk);
        await kernel.StartKernel();

        if (!string.IsNullOrWhiteSpace(request.SpaceName))
            disk.Configuration.SpaceName = request.SpaceName;
        if (configuration != null)
        {
            var cfgSpace = configuration["SpaceDisk:SpaceName"];
            if (!string.IsNullOrWhiteSpace(cfgSpace))
                disk.Configuration.SpaceName = cfgSpace;
            ApplyLongConfig(configuration, "SpaceDisk:VertexSequenceIndex", v => disk.Configuration.VertexSequenceIndex = v);
            ApplyLongConfig(configuration, "SpaceDisk:RelationSequenceIndex", v => disk.Configuration.RelationSequenceIndex = v);
            ApplyLongConfig(configuration, "SpaceDisk:ShapeSequenceIndex", v => disk.Configuration.ShapeSequenceIndex = v);
        }

        kernel.Configuration.VaultReader = ResolveVaultReader(request);

        try
        {
            InterpretationResult result;
            if (!string.IsNullOrEmpty(request.ProgramPath))
            {
                if (!File.Exists(request.ProgramPath))
                    return new ExecutionResult { Success = false, ErrorMessage = $"Program file not found: {request.ProgramPath}" };
                var ext = Path.GetExtension(request.ProgramPath).ToLowerInvariant();
                if (ext == ".agic" || ext == ".agiasm")
                    result = await kernel.InterpreteCompiledFileAsync(request.ProgramPath);
                else if (ext == ".agi")
                {
                    var comp = await kernel.CompileAsync(await File.ReadAllTextAsync(request.ProgramPath));
                    if (!comp.Success)
                        return new ExecutionResult { Success = false, ErrorMessage = comp.ErrorMessage ?? "Compilation failed." };
                    result = await kernel.InterpreteAsync(comp.Result!);
                }
                else
                    return new ExecutionResult { Success = false, ErrorMessage = $"Unsupported extension: {ext}. Use .agi, .agic or .agiasm." };
            }
            else if (!string.IsNullOrEmpty(request.ProgramCode))
            {
                var comp = await kernel.CompileAsync(request.ProgramCode);
                if (!comp.Success)
                    return new ExecutionResult { Success = false, ErrorMessage = comp.ErrorMessage ?? "Compilation failed." };
                result = await kernel.InterpreteAsync(comp.Result!);
            }
            else
                return new ExecutionResult { Success = false, ErrorMessage = "Provide ProgramPath or ProgramCode." };

            return new ExecutionResult { Success = result.Success, ErrorMessage = result.Success ? null : "Execution failed." };
        }
        catch (Exception ex)
        {
            return new ExecutionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static IVaultReader ResolveVaultReader(ExecutionRequest request)
    {
        if (request.VaultReader != null)
            return request.VaultReader;
        if (!string.IsNullOrEmpty(request.VaultPath) && File.Exists(request.VaultPath))
            return new JsonFileVaultReader(request.VaultPath);
        if (request.VaultData != null && request.VaultData.Count > 0)
            return new DictionaryVaultReader(request.VaultData);
        return new EnvironmentVaultReader();
    }

    private static void ApplyLongConfig(IConfiguration configuration, string key, Action<long> setter)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (long.TryParse(raw, out var v))
            setter(v);
    }
}
