using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Kernel;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Interpretation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Space.OS.ExecutionUnit;
using Space.OS.ExecutionUnit.Container;
using SpaceDb.Services;

OSSystem.StartKernel();

var configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddSingleton<MagicKernel>();
builder.Services.AddRocksDb(builder.Configuration);

using var host = builder.Build();

// One-time kernel + disk setup (shared across "run" commands)
using (var scope = host.Services.CreateScope())
{
    var kernel = scope.ServiceProvider.GetRequiredService<MagicKernel>();
    var defaultDisk = scope.ServiceProvider.GetRequiredService<RocksDbSpaceDisk>();
    kernel.Devices.Add(defaultDisk);
    await kernel.StartKernel();
    ApplyDiskConfig(defaultDisk, builder.Configuration);
    // Store in a way we can get in the loop - we need kernel in the loop
}

// We need kernel and disk in the loop; they're singletons, so we can get them from host.Services
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var consoleLogger = loggerFactory.CreateLogger("Console");

PrintHelp();

while (true)
{
    Console.Write("sps> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) continue;

    var parts = ParseCommandLine(line);
    if (parts.Count == 0) continue;
    var cmd = parts[0].ToLowerInvariant();
    var cmdArgs = parts.Skip(1).ToList();

    try
    {
        if (cmd is "exit" or "quit" or "q")
            break;
        if (cmd == "help" || cmd == "?")
        {
            PrintHelp();
            continue;
        }
        if (cmd == "run")
        {
            await RunProgramAsync(host, cmdArgs, configPath);
            continue;
        }
        if (cmd == "container")
        {
            await ContainerCommandAsync(host, cmdArgs, configPath);
            continue;
        }
        if (cmd == "config")
        {
            ConfigCommand(cmdArgs, configPath);
            continue;
        }
        if (cmd == "vault")
        {
            VaultCommand(host, cmdArgs, configPath);
            continue;
        }
        Console.WriteLine($"Unknown command: {cmd}. Type help for usage.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  run <file.agi|file.agic|file.agiasm>   Run program (compile .agi, then execute)");
    Console.WriteLine("  container list                           List execution unit instances");
    Console.WriteLine("  container add <id> [--code-dir dir] [--program file] [--vault file] [--rocksdb path] [--space-name name]");
    Console.WriteLine("  container run <id> [--code \"inline\"]   Run instance (from ExecutionUnit:Instances)");
    Console.WriteLine("  config edit [path]                      Open config in default editor (default: appsettings.json)");
    Console.WriteLine("  vault edit [path]                       Open vault JSON in default editor");
    Console.WriteLine("  help | exit | quit | q");
}

static List<string> ParseCommandLine(string line)
{
    var list = new List<string>();
    var current = new System.Text.StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (c == '"')
        {
            inQuotes = !inQuotes;
            continue;
        }
        if ((c == ' ' || c == '\t') && !inQuotes)
        {
            if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
            continue;
        }
        current.Append(c);
    }
    if (current.Length > 0) list.Add(current.ToString());
    return list;
}

static long? GetLong(IConfiguration cfg, string key)
{
    var raw = cfg[key];
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return long.TryParse(raw, out var v) ? v : null;
}

static void ApplyDiskConfig(RocksDbSpaceDisk disk, IConfiguration configuration)
{
    var spaceName = configuration["SpaceDisk:SpaceName"];
    if (!string.IsNullOrWhiteSpace(spaceName))
        disk.Configuration.SpaceName = spaceName.Trim();
    disk.Configuration.VertexSequenceIndex ??= GetLong(configuration, "SpaceDisk:VertexSequenceIndex") ?? 0;
    disk.Configuration.RelationSequenceIndex ??= GetLong(configuration, "SpaceDisk:RelationSequenceIndex") ?? 0;
    disk.Configuration.ShapeSequenceIndex ??= GetLong(configuration, "SpaceDisk:ShapeSequenceIndex") ?? 0;
}

static async Task RunProgramAsync(IHost host, List<string> args, string configPath)
{
    var filePath = args.Count > 0 ? args[0] : null;
    if (string.IsNullOrWhiteSpace(filePath))
    {
        Console.WriteLine("Usage: run <file.agi|file.agic|file.agiasm>");
        return;
    }
    if (!Path.IsPathRooted(filePath))
        filePath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
    if (!File.Exists(filePath))
    {
        Console.WriteLine($"File not found: {filePath}");
        return;
    }

    using var scope = host.Services.CreateScope();
    var kernel = scope.ServiceProvider.GetRequiredService<MagicKernel>();
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    InterpretationResult result;
    if (ext == ".agic" || ext == ".agiasm")
    {
        result = await kernel.InterpreteCompiledRootFileAsync(filePath);
    }
    else if (ext == ".agi")
    {
        var comp = await kernel.CompileAsync(await File.ReadAllTextAsync(filePath));
        if (!comp.Success)
        {
            Console.WriteLine($"Compilation failed: {comp.ErrorMessage}");
            return;
        }
        result = await kernel.InterpreteRootAsync(comp.Result!);
    }
    else
    {
        Console.WriteLine($"Unsupported extension: {ext}. Use .agi, .agic or .agiasm");
        return;
    }
    Console.WriteLine(result.Success ? "OK" : "Execution failed.");
}

static async Task ContainerCommandAsync(IHost host, List<string> args, string configPath)
{
    var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
    var subArgs = args.Skip(1).ToList();
    if (sub == "list")
    {
        var options = GetContainerOptions(host);
        var ids = options.Instances.Keys.ToList();
        if (ids.Count == 0)
            Console.WriteLine("No instances. Use: container add <id> [options]");
        else
            foreach (var id in ids)
                Console.WriteLine(id);
        return;
    }
    if (sub == "add")
    {
        if (subArgs.Count == 0) { Console.WriteLine("Usage: container add <id> [--code-dir dir] [--program file] [--vault file] [--rocksdb path] [--space-name name]"); return; }
        var id = subArgs[0];
        var rest = subArgs.Skip(1).ToList();
        string? codeDir = null, programFile = null, vaultFile = null, rocksDbPath = null, spaceName = null;
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--code-dir" && i + 1 < rest.Count) { codeDir = rest[++i]; continue; }
            if (rest[i] == "--program" && i + 1 < rest.Count) { programFile = rest[++i]; continue; }
            if (rest[i] == "--vault" && i + 1 < rest.Count) { vaultFile = rest[++i]; continue; }
            if (rest[i] == "--rocksdb" && i + 1 < rest.Count) { rocksDbPath = rest[++i]; continue; }
            if (rest[i] == "--space-name" && i + 1 < rest.Count) { spaceName = rest[++i]; continue; }
        }
        AddInstanceAndSave(configPath, id, codeDir, programFile, vaultFile, rocksDbPath, spaceName);
        Console.WriteLine($"Added instance: {id}");
        return;
    }
    if (sub == "run")
    {
        if (subArgs.Count == 0) { Console.WriteLine("Usage: container run <id> [--code \"inline code\"]"); return; }
        var id = subArgs[0];
        string? codeOverride = null;
        for (var i = 1; i < subArgs.Count; i++)
            if (subArgs[i] == "--code" && i + 1 < subArgs.Count) { codeOverride = subArgs[++i]; break; }
        var options = GetContainerOptions(host);
        if (!options.Instances.TryGetValue(id, out var inst) || inst == null)
        {
            Console.WriteLine($"Unknown instance: {id}. Known: [{string.Join(", ", options.Instances.Keys)}]");
            return;
        }
        var basePath = Directory.GetCurrentDirectory();
        var request = BuildRequestFromInstance(inst, basePath, host.Services.GetRequiredService<IConfiguration>());
        if (codeOverride != null) request.ProgramCode = codeOverride;
        var result = await ExecutionUnitRunner.RunAsync(request, host.Services.GetRequiredService<IConfiguration>(), host.Services.GetRequiredService<ILoggerFactory>());
        Console.WriteLine(result.Success ? "OK" : $"Failed: {result.ErrorMessage}");
        return;
    }
    Console.WriteLine("Usage: container list | container add <id> [options] | container run <id> [--code \"...\"]");
}

