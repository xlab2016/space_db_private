using FluentAssertions;
using Magic.Kernel.Compilation;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class HighLevelVaultAndStreamTests
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

            // Act
            var structure = parser.ParseProgram(source);

            // Assert
            structure.Procedures.Should().ContainKey("Main");
            var asm = structure.Procedures["Main"];
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

            // Act
            var structure = parser.ParseProgram(source);

            // Assert
            structure.Procedures.Should().ContainKey("Main");
            var asm = structure.Procedures["Main"];
            asm.Should().HaveCount(4);
            asm[0].Should().Be("push string: \"token\"");
            asm[1].Should().Be("push 1");
            asm[2].Should().Be("call vault_read");
            asm[3].Should().StartWith("pop [");
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

            // Act
            var structure = parser.ParseProgram(source);

            // Assert
            structure.Procedures.Should().ContainKey("Main");
            var asm = structure.Procedures["Main"];
            asm.Should().NotBeEmpty();
            asm[0].Should().Be("push stream");
            asm.Should().Contain(line => line == "defgen");
        }
    }
}

