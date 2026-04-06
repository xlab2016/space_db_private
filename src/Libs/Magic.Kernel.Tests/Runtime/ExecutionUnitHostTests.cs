using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Build;
using Magic.Kernel.Runtime;
using Xunit;

namespace Magic.Kernel.Tests.Runtime
{
    public class ExecutionUnitHostTests
    {
        [Fact]
        public async Task CompileArtifactAsync_AgiWithSourcePath_ShouldUseFileCompilationAndLinkUseModules()
        {
            var root = Path.Combine(Path.GetTempPath(), "exec_host_use_link_" + Guid.NewGuid().ToString("N"));
            var modularityDir = Path.Combine(root, "modularity");
            Directory.CreateDirectory(modularityDir);

            var modulePath = Path.Combine(modularityDir, "module1.agi");
            var callerPath = Path.Combine(root, "use_module1.agi");

            await File.WriteAllTextAsync(modulePath, @"@AGI 0.0.1;

program module1;
system samples;
module modularity;

function add(x, y) {
    return x + y;
}
");

            var callerSource = @"@AGI 0.0.1;

program use_module1;
system samples;
module modularity;

use modularity: module1 as {
    function add(x, y);
};

procedure Main() {
    var x:= 1;
    var y:= 2;
    var z:= module1: add(x, y);
    print(#""z: {z}"");
}

entrypoint {
    Main;
}";

            await File.WriteAllTextAsync(callerPath, callerSource);

            try
            {
                var kernel = new MagicKernel();
                var host = new ExecutionUnitHost(kernel)
                {
                    UnitArtifact = new Artifact
                    {
                        Name = "use_module1",
                        Namespace = string.Empty,
                        Type = ArtifactType.Agi,
                        Body = callerSource
                    }
                };

                var compileMethod = typeof(ExecutionUnitHost).GetMethod(
                    "CompileArtifactAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                compileMethod.Should().NotBeNull();

                var runCommand = new RunCommand { SourcePath = callerPath };
                var task = (Task<Magic.Kernel.Compilation.ExecutableUnit>)compileMethod!
                    .Invoke(host, new object?[] { runCommand })!;
                var unit = await task;

                unit.Functions.Should().ContainKey("samples:modularity:module1:add",
                    "when SourcePath is provided, host must compile by file path so `use` linkage works");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }
}
