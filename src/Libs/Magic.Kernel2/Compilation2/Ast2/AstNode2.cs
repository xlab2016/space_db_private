namespace Magic.Kernel2.Compilation2.Ast2
{
    /// <summary>Base class for all AST nodes in Magic compiler 2.0.</summary>
    public abstract class AstNode2
    {
        /// <summary>1-based source line number for diagnostics and debug info. 0 = not assigned.</summary>
        public int SourceLine { get; set; }
    }
}
