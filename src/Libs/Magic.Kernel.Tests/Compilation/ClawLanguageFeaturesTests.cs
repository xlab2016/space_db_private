using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    /// <summary>Tests for claw-device related AGI language features: procedure parameters, switch, return statement.</summary>
    public class ClawLanguageFeaturesTests
    {
        private readonly Compiler _compiler;

        public ClawLanguageFeaturesTests()
        {
            _compiler = new Compiler();
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithNamedParameter_ShouldEmitPopArityAndPopArg()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var x := data.command;
}

entrypoint {
    asm {
        push [0];
        push int 1;
        call call;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
            var proc = result.Result.Procedures["call"];
            // First two instructions should be Pop (discard arity) and Pop into slot (bind 'data')
            proc.Body.Count.Should().BeGreaterThan(1);
            proc.Body[0].Opcode.Should().Be(Opcodes.Pop); // discard arity
            proc.Body[1].Opcode.Should().Be(Opcodes.Pop); // bind 'data' param
            proc.Body[1].Operand1.Should().BeOfType<MemoryAddress>();
        }

        [Fact]
        public async Task CompileAsync_ReturnStatement_ShouldEmitRetOpcode()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure greet {
    return;
    var x := ""unreachable"";
}

entrypoint {
    asm {
        call greet;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("greet");
            var body = result.Result.Procedures["greet"].Body;
            // Should have Ret from return statement and then the final Ret from SemanticAnalyzer
            body.Any(cmd => cmd.Opcode == Opcodes.Ret).Should().BeTrue();
        }

        [Fact]
        public async Task CompileAsync_SwitchStatement_ShouldCompileSuccessfully()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure dispatch(cmd) {
    var command := cmd;
    switch command {
        if ""hello"" {
            var response := ""world"";
        }
        if ""bye"" {
            var response := ""goodbye"";
        }
    }
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call dispatch;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("dispatch");
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithMultipleParameters_ShouldEmitCorrectPrologue()
        {
            // Arrange — procedure with two parameters
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure add(a, b) {
    var result := a;
}

entrypoint {
    asm {
        push int 1;
        push int 2;
        push int 2;
        call add;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var proc = result.Result!.Procedures["add"];
            // prologue: Pop(arity), Pop[slotB], Pop[slotA]  (reverse order)
            proc.Body[0].Opcode.Should().Be(Opcodes.Pop); // discard arity
            proc.Body[1].Opcode.Should().Be(Opcodes.Pop); // bind 'b' (reverse: last param first)
            proc.Body[2].Opcode.Should().Be(Opcodes.Pop); // bind 'a'
        }

        [Fact]
        public async Task Parser_ProcedureWithParameters_ShouldStoreProcedureParameters()
        {
            // Arrange
            var parser = new Parser();
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var x := data;
}

entrypoint {
}";

            // Act
            var structure = parser.ParseProgram(source);

            // Assert
            structure.ProcedureParameters.Should().ContainKey("call");
            structure.ProcedureParameters["call"].Should().ContainSingle().Which.Should().Be("data");
        }

        [Fact]
        public async Task CompileAsync_StreamClawDefinition_ShouldCompileSuccessfully()
        {
            // Arrange: stream<claw> declaration via DefGen
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

entrypoint {
    var
        claw1: stream<claw>;
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            // Should have Def + DefGen + Ret
            result.Result!.EntryPoint.Count.Should().BeGreaterThan(0);
            result.Result.EntryPoint.Any(c => c.Opcode == Opcodes.DefGen).Should().BeTrue();
        }
    }
}
