namespace Magic.Kernel.Compilation
{
    /// <summary>Типы токенов, выдаваемых сканером.</summary>
    public enum TokenKind
    {
        EndOfInput,
        Identifier,
        Number,
        Float,
        StringLiteral,
        Colon,
        Comma,
        LBracket,
        RBracket,
        LBrace,
        RBrace,
        Semicolon,
    }
}
