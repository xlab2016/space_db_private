namespace SpaceCompiler.Models
{
    /// <summary>
    /// Represents a block of content (AST node)
    /// Blocks are intermediate nodes between Resources and Fragments
    /// </summary>
    public class Block
    {
        /// <summary>
        /// Block content (concatenation of fragments within max size)
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Block type
        /// </summary>
        public string Type { get; set; } = "block";

        /// <summary>
        /// Order/index in the resource
        /// </summary>
        public int Order { get; set; }

        /// <summary>
        /// Tokens within this block
        /// </summary>
        public List<Token> Tokens { get; set; } = new();

        /// <summary>
        /// Additional metadata for the block
        /// </summary>
        public Dictionary<string, object>? Metadata { get; set; }
    }
}

