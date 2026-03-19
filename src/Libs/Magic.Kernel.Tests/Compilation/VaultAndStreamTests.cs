using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using System.Linq;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class VaultAndStreamTests
    {
        [Fact]
        public void ParseProgram_WithVaultDeclaration_ShouldProduceNoAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;
            asm.Should().HaveCount(5);
            asm[0].Opcode.Should().Be(Opcodes.Push);
            asm[1].Opcode.Should().Be(Opcodes.Push);
            asm[2].Opcode.Should().Be(Opcodes.Def);
            asm[3].Opcode.Should().Be(Opcodes.Pop);
        }

        [Fact]
        public void ParseProgram_WithVaultRead_ShouldGenerateExpectedAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;
            asm.Should().HaveCount(10);
            asm[0].Opcode.Should().Be(Opcodes.Push);
            asm[1].Opcode.Should().Be(Opcodes.Push);
            asm[2].Opcode.Should().Be(Opcodes.Def);
            asm[3].Opcode.Should().Be(Opcodes.Pop);
            asm[4].Opcode.Should().Be(Opcodes.Push);
            asm[5].Opcode.Should().Be(Opcodes.Push);
            asm[6].Opcode.Should().Be(Opcodes.Push);
            asm[7].Opcode.Should().Be(Opcodes.CallObj);
            asm[8].Opcode.Should().Be(Opcodes.Pop);
            (asm[7].Operand1 as string).Should().Be("read");
        }

        [Fact]
        public void ParseProgram_WithStreamMessengerTelegram_ShouldGenerateStreamAsm()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<messenger, telegram>;
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;
            asm.Should().HaveCount(11);
            asm[0].Opcode.Should().Be(Opcodes.Push);
            asm[1].Opcode.Should().Be(Opcodes.Push);
            asm[2].Opcode.Should().Be(Opcodes.Def);
            asm[3].Opcode.Should().Be(Opcodes.Pop);
            asm[4].Opcode.Should().Be(Opcodes.Push);
            asm[5].Opcode.Should().Be(Opcodes.Push);
            asm[6].Opcode.Should().Be(Opcodes.Push);
            asm[7].Opcode.Should().Be(Opcodes.Push);
            asm[8].Opcode.Should().Be(Opcodes.DefGen);
            asm[9].Opcode.Should().Be(Opcodes.Pop);

            var pushTypes = asm
                .Where(c => c.Opcode == Opcodes.Push)
                .Select(c => c.Operand1)
                .OfType<PushOperand>()
                .Where(o => o.Kind == "Type")
                .Select(o => o.Value?.ToString())
                .ToList();

            pushTypes.Should().ContainInOrder("stream", "messenger", "telegram");

            var arityPush = asm[7].Operand1.Should().BeOfType<PushOperand>().Subject;
            arityPush.Kind.Should().Be("IntLiteral");
            arityPush.Value.Should().Be(2L);
        }

        [Fact]
        public void ParseProgram_WithStreamOpenObjectArgument_ShouldGenerateOpJsonThenCallObj()
        {
            // Arrange
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
    var stream1 := stream<messenger, telegram>;
    stream1.open({
        token: token
    });
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();

            // Act
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

            // Assert
            analyzed.Procedures.Should().ContainKey("Main");
            var asm = analyzed.Procedures["Main"].Body;

            var opjson = asm
                .FirstOrDefault(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "opjson");
            opjson.Should().NotBeNull();

            var callObjIndex = asm.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "open");
            callObjIndex.Should().BeGreaterThan(2);
            asm[callObjIndex - 2].Opcode.Should().Be(Opcodes.Push); // object argument from memory
            asm[callObjIndex - 1].Opcode.Should().Be(Opcodes.Push); // arity
        }

        [Fact]
        public void ParseProgram_WithStreamOpenNestedJson_ShouldGenerateJsonBuilderOpcodes()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var token := vault1.read(""token"");
    var stream1 := stream<messenger, telegram>;
    stream1.open({
        token: token,
        options: {
            retries: 3,
            enabled: true
        },
        tags: [""a"", ""b"", token, { nested: false }]
    });
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            var opjsonCalls = asm
                .Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "opjson")
                .Select(c => (CallInfo)c.Operand1!)
                .ToList();

            opjsonCalls.Should().NotBeEmpty();
            opjsonCalls.Select(c => c.Parameters["operation"]?.ToString()).Should().Contain("set");
            opjsonCalls.Select(c => c.Parameters["operation"]?.ToString()).Should().Contain("ensureObject");
            opjsonCalls.Select(c => c.Parameters["operation"]?.ToString()).Should().Contain("ensureArray");
            opjsonCalls.Select(c => c.Parameters["operation"]?.ToString()).Should().Contain("append");
        }

        [Fact]
        public void ParseProgram_WithVariableAssignedFromMultilineObjectLiteral_ShouldPopulateAllObjectFields()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var time := vault1.read(""time"");
    var chatId := vault1.read(""chatId"");
    var username := vault1.read(""username"");
    var text := vault1.read(""text"");
    var message = {
        Time: time,
        ChatId: chatId,
        Username: username,
        Message: text
    };
    print(message);
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            var opjsonCalls = asm
                .Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "opjson")
                .Select(c => (CallInfo)c.Operand1!)
                .ToList();

            opjsonCalls.Should().NotBeEmpty();
            var setCalls = opjsonCalls
                .Where(c => c.Parameters["operation"]?.ToString() == "set")
                .ToList();

            setCalls.Should().NotBeEmpty();
            setCalls.Select(c => c.Parameters["path"]?.ToString()).Should().Contain(new[] { "Time", "ChatId", "Username", "Message" });
        }

        [Fact]
        public void ParseProgram_WithPlusAssignUsingExistingObjectVariable_ShouldNotWrapWithRootSetInstruction()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var message = {
        Time: ""now"",
        ChatId: ""1"",
        Message: ""hello""
    };
    var bag = {
        items: []
    };
    bag.items += message;
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            var opjsonCalls = asm
                .Where(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "opjson")
                .Select(c => (CallInfo)c.Operand1!)
                .ToList();

            opjsonCalls
                .Any(c => c.Parameters["operation"]?.ToString() == "set" && c.Parameters["path"]?.ToString() == "")
                .Should()
                .BeFalse();
        }

        [Fact]
        public void ParseProgram_WithGenericDatabaseDef_ShouldGenerateDefGenAndOpenCall()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var connectionString := vault1.read(""connectionString"");
    var db1 := database<postgres, Db>>;
    db1.open(connectionString);
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            asm.Should().Contain(c => c.Opcode == Opcodes.DefGen);
            asm.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "open");

            var pushTypes = asm
                .Where(c => c.Opcode == Opcodes.Push)
                .Select(c => c.Operand1)
                .OfType<PushOperand>()
                .Where(o => o.Kind == "Type")
                .Select(o => o.Value?.ToString())
                .ToList();

            pushTypes.Should().Contain("database");
            pushTypes.Should().Contain("postgres");
            pushTypes.Should().Contain("db>");
        }

        [Fact]
        public void ParseProgram_WithOpenScalarArg_ShouldNotGenerateOpJson()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var vault1 := vault;
    var connectionString := vault1.read(""connectionString"");
    var db1 := database<postgres, Db>>;
    db1.open(connectionString);
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            asm.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "open");
            asm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "opjson").Should().BeFalse();
        }

        [Fact]
        public void ParseProgram_WithOpenStringLiteralSameAsVariableName_ShouldKeepLiteralArgument()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<file>;
    stream1.open(""stream1"");
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            var openCallIndex = asm.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "open");
            openCallIndex.Should().BeGreaterThan(2);

            asm[openCallIndex - 2].Opcode.Should().Be(Opcodes.Push);
            var argumentPush = asm[openCallIndex - 2].Operand1.Should().BeOfType<PushOperand>().Subject;
            argumentPush.Kind.Should().Be("StringLiteral");
            argumentPush.Value.Should().Be("stream1");
        }

        [Fact]
        public void ParseProgram_WithForStreamWaitByDelta_ShouldGenerateLoopAndAwaitOpcodes()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<file>;
    stream1.open(""stream1"");
    for streamwait by delta (stream1, delta, aggregate) {
        var semantic_file_system_delta = await compile(delta);
        print(semantic_file_system_delta);
    }
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            asm.Should().Contain(c => c.Opcode == Opcodes.Label);
            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWaitObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.Cmp);
            asm.Should().Contain(c => c.Opcode == Opcodes.Je);
            asm.Should().Contain(c => c.Opcode == Opcodes.Jmp);
            asm.Any(c => c.Opcode == Opcodes.ACall && c.Operand1 is CallInfo ci && ci.FunctionName.StartsWith("streamwait_loop_", StringComparison.Ordinal)).Should().BeTrue();
            asm.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
            asm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "compile").Should().BeTrue();
            asm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "print").Should().BeTrue();
        }

        [Fact]
        public void ParseProgram_WithForStreamWaitMemberAccessAndStreamwaitPrint_ShouldCompileBody()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<file>;
    stream1.open(""stream1"");
    for streamwait by delta (stream1, delta, aggregate) {
        var data := delta.data;
        var message := data!.message;
        data!.message := message;
        await stream1;
        streamwait print(message);
    }
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWaitObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWait);
            asm.Should().Contain(c => c.Opcode == Opcodes.GetObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.SetObj);

            var dataGetObjIdx = asm.FindIndex(c => c.Opcode == Opcodes.GetObj);
            dataGetObjIdx.Should().BeGreaterThan(1);
            asm[dataGetObjIdx - 2].Opcode.Should().Be(Opcodes.Push);
            asm[dataGetObjIdx - 1].Opcode.Should().Be(Opcodes.Push);

            var firstMemberPush = asm[dataGetObjIdx - 1].Operand1.Should().BeOfType<PushOperand>().Subject;
            firstMemberPush.Kind.Should().Be("StringLiteral");
            firstMemberPush.Value.Should().Be("data");

            var messagePushExists = asm.Any(c =>
                c.Opcode == Opcodes.Push &&
                c.Operand1 is PushOperand po &&
                po.Kind == "StringLiteral" &&
                Equals(po.Value, "message"));
            messagePushExists.Should().BeTrue();
        }

        [Fact]
        public void ParseProgram_WithStreamWaitTimeAndPlusAssign_ShouldGenerateGetAddAssignAwaitAndStreamwaitPrint()
        {
            var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<file>;
    var db1 := database<postgres, Db>>;
    for streamwait by delta (stream1, delta, aggregate) {
        var data := delta.data;
        var message := data!.message;
        var user := data!.user;
        var time = :time;
        db1.Messages<> += {
            Time: time,
            User: user,
            Message: message
        };
        await db1;
        streamwait print(message);
    }
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            asm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "get").Should().BeTrue();
            var getCallIndex = asm.FindIndex(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo callInfo && callInfo.FunctionName == "get");
            getCallIndex.Should().BeGreaterThan(1);
            asm[getCallIndex - 1].Opcode.Should().Be(Opcodes.Push);
            var getArityPush = asm[getCallIndex - 1].Operand1.Should().BeOfType<PushOperand>().Subject;
            getArityPush.Kind.Should().Be("IntLiteral");
            getArityPush.Value.Should().Be(1L);

            asm.Should().Contain(c => c.Opcode == Opcodes.GetObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.SetObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "add");
            asm.Should().Contain(c => c.Opcode == Opcodes.AwaitObj);
            asm.Should().Contain(c => c.Opcode == Opcodes.StreamWait);
        }

        [Fact]
        public void ParseProgram_WithTopLevelTableAndDatabase_ShouldCompilePreludeBeforeEntrypoint()
        {
            var source = @"@AGI 0.0.1;

program telegram_to_db;
module telegram;

Messages<> : table {
    Id: bigint primary key identity;
    Time: datetime;
    User: nvarchar(250)?;
    Message: json?;
}

Db> : database {
    Messages<>;
}

procedure Main {
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var entryAsm = analyzed.EntryPoint;

            var firstPush = entryAsm.FirstOrDefault(c => c.Opcode == Opcodes.Push);
            firstPush.Should().NotBeNull();
            var firstPushOperand = firstPush!.Operand1.Should().BeOfType<PushOperand>().Subject;
            firstPushOperand.Kind.Should().Be("StringLiteral");
            firstPushOperand.Value.Should().Be("Messages<>");

            entryAsm.Should().Contain(c => c.Opcode == Opcodes.Def);
            entryAsm.Should().Contain(c => c.Opcode == Opcodes.Pop);
            entryAsm.Any(c => c.Opcode == Opcodes.Call && c.Operand1 is CallInfo ci && ci.FunctionName == "Main").Should().BeTrue();
        }

        [Fact]
        public void ParseProgram_WithGlobalDatabaseSchema_ShouldUseGlobalRefInDefGen()
        {
            var source = @"@AGI 0.0.1;

program telegram_to_db;
module telegram;

Messages<> : table {
    Id: bigint;
}

Db> : database {
    Messages<>;
}

procedure Main {
    var db1 := database<postgres, Db>>;
}

entrypoint {
    Main;
}";

            var parser = new Parser();
            var semanticAnalyzer = new SemanticAnalyzer();
            var structure = parser.ParseProgram(source);
            var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);
            var asm = analyzed.Procedures["Main"].Body;

            var defgenIdx = asm.FindIndex(c => c.Opcode == Opcodes.DefGen);
            defgenIdx.Should().BeGreaterThan(2);

            var globalPush = asm[defgenIdx - 1].Operand1.Should().BeOfType<PushOperand>().Subject;
            globalPush.Kind.Should().Be("IntLiteral");
            globalPush.Value.Should().Be(2L);

            var refPush = asm[defgenIdx - 2].Operand1.Should().BeOfType<global::Magic.Kernel.MemoryAddress>().Subject;
            refPush.IsGlobal.Should().BeTrue();
            refPush.Index.Should().Be(1);
        }
    }
}

