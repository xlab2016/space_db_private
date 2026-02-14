using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class HighLevelSyntaxTests
    {
        private readonly Compiler _compiler;

        public HighLevelSyntaxTests()
        {
            _compiler = new Compiler();
        }

        [Fact]
        public async Task CompileAsync_WithVarBlockAndVertexDeclaration_ShouldCompileToAddVertex()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
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
            main.Body.Should().HaveCount(1);
            main.Body[0].Opcode.Should().Be(Opcodes.AddVertex);
        }

        [Fact]
        public async Task CompileAsync_WithMultipleVertexDeclarations_ShouldCompileAll()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.8, DATA:BIN:""sdsfdsgfg==""};
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
            main.Body.Should().HaveCount(3);
            main.Body[0].Opcode.Should().Be(Opcodes.AddVertex);
            main.Body[1].Opcode.Should().Be(Opcodes.AddVertex);
            main.Body[2].Opcode.Should().Be(Opcodes.AddVertex);
        }

        [Fact]
        public async Task CompileAsync_WithRelationDeclaration_ShouldCompileToAddRelation()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		r1: relation = {v1=>v2,W:0.6};
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
            main.Body.Should().HaveCount(3); // 2 vertices + 1 relation
            main.Body[2].Opcode.Should().Be(Opcodes.AddRelation);
        }

        [Fact]
        public async Task CompileAsync_WithRelationBetweenRelations_ShouldCompileCorrectly()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.5, DATA:""V3""};
		r1: relation = {v1=>v2,W:0.6};
		r2: relation = {v2=>v3,W:0.6};
		r3: relation = {r1=>r2,W:0.2};
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
            main.Body.Should().HaveCount(6); // 3 vertices + 3 relations
            main.Body[3].Opcode.Should().Be(Opcodes.AddRelation);
            main.Body[4].Opcode.Should().Be(Opcodes.AddRelation);
            main.Body[5].Opcode.Should().Be(Opcodes.AddRelation);
        }

        [Fact]
        public async Task CompileAsync_WithShapeDeclaration_ShouldCompileToAddShape()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.5, DATA:""V3""};
		a: shape = { [v1, v2, v3] };
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
            main.Body.Should().HaveCount(4); // 3 vertices + 1 shape
            main.Body[3].Opcode.Should().Be(Opcodes.AddShape);
        }

        [Fact]
        public async Task CompileAsync_WithShapeWithVerticesLiteral_ShouldCompileCorrectly()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		b: shape = { VERT:[{DIM:[1, 0, 0, 0]},{DIM:[1, 2, 0, 0]}] };
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
            // Should create 2 temporary vertices + 1 shape
            main.Body.Should().HaveCount(3);
            main.Body[0].Opcode.Should().Be(Opcodes.AddVertex);
            main.Body[1].Opcode.Should().Be(Opcodes.AddVertex);
            main.Body[2].Opcode.Should().Be(Opcodes.AddShape);
        }

        [Fact]
        public async Task CompileAsync_WithOriginOperator_ShouldCompileToCallOrigin()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.5, DATA:""V3""};
		a: shape = { [v1, v2, v3] };
		o = ] a;
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
            // 3 vertices + 1 shape + 1 call origin + 1 pop
            main.Body.Should().HaveCount(6);
            main.Body[4].Opcode.Should().Be(Opcodes.Call);
            main.Body[5].Opcode.Should().Be(Opcodes.Pop);
            
            var callInfo = main.Body[4].Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("origin");
        }

        [Fact]
        public async Task CompileAsync_WithIntersectionOperator_ShouldCompileToCallIntersect()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.5, DATA:""V3""};
		a: shape = { [v1, v2, v3] };
		b: shape = { VERT:[{DIM:[1, 0, 0, 0]},{DIM:[1, 2, 0, 0]}] };
		intersection1 = a | b;
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
            // 3 vertices + 1 shape (a) + 2 vertices + 1 shape (b) + 1 call intersect + 1 pop
            main.Body.Should().HaveCount(9);
            
            // Find the intersect call
            var intersectCall = main.Body.FirstOrDefault(c => 
                c.Opcode == Opcodes.Call && 
                c.Operand1 is CallInfo ci && 
                ci.FunctionName == "intersect");
            intersectCall.Should().NotBeNull();
        }

        [Fact]
        public async Task CompileAsync_WithPrintFunctionCall_ShouldCompileToCallPrint()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V2""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.5, DATA:""V3""};
		a: shape = { [v1, v2, v3] };
		o = ] a;
		print(o);
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
            // 3 vertices + 1 shape + 1 call origin + 1 pop + 1 call print
            main.Body.Should().HaveCount(7);
            
            var printCall = main.Body.Last();
            printCall.Opcode.Should().Be(Opcodes.Call);
            var callInfo = printCall.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("print");
        }

        [Fact]
        public async Task CompileAsync_WithFullHighLevelProgram_ShouldCompileAllStatements()
        {
            // Arrange - полный пример из документации
            var source = @"@AGI 0.0.1;

program L0;
module L/L0;

procedure Main {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
		v2: vertex = {DIM:[1, 1, 0, 0], W:0.5, DATA:""V1""};
		v3: vertex = {DIM:[1, 2, 0, 0], W:0.5, DATA:BIN:""sdsfdsgfg==""};

		r1: relation = {v1=>v2,W:0.6};
		r2: relation = {v1=>v3,W:0.6};
		r3: relation = {v2=>v3,W:0.6};

		r4: relation = {r1=>r3,W:0.6};

		a: shape = { [v1, v2, v3] };

		// this is origin of shape a
		o = ] a;
		print(o);

		b: shape = { VERT:[{DIM:[1, 0, 0, 0]},{DIM:[1, 2, 0, 0]}] };
		// this is intersection of two shapes
		// this will customizable by language
		intersection1 = a | b;
		print(intersection1);
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
            result.Result!.Version.Should().Be("0.0.1;");
            result.Result.Name.Should().Be("L0");
            result.Result.Module.Should().Be("L/L0");
            
            var main = result.Result.Procedures["Main"];
            main.Should().NotBeNull();
            main.Body.Should().NotBeEmpty();
            
            // Проверяем наличие основных команд
            main.Body.Should().Contain(c => c.Opcode == Opcodes.AddVertex);
            main.Body.Should().Contain(c => c.Opcode == Opcodes.AddRelation);
            main.Body.Should().Contain(c => c.Opcode == Opcodes.AddShape);
            main.Body.Should().Contain(c => c.Opcode == Opcodes.Call);
            main.Body.Should().Contain(c => c.Opcode == Opcodes.Pop);
        }

        [Fact]
        public async Task CompileAsync_WithMixedHighLevelAndAsm_ShouldCompileBoth()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure HighLevel {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
}

