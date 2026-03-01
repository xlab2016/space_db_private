using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class AssemblerTests
    {
        private readonly Assembler _assembler;

        public AssemblerTests()
        {
            _assembler = new Assembler();
        }

        [Fact]
        public void Emit_WithAddVertexOpcode_ShouldCreateAddVertexCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = 1 },
                new DimensionsParameterNode { Name = "dimensions", Values = new List<float> { 1.0f, 0.0f, 0.0f, 0.0f } },
                new WeightParameterNode { Name = "weight", Value = 0.5f },
                new DataParameterNode { Name = "data", Type = "text", Value = "V1" }
            };

            // Act
            var result = _assembler.Emit(Opcodes.AddVertex, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.AddVertex);
            result.Operand1.Should().BeOfType<Vertex>();
        }

        [Fact]
        public void EmitAddVertex_ShouldCreateAddVertexCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = 42 },
                new DimensionsParameterNode { Name = "dimensions", Values = new List<float> { 1.0f, 2.0f, 3.0f } },
                new WeightParameterNode { Name = "weight", Value = 0.75f },
                new DataParameterNode { Name = "data", Type = "text", Value = "TestVertex" }
            };

            // Act
            var result = _assembler.EmitAddVertex(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.AddVertex);
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
        public void Emit_WithAddRelationOpcode_ShouldCreateAddRelationCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = 1 },
                new FromParameterNode { Name = "from", EntityType = "vertex", Index = 1 },
                new ToParameterNode { Name = "to", EntityType = "vertex", Index = 2 },
                new WeightParameterNode { Name = "weight", Value = 0.6f }
            };

            // Act
            var result = _assembler.Emit(Opcodes.AddRelation, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.AddRelation);
            result.Operand1.Should().BeOfType<Relation>();
        }

        [Fact]
        public void EmitAddRelation_ShouldCreateAddRelationCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = 1 },
                new FromParameterNode { Name = "from", EntityType = "vertex", Index = 1 },
                new ToParameterNode { Name = "to", EntityType = "vertex", Index = 2 },
                new WeightParameterNode { Name = "weight", Value = 0.6f }
            };

            // Act
            var result = _assembler.EmitAddRelation(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.AddRelation);
            var relation = result.Operand1.Should().BeOfType<Relation>().Subject;
            relation.Index.Should().Be(1);
            relation.FromIndex.Should().Be(1);
            relation.FromType.Should().Be(EntityType.Vertex);
            relation.ToIndex.Should().Be(2);
            relation.ToType.Should().Be(EntityType.Vertex);
            relation.Weight.Should().Be(0.6f);
        }

        [Fact]
        public void Emit_WithAddShapeOpcode_ShouldCreateAddShapeCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = 1 },
                new VerticesParameterNode { Name = "vertices", Indices = new List<long> { 1, 2, 3 } }
            };

            // Act
            var result = _assembler.Emit(Opcodes.AddShape, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.AddShape);
            result.Operand1.Should().BeOfType<Shape>();
        }

        [Fact]
        public void EmitAddShape_ShouldCreateAddShapeCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = 1 },
                new VerticesParameterNode { Name = "vertices", Indices = new List<long> { 1, 2, 3 } }
            };

            // Act
            var result = _assembler.EmitAddShape(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.AddShape);
            var shape = result.Operand1.Should().BeOfType<Shape>().Subject;
            shape.Index.Should().Be(1);
            shape.VertexIndices.Should().NotBeNull();
            shape.VertexIndices.Should().HaveCount(3);
            shape.VertexIndices.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        }

        [Fact]
        public void Emit_WithCallOpcode_ShouldCreateCallCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "origin" },
                new FunctionParameterNode { Name = "shape", EntityType = "shape", Index = 1 }
            };

            // Act
            var result = _assembler.Emit(Opcodes.Call, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.Call);
            result.Operand1.Should().BeOfType<CallInfo>();
        }

        [Fact]
        public void EmitCall_ShouldCreateCallCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "origin" },
                new FunctionParameterNode { Name = "shape", EntityType = "shape", Index = 1 }
            };

            // Act
            var result = _assembler.EmitCall(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Call);
            var callInfo = result.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("origin");
            callInfo.Parameters.Should().HaveCount(1);
            callInfo.Parameters.Should().ContainKey("shape");
            
            var shapeParam = callInfo.Parameters["shape"].Should().BeOfType<Dictionary<string, object>>().Subject;
            shapeParam["type"].Should().Be(EntityType.Shape);
            shapeParam["index"].Should().Be(1L);
        }

        [Fact]
        public void Emit_WithCallObjOpcode_ShouldCreateCallObjCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "methodName" }
            };

            // Act
            var result = _assembler.Emit(Opcodes.CallObj, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.CallObj);
            result.Operand1.Should().Be("methodName");
        }

        [Fact]
        public void EmitCallObj_ShouldCreateCallObjCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "testMethod" }
            };

            // Act
            var result = _assembler.EmitCallObj(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.CallObj);
            result.Operand1.Should().Be("testMethod");
        }

        [Fact]
        public void EmitCallObj_WithNullParameters_ShouldReturnEmptyString()
        {
            // Act
            var result = _assembler.EmitCallObj(null);

            // Assert
            result.Opcode.Should().Be(Opcodes.CallObj);
            result.Operand1.Should().Be(string.Empty);
        }

        [Fact]
        public void Emit_WithPopOpcode_ShouldCreatePopCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new MemoryParameterNode { Name = "memory", Index = 5 }
            };

            // Act
            var result = _assembler.Emit(Opcodes.Pop, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.Pop);
            result.Operand1.Should().BeOfType<MemoryAddress>();
        }

        [Fact]
        public void EmitPop_ShouldCreatePopCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new MemoryParameterNode { Name = "memory", Index = 5 }
            };

            // Act
            var result = _assembler.EmitPop(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Pop);
            var memoryAddress = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            memoryAddress.Index.Should().Be(5);
        }

        [Fact]
        public void EmitPop_WithNullParameters_ShouldCreateDefaultMemoryAddress()
        {
            // Act
            var result = _assembler.EmitPop(null);

            // Assert
            result.Opcode.Should().Be(Opcodes.Pop);
            var memoryAddress = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            memoryAddress.Index.Should().BeNull();
        }

        [Fact]
        public void Emit_WithPushOpcode_ShouldCreatePushCommand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new MemoryParameterNode { Name = "memory", Index = 0 }
            };

            // Act
            var result = _assembler.Emit(Opcodes.Push, parameters);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.Push);
            result.Operand1.Should().NotBeNull();
        }

        [Fact]
        public void EmitPush_WithMemoryParameter_ShouldCreatePushCommandWithMemoryAddress()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new MemoryParameterNode { Name = "memory", Index = 3 }
            };

            // Act
            var result = _assembler.EmitPush(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Push);
            var memoryAddress = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            memoryAddress.Index.Should().Be(3);
        }

        [Fact]
        public void EmitPush_WithTypeLiteral_ShouldCreatePushCommandWithTypeOperand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new TypeLiteralParameterNode { Name = "type", TypeName = "stream" }
            };

            // Act
            var result = _assembler.EmitPush(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Push);
            var pushOperand = result.Operand1.Should().BeOfType<PushOperand>().Subject;
            pushOperand.Kind.Should().Be("Type");
            pushOperand.Value.Should().Be("stream");
        }

        [Fact]
        public void EmitPush_WithIntLiteral_ShouldCreatePushCommandWithIntOperand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "int", Value = 42 }
            };

            // Act
            var result = _assembler.EmitPush(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Push);
            var pushOperand = result.Operand1.Should().BeOfType<PushOperand>().Subject;
            pushOperand.Kind.Should().Be("IntLiteral");
            pushOperand.Value.Should().Be(42L);
        }

        [Fact]
        public void EmitPush_WithStringLiteral_ShouldCreatePushCommandWithStringOperand()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new StringParameterNode { Name = "string", Value = "test" }
            };

            // Act
            var result = _assembler.EmitPush(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Push);
            var pushOperand = result.Operand1.Should().BeOfType<PushOperand>().Subject;
            pushOperand.Kind.Should().Be("StringLiteral");
            pushOperand.Value.Should().Be("test");
        }

        [Fact]
        public void EmitPush_WithNullParameters_ShouldCreateDefaultMemoryAddress()
        {
            // Act
            var result = _assembler.EmitPush(null);

            // Assert
            result.Opcode.Should().Be(Opcodes.Push);
            var memoryAddress = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            memoryAddress.Index.Should().Be(0);
        }

        [Fact]
        public void EmitPush_WithEmptyParameters_ShouldCreateDefaultMemoryAddress()
        {
            // Arrange
            var parameters = new List<ParameterNode>();

            // Act
            var result = _assembler.EmitPush(parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Push);
            var memoryAddress = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            memoryAddress.Index.Should().Be(0);
        }

        [Fact]
        public void Emit_WithNopOpcode_ShouldCreateNopCommand()
        {
            // Act
            var result = _assembler.Emit(Opcodes.Nop, null);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be(Opcodes.Nop);
            result.Operand1.Should().BeNull();
        }

        [Fact]
        public void Emit_WithCallAndMemoryAddress_ShouldBuildCallInfoWithMemoryAddress()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "print" },
                new FunctionParameterNode { Name = "memory", ParameterName = "memory", EntityType = "memory", Index = 0 }
            };

            // Act
            var result = _assembler.EmitCall(parameters);

            // Assert
            var callInfo = result.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("print");
            callInfo.Parameters.Should().HaveCount(1);
            callInfo.Parameters.Should().ContainKey("memory");
            
            var memoryParam = callInfo.Parameters["memory"].Should().BeOfType<MemoryAddress>().Subject;
            memoryParam.Index.Should().Be(0);
        }

        [Fact]
        public void Emit_WithCmpOpcode_ShouldSplitMemoryAndLiteralIntoDifferentOperands()
        {
            // Arrange
            var parameters = new List<ParameterNode>
            {
                new MemoryParameterNode { Name = "index", Index = 10 },
                new IndexParameterNode { Name = "int", Value = 1 }
            };

            // Act
            var result = _assembler.Emit(Opcodes.Cmp, parameters);

            // Assert
            result.Opcode.Should().Be(Opcodes.Cmp);
            var left = result.Operand1.Should().BeOfType<MemoryAddress>().Subject;
            left.Index.Should().Be(10);
            result.Operand2.Should().Be(1L);
        }
    }
}
