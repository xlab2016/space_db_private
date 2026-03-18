using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using System.Linq;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    /// <summary>Tests for fixes required by issue #1 (simple_inference program).</summary>
    public class SimpleInferenceProgramTests
    {
        private readonly Parser _parser;
        private readonly SemanticAnalyzer _semanticAnalyzer;

        public SimpleInferenceProgramTests()
        {
            _parser = new Parser();
            _semanticAnalyzer = new SemanticAnalyzer();
        }

        // ──────────────────────────────────────────────────────────
        // Fix 1: history := [] should compile as DefList (list type)
        // ──────────────────────────────────────────────────────────

        [Fact]
        public void ParseProgram_EmptyArrayAssignment_ShouldCompileAsDefList()
        {
            // Arrange — history := [] should produce: push "[]"; push "list"; push 2; def; pop
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var history := [];
}

entrypoint {
    Main;
}";

            // Act
            var structure = _parser.ParseProgram(source);
            var analyzed = _semanticAnalyzer.AnalyzeProgram(structure, _parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;

            // Expected: push "[]", push type:"list", push 2, def, pop, ret
            asm.Should().HaveCountGreaterThanOrEqualTo(5);

            var defIndex = asm.Select((c, i) => (c, i)).First(x => x.c.Opcode == Opcodes.Def).i;
            defIndex.Should().BeGreaterThan(0);

            // Before def there should be a push of "list" type
            var listPush = asm.Take(defIndex).LastOrDefault(c =>
                c.Opcode == Opcodes.Push &&
                c.Operand1 is PushOperand po &&
                string.Equals(po.Value?.ToString(), "list", System.StringComparison.OrdinalIgnoreCase));
            listPush.Should().NotBeNull("The 'list' type push should appear before def");

            // And an arity push of 2
            var arityPush = asm[defIndex - 1].Operand1 as PushOperand;
            arityPush.Should().NotBeNull();
            arityPush!.Kind.Should().Be("IntLiteral");
            arityPush.Value.Should().Be(2L);
        }

        // ──────────────────────────────────────────────────────────
        // Fix 3: println(...) should compile (was not recognized)
        // ──────────────────────────────────────────────────────────

        [Fact]
        public async System.Threading.Tasks.Task CompileAsync_PrintlnStatement_ShouldCompileSuccessfully()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    println(""Hello, World!"");
}

entrypoint {
    Main;
}";

            var compiler = new Compiler();

            // Act
            var result = await compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage ?? "compilation failed");
            result.Result!.Procedures.Should().ContainKey("Main");
            var body = result.Result.Procedures["Main"].Body;

            // Should contain a Call to println
            var callInstr = body.FirstOrDefault(c => c.Opcode == Opcodes.Call);
            callInstr.Should().NotBeNull("println should produce a call instruction");
            (callInstr!.Operand1 as CallInfo)!.FunctionName.ToLowerInvariant().Should().BeOneOf("print", "println",
                "println should call the print/println system function");
        }

        [Fact]
        public async System.Threading.Tasks.Task CompileAsync_PrintlnWithVariable_ShouldCompileSuccessfully()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
    println(token);
}

entrypoint {
    Main;
}";

            var compiler = new Compiler();

            // Act
            var result = await compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage ?? "compilation failed");
        }

        // ──────────────────────────────────────────────────────────
        // Fix 4: stream<inference, openai> should compile
        // ──────────────────────────────────────────────────────────

        [Fact]
        public void ParseProgram_InferenceOpenAIStream_ShouldGenerateDefGenAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var gpt1 := stream<inference, openai>;
}

entrypoint {
    Main;
}";

            // Act
            var structure = _parser.ParseProgram(source);
            var analyzed = _semanticAnalyzer.AnalyzeProgram(structure, _parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;

            // Should contain Def then Pop (base stream) and DefGen then Pop
            asm.Any(c => c.Opcode == Opcodes.Def).Should().BeTrue("stream def should be emitted");
            asm.Any(c => c.Opcode == Opcodes.DefGen).Should().BeTrue("defgen for inference/openai should be emitted");

            // The type pushes should include "stream", "inference", "openai"
            var typePushes = asm
                .Where(c => c.Opcode == Opcodes.Push && c.Operand1 is PushOperand po && po.Kind == "Type")
                .Select(c => ((PushOperand)c.Operand1!).Value?.ToString()?.ToLowerInvariant())
                .ToList();

            typePushes.Should().Contain("stream");
            typePushes.Should().Contain("inference");
            typePushes.Should().Contain("openai");
        }

        // ──────────────────────────────────────────────────────────
        // Fix 2: JSON method call with :time symbolic values should
        // compile as structured data, not a flat string.
        // ──────────────────────────────────────────────────────────

        [Fact]
        public async System.Threading.Tasks.Task CompileAsync_MethodCallWithSymbolicJsonValue_ShouldCompileSuccessfully()
        {
            // Arrange — tests that :time inside a JSON object literal compiles without error
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
    var gpt1 := stream<inference, openai>;
    var history := [];
    gpt1.open({
        token: token,
        history: history
    });
    var response := gpt1.write({
        data: [{ currentTime: :time }],
        system: ""You are a helpful bot."",
        instruction: ""What time is it?""
    });
}

entrypoint {
    Main;
}";

            var compiler = new Compiler();

            // Act
            var result = await compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage ?? "compilation should succeed with :time symbolic reference in JSON");
        }

        // ──────────────────────────────────────────────────────────
        // DefList runtime type tests
        // ──────────────────────────────────────────────────────────

        [Fact]
        public void Hal_Def_ListType_ShouldReturnDefList()
        {
            // Arrange & Act
            var list = Core.OS.Hal.Def("list", null);

            // Assert
            list.Should().BeOfType<Types.DefList>();
            ((Types.DefList)list!).Items.Should().BeEmpty();
        }

        [Fact]
        public void Hal_Def_ListWithArgs_ShouldCreateDefListWithInitialItems()
        {
            // Arrange
            var initialItems = new System.Collections.Generic.List<object?> { "a", "b", "c" };
            var args = new object?[] { initialItems, "list" };

            // Act
            var list = Core.OS.Hal.Def(args, null);

            // Assert
            list.Should().BeOfType<Types.DefList>();
            var defList = (Types.DefList)list!;
            defList.Items.Should().HaveCount(3);
            defList.Items[0].Should().Be("a");
            defList.Items[1].Should().Be("b");
            defList.Items[2].Should().Be("c");
        }
    }
}
