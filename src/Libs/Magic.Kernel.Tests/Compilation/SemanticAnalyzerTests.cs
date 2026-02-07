using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class SemanticAnalyzerTests
    {
        private readonly SemanticAnalyzer _analyzer;

        public SemanticAnalyzerTests()
        {
            _analyzer = new SemanticAnalyzer();
        }

        [Fact]
        public void Analyze_WithAddVertexInstruction_ShouldCreateAddVertexCommand()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 1 },
                    new DimensionsParameterNode { Name = "dimensions", Values = new List<float> { 1.0f, 0.0f, 0.0f, 0.0f } },
                    new WeightParameterNode { Name = "weight", Value = 0.5f },
                    new DataParameterNode { Name = "data", Type = "text", Value = "V1" }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.AddVertex);
            result.Operand1.Should().BeOfType<Vertex>();
        }

        [Fact]
        public void Analyze_WithAddVertex_ShouldBuildVertexCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 42 },
                    new DimensionsParameterNode { Name = "dimensions", Values = new List<float> { 1.0f, 2.0f, 3.0f } },
                    new WeightParameterNode { Name = "weight", Value = 0.75f },
                    new DataParameterNode { Name = "data", Type = "text", Value = "TestVertex" }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Index.Should().Be(42);
            vertex.Position.Should().NotBeNull();
            vertex.Position!.Dimensions.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 3.0f });
            vertex.Weight.Should().Be(0.75f);
            vertex.Data.Should().NotBeNull();
            vertex.Data!.Data.Should().Be("TestVertex");
            vertex.Data.Type.Types.Should().Contain(DataType.Text);
        }

        [Fact]
        public void Analyze_WithOnlyIndex_ShouldSetOnlyIndex()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 10 }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Index.Should().Be(10);
            vertex.Position.Should().BeNull();
            vertex.Weight.Should().BeNull();
            vertex.Data.Should().BeNull();
        }

        [Fact]
        public void Analyze_WithOnlyDimensions_ShouldSetOnlyPosition()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new DimensionsParameterNode { Name = "dimensions", Values = new List<float> { 5.0f, 6.0f } }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Position.Should().NotBeNull();
            vertex.Position!.Dimensions.Should().BeEquivalentTo(new[] { 5.0f, 6.0f });
            vertex.Index.Should().BeNull();
        }

        [Fact]
        public void Analyze_WithOnlyWeight_ShouldSetOnlyWeight()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new WeightParameterNode { Name = "weight", Value = 0.9f }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Weight.Should().Be(0.9f);
        }

        [Fact]
        public void Analyze_WithOnlyData_ShouldSetOnlyData()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new DataParameterNode { Name = "data", Type = "text", Value = "Hello" }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Data.Should().NotBeNull();
            vertex.Data!.Data.Should().Be("Hello");
            vertex.Data.Type.Types.Should().Contain(DataType.Text);
        }

        [Fact]
        public void Analyze_WithJsonDataType_ShouldThrowInvalidDataType()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new DataParameterNode { Name = "data", Type = "json", Value = "{\"key\":\"value\"}", HasColon = true, OriginalString = "json \"{\\\"key\\\":\\\"value\\\"}\"" }
                }
            };

            // Act
            Action act = () => _analyzer.Analyze(instruction);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*недопустимый тип данных:*json*value*");
        }

        [Fact]
        public void Analyze_WithUnknownOpcode_ShouldMapToNop()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "unknownop",
                Parameters = new List<ParameterNode>()
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Opcode.Should().Be(Opcodes.Nop);
        }

        [Fact]
        public void Analyze_WithBinaryBase64DataType_ShouldMapToBothTypes()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new DataParameterNode 
                    { 
                        Name = "data", 
                        Types = new List<string> { "binary", "base64" },
                        Value = "sdsfdsgfg==" 
                    }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Data.Should().NotBeNull();
            vertex.Data!.Type.Types.Should().HaveCount(2);
            vertex.Data.Type.Types.Should().Contain(DataType.Binary);
            vertex.Data.Type.Types.Should().Contain(DataType.Base64);
            vertex.Data.Data.Should().Be("sdsfdsgfg==");
        }

        [Fact]
        public void Analyze_WithFullAddVertexBinaryBase64_ShouldBuildVertexCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addvertex",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 3 },
                    new DimensionsParameterNode { Name = "dimensions", Values = new List<float> { 1.0f, 2.0f, 0.0f, 0.0f } },
                    new WeightParameterNode { Name = "weight", Value = 0.8f },
                    new DataParameterNode 
                    { 
                        Name = "data", 
                        Types = new List<string> { "binary", "base64" },
                        Value = "sdsfdsgfg==" 
                    }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Opcode.Should().Be(Opcodes.AddVertex);
            var vertex = result.Operand1.Should().BeOfType<Vertex>().Subject;
            vertex.Index.Should().Be(3);
            vertex.Position.Should().NotBeNull();
            vertex.Position!.Dimensions.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 0.0f, 0.0f });
            vertex.Weight.Should().Be(0.8f);
            vertex.Data.Should().NotBeNull();
            vertex.Data!.Type.Types.Should().HaveCount(2);
            vertex.Data.Type.Types.Should().Contain(DataType.Binary);
            vertex.Data.Type.Types.Should().Contain(DataType.Base64);
            vertex.Data.Data.Should().Be("sdsfdsgfg==");
        }

        [Fact]
        public void Analyze_WithAddRelationInstruction_ShouldCreateAddRelationCommand()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addrelation",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 1 },
                    new FromParameterNode { Name = "from", EntityType = "vertex", Index = 1 },
                    new ToParameterNode { Name = "to", EntityType = "vertex", Index = 2 },
                    new WeightParameterNode { Name = "weight", Value = 0.6f }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.AddRelation);
            result.Operand1.Should().BeOfType<Relation>();
        }

        [Fact]
        public void Analyze_WithAddRelation_ShouldBuildRelationCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addrelation",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 1 },
                    new FromParameterNode { Name = "from", EntityType = "vertex", Index = 1 },
                    new ToParameterNode { Name = "to", EntityType = "vertex", Index = 2 },
                    new WeightParameterNode { Name = "weight", Value = 0.6f }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var relation = result.Operand1.Should().BeOfType<Relation>().Subject;
            relation.Index.Should().Be(1);
            relation.FromIndex.Should().Be(1);
            relation.FromType.Should().Be(EntityType.Vertex);
            relation.ToIndex.Should().Be(2);
            relation.ToType.Should().Be(EntityType.Vertex);
            relation.Weight.Should().Be(0.6f);
        }

        [Fact]
        public void Analyze_WithAddRelationBetweenRelations_ShouldMapEntityTypesCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addrelation",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 4 },
                    new FromParameterNode { Name = "from", EntityType = "relation", Index = 1 },
                    new ToParameterNode { Name = "to", EntityType = "relation", Index = 3 },
                    new WeightParameterNode { Name = "weight", Value = 0.2f }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var relation = result.Operand1.Should().BeOfType<Relation>().Subject;
            relation.FromType.Should().Be(EntityType.Relation);
            relation.ToType.Should().Be(EntityType.Relation);
            relation.FromIndex.Should().Be(1);
            relation.ToIndex.Should().Be(3);
        }

        [Fact]
        public void Analyze_WithAddShapeInstruction_ShouldCreateAddShapeCommand()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addshape",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 1 },
                    new VerticesParameterNode { Name = "vertices", Indices = new List<long> { 1, 2, 3 } }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.AddShape);
            result.Operand1.Should().BeOfType<Shape>();
        }

        [Fact]
        public void Analyze_WithAddShape_ShouldBuildShapeCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "addshape",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = 1 },
                    new VerticesParameterNode { Name = "vertices", Indices = new List<long> { 1, 2, 3 } }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var shape = result.Operand1.Should().BeOfType<Shape>().Subject;
            shape.Index.Should().Be(1);
            shape.VertexIndices.Should().NotBeNull();
            shape.VertexIndices.Should().HaveCount(3);
            shape.VertexIndices.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        }

        [Fact]
        public void Analyze_WithCallInstruction_ShouldCreateCallCommand()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "call",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { Name = "function", FunctionName = "origin" },
                    new FunctionParameterNode { Name = "shape", EntityType = "shape", Index = 1 }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.Call);
            result.Operand1.Should().BeOfType<CallInfo>();
        }

        [Fact]
        public void Analyze_WithCall_ShouldBuildCallInfoCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "call",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { Name = "function", FunctionName = "origin" },
                    new FunctionParameterNode { Name = "shape", EntityType = "shape", Index = 1 }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var callInfo = result.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("origin");
            callInfo.Parameters.Should().HaveCount(1);
            callInfo.Parameters.Should().ContainKey("shape");
            
            var shapeParam = callInfo.Parameters["shape"].Should().BeOfType<Dictionary<string, object>>().Subject;
            shapeParam["type"].Should().Be(EntityType.Shape);
            shapeParam["index"].Should().Be(1L);
        }

        [Fact]
        public void Analyze_WithPopInstruction_ShouldCreatePopCommand()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "pop",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "memory", Index = 0 }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.Pop);
            result.Operand1.Should().BeOfType<MemoryAddress>();
        }

        [Fact]
        public void Analyze_WithPop_ShouldBuildMemoryAddressCorrectly()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "pop",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "memory", Index = 5 }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var memoryAddress = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            memoryAddress.Index.Should().Be(5);
        }

        [Fact]
        public void Analyze_WithCallAndMemoryAddress_ShouldBuildCallInfoWithMemoryAddress()
        {
            // Arrange
            var instruction = new InstructionNode
            {
                Opcode = "call",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { Name = "function", FunctionName = "print" },
                    new FunctionParameterNode { Name = "memory", ParameterName = "memory", EntityType = "memory", Index = 0 }
                }
            };

            // Act
            var result = _analyzer.Analyze(instruction);

            // Assert
            var callInfo = result.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("print");
            callInfo.Parameters.Should().HaveCount(1);
            callInfo.Parameters.Should().ContainKey("memory");
            
            var memoryParam = callInfo.Parameters["memory"].Should().BeOfType<MemoryAddress>().Subject;
            memoryParam.Index.Should().Be(0);
        }
    }
}
