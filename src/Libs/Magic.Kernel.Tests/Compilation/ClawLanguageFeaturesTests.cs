using FluentAssertions;
using Magic.Kernel;
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

        [Fact]
        public async Task CompileAsync_ProcedureWithMemberAccessFromParam_ShouldRegisterLocalVar()
        {
            // Arrange: procedure that reads a member from a parameter and stores it in a local var.
            // Regression test for: member access from a global-kind (procedure param) variable was not
            // registering the result variable, causing subsequent uses to throw "undeclared variable".
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var command := data.command;
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call call;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithSwitchOnMemberAccessVar_ShouldCompileSuccessfully()
        {
            // Arrange: reproduces the client_claw.agi compilation failure where 'command' (obtained via
            // member access from a procedure parameter) was not in scope when compiling the switch statement.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var authentication := data.authentication;

    if !authentication.isAuthenticated return;

    var command := data.command;

    switch command {
        if ""hello_world""
            print(""Hello world"");
    }
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call call;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
        }

        [Fact]
        public async Task CompileAsync_InlineIfWithoutBraces_ShouldEmitRetInstruction()
        {
            // Arrange: "if !condition return;" without braces should compile and emit a ret opcode.
            // Previously this was silently dropped because TryParseIfStatement required braces.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure check(data) {
    var auth := data.authentication;
    if !auth.isAuthenticated return;
    var cmd := data.command;
}

entrypoint {
    asm {
        push int 0;
        push int 1;
        call check;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var proc = result.Result!.Procedures["check"];
            // Body should contain a Ret instruction from the inline if
            proc.Body.Count(cmd => cmd.Opcode == Opcodes.Ret).Should().BeGreaterThan(1,
                "inline 'if !cond return;' should emit a Ret opcode in addition to the final procedure ret");
        }

        [Fact]
        public async Task CompileAsync_AwaitStreamStatement_ShouldCompileSuccessfully()
        {
            // Arrange: "await claw1;" should compile for a stream variable.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main() {
    var claw1 := stream<claw>;
    await claw1;
}

entrypoint {
    Main;
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var proc = result.Result!.Procedures["Main"];
            proc.Body.Any(cmd => cmd.Opcode == Opcodes.AwaitObj).Should().BeTrue(
                "await claw1 should emit an AwaitObj opcode");
        }

        [Fact]
        public async Task CompileAsync_PrintWithFormatStringArgument_ShouldCompileSuccessfully()
        {
            // Arrange: print() with a #"format {expr}" argument.
            // Regression test for: '#' was treated as undeclared identifier instead of format string prefix.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure greet(data) {
    var socket1 := socket;
    print(#""Hello {socket1.name}"");
}

entrypoint {
    asm {
        push int 0;
        call greet;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("greet");
        }

        [Fact]
        public async Task CompileAsync_FullClientClawPattern_ShouldCompileSuccessfully()
        {
            // Arrange: the full client_claw.agi pattern including all fixed issues:
            // - inline if without braces: "if !condition return;"
            // - socket variable from global device (not from data parameter)
            // - await claw1 at end of Main to keep the stream loop alive
            var source = @"@AGI 0.0.1;

program clients_claw;
system samples;
module claw;

procedure call(data) {
    var authentication := data.authentication;

    if !authentication.isAuthenticated return;

    var command := data.command;
    var socket1 := socket;

    switch command {
        if ""hello_world""
            print(#""Hello world from Claw {socket1.name}"");
    }
}

procedure Main() {
    var vault1 := vault;
    var port := vault1.read(""port"");
    var credentials := vault1.read(""credentials"");

    var claw1 := stream<claw>;
    claw1.open({
        port: port,
        authentication: {
            credentials: credentials
        }
    });
    claw1.methods.add(""call"", &call);
    await claw1;
}

entrypoint {
    Main;
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
            result.Result.Procedures.Should().ContainKey("Main");
            // Main should contain an AwaitObj instruction for "await claw1"
            result.Result.Procedures["Main"].Body.Any(cmd => cmd.Opcode == Opcodes.AwaitObj).Should().BeTrue(
                "await claw1 should produce an AwaitObj opcode");
            // call procedure body should use local memory (push [N]) not global (push global: [N]) for params
            var callBody = result.Result.Procedures["call"].Body;
            callBody.Where(cmd => cmd.Opcode == Opcodes.Push)
                .Select(cmd => cmd.Operand1 as MemoryAddress)
                .Where(ma => ma != null)
                .Should().AllSatisfy(ma => ma!.IsGlobal.Should().BeFalse(
                    "procedure parameters should use local memory (push [N]) not global memory (push global: [N])"));
        }

        [Fact]
        public async Task Serialize_ProgramWithProcedures_ShouldEmitProceduresBeforeEntrypoint()
        {
            // Arrange: procedures should appear before the entrypoint body in serialized output
            // so that all definitions are present before the entry-point call.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure greet() {
    print(""hello"");
}

entrypoint {
    greet;
}";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.OutputFormat = "agiasm";
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_{System.Guid.NewGuid():N}.agiasm");
            try
            {
                // Act
                await result.Result.SaveAsync(path);
                var serialized = await System.IO.File.ReadAllTextAsync(path);

                // Assert: "procedure" should appear before "entrypoint" in the output
                var procedureIdx = serialized.IndexOf("\nprocedure ", StringComparison.Ordinal);
                var entrypointIdx = serialized.IndexOf("\nentrypoint", StringComparison.Ordinal);
                procedureIdx.Should().BeLessThan(entrypointIdx,
                    "all procedure definitions must appear before the entrypoint body in the serialized output");
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
    }
}
