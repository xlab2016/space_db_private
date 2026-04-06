using System.Collections.Generic;

namespace Magic.Kernel2.Compilation2.Ast2
{
    /// <summary>
    /// Nested procedure declaration inside a function/procedure body.
    /// Produced by Parser2 when it encounters a nested procedure block.
    /// </summary>
    public sealed class NestedProcedureStatement2 : StatementNode2
    {
        public string Name { get; set; } = "";
        public List<ParameterDeclaration2> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>
    /// Nested function declaration inside a function/procedure body.
    /// Produced by Parser2 when it encounters a nested function block.
    /// </summary>
    public sealed class NestedFunctionStatement2 : StatementNode2
    {
        public string Name { get; set; } = "";
        public List<ParameterDeclaration2> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }
}
