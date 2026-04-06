using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class TypeFieldDefinitionLoweringTests
    {
        [Fact]
        public async Task Compiler_ShouldEmitOwnerReferenceAndShortFieldName_ForTypeFields()
        {
            var source = @"@AGI 0.0.1;
program field_emit;

Point: type {
    public
        X: int;
        Y: int;
}

entrypoint {
}";

            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);

            var entry = result.Result!.EntryPoint;
            var fieldDefs = GetFieldDefArguments(entry);

            fieldDefs.Should().HaveCount(2);
            foreach (var args in fieldDefs)
            {
                args.Should().HaveCount(5);
                args[0].Should().BeOfType<MemoryAddress>();
                ((MemoryAddress)args[0]!).Index.Should().HaveValue();
                args[1].Should().BeOneOf("X", "Y");
                args[2].Should().Be("int");
                args[3].Should().Be("field");
                args[4].Should().Be("public");
            }

            fieldDefs.Select(a => a[1] as string)
                .Should().NotContain(name => name != null && name.Contains("Point."));
        }

        [Fact]
        public async Task Compiler_ShouldEmitPushClass_WithQualifiedName_ForReferenceTypeFields()
        {
            var source = @"@AGI 0.0.1;
program cls_field_push;

Point: type { public X: int; }

Shape: class {
    public Origin: Point;
}

entrypoint {
}";

            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);

            var entry = result.Result!.EntryPoint;
            var shapeFieldDefIndex = -1;
            for (var i = 0; i < entry.Count; i++)
            {
                if (entry[i].Opcode != Opcodes.Def)
                    continue;
                if (i == 0 || entry[i - 1].Opcode != Opcodes.Push)
                    continue;
                var arityPo = entry[i - 1].Operand1 as PushOperand;
                if (arityPo?.Kind != "IntLiteral")
                    continue;
                var arityVal = arityPo.Value switch
                {
                    int ni => ni,
                    long nl => (int)nl,
                    _ => -1
                };
                if (arityVal != 5)
                    continue;
                var start = i - arityVal - 1;
                if (start < 0 || start + 2 >= entry.Count)
                    continue;
                if (entry[start + 2].Operand1 is PushOperand typePo &&
                    typePo.Kind == "Class" &&
                    (string?)typePo.Value == "cls_field_push:Point")
                {
                    shapeFieldDefIndex = i;
                    break;
                }
            }

            shapeFieldDefIndex.Should().BeGreaterThan(-1, "Shape.Origin field def should push class cls_field_push:Point");
        }

        [Fact]
        public async Task Compiler_ShouldEmitSeparateFieldDefs_ForCommaSeparatedNamesWithSharedType()
        {
            var source = @"@AGI 0.0.1;
program multi_field;

Shape: class {
    public X: int;
}

Square: Shape {
    public
        W, H: float<decimal>;
}

entrypoint {
}";

            var compiler = new Compiler();
            var result = await compiler.CompileAsync(source);

            result.Success.Should().BeTrue(result.ErrorMessage);

            var fieldDefs = GetFieldDefArguments(result.Result!.EntryPoint);
            var squareFields = fieldDefs
                .Where(a => a[1] is string n && (n == "W" || n == "H"))
                .ToList();

            squareFields.Should().HaveCount(2);
            squareFields.Should().AllSatisfy(a =>
            {
                a[2].Should().Be("float<decimal>");
                a[3].Should().Be("field");
                a[4].Should().Be("public");
            });
        }

        private static List<List<object?>> GetFieldDefArguments(ExecutionBlock entry)
        {
            var result = new List<List<object?>>();
            for (var i = 0; i < entry.Count; i++)
            {
                if (entry[i].Opcode != Opcodes.Def)
                    continue;

                if (i == 0 || entry[i - 1].Opcode != Opcodes.Push || entry[i - 1].Operand1 is not PushOperand arityPush || arityPush.Kind != "IntLiteral")
                    continue;

                var arity = arityPush.Value switch
                {
                    long l => (int)l,
                    int n => n,
                    _ => 0
                };
                if (arity <= 0)
                    continue;

                var start = i - arity - 1;
                if (start < 0)
                    continue;

                var args = new List<object?>(arity);
                for (var j = start; j < i - 1; j++)
                {
                    if (entry[j].Opcode != Opcodes.Push)
                    {
                        args.Clear();
                        break;
                    }

                    args.Add(entry[j].Operand1 switch
                    {
                        PushOperand po => po.Value,
                        MemoryAddress ma => ma,
                        var other => other
                    });
                }

                if (args.Count == arity &&
                    args.Count >= 4 &&
                    args[3] is string kind &&
                    kind == "field")
                {
                    result.Add(args);
                }
            }

            return result;
        }
    }
}
