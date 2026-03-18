using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class StatementSyntaxTests
    {
        private readonly Compiler _compiler;

        public StatementSyntaxTests()
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
            // 1 AddVertex + Ret
            main.Body.Should().HaveCount(2);
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
            // 3 AddVertex + Ret
            main.Body.Should().HaveCount(4);
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
            // 2 vertices + 1 relation + Ret
            main.Body.Should().HaveCount(4);
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
            // 3 vertices + 3 relations + Ret
            main.Body.Should().HaveCount(7);
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
            // 3 vertices + 1 shape + Ret
            main.Body.Should().HaveCount(5);
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
            // 2 temporary vertices + 1 shape + Ret
            main.Body.Should().HaveCount(4);
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
            // 3 vertices + 1 shape + 1 call origin + 1 pop + Ret
            main.Body.Should().HaveCount(7);
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
            // 3 vertices + 1 shape (a) + 2 vertices + 1 shape (b) + 1 call intersect + 1 pop + Ret
            main.Body.Should().HaveCount(10);
            
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
            // 3 AddVertex + 1 AddShape + 1 Call origin + 1 Pop + 1 Push + 1 Push + 1 Call print + Pop result + Ret = 11
            main.Body.Should().HaveCount(11);
            
            var printCall = main.Body[^3];
            printCall.Opcode.Should().Be(Opcodes.Call);
            var callInfo = printCall.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("print");
        }

        [Fact]
        public async Task CompileAsync_WithPrintFunctionCall_MultipleArguments_ShouldCompileWithCorrectArity()
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
		print(o, ""origin"", 42);
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
            // 3 AddVertex + 1 AddShape + 1 Call origin + 1 Pop + 3 Push args + 1 Push arity + 1 Call print + Pop result + Ret = 13
            main.Body.Should().HaveCount(13);

            var callPrint = main.Body[^3];
            callPrint.Opcode.Should().Be(Opcodes.Call);
            var callInfo = callPrint.Operand1.Should().BeOfType<CallInfo>().Subject;
            callInfo.FunctionName.Should().Be("print");

            var arityPush = main.Body[^4];
            arityPush.Opcode.Should().Be(Opcodes.Push);
            var pushOperand = arityPush.Operand1.Should().BeOfType<PushOperand>().Subject;
            pushOperand.Kind.Should().Be("IntLiteral");
            pushOperand.Value.Should().Be(3L);
        }

        [Fact]
        public async Task CompileAsync_WithFullStatementProgram_ShouldCompileAllStatements()
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
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            result.ErrorMessage.Should().Contain("intersection1");
        }

        [Fact]
        public async Task CompileAsync_WithMixedStatementAndAsm_ShouldCompileBoth()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure StatementProc {
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
		call StatementProc;
		call AsmCode;
	}
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("StatementProc");
            result.Result.Procedures.Should().ContainKey("AsmCode");
            
            var statementProc = result.Result.Procedures["StatementProc"];
            // 1 AddVertex + Ret
            statementProc.Body.Should().HaveCount(2);
            statementProc.Body[0].Opcode.Should().Be(Opcodes.AddVertex);
            
            var asmCode = result.Result.Procedures["AsmCode"];
            // 1 AddVertex + Ret
            asmCode.Body.Should().HaveCount(2);
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
            // Push(0) + Call Helper + Ret
            main.Body.Should().HaveCount(3);
            main.Body[1].Opcode.Should().Be(Opcodes.Call);
            
            var callInfo = main.Body[1].Operand1.Should().BeOfType<CallInfo>().Subject;
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

            main.Body.Should().Contain(c => c.Opcode == Opcodes.CallObj && c.Operand1 as string == "open");
            main.Body.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
            var calls = main.Body.Where(c => c.Opcode == Opcodes.Call).Select(c => (CallInfo)c.Operand1!).ToList();
            calls.Select(c => c.FunctionName).Should().Contain("compile");
            calls.Select(c => c.FunctionName).Should().Contain("print");

            main.Body.Should().Contain(c => c.Opcode == Opcodes.Pop);
            main.Body.Should().Contain(c => c.Opcode == Opcodes.Push);
        }
    }
}
