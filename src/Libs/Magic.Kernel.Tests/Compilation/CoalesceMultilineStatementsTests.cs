using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class CoalesceMultilineStatementsTests
    {
        [Fact]
        public async Task Compiler_ShouldTreatSplitVarInitializationAsSingleStatement()
        {
            // Arrange
            var source = @"@AGI 0.0.1;
program use_module1;
system samples;
module modularity;

procedure Main() {
    var world := new Room(Board: new Board(Surface: new Point[], Shapes: new Shape[]),
        Persons: new Persons[]);
}

entrypoint {
    Main;
}";

            var compiler = new Compiler();

            // Act
            var result = await compiler.CompileAsync(source);

            // Assert
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Result!.Procedures.Should().ContainKey("Main");

            var main = result.Result.Procedures["Main"];

            // Должна быть объектная инициализация (DefObj), и установка полей (SetObj) для world.
            var hasDefObj = main.Body.Any(i => i.Opcode == Opcodes.DefObj);
            var hasSetObj = main.Body.Any(i => i.Opcode == Opcodes.SetObj);

            hasDefObj.Should().BeTrue("world должен создаваться через DefObj");
            hasSetObj.Should().BeTrue("world должен быть инициализирован через SetObj");
        }

        [Fact]
        public async Task Compiler_ShouldTreatVarTypedPrefixSameAsRoomColonInitializer()
        {
            var source = @"@AGI 0.0.1;
program p;
system s;
module m;

Room: type {
    public X: int;
}

procedure Main() {
    var w1 := Room: { X: 1 };
    var w2: Room := { X: 1 };
}

entrypoint { Main; }
";

            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);
            var main = result.Result!.Procedures["Main"].Body;
            var matCount = main.Count(c =>
                c.Opcode == Opcodes.Call &&
                c.Operand1 is CallInfo ci &&
                string.Equals(ci.FunctionName, "materialize", StringComparison.Ordinal));
            matCount.Should().Be(2, "оба синтаксиса должны давать materialize");
        }
    }
}

