using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Compilation.Ast;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class ParserTests
    {
        private readonly Parser _parser;

        public ParserTests()
        {
            _parser = new Parser();
        }

        [Fact]
        public void Parse_WithAddVertexInstruction_ShouldReturnInstructionNode()
        {
            // Arrange
            var source = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: \"V1\"";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Should().NotBeNull();
            result.Opcode.Should().Be("addvertex");
            result.Parameters.Should().HaveCount(4);
        }

        [Fact]
        public void Parse_WithIndexParameter_ShouldParseCorrectly()
        {
            // Arrange
            var source = "addvertex index: 42";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Parameters.Should().ContainSingle(p => p is IndexParameterNode);
            var indexParam = result.Parameters.OfType<IndexParameterNode>().First();
            indexParam.Name.Should().Be("index");
            indexParam.Value.Should().Be(42);
        }

        [Fact]
        public void Parse_WithDimensionsParameter_ShouldParseArrayCorrectly()
        {
            // Arrange
            var source = "addvertex dimensions: [1, 0, 0, 0]";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Parameters.Should().ContainSingle(p => p is DimensionsParameterNode);
            var dimParam = result.Parameters.OfType<DimensionsParameterNode>().First();
            dimParam.Name.Should().Be("dimensions");
            dimParam.Values.Should().BeEquivalentTo(new[] { 1.0f, 0.0f, 0.0f, 0.0f });
        }

        [Fact]
        public void Parse_WithWeightParameter_ShouldParseFloatCorrectly()
        {
            // Arrange
            var source = "addvertex weight: 0.5";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Parameters.Should().ContainSingle(p => p is WeightParameterNode);
            var weightParam = result.Parameters.OfType<WeightParameterNode>().First();
            weightParam.Name.Should().Be("weight");
            weightParam.Value.Should().Be(0.5f);
        }

        [Fact]
        public void Parse_WithDataParameter_ShouldParseTextDataCorrectly()
        {
            // Arrange
            var source = "addvertex data: text: \"V1\"";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Parameters.Should().ContainSingle(p => p is DataParameterNode);
            var dataParam = result.Parameters.OfType<DataParameterNode>().First();
            dataParam.Name.Should().Be("data");
            dataParam.Type.Should().Be("text");
            dataParam.Value.Should().Be("V1");
        }

        [Fact]
        public void Parse_WithFullAddVertexInstruction_ShouldParseAllParameters()
        {
            // Arrange
            var source = "addvertex index: 1, dimensions: [1, 0, 0, 0], weight: 0.5, data: text: \"V1\"";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("addvertex");
            result.Parameters.Should().HaveCount(4);

            var indexParam = result.Parameters.OfType<IndexParameterNode>().First();
            indexParam.Value.Should().Be(1);

            var dimParam = result.Parameters.OfType<DimensionsParameterNode>().First();
            dimParam.Values.Should().BeEquivalentTo(new[] { 1.0f, 0.0f, 0.0f, 0.0f });

            var weightParam = result.Parameters.OfType<WeightParameterNode>().First();
            weightParam.Value.Should().Be(0.5f);

            var dataParam = result.Parameters.OfType<DataParameterNode>().First();
            dataParam.Type.Should().Be("text");
            dataParam.Value.Should().Be("V1");
        }

        [Fact]
        public void Parse_WithNegativeIndex_ShouldParseCorrectly()
        {
            // Arrange
            var source = "addvertex index: -1";

            // Act
            var result = _parser.Parse(source);

            // Assert
            var indexParam = result.Parameters.OfType<IndexParameterNode>().First();
            indexParam.Value.Should().Be(-1);
        }

        [Fact]
        public void Parse_WithEmptyDimensions_ShouldParseEmptyArray()
        {
            // Arrange
            var source = "addvertex dimensions: []";

            // Act
            var result = _parser.Parse(source);

            // Assert
            var dimParam = result.Parameters.OfType<DimensionsParameterNode>().First();
            dimParam.Values.Should().BeEmpty();
        }

        [Fact]
        public void Parse_WithSpacesInArray_ShouldParseCorrectly()
        {
            // Arrange
            var source = "addvertex dimensions: [ 1 , 2 , 3 ]";

            // Act
            var result = _parser.Parse(source);

            // Assert
            var dimParam = result.Parameters.OfType<DimensionsParameterNode>().First();
            dimParam.Values.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 3.0f });
        }

        [Fact]
        public void Parse_WithQuotedStringContainingSpaces_ShouldParseCorrectly()
        {
            // Arrange
            var source = "addvertex data: text: \"Hello World\"";

            // Act
            var result = _parser.Parse(source);

            // Assert
            var dataParam = result.Parameters.OfType<DataParameterNode>().First();
            dataParam.Value.Should().Be("Hello World");
        }

        [Fact]
        public void Parse_WithOnlyOpcode_ShouldReturnEmptyParameters()
        {
            // Arrange
            var source = "addvertex";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("addvertex");
            result.Parameters.Should().BeEmpty();
        }

        [Fact]
        public void Parse_WithBinaryBase64Data_ShouldParseNestedTypesCorrectly()
        {
            // Arrange
            var source = "addvertex data: binary: base64: \"sdsfdsgfg==\"";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Parameters.Should().ContainSingle(p => p is DataParameterNode);
            var dataParam = result.Parameters.OfType<DataParameterNode>().First();
            dataParam.Name.Should().Be("data");
            dataParam.Types.Should().HaveCount(2);
            dataParam.Types[0].Should().Be("binary");
            dataParam.Types[1].Should().Be("base64");
            dataParam.Value.Should().Be("sdsfdsgfg==");
        }

        [Fact]
        public void Parse_WithFullAddVertexBinaryBase64_ShouldParseAllParameters()
        {
            // Arrange
            var source = "addvertex index: 3, dimensions: [1, 2, 0, 0], weight: 0.8, data: binary: base64: \"sdsfdsgfg==\"";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("addvertex");
            result.Parameters.Should().HaveCount(4);

            var indexParam = result.Parameters.OfType<IndexParameterNode>().First();
            indexParam.Value.Should().Be(3);

            var dimParam = result.Parameters.OfType<DimensionsParameterNode>().First();
            dimParam.Values.Should().BeEquivalentTo(new[] { 1.0f, 2.0f, 0.0f, 0.0f });

            var weightParam = result.Parameters.OfType<WeightParameterNode>().First();
            weightParam.Value.Should().Be(0.8f);

            var dataParam = result.Parameters.OfType<DataParameterNode>().First();
            dataParam.Types.Should().HaveCount(2);
            dataParam.Types[0].Should().Be("binary");
            dataParam.Types[1].Should().Be("base64");
            dataParam.Value.Should().Be("sdsfdsgfg==");
        }

        [Fact]
        public void Parse_WithAddRelationInstruction_ShouldParseFromAndTo()
        {
            // Arrange
            var source = "addrelation index: 1, from: vertex: index: 1, to: vertex: index: 2, weight: 0.6";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("addrelation");
            result.Parameters.Should().HaveCount(4);

            var indexParam = result.Parameters.OfType<IndexParameterNode>().First();
            indexParam.Value.Should().Be(1);

            var fromParam = result.Parameters.OfType<FromParameterNode>().First();
            fromParam.EntityType.Should().Be("vertex");
            fromParam.Index.Should().Be(1);

            var toParam = result.Parameters.OfType<ToParameterNode>().First();
            toParam.EntityType.Should().Be("vertex");
            toParam.Index.Should().Be(2);

            var weightParam = result.Parameters.OfType<WeightParameterNode>().First();
            weightParam.Value.Should().Be(0.6f);
        }

        [Fact]
        public void Parse_WithAddRelationBetweenRelations_ShouldParseCorrectly()
        {
            // Arrange
            var source = "addrelation index: 4, from: relation: index: 1, to: relation: index: 3, weight: 0.2";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("addrelation");
            
            var fromParam = result.Parameters.OfType<FromParameterNode>().First();
            fromParam.EntityType.Should().Be("relation");
            fromParam.Index.Should().Be(1);

            var toParam = result.Parameters.OfType<ToParameterNode>().First();
            toParam.EntityType.Should().Be("relation");
            toParam.Index.Should().Be(3);
        }

        [Fact]
        public void Parse_WithAddShapeInstruction_ShouldParseVertices()
        {
            // Arrange
            var source = "addshape index: 1, vertices: indices: [1, 2, 3]";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("addshape");
            result.Parameters.Should().HaveCount(2);

            var indexParam = result.Parameters.OfType<IndexParameterNode>().First();
            indexParam.Value.Should().Be(1);

            var verticesParam = result.Parameters.OfType<VerticesParameterNode>().First();
            verticesParam.Indices.Should().HaveCount(3);
            verticesParam.Indices.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        }

        [Fact]
        public void Parse_WithCallInstruction_ShouldParseFunctionNameAndParameters()
        {
            // Arrange
            var source = "call \"origin\", shape: index: 1";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("call");
            result.Parameters.Should().HaveCount(2);

            var nameParam = result.Parameters.OfType<FunctionNameParameterNode>().First();
            nameParam.FunctionName.Should().Be("origin");

            var funcParam = result.Parameters.OfType<FunctionParameterNode>().First();
            funcParam.ParameterName.Should().Be("shape");
            funcParam.EntityType.Should().Be("shape");
            funcParam.Index.Should().Be(1);
        }

        [Fact]
        public void Parse_WithCallInstructionWithoutQuotes_ShouldParseFunctionName()
        {
            // Arrange
            var source = "call Main";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("call");
            var nameParam = result.Parameters.OfType<FunctionNameParameterNode>().First();
            nameParam.FunctionName.Should().Be("Main");
        }

        [Fact]
        public void Parse_WithPopInstruction_ShouldParseMemoryAddress()
        {
            // Arrange
            var source = "pop [0]";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("pop");
            result.Parameters.Should().HaveCount(1);

            var memoryParam = result.Parameters.OfType<MemoryParameterNode>().First();
            memoryParam.Index.Should().Be(0);
        }

        [Fact]
        public void Parse_WithCallInstructionAndMemoryAddress_ShouldParseFunctionNameAndMemoryParameter()
        {
            // Arrange
            var source = "call \"print\", [0]";

            // Act
            var result = _parser.Parse(source);

            // Assert
            result.Opcode.Should().Be("call");
            result.Parameters.Should().HaveCount(2);

            var nameParam = result.Parameters.OfType<FunctionNameParameterNode>().First();
            nameParam.FunctionName.Should().Be("print");

            var funcParam = result.Parameters.OfType<FunctionParameterNode>().First();
            funcParam.ParameterName.Should().Be("memory");
            funcParam.EntityType.Should().Be("memory");
            funcParam.Index.Should().Be(0);
        }

        [Fact]
        public void Parse_WithComplexCallInstruction_ShouldParseNamedParametersAndNestedStructures()
        {
            // Arrange
            var source = "call \"intersect\", shapeA: shape: index: 1, shapeB: shape: { vertices: [ { dimensions: [1, 0, 0, 0] }, { dimensions: [1, 2, 0, 0] } ] }";
            
            // Act
            var result = _parser.Parse(source);
            
            // Assert
            result.Opcode.Should().Be("call");
            result.Parameters.Should().HaveCount(3); // function name + 2 parameters
            
            var nameParam = result.Parameters.OfType<FunctionNameParameterNode>().First();
            nameParam.FunctionName.Should().Be("intersect");
            
            var shapeAParam = result.Parameters.OfType<FunctionParameterNode>().First(p => p.ParameterName == "shapeA");
            shapeAParam.ParameterName.Should().Be("shapeA");
            shapeAParam.EntityType.Should().Be("shape");
            shapeAParam.Index.Should().Be(1);
            
            var shapeBParam = result.Parameters.OfType<ComplexValueParameterNode>().First();
            shapeBParam.ParameterName.Should().Be("shapeB");
            shapeBParam.Value.Should().ContainKey("shape");
            
            var shapeValue = shapeBParam.Value["shape"];
            shapeValue.Should().BeOfType<Dictionary<string, object>>();
            
            var shapeDict = shapeValue as Dictionary<string, object>;
            shapeDict.Should().ContainKey("vertices");
            
            var vertices = shapeDict["vertices"];
            vertices.Should().BeOfType<List<object>>();
            
            var verticesList = vertices as List<object>;
            verticesList.Should().HaveCount(2);
            
            var firstVertex = verticesList[0] as Dictionary<string, object>;
            firstVertex.Should().ContainKey("dimensions");
            var firstDimensions = firstVertex["dimensions"] as List<object>;
            firstDimensions.Should().BeEquivalentTo(new object[] { 1L, 0L, 0L, 0L });
            
            var secondVertex = verticesList[1] as Dictionary<string, object>;
            secondVertex.Should().ContainKey("dimensions");
            var secondDimensions = secondVertex["dimensions"] as List<object>;
            secondDimensions.Should().BeEquivalentTo(new object[] { 1L, 2L, 0L, 0L });
        }

        [Fact]
        public void Parse_WithScanner_FirstTokenIsOpcode_WatchPreviousReturnsLastConsumed()
        {
            var source = "addvertex index: 1";
            var parser = new Parser();
            parser.Parse(source);
            var prev = parser.Watch(-1);
            prev.Should().NotBeNull();
            // После полного разбора по токенам последний потреблённый токен — значение параметра
            prev!.Value.Kind.Should().Be(Magic.Kernel.Compilation.TokenKind.Number);
            prev.Value.Value.Should().Be("1");
        }

        [Fact]
        public void Parse_WhenFirstTokenIsNotIdentifier_ThrowsCompilationException()
        {
            var source = "123 x";
            var parser = new Parser();
            var act = () => parser.Parse(source);
            act.Should().Throw<CompilationException>()
                .WithMessage("*Expected*Identifier*");
        }
    }
}
