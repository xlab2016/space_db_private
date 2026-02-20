using FluentAssertions;
using Magic.Kernel;
using Magic.Kernel.Devices;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Moq;
using Xunit;

namespace Magic.Kernel.Tests.Integration
{
    public class EndToEndTests
    {
        [Fact]
        public async Task CompileAndInterpret_WithAddVertex_ShouldWorkEndToEnd()
        {
            // Arrange
            var sourceCode = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: \"V1\"";
            
            var spaceDiskMock = new Mock<ISpaceDisk>();
            spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            var kernel = new MagicKernel();
            kernel.Configuration.DefaultDisk = spaceDiskMock.Object;
            kernel.Devices.Add(spaceDiskMock.Object);
            await kernel.StartKernel();

            // Act
            var compilationResult = await kernel.CompileAsync(sourceCode);
            var interpretationResult = await kernel.InterpreteSourceCodeAsync(sourceCode);

            // Assert
            compilationResult.Success.Should().BeTrue();
            interpretationResult.Success.Should().BeTrue();
            
            spaceDiskMock.Verify(x => x.AddVertex(It.Is<Vertex>(v => 
                v.Index == 1 &&
                v.Position!.Dimensions.SequenceEqual(new[] { 1.0f, 0.0f, 0.0f, 0.0f }) &&
                v.Weight == 0.5f &&
                v.Data!.Data == "V1"
            ), It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task CompileAndInterpret_WithMultipleAddVertex_ShouldExecuteAll()
        {
            // Arrange
            var sourceCode = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: \"V1\"\naddvertex index: 2, dimensions: [0, 1, 0, 0], weight: 0.75, data: text: \"V2\"";

            var spaceDiskMock = new Mock<ISpaceDisk>();
            spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            var kernel = new MagicKernel();
            kernel.Configuration.DefaultDisk = spaceDiskMock.Object;
            kernel.Devices.Add(spaceDiskMock.Object);
            await kernel.StartKernel();

            // Act
            var compilationResult = await kernel.CompileAsync(sourceCode);
            var interpretationResult = await kernel.InterpreteSourceCodeAsync(sourceCode);

            // Assert
            compilationResult.Success.Should().BeTrue();
            interpretationResult.Success.Should().BeTrue();
            
            spaceDiskMock.Verify(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()), Times.Exactly(2));
        }

        [Fact]
        public async Task CompileAndInterpret_WithCompileStreamProgram_ShouldRunSuccessfully()
        {
            var sourceCode = @"@AGI 0.0.1;

program compile_stream;
module samples/streams/compile_stream;

procedure Main {
	var stream1 := stream<file>;
	stream1.open(""stream1"");
	var data = await stream1;
	var semantic_file_system = compile(data);

	print(semantic_file_system);
}

entrypoint {
	Main;
}";

            var spaceDiskMock = new Mock<ISpaceDisk>();
            spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            var kernel = new MagicKernel();
            kernel.Configuration.DefaultDisk = spaceDiskMock.Object;
            kernel.Devices.Add(spaceDiskMock.Object);
            await kernel.StartKernel();

            var compilationResult = await kernel.CompileAsync(sourceCode);
            compilationResult.Success.Should().BeTrue(compilationResult.ErrorMessage);

            var interpretationResult = await kernel.InterpreteSourceCodeAsync(sourceCode);
            interpretationResult.Success.Should().BeTrue();
        }

        [Fact]
        public async Task Compile_WithVaultAndMessengerStreamProgram_ShouldSucceed()
        {
            var sourceCode = @"@AGI 0.0.1;

program telegram_to_db_min;
module samples/telegram_to_db_min;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
    var stream1 := stream<messenger, telegram>;
}

entrypoint {
    Main;
}";

            var spaceDiskMock = new Mock<ISpaceDisk>();
            spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            var kernel = new MagicKernel();
            kernel.Configuration.DefaultDisk = spaceDiskMock.Object;
            kernel.Devices.Add(spaceDiskMock.Object);
            await kernel.StartKernel();

            var compilationResult = await kernel.CompileAsync(sourceCode);
            compilationResult.Success.Should().BeTrue(compilationResult.ErrorMessage);
        }
    }
}
