namespace SpaceCompiler.Models
{
    /// <summary>
    /// Token type enumeration
    /// </summary>
    public enum TokenType
    {
        /// <summary>
        /// Word token
        /// </summary>
        WORD = 0,

        /// <summary>
        /// Punctuation token
        /// </summary>
        PUNCTUATION = 1,

        /// <summary>
        /// Number token
        /// </summary>
        NUMBER = 2,

        /// <summary>
        /// Whitespace token
        /// </summary>
        WHITESPACE = 3,

        /// <summary>
        /// Symbol token
        /// </summary>
        SYMBOL = 4,

        /// <summary>
        /// Unknown token type
        /// </summary>
        UNKNOWN = 5
    }
}