procedure AsmCode {
	asm {
		addvertex index: 2, dimensions: [1, 1, 0, 0], weight: 0.5, data: text: ""V2"";
	}
}

entrypoint {
	asm {
		call HighLevel;
		call AsmCode;
	}
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("HighLevel");
            result.Result.Procedures.Should().ContainKey("AsmCode");
            
            var highLevel = result.Result.Procedures["HighLevel"];
            highLevel.Body.Should().HaveCount(1);
            highLevel.Body[0].Opcode.Should().Be(Opcodes.AddVertex);
            
            var asmCode = result.Result.Procedures["AsmCode"];
            asmCode.Body.Should().HaveCount(1);
            asmCode.Body[0].Opcode.Should().Be(Opcodes.AddVertex);
        }

        [Fact]
        public async Task CompileAsync_WithProcedureCall_ShouldCompileToCall()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Helper {
	var
		v1: vertex = {DIM:[1, 0, 0, 0], W:0.5, DATA:""V1""};
}

procedure Main {
	Helper;
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
            main.Body.Should().HaveCount(1);
            main.Body[0].Opcode.Should().Be(Opcodes.Call);
            
            var callInfo = main.Body[0].Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("Helper");
        }

        [Fact]
        public async Task CompileAsync_WithCompileStreamProgram_ShouldCompileToStreamOpenAwaitCompilePrint()
        {
            var source = @"@AGI 0.0.1;

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

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("Main");
            var main = result.Result.Procedures["Main"];
            main.Body.Should().NotBeEmpty();

            var calls = main.Body.Where(c => c.Opcode == Opcodes.Call).Select(c => (CallInfo)c.Operand1!).ToList();
            calls.Select(c => c.FunctionName).Should().Contain("stream_open");
            calls.Select(c => c.FunctionName).Should().Contain("stream_await");
            calls.Select(c => c.FunctionName).Should().Contain("compile");
            calls.Select(c => c.FunctionName).Should().Contain("print");

            main.Body.Should().Contain(c => c.Opcode == Opcodes.Pop);
            main.Body.Should().Contain(c => c.Opcode == Opcodes.Push);
        }
    }
}