static ContainerOptions GetContainerOptions(IHost host)
{
    var cfg = host.Services.GetRequiredService<IConfiguration>();
    var section = cfg.GetSection(ContainerOptions.SectionName);
    var options = new ContainerOptions();
    section.Bind(options);
    return options;
}

static ExecutionRequest BuildRequestFromInstance(InstanceConfig config, string basePath, IConfiguration rootConfig)
{
    var codeDir = string.IsNullOrWhiteSpace(config.CodeDirectory)
        ? Path.Combine(basePath, "Code")
        : Path.IsPathRooted(config.CodeDirectory!)
            ? config.CodeDirectory
            : Path.Combine(basePath, config.CodeDirectory.Trim());
    var programFile = config.ProgramFile ?? "program.agic";
    var vaultFile = config.VaultFile ?? "vault.json";
    string? programPath = null;
    var agicPath = Path.Combine(codeDir, "program.agic");
    var agiPath = Path.Combine(codeDir, "program.agi");
    var configPath = Path.Combine(codeDir, programFile);
    if (File.Exists(agicPath)) programPath = agicPath;
    else if (File.Exists(agiPath)) programPath = agiPath;
    else if (File.Exists(configPath)) programPath = configPath;
    var vaultPath = Path.Combine(codeDir, vaultFile);
    var rocksDbPath = config.RocksDbPath ?? rootConfig["RocksDb:Path"] ?? "rocksdb";
    return new ExecutionRequest
    {
        ProgramPath = programPath,
        VaultPath = File.Exists(vaultPath) ? vaultPath : null,
        RocksDbPath = config.RocksDbPath,
        SpaceName = config.SpaceName
    };
}

