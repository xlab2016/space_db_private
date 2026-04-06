using System.Collections.Generic;

namespace Magic.Kernel2.Compilation2.Ast2
{
    /// <summary>
    /// Top-level program node. Produced by <see cref="Magic.Kernel2.Compilation2.Parser2"/>.
    /// This is the complete, fully-parsed AST — no raw text stored for deferred lowering.
    /// </summary>
    public sealed class ProgramNode2 : AstNode2
    {
        public string Version { get; set; } = "0.0.1";
        public string? ProgramName { get; set; }
        public string? Module { get; set; }
        public string? System { get; set; }

        /// <summary>Top-level type declarations (Point: type { ... }).</summary>
        public List<TypeDeclarationNode2> TypeDeclarations { get; set; } = new();

        /// <summary>Top-level procedure declarations.</summary>
        public List<ProcedureDeclarationNode2> Procedures { get; set; } = new();

        /// <summary>Top-level function declarations.</summary>
        public List<FunctionDeclarationNode2> Functions { get; set; } = new();

        /// <summary>Entrypoint body statements.</summary>
        public BlockNode2? EntryPoint { get; set; }

        /// <summary>Use directives for module imports.</summary>
        public List<UseDirectiveNode2> UseDirectives { get; set; } = new();
    }
}
