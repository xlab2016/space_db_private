using System.Collections.Generic;

namespace Magic.Kernel2.Compilation2.Ast2
{
    /// <summary>Procedure declaration: procedure Foo(a, b) { ... }</summary>
    public sealed class ProcedureDeclarationNode2 : AstNode2
    {
        public string Name { get; set; } = "";
        public List<ParameterDeclaration2> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>Function declaration: function Foo(a, b) { ... }</summary>
    public sealed class FunctionDeclarationNode2 : AstNode2
    {
        public string Name { get; set; } = "";
        public List<ParameterDeclaration2> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>Type declaration: Point: type { X: int; Y: int; constructor(...) { ... } }</summary>
    public sealed class TypeDeclarationNode2 : AstNode2
    {
        public string Name { get; set; } = "";
        /// <summary>Base type name or "type" for plain type, "class" for class.</summary>
        public string? BaseType { get; set; }
        public bool IsClass { get; set; }
        public List<FieldDeclaration2> Fields { get; set; } = new();
        public List<MethodDeclaration2> Methods { get; set; } = new();
        public List<ConstructorDeclaration2> Constructors { get; set; } = new();
    }

    /// <summary>A formal parameter in a procedure/function/method/constructor.</summary>
    public sealed class ParameterDeclaration2
    {
        public string Name { get; set; } = "";
        public string? TypeSpec { get; set; }
    }

    /// <summary>Field in a type declaration: X: int;</summary>
    public sealed class FieldDeclaration2
    {
        public string Name { get; set; } = "";
        public string? TypeSpec { get; set; }
    }

    /// <summary>Method in a type declaration: method Foo(a: int) { ... }</summary>
    public sealed class MethodDeclaration2 : AstNode2
    {
        public string Name { get; set; } = "";
        public List<ParameterDeclaration2> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>Constructor in a type declaration: constructor(x: int, y: int) { ... }</summary>
    public sealed class ConstructorDeclaration2 : AstNode2
    {
        public List<ParameterDeclaration2> Parameters { get; set; } = new();
        public BlockNode2 Body { get; set; } = new();
    }

    /// <summary>Use directive: use module.path as { function f; procedure p; };</summary>
    public sealed class UseDirectiveNode2 : AstNode2
    {
        public string ModulePath { get; set; } = "";
        public List<UseImport2> Imports { get; set; } = new();
    }

    public sealed class UseImport2
    {
        public string Kind { get; set; } = ""; // "function" or "procedure"
        public string Name { get; set; } = "";
    }
}
