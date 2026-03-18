using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class IfStreamwaitJsonAssignmentTests
    {
        private readonly Compiler _compiler = new Compiler();

        [Fact]
        public async Task CompileAsync_WithObjectLiteralWithSemicolon_RHSOfAssign_ShouldSucceed()
        {
            // Object literal with ; (key: var;) is allowed on RHS of := or member assign. Only strict JSON forbids ; inside.
            var source = @"@AGI 0.0.1;

program Test;
module Test/IfJson;

procedure Main {
    var
        message: vertex = {DIM:[1,0,0,0],W:1,DATA:""x""};
        photo: vertex = {DIM:[0,1,0,0],W:1,DATA:""y""};
        stream2 := stream<messenger, telegram>;
        photoData := stream2;

    if (photo) {
        message.Photo = {
            data: photoData;
        }
    }
}

entrypoint {
    Main;
}";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(because: "object literal { data: photoData; } is allowed on RHS of assignment");
        }

        [Fact]
        public async Task CompileAsync_WithObjectLiteralWithSemicolon_SingleLine_ShouldSucceed()
        {
            var source = @"@AGI 0.0.1;

program Test;
module Test/IfJson;

procedure Main {
    var message: vertex = {DIM:[1,0,0,0],W:1,DATA:""x""};
    var photoData := stream<messenger, telegram>;
    message.Photo = { data: photoData; };
}

entrypoint {
    Main;
}";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(because: "object literal with ; is allowed");
        }

        [Fact]
        public async Task CompileAsync_WithStrictJsonLiteralHavingSemicolonInside_ShouldFail()
        {
            // Strict JSON (e.g. string keys, numbers) cannot contain ; inside. Only object literal with var refs (key: var;) is allowed.
            var source = @"@AGI 0.0.1;

program Test;
module Test/IfJson;

procedure Main {
    var x := { ""a"": 1; };
}

entrypoint {
    Main;
}";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            result.ErrorMessage!.Should().Contain("JSON literal").And.Contain("';'");
        }

        [Fact]
        public async Task CompileAsync_WithElseBranchAfterIf_ShouldEmitElseInstructions()
        {
            var source = @"@AGI 0.0.1;

program Test;
module Test/IfElse;

procedure Main {
    var
        stream2 := stream<messenger, telegram>;
    var message := stream2;
    var document := stream2;

    if (document) {
        var size = document.size;
        if (size < unit(20, ""mb"")) {
            stream2.open({
                file: document
            });
            var documentData := streamwait stream2;
            message.Document = {
                data: documentData
            }
        } else {
            message.Error = #""Size {size} exceeded 20mb"";
        }
    }
}

entrypoint {
    Main;
}";

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result.Should().NotBeNull();

            var commands = result.Result!.Procedures["Main"].Body;
            commands.Should().Contain(c => c.Opcode == Opcodes.Jmp, "if/else lowering must jump over the else branch");
            commands.Should().Contain(c => c.Opcode == Opcodes.SetObj, "the else branch assigns message.Error");
            commands
                .Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo callInfo && callInfo.FunctionName == "format")
                .Should()
                .BeTrue("the else branch formats the interpolated error string");
        }
    }
}

