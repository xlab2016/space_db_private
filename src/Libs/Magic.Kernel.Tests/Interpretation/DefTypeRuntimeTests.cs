using System.Collections.Generic;
using FluentAssertions;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Data;
using Magic.Kernel.Types;
using Xunit;

namespace Magic.Kernel.Tests.Interpretation
{
    public class DefTypeRuntimeTests
    {
        [Fact]
        public void Def_WithTypeKind_ShouldCreateDefType()
        {
            var unit = new ExecutableUnit();
            var value = Hal.Def(new object?[] { "Point", "type" }, executableUnit: unit);

            value.Should().BeOfType<DefType>();
            var type = (DefType)value!;
            type.Name.Should().Be("Point");
            unit.Types.Should().ContainSingle(t => t.Name == "Point");
        }

        [Fact]
        public void Def_WithTypeKind_ShouldReuseExistingTypeFromExecutableUnit()
        {
            var existing = new DefType { Name = "Point" };
            var unit = new ExecutableUnit();
            unit.Types.Add(existing);

            var value = Hal.Def(new object?[] { "point", "type" }, executableUnit: unit);

            value.Should().BeSameAs(existing);
            unit.Types.Should().ContainSingle();
        }

        [Fact]
        public void Def_WithFieldKind_ShouldAttachFieldToOwnerType()
        {
            var owner = new DefType { Name = "Point" };

            var value = Hal.Def(new object?[] { owner, "X", "field", "public" }, executableUnit: null);

            value.Should().BeSameAs(owner);
            owner.Fields.Should().ContainSingle(f => f.Name == "X" && f.Type == "any" && f.Visibility == "public");
        }

        [Fact]
        public void Def_WithTypedFieldKind_ShouldAttachFieldTypeToOwnerType()
        {
            var owner = new DefType { Name = "Point" };

            var value = Hal.Def(new object?[] { owner, "X", "int", "field", "public" }, executableUnit: null);

            value.Should().BeSameAs(owner);
            owner.Fields.Should().ContainSingle(f => f.Name == "X" && f.Type == "int" && f.Visibility == "public");
        }

        [Fact]
        public void Def_TableColumn_ShouldAddSqlColumn_NotTreatTableAsPlainDefTypeField()
        {
            // Table : DefType — раньше широкая ветка «поле типа» могла съесть сигнатуру column и вернуть не то / null.
            var table = new Table { Name = "Message" };
            var value = Hal.Def(new object?[]
            {
                table,
                "Id",
                "bigint",
                "primary key",
                "identity",
                "nullable:0",
                "column"
            }, executableUnit: null);

            value.Should().NotBeNull();
            value.Should().BeSameAs(table);
            table.Columns.Should().ContainSingle(c =>
                c.Name == "Id" &&
                c.Type == "bigint" &&
                c.Modifiers.Count == 3);
        }

        [Fact]
        public void Def_WithMethodKind_ShouldAttachMethodMetadataToOwnerType()
        {
            var owner = new DefType { Namespace = "samples:modularity:module2", Name = "Point" };

            var value = Hal.Def(new object?[]
            {
                owner,
                "Print",
                "samples:modularity:module2:Point_Print_1",
                "void",
                "method",
                "public"
            }, executableUnit: null);

            value.Should().BeSameAs(owner);
            owner.Methods.Should().ContainSingle(m =>
                m.Name == "Print" &&
                m.FullName == "samples:modularity:module2:Point_Print_1" &&
                m.ReturnType == "void" &&
                m.Visibility == "public");
        }

        [Fact]
        public void Def_WithMultipleBaseNames_ShouldCreateClassWithInheritance()
        {
            var value = Hal.Def(new object?[] { "CircleSquare", "Circle", "Square" }, executableUnit: null);

            value.Should().BeOfType<DefClass>();
            var cls = (DefClass)value!;
            cls.Name.Should().Be("CircleSquare");
            cls.Inheritances.Should().BeEquivalentTo(new List<string> { "Circle", "Square" });
            cls.Generalizations.Should().BeEmpty();
        }

