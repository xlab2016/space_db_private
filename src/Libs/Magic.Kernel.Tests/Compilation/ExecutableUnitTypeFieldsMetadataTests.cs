using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class ExecutableUnitTypeFieldsMetadataTests
    {
        [Fact]
        public async Task Compile_ShouldPopulateCircleRadius_WithFloatDecimalTypeSpec()
        {
            var source = @"@AGI 0.0.1;
program type_fields_meta;

Point: type {
    public X: int;
}

Shape: class {
    public Origin: Point;
}

Circle: Shape {
    public Radius: float<decimal>;
}

entrypoint {
}";

            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);
            var entry = result.Result!.EntryPoint;
            var radiusPush = entry.Any(c =>
                c.Opcode == Opcodes.Push &&
                c.Operand1 is PushOperand po &&
                po.Kind == "StringLiteral" &&
                (string?)po.Value == "Radius");
            radiusPush.Should().BeTrue("bytecode should contain field name push for Radius");

            var shape = result.Result!.Types.FirstOrDefault(t => t.FullName == "type_fields_meta:Shape");
            shape.Should().NotBeNull();
            var origin = shape!.Fields.FirstOrDefault(f => f.Name == "Origin");
            origin.Should().NotBeNull();
            origin!.Type.Should().Be("type_fields_meta:Point");

            var circle = result.Result!.Types.FirstOrDefault(t => t.FullName == "type_fields_meta:Circle");
            circle.Should().NotBeNull();
            var radius = circle!.Fields.FirstOrDefault(f => f.Name == "Radius");
            radius.Should().NotBeNull();
            radius!.Type.Should().Be("float<decimal>");
            radius.Visibility.Should().Be("public");
        }

        [Fact]
        public async Task Compile_PointType_ShouldEmitMethodDef_AndPopulateMethodsOnDefType()
        {
            var source = @"@AGI 0.0.1;
program mod2;
system samples;
module modularity;

Point: type {
    public X: int;
    method Print() { print(1); }
}

entrypoint { }";

            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);
            var point = result.Result!.Types.FirstOrDefault(t => t.Name == "Point");
            point.Should().NotBeNull();
            var print = point!.Methods.FirstOrDefault(m => m.Name == "Print");
            print.Should().NotBeNull();
            print!.FullName.Should().EndWith(":Point_Print_1");
            print.ReturnType.Should().Be("void");

            var entry = result.Result.EntryPoint;
            var pushesMethod = entry.Where(c =>
                c.Opcode == Opcodes.Push &&
                c.Operand1 is PushOperand po &&
                po.Kind == "StringLiteral" &&
                (string?)po.Value == "method").ToList();
            pushesMethod.Should().NotBeEmpty();
        }
    }
}
