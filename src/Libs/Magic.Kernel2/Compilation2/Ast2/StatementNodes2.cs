using System.Collections.Generic;

namespace Magic.Kernel2.Compilation2.Ast2
{
    /// <summary>Base for all statement nodes.</summary>
    public abstract class StatementNode2 : AstNode2
    {
        /// <summary>Original source text of the statement, preserved for V1 lowering fallback.</summary>
        public string? SourceText { get; set; }
    }

    /// <summary>A block of statements: { stmt1; stmt2; ... }</summary>
    public sealed class BlockNode2 : AstNode2
    {
        public List<StatementNode2> Statements { get; set; } = new();
    }

    /// <summary>Variable declaration: var x := expr; OR var x: Type := expr;</summary>
    public sealed class VarDeclarationStatement2 : StatementNode2
    {
        public string VariableName { get; set; } = "";
        public string? ExplicitType { get; set; }
        public ExpressionNode2? Initializer { get; set; }
    }

    /// <summary>Assignment: x := expr;</summary>
    public sealed class AssignmentStatement2 : StatementNode2
    {
        public ExpressionNode2 Target { get; set; } = null!;
        public ExpressionNode2 Value { get; set; } = null!;
    }

    /// <summary>Procedure/function call statement: Foo(a, b);</summary>
    public sealed class CallStatement2 : StatementNode2
    {
        public ExpressionNode2 Callee { get; set; } = null!;
        public List<ExpressionNode2> Arguments { get; set; } = new();
        /// <summary>True for async call (acall).</summary>
        public bool IsAsync { get; set; }
    }

    /// <summary>Return statement: return expr;</summary>
    public sealed class ReturnStatement2 : StatementNode2
    {
        public ExpressionNode2? Value { get; set; }
    }

    /// <summary>If statement: if (cond) { ... } [else { ... }]</summary>
    public sealed class IfStatement2 : StatementNode2
    {
        public ExpressionNode2 Condition { get; set; } = null!;
        public BlockNode2 ThenBlock { get; set; } = new();
        public BlockNode2? ElseBlock { get; set; }
    }

    /// <summary>Switch statement: switch (expr) { case X: { ... } ... }</summary>
    public sealed class SwitchStatement2 : StatementNode2
    {
        public ExpressionNode2 Expression { get; set; } = null!;
        public List<SwitchCaseNode2> Cases { get; set; } = new();
        public BlockNode2? DefaultBlock { get; set; }
    }

    public sealed class SwitchCaseNode2 : AstNode2
    {
        public ExpressionNode2 Pattern { get; set; } = null!;
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>Stream wait for loop: for (var x in stream) { ... }</summary>
    public sealed class StreamWaitForLoop2 : StatementNode2
    {
        public string VariableName { get; set; } = "";
        public ExpressionNode2 Stream { get; set; } = null!;
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>
    /// Raw instruction node — for low-level .agi instructions like:
    /// addvertex, addrelation, push, pop, call, def, etc.
    /// These are produced when the parser encounters direct instruction syntax.
    /// </summary>
    public sealed class InstructionStatement2 : StatementNode2
    {
        public string Opcode { get; set; } = "";
        public List<InstructionParam2> Parameters { get; set; } = new();
    }

    public sealed class InstructionParam2
    {
        public string Name { get; set; } = "";
        public object? Value { get; set; }
        public string? ValueType { get; set; }
    }
}
