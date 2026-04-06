using Magic.Kernel.Build;
using Magic.Kernel.Core;
using Magic.Kernel.Runtime;

namespace Magic.Kernel.Terminal.Services;

public sealed class SimulatorRunService
{
    public string? GetOutputFormatFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    var val = args[i + 1].Trim().ToLowerInvariant();
                    if (val is "agiasm" or "agic")
                    {
                        return val;
                    }
                }

                return null;
            }

            if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
            {
                var val = a[9..].Trim().ToLowerInvariant();
                if (val is "agiasm" or "agic")
                {
                    return val;
                }

                return null;
            }
        }

        return null;
    }

    public async Task<bool> RunSingleFileAsync(ExecutionUnitHost execHost, string filePath, string[] args)
    {
        var artifact = new Artifact
        {
            Name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty,
            Namespace = string.Empty
        };

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var runCommand = new RunCommand { Type = RunType.NewThread };

        if (extension is ".agic" or ".agiasm")
        {
            if (extension == ".agic")
            {
                artifact.Type = ArtifactType.Agic;
                artifact.Body = filePath;
            }
            else
            {
                artifact.Type = ArtifactType.AgiAsm;
                artifact.Body = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            }

            execHost.UnitArtifact = artifact;
            return await execHost.RunAsync(runCommand).ConfigureAwait(false);
        }

        if (extension == ".agi")
        {
            artifact.Type = ArtifactType.Agi;
            artifact.Body = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            runCommand.OutputFormat = GetOutputFormatFromArgs(args) ?? "agiasm";
            runCommand.SourcePath = filePath;

            execHost.UnitArtifact = artifact;
            return await execHost.RunAsync(runCommand).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"Unsupported file extension: {extension}. Expected .agi, .agic or .agiasm");
        return false;
    }

    public async Task<bool> RunUnitsFromConfigAsync(ExecutionUnitHost execHost, IReadOnlyList<SpaceEnvironment.ExecutionUnitConfig> executionUnits)
    {
        var results = new List<bool>();
        foreach (var unit in executionUnits)
        {
            var ok = await RunConfiguredUnitAsync(execHost, unit).ConfigureAwait(false);
            results.Add(ok);
        }

        return results.Count > 0 && results.All(x => x);
    }

    public async Task<bool> RunConfiguredUnitAsync(ExecutionUnitHost execHost, SpaceEnvironment.ExecutionUnitConfig unit)
    {
        var fullPath = SpaceEnvironment.GetFilePath(unit.Path);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Configured executionUnit file not found: {Path.GetFullPath(fullPath)}");
            return false;
        }

        var artifact = new Artifact
        {
            Name = Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty,
            Namespace = string.Empty
        };

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is ".agic" or ".agiasm")
        {
            if (extension == ".agic")
            {
                artifact.Type = ArtifactType.Agic;
                artifact.Body = fullPath;
            }
            else
            {
                artifact.Type = ArtifactType.AgiAsm;
                artifact.Body = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
            }

            execHost.UnitArtifact = artifact;
            return await execHost.RunAllInstancesAsync(new RunCommand
            {
                Type = RunType.NewThread,
                InstanceCount = unit.InstanceCount,
                SourcePath = fullPath
            }).ConfigureAwait(false);
        }

        if (extension == ".agi")
        {
            artifact.Type = ArtifactType.Agi;
            artifact.Body = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
            execHost.UnitArtifact = artifact;
            return await execHost.RunAllInstancesAsync(new RunCommand
            {
                Type = RunType.NewThread,
                InstanceCount = unit.InstanceCount,
                SourcePath = fullPath
            }).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"Unsupported file extension in executionUnits config: {extension}. Expected .agi, .agic or .agiasm");
        return false;
    }
}
