using Magic.Kernel;
using Magic.Kernel.Interpretation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpaceDb.Services;

static string? GetFirstNonOptionArg(string[] args)
{
    foreach (var a in args)
    {
        if (string.IsNullOrWhiteSpace(a)) continue;
        if (a.StartsWith("-", StringComparison.Ordinal)) continue;
        return a;
    }
    return null;
}

static string? GetOutputFormatFromArgs(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                var val = args[i + 1].Trim().ToLowerInvariant();
                if (val is "agiasm" or "agic") return val;
            }
            return null;
        }
        if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
        {
            var val = a.Substring(9).Trim().ToLowerInvariant();
            if (val is "agiasm" or "agic") return val;
            return null;
        }
    }
    return null;
}

static long? GetLong(Microsoft.Extensions.Configuration.IConfiguration cfg, string key)
{
    var raw = cfg[key];
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return long.TryParse(raw, out var v) ? v : null;
}

var filePath = GetFirstNonOptionArg(args);

if (args.Length == 0 || filePath is null || args.Any(a => a is "--help" or "-h" or "/?"))
{
    Console.WriteLine("Usage: Space.OS.Simulator [options] [file.agi|file.agic|file.agiasm]");
    Console.WriteLine();
    Console.WriteLine("  file.agi    - Source file (will be compiled and executed)");
    Console.WriteLine("  file.agic   - Compiled file binary/JSON (executed directly)");
    Console.WriteLine("  file.agiasm - Compiled file text assembly (executed directly)");
    Console.WriteLine();
    Console.WriteLine("Options (when compiling .agi):");
    Console.WriteLine("  --output agiasm|agic, -o agiasm|agic   Output format (default: agiasm)");
    Console.WriteLine();
    Console.WriteLine("Config:");
    Console.WriteLine("  RocksDb:Path            (or env ROCKSDB_PATH)");
    Console.WriteLine("  SpaceDisk:SpaceName (key prefix; else from program: module|program)");
    Console.WriteLine("  SpaceDisk:VertexSequenceIndex / RelationSequenceIndex / ShapeSequenceIndex");
    return;
}

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"File not found: {Path.GetFullPath(filePath)}");
    Environment.ExitCode = 2;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<MagicKernel>();
builder.Services.AddRocksDb(builder.Configuration);

using var host = builder.Build();

using var scope = host.Services.CreateScope();
var kernel = scope.ServiceProvider.GetRequiredService<MagicKernel>();
var defaultDisk = scope.ServiceProvider.GetRequiredService<RocksDbSpaceDisk>();

kernel.Devices.Add(defaultDisk);
await kernel.StartKernel();

// Apply disk configuration AFTER StartKernel() (it overwrites ISpaceDisk.Configuration).
var spaceNameFromConfig = builder.Configuration["SpaceDisk:SpaceName"];
if (!string.IsNullOrWhiteSpace(spaceNameFromConfig))
    defaultDisk.Configuration.SpaceName = spaceNameFromConfig.Trim();
defaultDisk.Configuration.VertexSequenceIndex ??= GetLong(builder.Configuration, "SpaceDisk:VertexSequenceIndex") ?? 0;
defaultDisk.Configuration.RelationSequenceIndex ??= GetLong(builder.Configuration, "SpaceDisk:RelationSequenceIndex") ?? 0;
defaultDisk.Configuration.ShapeSequenceIndex ??= GetLong(builder.Configuration, "SpaceDisk:ShapeSequenceIndex") ?? 0;

InterpretationResult result;
var extension = Path.GetExtension(filePath).ToLowerInvariant();

if (extension == ".agic" || extension == ".agiasm")
{
    // Execute compiled file directly (binary/JSON or text assembly)
    result = await kernel.InterpreteCompiledFileAsync(filePath);
}
else if (extension == ".agi")
{
    // Compile source file
    var compilationResult = await kernel.CompileAsync(await File.ReadAllTextAsync(filePath));
    
    if (!compilationResult.Success)
    {
        Console.Error.WriteLine($"Compilation failed: {compilationResult.ErrorMessage}");
        Environment.ExitCode = 1;
        return;
    }
    
    // Save compiled file to disk: формат вывода из args (--output agiasm|agic), по умолчанию agiasm
    var outputFormat = GetOutputFormatFromArgs(args) ?? "agiasm";
    compilationResult.Result!.OutputFormat = outputFormat;
    var compiledPath = Path.ChangeExtension(filePath, outputFormat == "agiasm" ? ".agiasm" : ".agic");
    await compilationResult.Result.SaveAsync(compiledPath);
    
    // Execute compiled structure directly from memory (not reading from disk)
    result = await kernel.Interpreter.InterpreteAsync(compilationResult.Result);
}
else
{
    Console.Error.WriteLine($"Unsupported file extension: {extension}. Expected .agi, .agic or .agiasm");
    Environment.ExitCode = 2;
    return;
}

if (!result.Success)
{
    Console.Error.WriteLine("Execution failed.");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine("OK");
