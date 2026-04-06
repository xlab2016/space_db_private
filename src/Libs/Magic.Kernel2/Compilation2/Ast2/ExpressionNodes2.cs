using System.Collections.Generic;

namespace Magic.Kernel2.Compilation2.Ast2
{
    /// <summary>Base for all expression nodes.</summary>
    public abstract class ExpressionNode2 : AstNode2 { }

    /// <summary>Literal value: "hello", 42, 3.14, true.</summary>
    public sealed class LiteralExpression2 : ExpressionNode2
    {
        public object? Value { get; set; }
        public LiteralKind2 Kind { get; set; }
    }

    public enum LiteralKind2
    {
        String,
        Integer,
        Float,
        Boolean,
        Null
    }

    /// <summary>Variable reference: x, myVar.</summary>
    public sealed class VariableExpression2 : ExpressionNode2
    {
        public string Name { get; set; } = "";
    }

    /// <summary>Member access: obj.Field or obj.Method(...).</summary>
    public sealed class MemberAccessExpression2 : ExpressionNode2
    {
        public ExpressionNode2 Object { get; set; } = null!;
        public string MemberName { get; set; } = "";
    }

    /// <summary>Function/method call expression: Foo(a, b) or obj.Method(a, b).</summary>
    public sealed class CallExpression2 : ExpressionNode2
    {
        public ExpressionNode2 Callee { get; set; } = null!;
        public List<ExpressionNode2> Arguments { get; set; } = new();
        public bool IsAsync { get; set; }
        public bool IsObjectCall { get; set; }
    }

    /// <summary>Binary operation: a + b, a == b, etc.</summary>
    public sealed class BinaryExpression2 : ExpressionNode2
    {
        public ExpressionNode2 Left { get; set; } = null!;
        public string Operator { get; set; } = "";
        public ExpressionNode2 Right { get; set; } = null!;
    }

    /// <summary>Unary operation: !x, -x.</summary>
    public sealed class UnaryExpression2 : ExpressionNode2
    {
        public string Operator { get; set; } = "";
        public ExpressionNode2 Operand { get; set; } = null!;
    }

    /// <summary>Object construction: new Point(1, 2) or Point: { X: 1, Y: 2 }.</summary>
    public sealed class ObjectCreationExpression2 : ExpressionNode2
    {
        public string TypeName { get; set; } = "";
        public List<ExpressionNode2> PositionalArgs { get; set; } = new();
        public List<(string Name, ExpressionNode2 Value)> NamedArgs { get; set; } = new();
        public bool IsInitializerSyntax { get; set; }
    }

    /// <summary>Type instantiation with generic: stream&lt;file&gt;.</summary>
    public sealed class GenericTypeExpression2 : ExpressionNode2
    {
        public string TypeName { get; set; } = "";
        public string TypeArg { get; set; } = "";
    }

    /// <summary>Await expression: await stream1.</summary>
    public sealed class AwaitExpression2 : ExpressionNode2
    {
        public ExpressionNode2 Operand { get; set; } = null!;
        public bool IsObjectAwait { get; set; }
    }

    /// <summary>Lambda expression: lambda { ... }.</summary>
    public sealed class LambdaExpression2 : ExpressionNode2
    {
        public List<string> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>Index access: arr[i].</summary>
    public sealed class IndexExpression2 : ExpressionNode2
    {
        public ExpressionNode2 Object { get; set; } = null!;
        public ExpressionNode2 Index { get; set; } = null!;
    }

    /// <summary>Memory slot reference: [0], [1] — direct slot addressing.</summary>
    public sealed class MemorySlotExpression2 : ExpressionNode2
    {
        public int SlotIndex { get; set; }
    }
}
