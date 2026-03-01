using Magic.Kernel.Core.OS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace Magic.Kernel.Core
{
    /// <summary>Global Space root path: from SPACE_PATH env var, default c:/Space.</summary>
    public static class SpaceEnvironment
    {
        private const string DefaultPath = OSSystem.SpaceDefaultPath;
        private static readonly string PathValue = ResolvePath();

        private static Dictionary<string, JsonElement>? _configuration;
        private static readonly object _configLock = new object();

        /// <summary>Root directory for Space (vault.json, samples, etc.).</summary>
        public static string Path => PathValue;

        /// <summary>Configuration loaded from Space/dev.json (debug) or Space/release.json (release). Valid after StartKernel().</summary>
        public static IReadOnlyDictionary<string, JsonElement> Configuration => (IReadOnlyDictionary<string, JsonElement>?)_configuration ?? new Dictionary<string, JsonElement>();

        /// <summary>Инициализация Space: считывает конфиг (dev.json в отладке, release.json в релизе).</summary>
        public static void StartKernel()
        {
            if (_configuration != null)
                return;
            lock (_configLock)
            {
                if (_configuration != null)
                    return;
                var fileName = Debugger.IsAttached ? "dev.json" : "release.json";
                var path = GetFilePath(fileName);
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    using var doc = JsonDocument.Parse(stream);
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        dict[prop.Name] = prop.Value.Clone();
                    _configuration = dict;
                }
                else
                    _configuration = new Dictionary<string, JsonElement>();
            }
        }

        private static string ResolvePath()
        {
            var env = Environment.GetEnvironmentVariable("SPACE_PATH");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim().Replace('\\', '/').TrimEnd('/');
            return DefaultPath;
        }

        /// <summary>Full path for a file under Space root (e.g. vault.json).</summary>
        public static string GetFilePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return PathValue;
            var normalized = relativePath.Trim().Replace('\\', '/').TrimStart('/');
            return string.IsNullOrEmpty(normalized) ? PathValue : System.IO.Path.Combine(PathValue, normalized).Replace('\\', '/');
        }
    }
}
