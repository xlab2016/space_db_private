namespace SpaceCompiler.Models
{
    /// <summary>
    /// Represents a token extracted from text
    /// </summary>
    public class Token
    {
        /// <summary>
        /// Token text content
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Token type
        /// </summary>
        public TokenType Type { get; set; } = TokenType.WORD;

        /// <summary>
        /// Global index in the file (character position)
        /// </summary>
        public int GlobalIndex { get; set; }

        /// <summary>
        /// Row number (line number, 0-based)
        /// </summary>
        public int Row { get; set; }

        /// <summary>
        /// Column number (character position in line, 0-based)
        /// </summary>
        public int Column { get; set; }

        /// <summary>
        /// Order/index in the tokenization sequence
        /// </summary>
        public int Order { get; set; }

        /// <summary>
        /// Additional metadata for the token (statistics, semantic analysis, etc.)
        /// </summary>
        public Dictionary<string, object>? Metadata { get; set; }
    }
}