static void AddInstanceAndSave(string configPath, string id, string? codeDir, string? programFile, string? vaultFile, string? rocksDbPath, string? spaceName)
{
    var root = LoadConfigNode(configPath);
    var eu = root["ExecutionUnit"] as JsonObject ?? new JsonObject();
    var instances = eu["Instances"] as JsonObject ?? new JsonObject();
    var inst = new JsonObject();
    if (codeDir != null) inst["CodeDirectory"] = codeDir;
    if (programFile != null) inst["ProgramFile"] = programFile;
    if (vaultFile != null) inst["VaultFile"] = vaultFile;
    if (rocksDbPath != null) inst["RocksDbPath"] = rocksDbPath;
    if (spaceName != null) inst["SpaceName"] = spaceName;
    instances[id] = inst;
    eu["Instances"] = instances;
    root["ExecutionUnit"] = eu;
    var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(configPath, json);
}

static JsonObject LoadConfigNode(string configPath)
{
    if (!File.Exists(configPath))
        return new JsonObject();
    var text = File.ReadAllText(configPath);
    var node = JsonNode.Parse(text);
    return node as JsonObject ?? new JsonObject();
}

static void ConfigCommand(List<string> args, string configPath)
{
    var path = configPath;
    if (args.Count > 0)
    {
        if (args[0].ToLowerInvariant() == "edit")
            path = args.Count > 1 ? args[1] : configPath;
        else
            path = args[0];
    }
    if (!Path.IsPathRooted(path)) path = Path.Combine(Directory.GetCurrentDirectory(), path);
    if (!File.Exists(path)) File.WriteAllText(path, "{}");
    OpenInEditor(path);
}

static void VaultCommand(IHost host, List<string> args, string configPath)
{
    var path = args.Count > 0 ? args[0] : null;
    if (path?.ToLowerInvariant() == "edit") path = args.Count > 1 ? args[1] : null;
    if (string.IsNullOrWhiteSpace(path))
    {
        var options = GetContainerOptions(host);
        var first = options.Instances.Keys.FirstOrDefault();
        if (first == null || !options.Instances.TryGetValue(first, out var inst) || inst == null)
        {
            Console.WriteLine("Usage: vault edit [path] or add an instance first (container add)");
            return;
        }
        var basePath = Directory.GetCurrentDirectory();
        var codeDir = string.IsNullOrWhiteSpace(inst.CodeDirectory) ? Path.Combine(basePath, "Code") : Path.Combine(basePath, inst.CodeDirectory!);
        path = Path.Combine(codeDir, inst.VaultFile ?? "vault.json");
    }
    if (!Path.IsPathRooted(path)) path = Path.Combine(Directory.GetCurrentDirectory(), path);
    if (!File.Exists(path)) File.WriteAllText(path, "{}");
    OpenInEditor(path);
}

static void OpenInEditor(string filePath)
{
    var fullPath = Path.GetFullPath(filePath);
    try
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not open editor: {ex.Message}. Edit manually: {fullPath}");
    }
}
