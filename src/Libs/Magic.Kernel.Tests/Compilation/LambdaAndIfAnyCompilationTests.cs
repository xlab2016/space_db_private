using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
        /// <summary>Tests for expr, defexpr, lambda, equals, not opcodes and if !expr.any(lambda) compilation.</summary>
    public class LambdaAndIfAnyCompilationTests
    {
        private readonly Compiler _compiler = new Compiler();
        private readonly InstructionParser _instructionParser = new InstructionParser();
        private readonly SemanticAnalyzer _semanticAnalyzer = new SemanticAnalyzer();

        #region Instruction parsing: expr, defexpr, lambda, equals, not, push lambda:arg0

        [Fact]
        public void InstructionParser_Parse_expr_ReturnsExprOpcode()
        {
            var ast = _instructionParser.Parse("expr");
            ast.Opcode.Should().Be("expr");
            ast.Parameters.Should().NotBeNull().And.BeEmpty();

            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.Expr);
        }

        [Fact]
        public void InstructionParser_Parse_defexpr_ReturnsDefExprOpcode()
        {
            var ast = _instructionParser.Parse("defexpr");
            ast.Opcode.Should().Be("defexpr");
            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.DefExpr);
        }

        [Fact]
        public void InstructionParser_Parse_lambda_ReturnsLambdaOpcode()
        {
            var ast = _instructionParser.Parse("lambda");
            ast.Opcode.Should().Be("lambda");
            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.Lambda);
        }

        [Fact]
        public void InstructionParser_Parse_lambda_WithParameter_PreservesParameterName()
        {
            var ast = _instructionParser.Parse("lambda _");
            ast.Opcode.Should().Be("lambda");
            ast.Parameters.Should().ContainSingle();
            ast.Parameters![0].Should().BeOfType<LambdaParametersParameterNode>()
                .Which.Parameters.Should().Equal("_");

            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.Lambda);
            cmd.Operand1.Should().BeAssignableTo<List<object>>()
                .Which.Should().ContainSingle()
                .Which.Should().Be("_");
        }

        [Fact]
        public void InstructionParser_Parse_equals_ReturnsEqualsOpcode()
        {
            var ast = _instructionParser.Parse("equals");
            ast.Opcode.Should().Be("equals");
            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.Equals);
        }

        [Fact]
        public void InstructionParser_Parse_not_ReturnsNotOpcode()
        {
            var ast = _instructionParser.Parse("not");
            ast.Opcode.Should().Be("not");
            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.Not);
        }

        [Fact]
        public void InstructionParser_Parse_push_lambda_arg0_ReturnsPushWithLambdaArgOperand()
        {
            var ast = _instructionParser.Parse("push lambda: arg0");
            ast.Opcode.Should().Be("push");
            ast.Parameters.Should().HaveCount(1);
            ast.Parameters![0].Should().BeOfType<LambdaArgParameterNode>()
                .Which.Index.Should().Be(0);

            var cmd = _semanticAnalyzer.Analyze(ast);
            cmd.Opcode.Should().Be(Opcodes.Push);
            cmd.Operand1.Should().BeOfType<PushOperand>()
                .Which.Kind.Should().Be("LambdaArg");
            cmd.Operand1.Should().BeOfType<PushOperand>()
                .Which.Value.Should().Be(0);
        }

        [Fact]
        public void InstructionParser_Parse_push_lambda_arg1_ReturnsIndex1()
        {
            var ast = _instructionParser.Parse("push lambda: arg1");
            ast.Parameters![0].Should().BeOfType<LambdaArgParameterNode>()
                .Which.Index.Should().Be(1);
        }

        #endregion

        #region SemanticAnalyzer maps expr/defexpr/lambda/equals/not

        [Fact]
        public void SemanticAnalyzer_MapOpcode_expr_ReturnsOpcodesExpr()
        {
            var node = new InstructionNode { Opcode = "expr", Parameters = new List<ParameterNode>() };
            var cmd = _semanticAnalyzer.Analyze(node);
            cmd.Opcode.Should().Be(Opcodes.Expr);
        }

        [Fact]
        public void SemanticAnalyzer_MapOpcode_defexpr_ReturnsOpcodesDefExpr()
        {
            var node = new InstructionNode { Opcode = "defexpr", Parameters = new List<ParameterNode>() };
            var cmd = _semanticAnalyzer.Analyze(node);
            cmd.Opcode.Should().Be(Opcodes.DefExpr);
        }

        [Fact]
        public void SemanticAnalyzer_MapOpcode_lambda_ReturnsOpcodesLambda()
        {
            var node = new InstructionNode { Opcode = "lambda", Parameters = new List<ParameterNode>() };
            var cmd = _semanticAnalyzer.Analyze(node);
            cmd.Opcode.Should().Be(Opcodes.Lambda);
        }

        [Fact]
        public void SemanticAnalyzer_MapOpcode_equals_ReturnsOpcodesEquals()
        {
            var node = new InstructionNode { Opcode = "equals", Parameters = new List<ParameterNode>() };
            var cmd = _semanticAnalyzer.Analyze(node);
            cmd.Opcode.Should().Be(Opcodes.Equals);
        }

        [Fact]
        public void SemanticAnalyzer_MapOpcode_not_ReturnsOpcodesNot()
        {
            var node = new InstructionNode { Opcode = "not", Parameters = new List<ParameterNode>() };
            var cmd = _semanticAnalyzer.Analyze(node);
            cmd.Opcode.Should().Be(Opcodes.Not);
        }

        #endregion

        #region Agiasm roundtrip for expr, lambda, equals, not, push lambda:arg0

        [Fact]
        public async Task Agiasm_Roundtrip_Expr_Lambda_Equals_DeserializesCorrectly()
        {
            var text = @"@AGIASM 1
@AGI 0.0.1
program ExprTest
module M/E
entrypoint
expr
push lambda: arg0
push string: ""Time""
getobj
pop [40]
push [40]
push [26]
equals
lambda
defexpr
pop [41]
";
            var path = Path.Combine(Path.GetTempPath(), $"agiasm_expr_{Guid.NewGuid():N}.agiasm");
            try
            {
                await File.WriteAllTextAsync(path, text);
                var unit = await ExecutableUnit.LoadAsync(path);
                unit.EntryPoint.Should().NotBeEmpty();
                var opcodes = unit.EntryPoint!.Select(c => c.Opcode).ToList();
                opcodes.Should().Contain(Opcodes.Expr);
                opcodes.Should().Contain(Opcodes.DefExpr);
                opcodes.Should().Contain(Opcodes.Lambda);
                opcodes.Should().Contain(Opcodes.Equals);
                opcodes.Should().Contain(Opcodes.GetObj);
                var lambdaArgPush = unit.EntryPoint.FirstOrDefault(c =>
                    c.Opcode == Opcodes.Push && c.Operand1 is PushOperand po && po.Kind == "LambdaArg");
                lambdaArgPush.Should().NotBeNull();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task Agiasm_CompileIfAny_SaveLoad_Roundtrip_PreservesExprLambdaEquals()
        {
            var source = @"@AGI 0.0.1
program Roundtrip
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	if !db1.Message<>.any(_ => _.Time = time) { nop; }
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            result.Result!.OutputFormat = "agiasm";
            var path = Path.Combine(Path.GetTempPath(), $"agiasm_ifany_{Guid.NewGuid():N}.agiasm");
            try
            {
                await result.Result.SaveAsync(path);
                var savedText = NormalizeNewlines(await File.ReadAllTextAsync(path));
                var expectedLambdaBody = string.Join("\n", new[]
                {
                    "expr",
                    "push lambda: arg0",
                    "push lambda: arg0",
                    "push string: \"Time\"",
                    "getobj",
                    "pop [5]",
                    "push [5]",
                    "push [3]",
                    "equals",
                    "lambda",
                    "defexpr"
                });
                var previousBrokenLambdaBody = string.Join("\n", new[]
                {
                    "expr",
                    "push lambda: arg0",
                    "push string: \"Time\"",
                    "getobj",
                    "pop [5]",
                    "push [5]",
                    "push [3]",
                    "equals",
                    "lambda",
                    "defexpr"
                });
                savedText.Should().Contain("expr");
                savedText.Should().Contain("defexpr");
                savedText.Should().Contain("lambda");
                savedText.Should().NotContain("lambda _");
                savedText.Should().Contain("equals");
                savedText.Should().Contain("not");
                savedText.Should().Contain("callobj \"any\"");
                savedText.Should().Contain(expectedLambdaBody);
                savedText.Should().NotContain(previousBrokenLambdaBody);

                var loaded = await ExecutableUnit.LoadAsync(path);
                loaded.Procedures.Should().ContainKey("Main");
                var body = loaded.Procedures["Main"].Body!;
                body.Should().Contain(c => c.Opcode == Opcodes.Expr);
                body.Should().Contain(c => c.Opcode == Opcodes.Equals);
                body.Should().Contain(c => c.Opcode == Opcodes.Not);
                body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        #endregion

        #region Full program: if !db1.Message<>.any(_ => _.Time = time) { body }

        [Fact]
        public async Task CompileAsync_IfNotAny_WithDbMessageAndLambda_ProducesExprLambdaNotAndCmpJe()
        {
            var source = @"@AGI 0.0.1
program IfAnyTest
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	var message := 0;
	if !db1.Message<>.any(_ => _.Time = time) {
		db1.Message<> += message;
		await db1;
	}
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var main = result.Result!.Procedures.Should().ContainKey("Main").WhoseValue;
            var body = main.Body!;

            body.Should().Contain(c => c.Opcode == Opcodes.Expr);
            body.Should().Contain(c => c.Opcode == Opcodes.DefExpr);
            body.Should().Contain(c => c.Opcode == Opcodes.Lambda);
            body.Should().Contain(c => c.Opcode == Opcodes.Equals);
            body.Should().Contain(c => c.Opcode == Opcodes.Not);
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            body.Should().Contain(c => c.Opcode == Opcodes.Cmp);
            body.Should().Contain(c => c.Opcode == Opcodes.Je);

            var lambdaCommand = body.FirstOrDefault(c => c.Opcode == Opcodes.Lambda);
            lambdaCommand.Should().NotBeNull();
            lambdaCommand!.Operand1.Should().BeNull();

            var exprIdx = body.FindIndex(c => c.Opcode == Opcodes.Expr);
            var defexprIdx = body.FindIndex(c => c.Opcode == Opcodes.DefExpr);
            exprIdx.Should().BeGreaterThan(-1);
            defexprIdx.Should().BeGreaterThan(exprIdx);
            body[exprIdx].Opcode.Should().Be(Opcodes.Expr);
            AssertPushLambdaArg(body[exprIdx + 1], 0);
            AssertPushLambdaArg(body[exprIdx + 2], 0);
            AssertPushString(body[exprIdx + 3], "Time");
            body[exprIdx + 4].Opcode.Should().Be(Opcodes.GetObj);
            AssertPopMemory(body[exprIdx + 5], 6);
            AssertPushMemory(body[exprIdx + 6], 6);
            AssertPushMemory(body[exprIdx + 7], 3);
            body[exprIdx + 8].Opcode.Should().Be(Opcodes.Equals);
            body[exprIdx + 9].Opcode.Should().Be(Opcodes.Lambda);
            body[exprIdx + 10].Opcode.Should().Be(Opcodes.DefExpr);

            var anyIdx = body.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            anyIdx.Should().BeGreaterThan(-1);
            body[anyIdx + 2].Opcode.Should().Be(Opcodes.Push);
            body[anyIdx + 3].Opcode.Should().Be(Opcodes.Not);
            body[anyIdx + 4].Opcode.Should().Be(Opcodes.Pop);
        }

        [Fact]
        public async Task CompileAsync_DbMessageMultiplyAssign_EmitsMulCallObj()
        {
            var source = @"@AGI 0.0.1
program UpsertAssignTest
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var message := 0;
	db1.Message<> *= message;
	await db1;
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.GetObj);
            body.Should().Contain(c => c.Opcode == Opcodes.SetObj);
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "mul");
            body.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
        }

        [Fact]
        public async Task CompileAsync_IfNotAny_MemberPathMessage_ProducesGetObjBeforeAny()
        {
            var source = @"@AGI 0.0.1
program IfAnyTest
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	if !db1.Message<>.any(_ => _.Time = time) {
		nop;
	}
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var body = result.Result!.Procedures["Main"].Body!;
            var getObjCount = body.Count(c => c.Opcode == Opcodes.GetObj);
            getObjCount.Should().BeGreaterThanOrEqualTo(1);
            var anyCallIdx = body.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            anyCallIdx.Should().BeGreaterThan(-1);
        }

        /// <summary>Regression: condition '(!db1.Message<>.any(_ => _.Time = time))' must compile; do not split on &lt;&gt; in Message&lt;&gt;.</summary>
        [Fact]
        public async Task CompileAsync_IfConditionWithParensAndMessageGeneric_CompilesSuccessfully()
        {
            var source = @"@AGI 0.0.1
program IfAnyParens
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	if (!(db1.Message<>.any(_ => _.Time = time))) { nop; }
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            body.Should().Contain(c => c.Opcode == Opcodes.GetObj);
            body.Should().Contain(c => c.Opcode == Opcodes.Not);
            body.Should().Contain(c => c.Opcode == Opcodes.Cmp);
            body.Should().Contain(c => c.Opcode == Opcodes.Je);
        }

        /// <summary>Regression: condition with !(expr) and Message&lt;&gt;.any(lambda) must not be split at &lt; in Message&lt;&gt;.</summary>
        [Fact]
        public async Task CompileAsync_IfNotAny_MessageGeneric_DoesNotSplitOnAngleBrackets()
        {
            var source = @"@AGI 0.0.1
program IfNotAnyMessage
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	if !db1.Message<>.any(_ => _.Time = time) { nop; }
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var body = result.Result!.Procedures["Main"].Body!;
            var anyIdx = body.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            anyIdx.Should().BeGreaterThan(-1, "callobj any must be present (condition must compile as .any(...), not split at <)");
        }

        [Fact]
        public async Task CompileAsync_IfNotAwaitAny_MessageGeneric_CompilesAwaitAndAny()
        {
            var source = @"@AGI 0.0.1
program IfNotAwaitAnyMessage
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	if (!await db1.Message<>.any(_ => _.Time = time)) { nop; }
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "any");
            body.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
        }

        [Fact]
        public async Task CompileAsync_VarAssignment_WithAwaitedMultilineWhereMax_CompilesToQueryExprAndAwait()
        {
            var source = @"@AGI 0.0.1
program WhereMaxMultiline
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var channelTitle := ""test-channel"";
	var offsetId := await db1.Message<>.
		where(_ => _.ChatId = channelTitle).
		max(_ => _.MessageId);
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "where");
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "max");
            body.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
            body.Should().NotContain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "maxWhere");
        }

        [Fact]
        public async Task CompileAsync_VarAssignment_WithAwaitedFind_CompilesFindLambdaAndAwait()
        {
            var source = @"@AGI 0.0.1
program FindOne
module M/E
Db> : database { }
procedure Main {
	var db1 := database<postgres, Db>>;
	var time := 0;
	var original := await db1.Message<>.find(_ => _.Time = time);
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.Expr);
            body.Should().Contain(c => c.Opcode == Opcodes.Lambda);
            body.Should().Contain(c => c.Opcode == Opcodes.DefExpr);
            body.Should().Contain(c => c.Opcode == Opcodes.Equals);
            body.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "find");
            body.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);

            var findIdx = body.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "find");
            findIdx.Should().BeGreaterThan(-1);
            body[findIdx - 1].Opcode.Should().Be(Opcodes.Push);
            body[findIdx - 2].Opcode.Should().Be(Opcodes.DefExpr);
            body[findIdx - 3].Opcode.Should().Be(Opcodes.Lambda);
        }

        [Fact]
        public async Task CompileAsync_ExpressionToSlot_SingleVariable_PushesAndPops()
        {
            var source = @"@AGI 0.0.1
program CondVar
module M/E
procedure Main {
	var x := 0;
	if x { nop; }
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.Cmp);
            body.Should().Contain(c => c.Opcode == Opcodes.Je);
        }

        [Fact]
        public async Task CompileAsync_IfNegated_EmitsNot_AndCmpWith0()
        {
            var source = @"@AGI 0.0.1
program IfNot
module M/E
procedure Main {
	var flag := 0;
	if !flag { nop; }
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var body = result.Result!.Procedures["Main"].Body!;
            body.Should().Contain(c => c.Opcode == Opcodes.Not);
            var cmpCmd = body.FirstOrDefault(c => c.Opcode == Opcodes.Cmp);
            cmpCmd.Should().NotBeNull();
            (cmpCmd!.Operand2 is long l ? l : (long)(cmpCmd.Operand2 ?? 0)).Should().Be(0L);
        }

        #endregion

        private static string NormalizeNewlines(string text) =>
            (text ?? string.Empty).Replace("\r\n", "\n");

        private static void AssertPushLambdaArg(Command command, int index)
        {
            command.Opcode.Should().Be(Opcodes.Push);
            var operand = command.Operand1.Should().BeOfType<PushOperand>().Subject;
            operand.Kind.Should().Be("LambdaArg");
            operand.Value.Should().Be(index);
        }

        private static void AssertPushString(Command command, string value)
        {
            command.Opcode.Should().Be(Opcodes.Push);
            var operand = command.Operand1.Should().BeOfType<PushOperand>().Subject;
            operand.Kind.Should().Be("StringLiteral");
            operand.Value.Should().Be(value);
        }

        private static void AssertPushMemory(Command command, long index)
        {
            command.Opcode.Should().Be(Opcodes.Push);
            command.Operand1.Should().BeOfType<MemoryAddress>()
                .Which.Index.Should().Be(index);
        }

        private static void AssertPopMemory(Command command, long index)
        {
            command.Opcode.Should().Be(Opcodes.Pop);
            command.Operand1.Should().BeOfType<MemoryAddress>()
                .Which.Index.Should().Be(index);
        }

        /// <summary>#&quot;...&quot; with {expr} compiles to call format(template, evaluated expr refs).</summary>
        [Fact]
        public async Task CompileAsync_FormatStringLiteral_CompilesToFormatCall()
        {
            var source = @"@AGI 0.0.1
program FormatTest
module M/E
procedure Main {
	var message := {};
	var size := 0;
	message.Error = #""Size {size} exceeded 20mb"";
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");
            var body = result.Result!.Procedures["Main"].Body!;
            var callFormat = body.FirstOrDefault(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && string.Equals(ci.FunctionName, "format", StringComparison.OrdinalIgnoreCase));
            callFormat.Should().NotBeNull();
            var ci = (CallInfo)callFormat!.Operand1!;
            ci.Parameters.Should().ContainKey("0");
            (ci.Parameters["0"] as string).Should().Be("Size {0} exceeded 20mb");
        }

        [Fact]
        public async Task CompileAsync_FormatStringLiteral_WithFunctionCallArgument_CompilesToFormatCall()
        {
            var source = @"@AGI 0.0.1
program FormatTest
module M/E
procedure Main {
	var message := {};
	var size := 0;
	message.Error = #""Size {unit(size, ""1/mb"")} exceeded 20mb"";
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var body = result.Result!.Procedures["Main"].Body!;
            body.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && string.Equals(ci.FunctionName, "unit", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue();

            var callFormat = body.FirstOrDefault(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && string.Equals(ci.FunctionName, "format", StringComparison.OrdinalIgnoreCase));
            callFormat.Should().NotBeNull();

            var ci = (CallInfo)callFormat!.Operand1!;
            (ci.Parameters["0"] as string).Should().Be("Size {0} exceeded 20mb");
            ci.Parameters.Should().ContainKey("1");
            ci.Parameters["1"].Should().BeOfType<MemoryAddress>();
        }

        [Fact]
        public async Task CompileAsync_FormatStringLiteral_WithFunctionCallTypeLiteralArgument_CompilesTypeLiteralAsStringParameter()
        {
            var source = @"@AGI 0.0.1
program FormatTest
module M/E
procedure Main {
	var message := {};
	var size := 0;
	message.Error = #""Size {unit(size, ""1/mb"", float<decimal>)} exceeded 20mb"";
}
entrypoint { asm { call Main; } }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage ?? "");

            var body = result.Result!.Procedures["Main"].Body!;
            var unitCall = body.FirstOrDefault(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && string.Equals(ci.FunctionName, "unit", StringComparison.OrdinalIgnoreCase));
            unitCall.Should().NotBeNull();

            var unitCallInfo = (CallInfo)unitCall!.Operand1!;
            unitCallInfo.Parameters.Should().ContainKey("2");
            unitCallInfo.Parameters["2"].Should().Be("float<decimal>");
        }
    }
}
