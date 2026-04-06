using System.Linq;
using System.IO;
using FluentAssertions;
using Magic.Kernel;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    /// <summary>Tests for claw-device related AGI language features: procedure parameters, switch, return statement.</summary>
    public class ClawLanguageFeaturesTests
    {
        private readonly Compiler _compiler;

        public ClawLanguageFeaturesTests()
        {
            _compiler = new Compiler();
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithNamedParameter_ShouldEmitPopArityAndPopArg()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var x := data.command;
}

entrypoint {
    asm {
        push [0];
        push int 1;
        call call;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
            var proc = result.Result.Procedures["call"];
            // First two instructions should be Pop (discard arity) and Pop into slot (bind 'data')
            proc.Body.Count.Should().BeGreaterThan(1);
            proc.Body[0].Opcode.Should().Be(Opcodes.Pop); // discard arity
            proc.Body[1].Opcode.Should().Be(Opcodes.Pop); // bind 'data' param
            proc.Body[1].Operand1.Should().BeOfType<MemoryAddress>();
        }

        [Fact]
        public async Task CompileAsync_ReturnStatement_ShouldEmitRetOpcode()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure greet {
    return;
    var x := ""unreachable"";
}

entrypoint {
    asm {
        call greet;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("greet");
            var body = result.Result.Procedures["greet"].Body;
            // Should have Ret from return statement and then the final Ret from SemanticAnalyzer
            body.Any(cmd => cmd.Opcode == Opcodes.Ret).Should().BeTrue();
        }

        [Fact]
        public async Task CompileAsync_SwitchStatement_ShouldCompileSuccessfully()
        {
            // Arrange
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure dispatch(cmd) {
    var command := cmd;
    switch command {
        if ""hello"" {
            var response := ""world"";
        }
        if ""bye"" {
            var response := ""goodbye"";
        }
    }
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call dispatch;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("dispatch");
        }

        [Fact]
        public async Task CompileAsync_Switch_UnbracedArmsOnNewLines_SecondArmKeepsBody()
        {
            // Регрессия: строка тела после «if "b"» на новой строке не должна теряться,
            // когда предыдущая ветка уже имеет тело (не пустое Item2).
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure op() {
    println(""op"");
}

procedure dispatch(cmd) {
    var command := cmd;
    switch command {
        if ""a"" {
            println(""A"");
        }
        if ""b""
            op();
    }
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call dispatch;
    }
}";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            var body = result.Result!.Procedures["dispatch"].Body;
            var callSummary = string.Join(" | ", body.Where(c => c.Opcode == Opcodes.Call).Select(c => c.Operand1?.ToString()));
            callSummary.Should().Contain("op", "second switch arm must emit call op(); got: " + callSummary);
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithMultipleParameters_ShouldEmitCorrectPrologue()
        {
            // Arrange — procedure with two parameters
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure add(a, b) {
    var result := a;
}

entrypoint {
    asm {
        push int 1;
        push int 2;
        push int 2;
        call add;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var proc = result.Result!.Procedures["add"];
            // prologue: Pop(arity), Pop[slotB], Pop[slotA]  (reverse order)
            proc.Body[0].Opcode.Should().Be(Opcodes.Pop); // discard arity
            proc.Body[1].Opcode.Should().Be(Opcodes.Pop); // bind 'b' (reverse: last param first)
            proc.Body[2].Opcode.Should().Be(Opcodes.Pop); // bind 'a'
        }

        [Fact]
        public async Task Parser_ProcedureWithParameters_ShouldStoreProcedureParameters()
        {
            // Arrange
            var parser = new Parser();
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var x := data;
}

entrypoint {
}";

            // Act
            var structure = parser.ParseProgram(source);

            // Assert
            structure.Procedures.Should().ContainKey("call");
            var procNode = structure.Procedures["call"];
            procNode.Parameters.Should().ContainSingle().Which.Should().Be("data");
        }

        [Fact]
        public async Task CompileAsync_StreamClawDefinition_ShouldCompileSuccessfully()
        {
            // Arrange: stream<claw> declaration via DefGen
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

entrypoint {
    var
        claw1: stream<claw>;
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            // Should have Def + DefGen + Ret
            result.Result!.EntryPoint.Count.Should().BeGreaterThan(0);
            result.Result.EntryPoint.Any(c => c.Opcode == Opcodes.DefGen).Should().BeTrue();
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithMemberAccessFromParam_ShouldRegisterLocalVar()
        {
            // Arrange: procedure that reads a member from a parameter and stores it in a local var.
            // Regression test for: member access from a global-kind (procedure param) variable was not
            // registering the result variable, causing subsequent uses to throw "undeclared variable".
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var command := data.command;
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call call;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
        }

        [Fact]
        public async Task CompileAsync_ProcedureWithSwitchOnMemberAccessVar_ShouldCompileSuccessfully()
        {
            // Arrange: reproduces the client_claw.agi compilation failure where 'command' (obtained via
            // member access from a procedure parameter) was not in scope when compiling the switch statement.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure call(data) {
    var authentication := data.authentication;

    if !authentication.isAuthenticated return;

    var command := data.command;

    switch command {
        if ""hello_world""
            print(""Hello world"");
    }
}

entrypoint {
    asm {
        push string ""hello"";
        push int 1;
        call call;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
        }

        [Fact]
        public async Task CompileAsync_InlineIfWithoutBraces_ShouldEmitRetInstruction()
        {
            // Arrange: "if !condition return;" without braces should compile and emit a ret opcode.
            // Previously this was silently dropped because TryParseIfStatement required braces.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure check(data) {
    var auth := data.authentication;
    if !auth.isAuthenticated return;
    var cmd := data.command;
}

entrypoint {
    asm {
        push int 0;
        push int 1;
        call check;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var proc = result.Result!.Procedures["check"];
            // Body should contain a Ret instruction from the inline if
            proc.Body.Count(cmd => cmd.Opcode == Opcodes.Ret).Should().BeGreaterThan(1,
                "inline 'if !cond return;' should emit a Ret opcode in addition to the final procedure ret");
        }

        [Fact]
        public async Task CompileAsync_AwaitStreamStatement_ShouldCompileSuccessfully()
        {
            // Arrange: "await claw1;" should compile for a stream variable.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure Main() {
    var claw1 := stream<claw>;
    await claw1;
}

entrypoint {
    Main;
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            var proc = result.Result!.Procedures["Main"];
            proc.Body.Any(cmd => cmd.Opcode == Opcodes.AwaitObj).Should().BeTrue(
                "await claw1 should emit an AwaitObj opcode");
        }

        [Fact]
        public async Task CompileAsync_PrintWithFormatStringArgument_ShouldCompileSuccessfully()
        {
            // Arrange: print() with a #"format {expr}" argument.
            // Regression test for: '#' was treated as undeclared identifier instead of format string prefix.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure greet(data) {
    var socket1 := socket;
    print(#""Hello {socket1.name}"");
}

entrypoint {
    asm {
        push int 0;
        call greet;
    }
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("greet");
        }

        [Fact]
        public async Task CompileAsync_FullClientClawPattern_ShouldCompileSuccessfully()
        {
            // Arrange: the full client_claw.agi pattern including all fixed issues:
            // - inline if without braces: "if !condition return;"
            // - socket variable from global device (not from data parameter)
            // - await claw1 at end of Main to keep the stream loop alive
            var source = @"@AGI 0.0.1;

program clients_claw;
system samples;
module claw;

procedure call(data) {
    var authentication := data.authentication;

    if !authentication.isAuthenticated return;

    var command := data.command;
    var socket1 := socket;

    switch command {
        if ""hello_world""
            print(#""Hello world from Claw {socket1.name}"");
    }
}

procedure Main() {
    var vault1 := vault;
    var port := vault1.read(""port"");
    var credentials := vault1.read(""credentials"");

    var claw1 := stream<claw>;
    claw1.open({
        port: port,
        authentication: {
            credentials: credentials
        }
    });
    claw1.methods.add(""call"", &call);
    await claw1;
}

entrypoint {
    Main;
}";

            // Act
            var result = await _compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("call");
            result.Result.Procedures.Should().ContainKey("Main");
            // Main should contain an AwaitObj instruction for "await claw1"
            result.Result.Procedures["Main"].Body.Any(cmd => cmd.Opcode == Opcodes.AwaitObj).Should().BeTrue(
                "await claw1 should produce an AwaitObj opcode");
            // call procedure body should use local memory (push [N]) not global (push global: [N]) for params
            var callBody = result.Result.Procedures["call"].Body;
            callBody.Where(cmd => cmd.Opcode == Opcodes.Push)
                .Select(cmd => cmd.Operand1 as MemoryAddress)
                .Where(ma => ma != null)
                .Should().AllSatisfy(ma => ma!.IsGlobal.Should().BeFalse(
                    "procedure parameters should use local memory (push [N]) not global memory (push global: [N])"));
        }

        [Fact]
        public async Task Serialize_ProgramWithProcedures_ShouldEmitProceduresBeforeEntrypoint()
        {
            // Arrange: procedures should appear before the entrypoint body in serialized output
            // so that all definitions are present before the entry-point call.
            var source = @"@AGI 0.0.1
program Test;
module Test/Test;

procedure greet() {
    print(""hello"");
}

entrypoint {
    greet;
}";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.OutputFormat = "agiasm";
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_{System.Guid.NewGuid():N}.agiasm");
            try
            {
                // Act
                await result.Result.SaveAsync(path);
                var serialized = await System.IO.File.ReadAllTextAsync(path);

                // Assert: "procedure" should appear before "entrypoint" in the output
                var procedureIdx = serialized.IndexOf("\nprocedure ", StringComparison.Ordinal);
                var entrypointIdx = serialized.IndexOf("\nentrypoint", StringComparison.Ordinal);
                procedureIdx.Should().BeLessThan(entrypointIdx,
                    "all procedure definitions must appear before the entrypoint body in the serialized output");
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }

        [Fact]
        public async Task CompileFileAsync_UseAsFunctionSignature_ShouldLinkImportedFunction()
        {
            // Regression: file-based compilation must resolve `use ... as { function ... }`
            // so runtime call lookup can find the imported function body.
            var root = Path.Combine(Path.GetTempPath(), "agi_use_link_" + Guid.NewGuid().ToString("N"));
            var modularityDir = Path.Combine(root, "modularity");
            Directory.CreateDirectory(modularityDir);

            var modulePath = Path.Combine(modularityDir, "module1.agi");
            var callerPath = Path.Combine(root, "use_module1.agi");

            try
            {
                await File.WriteAllTextAsync(modulePath, @"@AGI 0.0.1;

program module1;
system samples;
module modularity;

function add(x, y) {
    return x + y;
}
");

                await File.WriteAllTextAsync(callerPath, @"@AGI 0.0.1;

program use_module1;
system samples;
module modularity;

use modularity: module1 as {
    function add(x, y);
};

procedure Main() {
    var x:= 1;
    var y:= 2;
    var z:= module1: add(x, y);
    print(#""z: {z}"");
}

entrypoint {
    Main;
}");

                var compiler = new Compiler();
                var result = await compiler.CompileFileAsync(callerPath);

                result.Success.Should().BeTrue(result.ErrorMessage);
                result.Result!.Functions.Should().ContainKey("samples:modularity:module1:add",
                    "imported function from use-signature must be linked into executable unit");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task CompileFileAsync_UseModule_ShouldMergeImportedTypeDefsIntoEntrypoint()
        {
            var root = Path.Combine(Path.GetTempPath(), "agi_use_type_link_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var modulePath = Path.Combine(root, "module2.agi");
            var callerPath = Path.Combine(root, "use_module2.agi");

            try
            {
                await File.WriteAllTextAsync(modulePath, @"@AGI 0.0.1;

program module2;
system samples;
module modularity;

Point: type {
    public
        X: int;
}
");

                await File.WriteAllTextAsync(callerPath, @"@AGI 0.0.1;

program use_module2;
system samples;
module modularity;

use module2;

entrypoint {
}");

                var compiler = new Compiler();
                var result = await compiler.CompileFileAsync(callerPath);

                result.Success.Should().BeTrue(result.ErrorMessage);
                result.Result!.EntryPoint.Any(cmd =>
                    cmd.Opcode == Opcodes.Push &&
                    cmd.Operand1 is PushOperand po &&
                    string.Equals(po.Kind, "StringLiteral", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(po.Value as string, "samples:modularity:module2:Point", StringComparison.Ordinal))
                    .Should().BeTrue("imported module entrypoint prelude must be merged so type defs are executed");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task CompileFileAsync_DefObjInImporter_ShouldQualifyExternalTypeWithDefiningModuleNotConsumer()
        {
            var root = Path.Combine(Path.GetTempPath(), "agi_defobj_qual_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var modulePath = Path.Combine(root, "module2.agi");
            var callerPath = Path.Combine(root, "consumer.agi");

            try
            {
                await File.WriteAllTextAsync(modulePath, @"@AGI 0.0.1;

program module2;
system samples;
module modularity;

Room: type {
    public X: int;
}

entrypoint {
}
");

                await File.WriteAllTextAsync(callerPath, @"@AGI 0.0.1;

program consumer;
system samples;
module modularity;

use module2;

procedure Main() {
    var r := new Room(X: 1);
}

entrypoint {
    Main;
}
");

                var compiler = new Compiler();
                var result = await compiler.CompileFileAsync(callerPath);

                result.Success.Should().BeTrue(result.ErrorMessage);
                var main = result.Result!.Procedures["Main"].Body;
                main.Any(c =>
                    c.Opcode == Opcodes.Push &&
                    c.Operand1 is PushOperand po &&
                    string.Equals(po.Kind, "Class", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(po.Value as string, "samples:modularity:module2:Room", StringComparison.Ordinal))
                    .Should().BeTrue("defobj must use the module where Room is defined, not samples:modularity:consumer:Room");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task CompileFileAsync_BareTypeCall_ShouldEmitCtorViaDefObjNotCallPointFunction()
        {
            var root = Path.Combine(Path.GetTempPath(), "agi_bare_ctor_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var modulePath = Path.Combine(root, "module2.agi");
            var callerPath = Path.Combine(root, "consumer.agi");

            try
            {
                await File.WriteAllTextAsync(modulePath, @"@AGI 0.0.1;

program module2;
system samples;
module modularity;

Point: type {
    public
        X: int;
        Y: int;
    constructor Point(x, y) {
        X:= x;
        Y:= y;
    }
}

entrypoint {
}
");

                await File.WriteAllTextAsync(callerPath, @"@AGI 0.0.1;

program consumer;
system samples;
module modularity;

use module2;

procedure Main() {
    var p := Point(1, 2);
}

entrypoint {
    Main;
}
");

                var compiler = new Compiler();
                var result = await compiler.CompileFileAsync(callerPath);

                result.Success.Should().BeTrue(result.ErrorMessage);
                var main = result.Result!.Procedures["Main"].Body;
                main.Any(c => c.Opcode == Opcodes.DefObj).Should().BeTrue("Point(1,2) must allocate via defobj");
                main.Any(c =>
                    c.Opcode == Opcodes.CallObj &&
                    c.Operand1 is string mn &&
                    mn.Contains("Point_ctor", StringComparison.Ordinal)).Should().BeTrue();
                main.Any(c =>
                    c.Opcode == Opcodes.Call &&
                    c.Operand1 is CallInfo ci2 &&
                    string.Equals(ci2.FunctionName, "Point", StringComparison.Ordinal)).Should().BeFalse(
                    "must not call a function named Point — this is the imported type constructor");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task CompileAsync_FunctionWithFloatDecimalCastReturn_ShouldEmitDivNotEmptyBody()
        {
            var source = """
@AGI 0.0.1;
program t;
module t;

function div(x, y) {
    return float<decimal>: x / y;
}

entrypoint { }
""";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Functions["div"].Body.Should().Contain(c => c.Opcode == Opcodes.Div);
        }

        [Fact]
        public async Task CompileAsync_TypeWithConstructorAndMethods_ShouldNotTreatConstructorBodyAsFields()
        {
            var source = """
@AGI 0.0.1;

program module2;
system samples;
module modularity;

Point: type {
    public
        X: int;
        Y: int;
    constructor Point(x, y) {
        X:= x;
        Y:= y;
    }

    method Add(p: Point) {
        X += p.X;
        Y += p.Y;
    }

    method Print() {
        print(#"X: {X}, Y: {Y}");
    }
}

entrypoint { }
""";

            var result = await _compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);

            // В выходном юните не должно появляться field-декларации с типом "= x"
            // (раньше строка "X:= x;" парсилась как поле "X: = x;").
            var hasBogusFieldType =
                result.Result!.EntryPoint
                    .Where(c => c.Opcode == Opcodes.Push && c.Operand1 is PushOperand)
                    .Select(c => (PushOperand)c.Operand1!)
                    .Any(po => string.Equals(po.Kind, "StringLiteral", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(po.Value as string, "= x", StringComparison.Ordinal));

            hasBogusFieldType.Should().BeFalse("constructor body must not be lowered as field 'X: = x'");
        }

        [Fact]
        public async Task CompileAsync_TypesWithConstructorAssignments_ShouldKeepTypeDefsInEntrypoint()
        {
            var source = """
@AGI 0.0.1;
program module2;
system samples;
module modularity;

Point: type {
    public
        X: int;
    constructor Point(x) {
        X:= x;
    }
}

Shape: class {
    public
        Origin: Point;
    constructor Shape(origin: Point) {
        Origin:= origin;
    }
}

Circle: Shape {
    public
        Radius: int;
    constructor Circle(origin: Point, r) {
        Shape(origin);
        Radius:= r;
    }
}

Square: Shape {
    public
        W: int;
    constructor Square(origin: Point, w) {
        Shape(origin);
        W:= w;
    }
}

entrypoint { }
""";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);

            var unit = result.Result!;
            unit.OutputFormat = "agiasm";
            var agiasm = unit.ToAgiasmText(source);

            const string Q = "samples:modularity:module2:";
            agiasm.Should().Contain($"push string: \"{Q}Point\"");
            agiasm.Should().Contain($"push string: \"{Q}Shape\"");
            agiasm.Should().Contain($"push string: \"{Q}Circle\"");
            agiasm.Should().Contain($"push string: \"{Q}Square\"");
        }

        [Fact]
        public async Task CompileAsync_TypeMethods_ShouldEmitAsMethodsAndBehaveLikeFunctions()
        {
            var source = """
@AGI 0.0.1;
program module2;
system samples;
module modularity;

Point: type {
    public
        X: int;
        Y: int;
    constructor Point(x, y) {
        // ctor body intentionally empty for now
    }

    method Add(p: Point) {
        // body intentionally empty for now
    }

    method Print() {
        // body intentionally empty for now
    }
}

entrypoint { }
""";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);

            var unit = result.Result!;
            // Имена методов типов теперь включают суффикс арности (_1 и т.п.),
            // поэтому проверяем по префиксу.
            unit.Functions.Keys.Should().Contain(k => k.Contains("Point_ctor", StringComparison.Ordinal));
            unit.Functions.Keys.Should().Contain(k => k.Contains("Point_Add", StringComparison.Ordinal));
            unit.Functions.Keys.Should().Contain(k => k.Contains("Point_Print", StringComparison.Ordinal));

            // AGIASM-сериализация должна использовать заголовок 'method' для этих функций.
            unit.OutputFormat = "agiasm";
            var agiasm = unit.ToAgiasmText(source);
            agiasm.Should().Contain("method samples:modularity:module2:Point_ctor_1", "constructor must be emitted as 'method' in AGIASM");
            agiasm.Should().Contain("Point_Add", "Add must be emitted as 'method' in AGIASM");
            agiasm.Should().Contain("Point_Print", "Print must be emitted as 'method' in AGIASM");
        }

        [Fact]
        public async Task CompileAsync_TypeMethod_WithBody_ShouldLowerBodyStatements()
        {
            var source = """
@AGI 0.0.1;
program module2;
system samples;
module modularity;

Point: type {
    public
        X: int;
    method Hello() {
        print("hello");
    }
}

entrypoint { }
""";

            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);

            var unit = result.Result!;
            // Имя метода теперь может включать суффикс арности (Point_Hello_1 и т.п.),
            // поэтому ищем функцию по префиксу.
            unit.Functions.Keys.Should().Contain(k => k.Contains("Point_Hello", StringComparison.Ordinal));
            var helloKey = unit.Functions.Keys.Single(k => k.Contains("Point_Hello", StringComparison.Ordinal));
            var body = unit.Functions[helloKey].Body;

            // В теле метода должен быть вызов print (через Call).
            body.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName.Contains("print", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue("type method body must be lowered into real instructions, not an empty stub");
        }

        [Fact]
        public async Task CompileAsync_NestedSubfunctions_GetMangledNamesAndPowInInnerBody()
        {
            var source = @"
@AGI 0.0.1;
program t;
module t;

function calculate(x, y) {
    function add(a, b) { return a + b; }
    function mul(a, b) { return a * b; }
    function pow(a, b) { return a ^ b; }
    return add(x, y) + mul(x, y) + pow(x, y);
}

entrypoint { }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            var funcs = result.Result!.Functions;
            funcs.Should().ContainKey("calculate");
            funcs.Should().ContainKey("calculate_add");
            funcs.Should().ContainKey("calculate_mul");
            funcs.Should().ContainKey("calculate_pow");
            funcs["calculate"].Body.Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo)
                .Select(c => ((CallInfo)c.Operand1!).FunctionName)
                .Should().Contain(fn => string.Equals(fn, "calculate_add", StringComparison.Ordinal));
            funcs["calculate_pow"].Body.Should().Contain(c => c.Opcode == Opcodes.Pow);
        }

        [Fact]
        public async Task CompileAsync_PowerOperator_EmitsPowOpcode()
        {
            var source = @"
@AGI 0.0.1;
program t;
module t;
function p(a, b) { return a ^ b; }
entrypoint { }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Functions["p"].Body.Should().Contain(c => c.Opcode == Opcodes.Pow);
        }

        [Fact]
        public async Task CompileAsync_NestedProcedureInsideProcedure_ManglesAndCallUsesMangledName()
        {
            var source = @"
@AGI 0.0.1;
program t;
module t;
procedure Main() {
    procedure debug() {
        println(""ok"");
    }
    debug();
}
entrypoint { Main; }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("Main_debug");
            result.Result.Functions.Should().NotContainKey("Main_debug");
            var calls = result.Result.Procedures["Main"].Body
                .Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo)
                .Select(c => ((CallInfo)c.Operand1!).FunctionName)
                .ToList();
            calls.Should().Contain(fn => string.Equals(fn, "Main_debug", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CompileAsync_DottedNestedFunctionCall_ManglesToUnderscorePath()
        {
            var source = @"
@AGI 0.0.1;
program t;
module t;
function box() {
    function val() { return 7; }
    return val();
}
function main() {
    return box.val();
}
entrypoint { }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            var calls = result.Result!.Functions["main"].Body
                .Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo)
                .Select(c => ((CallInfo)c.Operand1!).FunctionName)
                .ToList();
            calls.Should().Contain(fn => string.Equals(fn, "box_val", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CompileAsync_ModuleColonThenDottedPath_JoinsSegmentsWithUnderscore()
        {
            var source = @"
@AGI 0.0.1;
program t;
module t;
procedure Main() {
    mod1:outer.inner.mid(1);
}
entrypoint { Main; }
";
            var result = await _compiler.CompileAsync(source);
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures["Main"].Body.Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo)
                .Select(c => ((CallInfo)c.Operand1!).FunctionName)
                .Should().Contain(fn => string.Equals(fn, "mod1:outer_inner_mid", StringComparison.Ordinal));
        }
    }
}
