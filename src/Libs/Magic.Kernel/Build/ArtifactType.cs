namespace Magic.Kernel.Build
{
    /// <summary>Specifies the type (format) of an AGI artifact.</summary>
    public enum ArtifactType
    {
        /// <summary>AGI source code (.agi file).</summary>
        Agi,

        /// <summary>AGI assembly text (.agiasm file).</summary>
        AgiAsm,

        /// <summary>Compiled binary/JSON artifact (.agic file).</summary>
        Agic
    }
}
