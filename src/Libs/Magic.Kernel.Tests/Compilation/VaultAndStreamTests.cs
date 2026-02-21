using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class VaultAndStreamTests
    {
        [Fact]
        public void ParseProgram_WithVaultDeclaration_ShouldProduceNoAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;
            asm.Should().BeEmpty();
        }

        [Fact]
        public void ParseProgram_WithVaultRead_ShouldGenerateExpectedAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;
            asm.Should().HaveCount(4);
            asm[0].Opcode.Should().Be(Opcodes.Push);
            asm[1].Opcode.Should().Be(Opcodes.Push);
            asm[2].Opcode.Should().Be(Opcodes.Call);
            ((CallInfo)asm[2].Operand1!).FunctionName.Should().Be("vault_read");
            asm[3].Opcode.Should().Be(Opcodes.Pop);
        }

        [Fact]
        public void ParseProgram_WithStreamMessengerTelegram_ShouldGenerateStreamAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<messenger, telegram>;
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;
            asm.Should().HaveCount(8);
            asm[0].Opcode.Should().Be(Opcodes.Push);
            asm[1].Opcode.Should().Be(Opcodes.Def);
            asm[2].Opcode.Should().Be(Opcodes.Pop);
            asm[3].Opcode.Should().Be(Opcodes.Push);
            asm[4].Opcode.Should().Be(Opcodes.Push);
            asm[5].Opcode.Should().Be(Opcodes.Push);
            asm[6].Opcode.Should().Be(Opcodes.DefGen);
            asm[7].Opcode.Should().Be(Opcodes.Pop);
        }
    }
}

