namespace Magic.Kernel.Build
{
    /// <summary>Represents an AGI execution artifact with its type and content.</summary>
    public class Artifact
    {
        /// <summary>Name of the artifact (typically the file name without extension).</summary>
        public string? Name { get; set; }

        /// <summary>Namespace of the artifact.</summary>
        public string? Namespace { get; set; }

        /// <summary>Type/format of the artifact.</summary>
        public ArtifactType Type { get; set; } = ArtifactType.Agi;

        /// <summary>
        /// Content of the artifact. For <see cref="ArtifactType.Agi"/> and
        /// <see cref="ArtifactType.AgiAsm"/> this is the source/assembly text.
        /// For <see cref="ArtifactType.Agic"/> this is the path to the compiled file.
        /// </summary>
        public string? Body { get; set; }
    }
}
