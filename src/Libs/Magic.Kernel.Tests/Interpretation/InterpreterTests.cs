using FluentAssertions;
using Magic.Kernel;
using Magic.Kernel.Compilation;
using Magic.Kernel.Devices;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Moq;
using System;
using System.IO;
using Xunit;

namespace Magic.Kernel.Tests.Interpretation
{
    public class InterpreterTests
    {
        private readonly Interpreter _interpreter;
        private readonly Mock<ISpaceDisk> _spaceDiskMock;
        private readonly KernelConfiguration _configuration;

        public InterpreterTests()
        {
            _interpreter = new Interpreter();
            _spaceDiskMock = new Mock<ISpaceDisk>();
            _configuration = new KernelConfiguration
            {
                DefaultDisk = _spaceDiskMock.Object
            };
            _interpreter.Configuration = _configuration;
        }

        [Fact]
        public async Task InterpreteAsync_WithAddVertexCommand_ShouldCallAddVertexOnSpaceDisk()
        {
            // Arrange
            var vertex = new Vertex
            {
                Index = 1,
                Position = new Position { Dimensions = new List<float> { 1.0f, 0.0f, 0.0f, 0.0f } },
                Weight = 0.5f,
                Data = new EntityData
                {
                    Type = new HierarchicalDataType { Types = new List<DataType> { DataType.Text } },
                    Data = "V1"
                }
            };

            var command = new Command
            {
                Opcode = Opcodes.AddVertex,
                Operand1 = vertex
            };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock { command }
            };

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            // Act
            var result = await _interpreter.InterpreteAsync(executableUnit);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            _spaceDiskMock.Verify(x => x.AddVertex(It.Is<Vertex>(v => v.Index == 1), It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task InterpreteAsync_WithAddVertex_ShouldPassCorrectVertexToSpaceDisk()
        {
            // Arrange
            var vertex = new Vertex
            {
                Index = 42,
                Position = new Position { Dimensions = new List<float> { 1.0f, 2.0f, 3.0f } },
                Weight = 0.75f,
                Data = new EntityData
                {
                    Type = new HierarchicalDataType { Types = new List<DataType> { DataType.Text } },
                    Data = "TestVertex"
                }
            };

            var command = new Command
            {
                Opcode = Opcodes.AddVertex,
                Operand1 = vertex
            };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock { command }
            };

            Vertex? capturedVertex = null;
            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success)
                .Callback<Vertex, string?>((v, _) => capturedVertex = v);

            // Act
            await _interpreter.InterpreteAsync(executableUnit);

            // Assert
            capturedVertex.Should().NotBeNull();
            capturedVertex!.Index.Should().Be(42);
            capturedVertex.Position!.Dimensions.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 3.0f });
            capturedVertex.Weight.Should().Be(0.75f);
            capturedVertex.Data!.Data.Should().Be("TestVertex");
        }

        [Fact]
        public async Task InterpreteAsync_WhenSpaceDiskNotConfigured_ShouldThrow()
        {
            // Arrange
            var interpreterWithoutConfig = new Interpreter();
            interpreterWithoutConfig.Configuration = null;

            var command = new Command
            {
                Opcode = Opcodes.AddVertex,
                Operand1 = new Vertex { Index = 1 }
            };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock { command }
            };

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await interpreterWithoutConfig.InterpreteAsync(executableUnit));
        }

        [Fact]
        public async Task InterpreteAsync_WhenAddVertexReturnsFailure_ShouldThrow()
        {
            // Arrange
            var vertex = new Vertex { Index = 1 };
            var command = new Command
            {
                Opcode = Opcodes.AddVertex,
                Operand1 = vertex
            };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock { command }
            };

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Failed);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _interpreter.InterpreteAsync(executableUnit));
        }

        [Fact]
        public async Task InterpreteAsync_WithInvalidOperand1_ShouldThrow()
        {
            // Arrange
            var command = new Command
            {
                Opcode = Opcodes.AddVertex,
                Operand1 = "invalid operand"
            };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock { command }
            };

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _interpreter.InterpreteAsync(executableUnit));
        }

        [Fact]
        public async Task InterpreteAsync_WithMultipleCommands_ShouldExecuteAll()
        {
            // Arrange
            var vertex1 = new Vertex { Index = 1 };
            var vertex2 = new Vertex { Index = 2 };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = vertex1 },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = vertex2 }
                }
            };

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            // Act
            var result = await _interpreter.InterpreteAsync(executableUnit);

            // Assert
            result.Success.Should().BeTrue();
            _spaceDiskMock.Verify(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()), Times.Exactly(2));
        }

        [Fact]
        public async Task InterpreteAsync_WithNopCommand_ShouldNotThrow()
        {
            // Arrange
            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command { Opcode = Opcodes.Nop }
                }
            };

            // Act
            var result = await _interpreter.InterpreteAsync(executableUnit);

            // Assert
            result.Success.Should().BeTrue();
            _spaceDiskMock.Verify(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task InterpreteAsync_WithPrintMultipleArguments_ShouldWriteAllArguments()
        {
            // Arrange
            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command { Opcode = Opcodes.Push, Operand1 = new PushOperand { Kind = "StringLiteral", Value = "hello" } },
                    new Command { Opcode = Opcodes.Push, Operand1 = new PushOperand { Kind = "IntLiteral", Value = 7L } },
                    new Command { Opcode = Opcodes.Push, Operand1 = new PushOperand { Kind = "IntLiteral", Value = 2L } },
                    new Command { Opcode = Opcodes.Call, Operand1 = new CallInfo { FunctionName = "print" } }
                }
            };

            var originalOut = Console.Out;
            using var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                // Act
                var result = await _interpreter.InterpreteAsync(executableUnit);

                // Assert
                result.Success.Should().BeTrue();
                var output = writer.ToString();
                output.Should().Contain("arg0: hello");
                output.Should().Contain("arg1: 7");
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public async Task InterpreteAsync_WithCallIntoProcedureAndRet_ShouldReturnToCaller()
        {
            // Arrange
            var v1 = new Vertex { Index = 1 };
            var v2 = new Vertex { Index = 2 };

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command
                    {
                        Opcode = Opcodes.Call,
                        Operand1 = new CallInfo { FunctionName = "p1" }
                    },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = v2 }
                },
                Procedures = new Dictionary<string, Magic.Kernel.Processor.Procedure>
                {
                    ["p1"] = new Magic.Kernel.Processor.Procedure
                    {
                        Name = "p1",
                        Body = new ExecutionBlock
                        {
                            new Command { Opcode = Opcodes.AddVertex, Operand1 = v1 },
                            new Command { Opcode = Opcodes.Ret }
                        }
                    }
                }
            };

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success);

            // Act
            var result = await _interpreter.InterpreteAsync(executableUnit);

            // Assert
            result.Success.Should().BeTrue();
            _spaceDiskMock.Verify(x => x.AddVertex(It.Is<Vertex>(v => v.Index == 1), It.IsAny<string?>()), Times.Once);
            _spaceDiskMock.Verify(x => x.AddVertex(It.Is<Vertex>(v => v.Index == 2), It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task InterpreteAsync_WithCmpMemoryAndLiteralEqual_ShouldJumpToLabel()
        {
            var executedIndices = new List<long?>();

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success)
                .Callback<Vertex, string?>((v, _) => executedIndices.Add(v.Index));

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command
                    {
                        Opcode = Opcodes.Push,
                        Operand1 = new PushOperand { Kind = "IntLiteral", Value = 1L }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Pop,
                        Operand1 = new MemoryAddress { Index = 10 }
                    },
                    new Command { Opcode = Opcodes.Cmp, Operand1 = new MemoryAddress { Index = 10 }, Operand2 = 1L },
                    new Command { Opcode = Opcodes.Je, Operand1 = "equal" },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = new Vertex { Index = 1 } },
                    new Command { Opcode = Opcodes.Label, Operand1 = "equal" },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = new Vertex { Index = 2 } }
                }
            };

            var result = await _interpreter.InterpreteAsync(executableUnit);

            result.Success.Should().BeTrue();
            executedIndices.Should().ContainSingle().Which.Should().Be(2);
        }

        [Fact]
        public async Task InterpreteAsync_WithCmpTwoMemoryOperandsEqual_ShouldJumpToLabel()
        {
            var executedIndices = new List<long?>();

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success)
                .Callback<Vertex, string?>((v, _) => executedIndices.Add(v.Index));

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command
                    {
                        Opcode = Opcodes.Push,
                        Operand1 = new PushOperand { Kind = "IntLiteral", Value = 5L }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Pop,
                        Operand1 = new MemoryAddress { Index = 10 }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Push,
                        Operand1 = new PushOperand { Kind = "IntLiteral", Value = 5L }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Pop,
                        Operand1 = new MemoryAddress { Index = 11 }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Cmp,
                        Operand1 = new MemoryAddress { Index = 10 },
                        Operand2 = new MemoryAddress { Index = 11 }
                    },
                    new Command { Opcode = Opcodes.Je, Operand1 = "equal" },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = new Vertex { Index = 1 } },
                    new Command { Opcode = Opcodes.Label, Operand1 = "equal" },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = new Vertex { Index = 2 } }
                }
            };

            var result = await _interpreter.InterpreteAsync(executableUnit);

            result.Success.Should().BeTrue();
            executedIndices.Should().ContainSingle().Which.Should().Be(2);
        }

        [Fact]
        public async Task InterpreteAsync_WithCmpLiteralAndMemoryEqual_ShouldJumpToLabel()
        {
            var executedIndices = new List<long?>();

            _spaceDiskMock
                .Setup(x => x.AddVertex(It.IsAny<Vertex>(), It.IsAny<string?>()))
                .ReturnsAsync(SpaceOperationResult.Success)
                .Callback<Vertex, string?>((v, _) => executedIndices.Add(v.Index));

            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command
                    {
                        Opcode = Opcodes.Push,
                        Operand1 = new PushOperand { Kind = "IntLiteral", Value = 7L }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Pop,
                        Operand1 = new MemoryAddress { Index = 12 }
                    },
                    new Command { Opcode = Opcodes.Cmp, Operand1 = 7L, Operand2 = new MemoryAddress { Index = 12 } },
                    new Command { Opcode = Opcodes.Je, Operand1 = "equal" },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = new Vertex { Index = 1 } },
                    new Command { Opcode = Opcodes.Label, Operand1 = "equal" },
                    new Command { Opcode = Opcodes.AddVertex, Operand1 = new Vertex { Index = 2 } }
                }
            };

            var result = await _interpreter.InterpreteAsync(executableUnit);

            result.Success.Should().BeTrue();
            executedIndices.Should().ContainSingle().Which.Should().Be(2);
        }

        [Fact]
        public async Task InterpreteAsync_WithCmpEqual_ShouldPushOneToStack()
        {
            var executableUnit = new ExecutableUnit
            {
                EntryPoint = new ExecutionBlock
                {
                    new Command
                    {
                        Opcode = Opcodes.Push,
                        Operand1 = new PushOperand { Kind = "IntLiteral", Value = 3L }
                    },
                    new Command
                    {
                        Opcode = Opcodes.Pop,
                        Operand1 = new MemoryAddress { Index = 20 }
                    },
                    new Command { Opcode = Opcodes.Cmp, Operand1 = new MemoryAddress { Index = 20 }, Operand2 = 3L },
                    new Command { Opcode = Opcodes.Pop, Operand1 = new MemoryAddress { Index = 21 } }
                }
            };

            var result = await _interpreter.InterpreteAsync(executableUnit);

            result.Success.Should().BeTrue();
            _interpreter.GlobalMemory.Should().ContainKey(21);
            _interpreter.GlobalMemory[21].Should().Be(1L);
        }
    }
}
