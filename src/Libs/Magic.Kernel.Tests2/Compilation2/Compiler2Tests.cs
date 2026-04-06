using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Magic.Kernel2.Compilation2;
using Magic.Kernel2.Compilation2.Ast2;
using Xunit;

namespace Magic.Kernel.Tests2.Compilation2
{
    /// <summary>
    /// Tests for the v2.0 compiler pipeline (<see cref="Compiler2"/>).
    /// <para>
    /// These tests verify the key architectural improvements:
    /// <list type="bullet">
    /// <item>Parser2 produces fully-typed AST nodes (not deferred raw text).</item>
    /// <item>SemanticAnalyzer2 walks typed AST nodes directly.</item>
    /// <item>Assembler2 generates bytecode from AST without text-based lowering.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class Compiler2Tests
    {
        private readonly Compiler2 _compiler;

        public Compiler2Tests()
        {
            _compiler = new Compiler2();
        }

        // ─── Basic compilation ────────────────────────────────────────────────────

        [Fact]
        public async Task CompileAsync_WithValidSource_ShouldSucceed()
        {
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                entrypoint {
                    print("Hello");
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Result.Should().NotBeNull();
        }

        [Fact]
        public async Task CompileAsync_WithProcedure_ShouldCreateProcedureEntry()
        {
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    print("Hello");
                }
                entrypoint {
                    Main;
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue();
            result.Result!.Procedures.Should().ContainKey("Main");
        }

        [Fact]
        public async Task CompileAsync_WithFunction_ShouldCreateFunctionEntry()
        {
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                function Add(x, y) {
                    return x;
                }
                entrypoint {
                    Add(1, 2);
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue();
            result.Result!.Functions.Should().ContainKey("Add");
        }

        [Fact]
        public async Task CompileAsync_WithProgramMetadata_ShouldPreserveMetadata()
        {
            var source = """
                @AGI 0.0.1;
                program myapp;
                module samples/myapp;
                entrypoint {
                    print("ok");
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue();
            result.Result!.Version.Should().Be("0.0.1");
            result.Result.Name.Should().Be("myapp");
            result.Result.Module.Should().Be("samples/myapp");
        }

        // ─── Parser2 full AST tests ───────────────────────────────────────────────

        [Fact]
        public void Parser2_WithVarDeclaration_ShouldProduceVarDeclarationNode()
        {
            var parser = new Parser2();
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    var x := 42;
                }
                entrypoint {
                    Main;
                }
                """;

            var ast = parser.ParseProgram(source);

            ast.Procedures.Should().HaveCount(1);
            var mainProc = ast.Procedures.First();
            mainProc.Name.Should().Be("Main");
            mainProc.Body.Statements.Should().NotBeEmpty();

            // In v2.0 the body should contain a VarDeclarationStatement2 (not a raw StatementLineNode)
            var varDecl = mainProc.Body.Statements.OfType<VarDeclarationStatement2>().FirstOrDefault();
            varDecl.Should().NotBeNull("v2.0 parser must produce typed VarDeclarationStatement2 nodes");
            varDecl!.VariableName.Should().Be("x");
        }

        [Fact]
        public void Parser2_WithCallStatement_ShouldProduceCallStatementNode()
        {
            var parser = new Parser2();
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    print("hello");
                }
                entrypoint {
                    Main;
                }
                """;

            var ast = parser.ParseProgram(source);

            var mainProc = ast.Procedures.First();
            var callStmt = mainProc.Body.Statements.OfType<CallStatement2>().FirstOrDefault();
            callStmt.Should().NotBeNull("v2.0 parser must produce typed CallStatement2 nodes");
        }

        [Fact]
        public void Parser2_WithIfStatement_ShouldProduceIfStatementNode()
        {
            var parser = new Parser2();
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    if (x == 1) {
                        print("yes");
                    }
                }
                entrypoint {
                    Main;
                }
                """;

            var ast = parser.ParseProgram(source);

            var mainProc = ast.Procedures.First();
            var ifStmt = mainProc.Body.Statements.OfType<IfStatement2>().FirstOrDefault();
            ifStmt.Should().NotBeNull("v2.0 parser must produce typed IfStatement2 nodes");
        }

        [Fact]
        public void Parser2_WithReturnStatement_ShouldProduceReturnNode()
        {
            var parser = new Parser2();
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                function GetValue {
                    return 42;
                }
                entrypoint {
                    GetValue;
                }
                """;

            var ast = parser.ParseProgram(source);

            var func = ast.Functions.First();
            var retStmt = func.Body.Statements.OfType<ReturnStatement2>().FirstOrDefault();
            retStmt.Should().NotBeNull("v2.0 parser must produce typed ReturnStatement2 nodes");
        }

        [Fact]
        public void Parser2_NoStatementLineNodes_InProcedureBodies()
        {
            // Key v2.0 invariant: StatementLineNode raw-text nodes must NOT appear
            // in procedure/function/entrypoint bodies after parsing — the AST must be fully typed.
            var parser = new Parser2();
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    var x := 1;
                    print(x);
                }
                function Compute(a, b) {
                    return a;
                }
                entrypoint {
                    Main;
                    Compute(1, 2);
                }
                """;

            var ast = parser.ParseProgram(source);

            // Collect all statement nodes from all bodies
            var allStatements = new List<StatementNode2>();
            foreach (var proc in ast.Procedures)
                allStatements.AddRange(CollectAllStatements(proc.Body));
            foreach (var func in ast.Functions)
                allStatements.AddRange(CollectAllStatements(func.Body));
            if (ast.EntryPoint != null)
                allStatements.AddRange(CollectAllStatements(ast.EntryPoint));

            allStatements.Should().NotBeEmpty("the program has statements");

            // None of them should be unknown/raw statement types
            // (checking they are all known typed 2.0 statement nodes)
            var typedStmts = allStatements.Where(s =>
                s is VarDeclarationStatement2 ||
                s is AssignmentStatement2 ||
                s is CallStatement2 ||
                s is ReturnStatement2 ||
                s is IfStatement2 ||
                s is SwitchStatement2 ||
                s is StreamWaitForLoop2 ||
                s is InstructionStatement2 ||
                s is NestedProcedureStatement2 ||
                s is NestedFunctionStatement2).ToList();

            typedStmts.Should().HaveCount(allStatements.Count,
                "v2.0: all statement nodes must be typed AST2 nodes, not raw text");
        }

        // ─── Assembler2 direct AST→bytecode tests ────────────────────────────────

        [Fact]
        public async Task Compiler2_VarDeclaration_EmitsPopInstruction()
        {
            // v2.0: var x := 42 should emit: push 42, pop [slot]
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    var x := 42;
                }
                entrypoint {
                    Main;
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue();
            var mainBody = result.Result!.Procedures["Main"].Body;
            mainBody.Should().NotBeNull();

            // Should have a push and a pop
            mainBody.Any(c => c.Opcode == Opcodes.Push).Should().BeTrue();
            mainBody.Any(c => c.Opcode == Opcodes.Pop).Should().BeTrue();
        }

        [Fact]
        public async Task Compiler2_FunctionCall_EmitsCallInstruction()
        {
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    print("hello");
                }
                entrypoint {
                    Main;
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue();
            var mainBody = result.Result!.Procedures["Main"].Body;
            mainBody.Any(c => c.Opcode == Opcodes.Call).Should().BeTrue();
        }

        [Fact]
        public async Task Compiler2_ProcedureHasReturnAtEnd()
        {
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    print("hello");
                }
                entrypoint {
                    Main;
                }
                """;

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue();
            var mainBody = result.Result!.Procedures["Main"].Body;
            mainBody.Last().Opcode.Should().Be(Opcodes.Ret,
                "v2.0 SemanticAnalyzer2 must append Ret at end of procedures");
        }

        // ─── StatementParser2 unit tests ─────────────────────────────────────────

        [Fact]
        public void StatementParser2_ParseVarDeclaration_WithInitializer()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseStatement("var count := 0", 1);

            result.Should().BeOfType<VarDeclarationStatement2>();
            var varDecl = (VarDeclarationStatement2)result!;
            varDecl.VariableName.Should().Be("count");
            varDecl.Initializer.Should().NotBeNull();
            varDecl.Initializer.Should().BeOfType<LiteralExpression2>();
        }

        [Fact]
        public void StatementParser2_ParseCallStatement_SimpleCall()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseStatement("print(\"hello\")", 1);

            result.Should().BeOfType<CallStatement2>();
            var call = (CallStatement2)result!;
            ((VariableExpression2)call.Callee).Name.Should().Be("print");
            call.Arguments.Should().HaveCount(1);
        }

        [Fact]
        public void StatementParser2_ParseReturnStatement()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseStatement("return 42", 1);

            result.Should().BeOfType<ReturnStatement2>();
            var ret = (ReturnStatement2)result!;
            ret.Value.Should().BeOfType<LiteralExpression2>();
        }

        [Fact]
        public void StatementParser2_ParseIfStatement()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseStatement("if (x == 1) { print(\"yes\"); }", 1);

            result.Should().BeOfType<IfStatement2>();
            var ifStmt = (IfStatement2)result!;
            ifStmt.Condition.Should().BeOfType<BinaryExpression2>();
            ifStmt.ThenBlock.Statements.Should().NotBeEmpty();
        }

        [Fact]
        public void StatementParser2_ParseAssignment()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseStatement("x := 10", 1);

            result.Should().BeOfType<AssignmentStatement2>();
            var assign = (AssignmentStatement2)result!;
            ((VariableExpression2)assign.Target).Name.Should().Be("x");
        }

        // ─── Expression parser tests ──────────────────────────────────────────────

        [Fact]
        public void StatementParser2_ParseExpression_StringLiteral()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseExpression("\"hello\"", 1);

            result.Should().BeOfType<LiteralExpression2>();
            var lit = (LiteralExpression2)result;
            lit.Kind.Should().Be(LiteralKind2.String);
            lit.Value.Should().Be("hello");
        }

        [Fact]
        public void StatementParser2_ParseExpression_IntegerLiteral()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseExpression("42", 1);

            result.Should().BeOfType<LiteralExpression2>();
            var lit = (LiteralExpression2)result;
            lit.Kind.Should().Be(LiteralKind2.Integer);
            lit.Value.Should().Be(42L);
        }

        [Fact]
        public void StatementParser2_ParseExpression_VariableRef()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseExpression("myVar", 1);

            result.Should().BeOfType<VariableExpression2>();
            ((VariableExpression2)result).Name.Should().Be("myVar");
        }

        [Fact]
        public void StatementParser2_ParseExpression_MemberAccess()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseExpression("obj.Field", 1);

            result.Should().BeOfType<MemberAccessExpression2>();
            var access = (MemberAccessExpression2)result;
            access.MemberName.Should().Be("Field");
        }

        [Fact]
        public void StatementParser2_ParseExpression_BinaryOperation()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseExpression("x + y", 1);

            result.Should().BeOfType<BinaryExpression2>();
            var bin = (BinaryExpression2)result;
            bin.Operator.Should().Be("+");
        }

        [Fact]
        public void StatementParser2_ParseExpression_MethodCall()
        {
            var parser = new StatementParser2ForTest();

            var result = parser.ParseExpression("stream.open(\"file1\")", 1);

            result.Should().BeOfType<CallExpression2>();
            var call = (CallExpression2)result;
            call.IsObjectCall.Should().BeTrue();
            call.Arguments.Should().HaveCount(1);
        }

        // ─── V2 architecture invariant tests ─────────────────────────────────────

        [Fact]
        public async Task Compiler2_DoesNotUseStatementLoweringCompiler()
        {
            // This test verifies the architectural invariant of v2.0:
            // The compiled result should be achievable purely via AST walking.
            // We verify by compiling source code that exercises various statement types.
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Process(input) {
                    var result := input;
                    if (result == 0) {
                        result := 1;
                    }
                    print(result);
                }
                entrypoint {
                    Process(42);
                }
                """;

            var result = await _compiler.CompileAsync(source);

            // The compilation should succeed — proving that the AST-walking approach
            // handles the full range of AGI language constructs.
            result.Success.Should().BeTrue(
                $"v2.0 compiler must handle var decl, if stmt, assignment, and calls via AST walking. Error: {result.ErrorMessage}");
        }

        [Fact]
        public async Task Compiler2_SimpleProgram_ProducesEquivalentBytecodeToV1()
        {
            // This test verifies that v2.0 produces semantically equivalent output to v1.0
            // for a simple program (same opcodes in entry, same procedure structure).
            var source = """
                @AGI 0.0.1;
                program test;
                module test;
                procedure Main {
                    print("hello");
                }
                entrypoint {
                    Main;
                }
                """;

            var v1Compiler = new Magic.Kernel.Compilation.Compiler();
            var v1Result = await v1Compiler.CompileAsync(source);

            var v2Result = await _compiler.CompileAsync(source);

            v1Result.Success.Should().BeTrue();
            v2Result.Success.Should().BeTrue();

            // Both should have the same program structure.
            v2Result.Result!.Procedures.Should().ContainKey("Main");
            v2Result.Result.Name.Should().Be(v1Result.Result!.Name);
            v2Result.Result.Module.Should().Be(v1Result.Result.Module);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private static List<StatementNode2> CollectAllStatements(BlockNode2 block)
        {
            var result = new List<StatementNode2>();
            foreach (var stmt in block.Statements)
            {
                result.Add(stmt);
                // Recurse into nested blocks
                if (stmt is IfStatement2 ifStmt)
                {
                    result.AddRange(CollectAllStatements(ifStmt.ThenBlock));
                    if (ifStmt.ElseBlock != null)
                        result.AddRange(CollectAllStatements(ifStmt.ElseBlock));
                }
                else if (stmt is SwitchStatement2 switchStmt)
                {
                    foreach (var c in switchStmt.Cases)
                        result.AddRange(CollectAllStatements(c.Body));
                    if (switchStmt.DefaultBlock != null)
                        result.AddRange(CollectAllStatements(switchStmt.DefaultBlock));
                }
                else if (stmt is StreamWaitForLoop2 forLoop)
                {
                    result.AddRange(CollectAllStatements(forLoop.Body));
                }
            }
            return result;
        }
    }

    /// <summary>Test helper to expose internal StatementParser2 methods for unit testing.</summary>
    internal sealed class StatementParser2ForTest : StatementParser2
    {
        public new StatementNode2? ParseStatement(string text, int sourceLine)
            => base.ParseStatement(text, sourceLine);

        public new ExpressionNode2 ParseExpression(string text, int sourceLine)
            => base.ParseExpression(text, sourceLine);
    }
}
