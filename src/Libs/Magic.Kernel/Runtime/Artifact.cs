namespace Magic.Kernel.Build
{
    /// <summary>Describes the type of an artifact to be compiled or executed.</summary>
    public enum ArtifactType
    {
        /// <summary>AGI source code (text).</summary>
        Agi,

        /// <summary>AGIASM assembly text.</summary>
        AgiAsm,

        /// <summary>Compiled binary .agic file (path stored in Body).</summary>
        Agic
    }

    /// <summary>Represents a runnable artifact: a named unit of AGI code or compiled binary.</summary>
    public class Artifact
    {
        /// <summary>Display name / identifier (typically the file name without extension).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional namespace/scope for the artifact.</summary>
        public string Namespace { get; set; } = string.Empty;

        /// <summary>Kind of artifact: AGI source, AGIASM text, or compiled Agic path.</summary>
        public ArtifactType Type { get; set; } = ArtifactType.Agi;

        /// <summary>
        /// The content of the artifact.
        /// For <see cref="ArtifactType.Agi"/> and <see cref="ArtifactType.AgiAsm"/> this is source text.
        /// For <see cref="ArtifactType.Agic"/> this is the file path to the compiled binary.
        /// </summary>
        public string? Body { get; set; }
    }
}
