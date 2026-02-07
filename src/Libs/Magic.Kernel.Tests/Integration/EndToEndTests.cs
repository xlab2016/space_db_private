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
                .Setup(x => x.AddVertex(It.IsAny<Vertex>()))
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
            )), Times.Once);
        }

        [Fact]
        public async Task CompileAndInterpret_WithMultipleAddVertex_ShouldExecuteAll()
        {
            // Arrange
            var sourceCode = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: \"V1\"\naddvertex index: 2, dimensions: [0, 1, 0, 0], weight: 0.75, data: text: \"V2\"";

            var spaceDiskMock = new Mock<ISpaceDisk>();
            spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>()))
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
            
            spaceDiskMock.Verify(x => x.AddVertex(It.IsAny<Vertex>()), Times.Exactly(2));
        }
    }
}
