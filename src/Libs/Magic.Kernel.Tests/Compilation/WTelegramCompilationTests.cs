using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using System.IO;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class WTelegramCompilationTests
    {
        [Fact]
        public void ParseProgram_WithWTelegramHistoryAssignment_ShouldCompileMethodCallExpression()
        {
            var source = @"@AGI 0.0.1;

program test;
module telegram;

procedure Main {
    var stream1 := stream<messenger, telegram, client>;
    var history := stream1.history({
        filter: {
            channelTitle: ""demo""
        },
        paging: {
            take: 100
        }
    });
    for streamwait by delta (history, delta) {
        var data := delta.data;
        streamwait print(data);
    }
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;
            var hasSyncLoopCall = asm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName.StartsWith("streamwait_loop_", StringComparison.Ordinal));
            var hasAsyncLoopCall = asm.Any(c => c.Opcode == Opcodes.ACall && c.Operand1 is CallInfo ci && ci.FunctionName.StartsWith("streamwait_loop_", StringComparison.Ordinal));

            asm.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "history");
            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWaitObj);
            hasSyncLoopCall.Should().BeTrue();
            hasAsyncLoopCall.Should().BeFalse();

            var pushTypes = asm
                .Where(c => c.Opcode == Opcodes.Push)
                .Select(c => c.Operand1)
                .OfType<PushOperand>()
                .Where(o => o.Kind == "Type")
                .Select(o => o.Value?.ToString())
                .ToList();

            pushTypes.Should().ContainInOrder("stream", "messenger", "telegram", "client");
        }

        [Fact]
        public void ParseProgram_WithForStreamWaitWithoutAggregate_ShouldCompileLoop()
        {
            var source = @"@AGI 0.0.1;

program test;
module telegram;

procedure Main {
    var history := stream<file>;
    for streamwait by delta (history, delta) {
        streamwait print(delta);
    }
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWaitObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.Je);
            asm.Should().Contain(c => c.Opcode == Opcodes.ACall);
        }

        [Fact]
        public void ParseProgram_WithForSyncStreamWait_ShouldCompileSynchronousLoopBodyCall()
        {
            var source = @"@AGI 0.0.1;

program test;
module telegram;

procedure Main {
    var stream1 := stream<file>;
    for sync streamwait by delta (stream1, delta) {
        streamwait print(delta);
    }
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;
            var hasSyncLoopCall = asm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName.StartsWith("streamwait_loop_", StringComparison.Ordinal));
            var hasAsyncLoopCall = asm.Any(c => c.Opcode == Opcodes.ACall && c.Operand1 is CallInfo ci && ci.FunctionName.StartsWith("streamwait_loop_", StringComparison.Ordinal));

            hasSyncLoopCall.Should().BeTrue();
            hasAsyncLoopCall.Should().BeFalse();
        }

        [Fact]
        public async Task CompileAsync_TelegramHistoryToDbSample_ShouldCompileSuccessfully()
        {
            var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "design", "Space", "samples", "telegram_history_to_db.agi"));
            var source = await File.ReadAllTextAsync(samplePath);
            var compiler = new Compiler();

            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var asm = result.Result!.Procedures["Main"].Body!;
            asm.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "history");
            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWaitObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "find");
            asm.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
            asm.Any(c => (c.Opcode == Opcodes.Call || c.Opcode == Opcodes.ACall) && c.Operand1 is CallInfo ci && ci.FunctionName.StartsWith("streamwait_loop_", StringComparison.Ordinal))
                .Should().BeTrue();
        }
    }
}
