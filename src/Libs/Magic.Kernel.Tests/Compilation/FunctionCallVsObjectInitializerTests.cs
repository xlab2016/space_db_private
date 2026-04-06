using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class FunctionCallVsObjectInitializerTests
    {
        private static string DescribeCommand(Command cmd)
        {
            var operandText = cmd.Operand1 switch
            {
                PushOperand p => $"{p.Kind}:{p.Value}",
                CallInfo ci => $"call:{ci.FunctionName}",
                _ => cmd.Operand1?.ToString() ?? ""
            };
            return $"{cmd.Opcode} {operandText}".Trim();
        }

        [Fact]
        public async Task UseModule1_ShouldNotTreatCalculateAsObject()
        {
            // Arrange: реальный пример из design/samples — use_module1.agi
            // где calculate используется как функция, а не как объект/тип.
            // Тесты запускаются из bin/Debug/net9.0, поднимаемся на пять уровней до корня src.
            var path = @"C:\dev\DTAI\space_db_private\design\Space\samples\modularity\use_module1.agi";
            var source = File.ReadAllText(path, Encoding.UTF8);

            var compiler = new Compiler();

            // Act
            var result = await compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);

            var exec = result.Result!;
            var allCommands =
                (exec.EntryPoint ?? new ExecutionBlock())
                .Concat(exec.Procedures.Values.SelectMany(p => p.Body));

            // Не должно быть пары push "calculate" → def (calculate как объект/тип).
            // Для диагностики собираем подробный дамп по всем push "calculate" и месту совпадения.
            bool HasPushCalculateThenDef(out string debugDump)
            {
                var sb = new StringBuilder();
                var index = 0;
                Command? prev = null;

                foreach (var cmd in allCommands)
                {
                    if (cmd.Opcode == Opcodes.Push &&
                        cmd.Operand1 is PushOperand po &&
                        po.Kind == "StringLiteral" &&
                        string.Equals(po.Value as string, "calculate", System.StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"push \"calculate\" at #{index}: {DescribeCommand(cmd)}");
                    }

                    if (prev != null &&
                        prev.Opcode == Opcodes.Push &&
                        prev.Operand1 is PushOperand o &&
                        o.Kind == "StringLiteral" &&
                        string.Equals(o.Value as string, "calculate", System.StringComparison.OrdinalIgnoreCase) &&
                        cmd.Opcode == Opcodes.Def)
                    {
                        sb.AppendLine($"MATCH push \"calculate\" → def at #{index - 1} -> #{index}");
                        sb.AppendLine($"  prev: {DescribeCommand(prev)}");
                        sb.AppendLine($"  curr: {DescribeCommand(cmd)}");

                        debugDump = sb.ToString();
                        return true;
                    }

                    prev = cmd;
                    index++;
                }

                debugDump = sb.ToString();
                return false;
            }

            HasPushCalculateThenDef(out var debug)
                .Should().BeFalse(
                    "calculate в use_module1 должен трактоваться как функция, а не как объект/тип. " +
                    "Diagnostic dump:\n" + debug);
        }

        /// <summary>
        /// Вложенные <c>Room(Board: Board(Surface: Point[], ...), Persons: Person[])</c> и <c>Type[]</c> как пустой DefList.
        /// </summary>
        [Fact]
        public async Task UseModule1_NestedTypeInitializers_ShouldLowerToSetObj()
        {
            var path = @"C:\dev\DTAI\space_db_private\design\Space\samples\modularity\use_module1.agi";
            var source = File.ReadAllText(path, Encoding.UTF8);
            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source, path);
            result.Success.Should().BeTrue(result.ErrorMessage);
            var main = result.Result!.Procedures["Main"];
            var setObjCount = main.Body.Count(c => c.Opcode == Opcodes.SetObj);
            setObjCount.Should().BeGreaterThanOrEqualTo(4, "Room+Board nested object init должны дать setobj по полям Board, Persons, Surface, Shapes");
        }

        [Fact]
        public async Task Debug_VarMePerson_ShouldEmitSingleDefObjForPerson()
        {
            var path = @"C:\dev\DTAI\space_db_private\design\Space\samples\modularity\use_module1.agi";
            var source = File.ReadAllText(path, Encoding.UTF8);
            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source, path);
            result.Success.Should().BeTrue(result.ErrorMessage);
            var main = result.Result!.Procedures["Main"].Body;
            var seq = main.Select((c, i) => (c, i)).Where(x =>
                x.c.Opcode == Opcodes.Push &&
                x.c.Operand1 is PushOperand po &&
                po.Kind == "Class" &&
                (po.Value as string ?? "").Contains("Person", StringComparison.Ordinal)).ToList();
            var dump = string.Join("\n", seq.Select(x => $"#{x.i}: {DescribeCommand(x.c)}"));
            for (var i = 95; i < 135 && i < main.Count; i++)
                dump += $"\n@{i}: {DescribeCommand(main[i])}";
            // Person[] on world.Persons plus a single defobj for var me — not three redundant Person defobjs.
            seq.Count.Should().Be(2, $"Person push class count:\n{dump}");
        }
    }
}