        [Fact]
        public void MaterializeJsonIntoDefObject_ShouldFillNestedObjectAndArray()
        {
            var catalog = new List<DefType>
            {
                new DefType
                {
                    Name = "App:Inner",
                    Fields = new List<DefTypeField> { new() { Name = "N", Type = "int" } }
                },
                new DefType
                {
                    Name = "App:Root",
                    Fields = new List<DefTypeField>
                    {
                        new() { Name = "Inner", Type = "App:Inner" },
                        new() { Name = "Items", Type = "App:Inner[]" }
                    }
                }
            };

            var schema = catalog[1];
            var target = Hal.DefObj(schema);
            var json = new Dictionary<string, object?>
            {
                ["Inner"] = new Dictionary<string, object?> { ["N"] = 7L },
                ["Items"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["N"] = 1L },
                    new Dictionary<string, object?> { ["N"] = 2L }
                }
            };

            Hal.Materialize(schema, target, json, catalog);

            target.TryGetField("Inner", out var inner).Should().BeTrue();
            inner.Should().BeOfType<DefObject>();
            ((DefObject)inner!).TryGetField("N", out var n).Should().BeTrue();
            n.Should().Be(7L);

            target.TryGetField("Items", out var items).Should().BeTrue();
            items.Should().BeOfType<DefList>();
            var list = (DefList)items!;
            list.Items.Should().HaveCount(2);
            list.Items[0].Should().BeOfType<DefObject>();
            ((DefObject)list.Items[0]!).TryGetField("N", out var i0).Should().BeTrue();
            i0.Should().Be(1L);
        }

        [Fact]
        public void DefObject_ToConstructorStyleString_TwoFields_IsMultilineWithTabs()
        {
            var boardType = new DefType
            {
                Name = "Board",
                Fields = new List<DefTypeField>
                {
                    new() { Name = "Surface", Type = "Point[]" },
                    new() { Name = "Shapes", Type = "Shape[]" }
                }
            };
            var roomType = new DefType
            {
                Name = "Room",
                Fields = new List<DefTypeField>
                {
                    new() { Name = "Board", Type = "Board" },
                    new() { Name = "Persons", Type = "Person[]" }
                }
            };

            var board = new DefObject(boardType);
            board.SetField("Surface", new DefList { Name = "Point" });
            board.SetField("Shapes", new DefList { Name = "Shape" });

            var room = new DefObject(roomType);
            room.SetField("Board", board);
            room.SetField("Persons", new DefList { Name = "Person" });

            var s = room.ToConstructorStyleString();
            s.Should().Contain("\n");
            s.Should().Contain("\tBoard: Board(");
            s.Should().Contain("\t\tSurface: Point[],");
            s.Should().Contain("\t\tShapes: Shape[]");
            s.Should().Contain("\tPersons: Person[]");
        }

        [Fact]
        public void DefObject_ToConstructorStyleString_SingleSimpleField_IsSingleLine()
        {
            var pointType = new DefType
            {
                Name = "Point",
                Fields = new List<DefTypeField>
                {
                    new() { Name = "X", Type = "int" },
                    new() { Name = "Y", Type = "int" }
                }
            };
            var p = new DefObject(pointType);
            p.SetField("X", 1L);
            p.SetField("Y", 2L);
            var s = p.ToConstructorStyleString();
            s.Should().Be("Point(X: 1, Y: 2)");
        }

        [Fact]
        public void DefObject_ToConstructorStyleString_WithInheritanceAndCatalog_IncludesBaseSchemaFields()
        {
            var shapeType = new DefType
            {
                Name = "Shape",
                Fields = new List<DefTypeField> { new() { Name = "Origin", Type = "Point" } }
            };
            var circleType = new DefClass("Circle", new[] { "Shape" })
            {
                Fields = new List<DefTypeField> { new() { Name = "Radius", Type = "float" } }
            };
            IReadOnlyList<DefType> catalog = new List<DefType> { shapeType, circleType };

            var circle = new DefObject(circleType);
            circle.SetField("Radius", 3m);
            circle.SetField("Origin", 99L);

            var s = circle.ToConstructorStyleString(typeCatalog: catalog);
            s.Should().Be("Circle(Radius: 3, Origin: 99)");

            var json = circle.ToJsonMapBySchemaFieldNames(includeTypeDiscriminator: false, catalog);
            json.Should().ContainKey("Radius");
            json.Should().ContainKey("Origin");
        }
    }
}
