using System;
using System.IO;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class ExecutableUnitSerializationTests
    {
        [Fact]
        public async Task SerializeDeserializeAsync_Binary_Roundtrip_ShouldPreservePolymorphicCallParameters()
        {
            var compiler = new Compiler();

            var source = @"@AGI 0.0.1

program L0;
module L/L0;

procedure Main {
    asm {
        addshape index: 1, vertices: indices: [1, 2, 3];

        call ""intersect"", shapeA: shape: index: 1, shapeB: shape: {
            vertices: [
                { dimensions: [1, 0, 0, 0] },
                { dimensions: [1, 2, 0, 0] },
            ],
        };

        pop [1];
        call ""print"", [1];
    }
}

entrypoint {
    asm {
        call Main;
    }
}";

            var result = await compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            var unit = result.Result!;

            var bytes = await unit.SerializeAsync();
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            json.Should().Contain("\"$format\":\"mkex-json\"");
            json.Should().Contain("\"opcode\":\"Call\"");
            json.Should().Contain("\"$t\":\"call\"");
            json.Should().Contain("\"$t\":\"shape\"");

            var roundtripped = await ExecutableUnit.DeserializeAsync(bytes);

            roundtripped.Version.Should().Be("0.0.1");
            roundtripped.Name.Should().Be("L0");
            roundtripped.Module.Should().Be("L/L0");

            // entrypoint: push 0-arity, call Main;
            roundtripped.EntryPoint.Should().HaveCount(2);
            roundtripped.EntryPoint[1].Opcode.Should().Be(Opcodes.Call);
            var entryCall = roundtripped.EntryPoint[1].Operand1.Should().BeOfType<CallInfo>().Subject;
            entryCall.FunctionName.Should().Be("Main");

            // procedure Main: intersect call should have shapeA (ref dict) and shapeB (Shape) + trailing Ret
            roundtripped.Procedures.Should().ContainKey("Main");
            var main = roundtripped.Procedures["Main"];
            main.Body.Should().HaveCount(5);

            var intersectCmd = main.Body[1];
            intersectCmd.Opcode.Should().Be(Opcodes.Call);
            var intersect = intersectCmd.Operand1.Should().BeOfType<CallInfo>().Subject;
            intersect.FunctionName.Should().Be("intersect");

            intersect.Parameters.Should().ContainKey("shapeA");
            intersect.Parameters.Should().ContainKey("shapeB");

            var shapeARef = intersect.Parameters["shapeA"].Should().BeOfType<Dictionary<string, object>>().Subject;
            shapeARef["index"].Should().Be(1L);

            var shapeB = intersect.Parameters["shapeB"].Should().BeOfType<Shape>().Subject;
            shapeB.Vertices.Should().NotBeNull();
            shapeB.Vertices!.Should().HaveCount(2);
            shapeB.Vertices[0].Position!.Dimensions.Should().BeEquivalentTo(new[] { 1f, 0f, 0f, 0f });
            shapeB.Vertices[1].Position!.Dimensions.Should().BeEquivalentTo(new[] { 1f, 2f, 0f, 0f });
        }

        [Fact]
        public async Task SaveAsync_WithOutputFormatAgiasm_ShouldWriteTextFile()
        {
            var compiler = new Compiler();
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;
procedure Main { asm { nop; } }
entrypoint { asm { call Main; } }
";
            var result = await compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.OutputFormat = "agiasm";

            var path = Path.Combine(Path.GetTempPath(), $"agiasm_test_{Guid.NewGuid():N}.agiasm");
            try
            {
                await result.Result.SaveAsync(path);
                var text = await File.ReadAllTextAsync(path);
                text.Should().StartWith("@AGIASM");
                text.Should().Contain("program Test");
                text.Should().Contain("module Test/Test");
                text.Should().Contain("entrypoint");
                text.Should().Contain("procedure Main");

                var loaded = await ExecutableUnit.LoadAsync(path);
                loaded.Name.Should().Be("Test");
                loaded.Module.Should().Be("Test/Test");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task SaveAsync_WithOutputFormatAgiasm_ShouldRoundtripACall()
        {
            var compiler = new Compiler();
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;
procedure Worker { asm { nop; } }
entrypoint { asm { acall Worker; } }
";
            var result = await compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.OutputFormat = "agiasm";

            var path = Path.Combine(Path.GetTempPath(), $"agiasm_acall_test_{Guid.NewGuid():N}.agiasm");
            try
            {
                await result.Result.SaveAsync(path);
                var text = await File.ReadAllTextAsync(path);
                text.Should().Contain("acall Worker");

                var loaded = await ExecutableUnit.LoadAsync(path);
                loaded.EntryPoint.Should().HaveCount(1);
                loaded.EntryPoint[0].Opcode.Should().Be(Opcodes.ACall);
                var callInfo = loaded.EntryPoint[0].Operand1.Should().BeOfType<CallInfo>().Subject;
                callInfo.FunctionName.Should().Be("Worker");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}

