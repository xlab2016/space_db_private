using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class CompilerTests
    {
        private readonly Compiler _compiler;

        public CompilerTests()
        {
            _compiler = new Compiler();
        }

        [Fact]
        public async Task CompileAsync_WithAddVertexInstruction_ShouldCompileSuccessfully()
        {
            // Arrange
            var source = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: \"V1\"";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
        }

        [Fact]
        public async Task CompileAsync_WithAddVertex_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "addvertex index: 42, dimensions: [1, 2, 3], weight: 0.75, data: text: \"Test\"";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            var command = result.Result!.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.AddVertex);
            command.Operand1.Should().BeOfType<Vertex>();

            var vertex = (Vertex)command.Operand1!;
            vertex.Index.Should().Be(42);
            vertex.Position!.Dimensions.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 3.0f });
            vertex.Weight.Should().Be(0.75f);
            vertex.Data!.Data.Should().Be("Test");
        }

        [Fact]
        public async Task CompileAsync_WithInvalidSyntax_ShouldCreateNopCommand()
        {
            // Arrange
            var source = "invalid syntax !@#$%";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            // Парсер создает opcode "invalid", SemanticAnalyzer маппит его в Nop
            result.Success.Should().BeTrue();
            result.Result!.EntryPoint.Should().HaveCount(1);
            result.Result.EntryPoint.First().Opcode.Should().Be(Opcodes.Nop);
        }

        [Fact]
        public async Task CompileAsync_WithAddVertexBinaryBase64_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "addvertex index: 3, dimensions: [1, 2, 0, 0], weight: 0.8, data: binary: base64: \"sdsfdsgfg==\"";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(1);
            var command = result.Result.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.AddVertex);
            command.Operand1.Should().BeOfType<Vertex>();
            
            var vertex = (Vertex)command.Operand1!;
            vertex.Index.Should().Be(3);
            vertex.Position!.Dimensions.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 0.0f, 0.0f });
            vertex.Weight.Should().Be(0.8f);
            vertex.Data.Should().NotBeNull();
            vertex.Data!.Type.Types.Should().HaveCount(2);
            vertex.Data.Type.Types.Should().Contain(DataType.Binary);
            vertex.Data.Type.Types.Should().Contain(DataType.Base64);
            vertex.Data.Data.Should().Be("sdsfdsgfg==");
        }

        [Fact]
        public async Task CompileAsync_WithAddVertexTextWithoutColon_ShouldFailAndPreserveValue()
        {
            // Arrange
            var source = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text \"V1\"";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNull();
            result.ErrorMessage!.Should().Contain("недопустимый тип данных");
            result.ErrorMessage!.Should().Contain("text V1");
        }

        [Fact]
        public async Task CompileAsync_WithAddVertexTextWithoutColonAndQuotes_ShouldFailAndPreserveValue()
        {
            // Arrange
            var source = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text V2";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNull();
            result.ErrorMessage!.Should().Contain("недопустимый тип данных");
            result.ErrorMessage!.Should().Contain("text V2");
        }

        [Fact]
        public async Task CompileAsync_WithAddRelation_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "addrelation index: 1, from: vertex: index: 1, to: vertex: index: 2, weight: 0.6";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(1);
            var command = result.Result.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.AddRelation);
            command.Operand1.Should().BeOfType<Relation>();
            
            var relation = (Relation)command.Operand1!;
            relation.Index.Should().Be(1);
            relation.FromIndex.Should().Be(1);
            relation.FromType.Should().Be(EntityType.Vertex);
            relation.ToIndex.Should().Be(2);
            relation.ToType.Should().Be(EntityType.Vertex);
            relation.Weight.Should().Be(0.6f);
        }

        [Fact]
        public async Task CompileAsync_WithAddRelationBetweenRelations_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "addrelation index: 4, from: relation: index: 1, to: relation: index: 3, weight: 0.2";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            var command = result.Result!.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.AddRelation);
            
            var relation = (Relation)command.Operand1!;
            relation.FromType.Should().Be(EntityType.Relation);
            relation.ToType.Should().Be(EntityType.Relation);
            relation.FromIndex.Should().Be(1);
            relation.ToIndex.Should().Be(3);
        }

        [Fact]
        public async Task CompileAsync_WithAddShape_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "addshape index: 1, vertices: indices: [1, 2, 3]";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(1);
            var command = result.Result.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.AddShape);
            command.Operand1.Should().BeOfType<Shape>();
            
            var shape = (Shape)command.Operand1!;
            shape.Index.Should().Be(1);
            shape.VertexIndices.Should().NotBeNull();
            shape.VertexIndices.Should().HaveCount(3);
            shape.VertexIndices.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        }

        [Fact]
        public async Task CompileAsync_WithCall_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "call \"origin\", shape: index: 1";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(1);
            var command = result.Result.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.Call);
            command.Operand1.Should().BeOfType<CallInfo>();
            
            var callInfo = (CallInfo)command.Operand1!;
            callInfo.FunctionName.Should().Be("origin");
            callInfo.Parameters.Should().HaveCount(1);
            callInfo.Parameters.Should().ContainKey("shape");
        }

        [Fact]
        public async Task CompileAsync_WithCallIntersect_WithInlineShapeLiteral_ShouldIncludeShapeB()
        {
            // Arrange
            var source =
                "call \"intersect\", " +
                "shapeA: shape: index: 1, " +
                "shapeB: shape: { vertices: [ { dimensions: [1, 0, 0, 0] }, { dimensions: [1, 2, 0, 0] } ] }";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var command = result.Result!.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.Call);
            var callInfo = command.Operand1.Should().BeOfType<CallInfo>().Subject;

            callInfo.Parameters.Should().ContainKey("shapeA");
            callInfo.Parameters.Should().ContainKey("shapeB");

            // shapeA — ссылка на shape index=1
            var shapeARef = callInfo.Parameters["shapeA"].Should().BeOfType<Dictionary<string, object>>().Subject;
            shapeARef.Should().ContainKey("type");
            shapeARef.Should().ContainKey("index");

            // shapeB — inline Shape
            var shapeB = callInfo.Parameters["shapeB"].Should().BeOfType<Shape>().Subject;
            shapeB.Vertices.Should().NotBeNull();
            shapeB.Vertices!.Should().HaveCount(2);
            shapeB.Vertices[0].Position!.Dimensions.Should().BeEquivalentTo(new[] { 1f, 0f, 0f, 0f });
            shapeB.Vertices[1].Position!.Dimensions.Should().BeEquivalentTo(new[] { 1f, 2f, 0f, 0f });
        }

        [Fact]
        public async Task CompileAsync_WithProgramStructure_MultilineIntersectWithTrailingCommas_ShouldNotDropShapeBOrCreateNops()
        {
            // Arrange — воспроизводим формат из issue: многострочный shapeB и trailing commas
            var source = @"@AGI 0.0.1

program L0;
module L/L0;

procedure Main {
    asm {
        addshape index: 1, vertices: indices: [1, 2, 3];

        call ""intersect"", shapeA: shape: index: 1, shapeB: shape: {
            vertices: [
                { dimensions: [1, 0, 0, 0] },
                { dimensions: [1, 2, 0, 0] },
            ],
        };

        pop [1];
        call ""print"", [1];
    }
}

entrypoint {
    asm {
        call Main;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("Main");

            var main = result.Result.Procedures["Main"];
            main.Body.Should().HaveCount(4);

            main.Body[0].Opcode.Should().Be(Opcodes.AddShape);
            main.Body[1].Opcode.Should().Be(Opcodes.Call);
            main.Body[2].Opcode.Should().Be(Opcodes.Pop);
            main.Body[3].Opcode.Should().Be(Opcodes.Call);

            var intersectCall = main.Body[1].Operand1.Should().BeOfType<CallInfo>().Subject;
            intersectCall.FunctionName.Should().Be("intersect");
            intersectCall.Parameters.Should().ContainKey("shapeA");
            intersectCall.Parameters.Should().ContainKey("shapeB");

            var shapeB = intersectCall.Parameters["shapeB"].Should().BeOfType<Shape>().Subject;
            shapeB.Vertices.Should().NotBeNull();
            shapeB.Vertices!.Should().HaveCount(2);
            shapeB.Vertices[0].Position!.Dimensions.Should().BeEquivalentTo(new[] { 1f, 0f, 0f, 0f });
            shapeB.Vertices[1].Position!.Dimensions.Should().BeEquivalentTo(new[] { 1f, 2f, 0f, 0f });

            // И главное — никаких Nop из-за рассинхронизации
            main.Body.Should().NotContain(c => c.Opcode == Opcodes.Nop);
        }

        [Fact]
        public async Task CompileAsync_WithProgramStructure_IntersectEndedByBraceWithoutSemicolon_ShouldNotEatNextInstruction()
        {
            // Arrange — как в репорте: после inline-shape литерала нет ';', следующая инструкция начинается с новой строки
            var source = @"@AGI 0.0.1

program L0;
module L/L0;

procedure Main {
    asm {
        addshape index: 1, vertices: indices: [1, 2, 3];

        call ""intersect"", shapeA: shape: index: 1, shapeB: shape: {
            vertices: [
                { dimensions: [1, 0, 0, 0] },
                { dimensions: [1, 2, 0, 0] },
            ]
        }
        pop [1];
        call ""print"", [1];
    }
}

entrypoint {
    asm {
        call Main;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var main = result.Result!.Procedures["Main"];

            main.Body.Should().HaveCount(4);
            main.Body[0].Opcode.Should().Be(Opcodes.AddShape);
            main.Body[1].Opcode.Should().Be(Opcodes.Call);
            main.Body[2].Opcode.Should().Be(Opcodes.Pop);
            main.Body[3].Opcode.Should().Be(Opcodes.Call);

            var intersectCall = main.Body[1].Operand1.Should().BeOfType<CallInfo>().Subject;
            intersectCall.FunctionName.Should().Be("intersect");
            intersectCall.Parameters.Should().ContainKey("shapeB");

            // entrypoint должен вызывать Main по имени
            result.Result.EntryPoint.Should().HaveCount(1);
            var entryCall = result.Result.EntryPoint[0];
            entryCall.Opcode.Should().Be(Opcodes.Call);
            var entryCallInfo = entryCall.Operand1.Should().BeOfType<CallInfo>().Subject;
            entryCallInfo.FunctionName.Should().Be("Main");
        }

        [Fact]
        public async Task CompileAsync_WithPop_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "pop [0]";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(1);
            var command = result.Result.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.Pop);
            command.Operand1.Should().BeOfType<MemoryAddress>();
            
            var memoryAddress = (MemoryAddress)command.Operand1!;
            memoryAddress.Index.Should().Be(0);
        }

        [Fact]
        public async Task CompileAsync_WithCallAndMemoryAddress_ShouldCreateCorrectCommand()
        {
            // Arrange
            var source = "call \"print\", [0]";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(1);
            var command = result.Result.EntryPoint.First();
            command.Opcode.Should().Be(Opcodes.Call);
            command.Operand1.Should().BeOfType<CallInfo>();
            
            var callInfo = (CallInfo)command.Operand1!;
            callInfo.FunctionName.Should().Be("print");
            callInfo.Parameters.Should().HaveCount(1);
            callInfo.Parameters.Should().ContainKey("memory");
            
            var memoryParam = callInfo.Parameters["memory"].Should().BeOfType<MemoryAddress>().Subject;
            memoryParam.Index.Should().Be(0);
        }
        [Fact]
        public async Task CompileAsync_WithFullProgramExample_ShouldCompileAllInstructions()
        {
            // Arrange - полный пример программы из документации
            var source = @"addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: ""V1"";
addvertex index: 2, dimensions: [1, 1, 0, 0], weight: 0.5, data: text: ""V2"";
addvertex index: 3, dimensions: [1, 2, 0, 0], weight: 0.8, data: binary: base64: ""sdsfdsgfg=="";
addrelation index: 1, from: vertex: index: 1, to: vertex: index: 2, weight: 0.6;
addrelation index: 2, from: vertex: index: 1, to: vertex: index: 3, weight: 0.6;
addrelation index: 3, from: vertex: index: 2, to: vertex: index: 3, weight: 0.6;
addrelation index: 4, from: relation: index: 1, to: relation: index: 3, weight: 0.2;
addshape index: 1, vertices: indices: [1, 2, 3];
call ""origin"", shape: index: 1;
pop [0];
call ""print"", [0];
call ""intersect"", shapeA: shape: index: 1, shapeB: shape: { vertices: [ { dimensions: [1, 0, 0, 0] }, { dimensions: [1, 2, 0, 0] } ] };
pop [1];
call ""print"", [1];";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.EntryPoint.Should().HaveCount(14); // 3 addvertex + 4 addrelation + 1 addshape + 6 call/pop
            
            var commands = result.Result.EntryPoint;
            
            // Проверяем, что все команды имеют правильные opcodes
            commands[0].Opcode.Should().Be(Opcodes.AddVertex);
            commands[1].Opcode.Should().Be(Opcodes.AddVertex);
            commands[2].Opcode.Should().Be(Opcodes.AddVertex);
            commands[3].Opcode.Should().Be(Opcodes.AddRelation);
            commands[4].Opcode.Should().Be(Opcodes.AddRelation);
            commands[5].Opcode.Should().Be(Opcodes.AddRelation);
            commands[6].Opcode.Should().Be(Opcodes.AddRelation);
            commands[7].Opcode.Should().Be(Opcodes.AddShape);
            commands[8].Opcode.Should().Be(Opcodes.Call);
            commands[9].Opcode.Should().Be(Opcodes.Pop);
            commands[10].Opcode.Should().Be(Opcodes.Call);
            commands[11].Opcode.Should().Be(Opcodes.Call);
            commands[12].Opcode.Should().Be(Opcodes.Pop);
            commands[13].Opcode.Should().Be(Opcodes.Call);
            
            // Проверяем сложный вызов intersect
            var intersectCall = commands[11];
            intersectCall.Operand1.Should().BeOfType<CallInfo>();
            var intersectCallInfo = intersectCall.Operand1 as CallInfo;
            intersectCallInfo!.FunctionName.Should().Be("intersect");
            intersectCallInfo.Parameters.Should().ContainKey("shapeA");
            intersectCallInfo.Parameters.Should().ContainKey("shapeB");
        }

        [Fact]
        public async Task CompileAsync_WithFullProgramStructure_ShouldParseDeclarations()
        {
            // Arrange - полная структура программы с декларациями
            var source = @"@AGI 0.0.1

program L0;
module L/L0;

procedure Main {
	asm {
		addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: ""V1"";
		call ""origin"", shape: index: 1;
	}
}

entrypoint {
	asm {
		call Main;
	}
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
            result.Result!.Version.Should().Be("0.0.1");
            result.Result.Name.Should().Be("L0");
            result.Result.Module.Should().Be("L/L0");
            result.Result.Procedures.Should().ContainKey("Main");
            result.Result.Procedures["Main"].Body.Should().HaveCount(2);
            result.Result.EntryPoint.Should().HaveCount(1);
            result.Result.EntryPoint[0].Opcode.Should().Be(Opcodes.Call);
        }
    }
}
