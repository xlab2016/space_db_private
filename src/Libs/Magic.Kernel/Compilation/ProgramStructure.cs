using System.Collections.Generic;

namespace Magic.Kernel.Compilation
{
    public class ProgramStructure
    {
        public string Version { get; set; } = "0.0.1";
        public string? ProgramName { get; set; }
        public string? Module { get; set; }
        public List<string>? EntryPoint { get; set; }
        public Dictionary<string, List<string>> Procedures { get; set; } = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> Functions { get; set; } = new Dictionary<string, List<string>>();
    }
}
