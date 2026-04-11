using System.Text.RegularExpressions;

namespace Magic.Kernel.Devices.Streams.Views
{
    /// <summary>
    /// Parses view field and button type specs from AGI view declarations.
    /// <para>
    /// Supported field type specs:
    /// <list type="bullet">
    /// <item><c>field&lt;string&gt;(label: "Username")</c> → ViewField with FieldType="string", Label="Username"</item>
    /// <item><c>field&lt;string&gt;(type: "password")</c> → ViewField with FieldType="string", InputType="password"</item>
    /// <item><c>bool</c> → ViewField with FieldType="bool"</item>
    /// <item><c>button&lt;Login_logon&gt;</c> → ButtonDefinition with ComponentType="Login_logon"</item>
    /// <item><c>button</c> → ButtonDefinition without component type</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ViewFieldParser
    {
        // Matches: field<Type>(key: "value", key2: "value2")
        private static readonly Regex FieldGenericRegex = new(
            @"^field\s*<\s*(?<type>[^>]+)\s*>\s*(?:\((?<params>[^)]*)\))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches: button<ComponentType>
        private static readonly Regex ButtonGenericRegex = new(
            @"^button\s*<\s*(?<component>[^>]+)\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches: key: "value" or key: value in field parameter lists
        private static readonly Regex ParamPairRegex = new(
            @"(?<key>\w+)\s*:\s*(?:""(?<strval>[^""]*)""|(?<val>[^\s,]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Attempts to parse a field type spec into a <see cref="ViewField"/>.
        /// Returns null if the spec is not a field declaration.
        /// </summary>
        public static ViewField? TryParseField(string fieldName, string typeSpec)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(typeSpec))
                return null;

            var spec = typeSpec.Trim().TrimEnd(';').Trim();

            // field<Type>(options) pattern
            var fieldMatch = FieldGenericRegex.Match(spec);
            if (fieldMatch.Success)
            {
                var fieldType = fieldMatch.Groups["type"].Value.Trim();
                var field = new ViewField
                {
                    Name = fieldName.Trim(),
                    FieldType = string.IsNullOrEmpty(fieldType) ? "string" : fieldType
                };

                // Parse optional parameters.
                var paramsText = fieldMatch.Groups["params"].Value;
                if (!string.IsNullOrWhiteSpace(paramsText))
                {
                    foreach (Match paramMatch in ParamPairRegex.Matches(paramsText))
                    {
                        var key = paramMatch.Groups["key"].Value.Trim().ToLowerInvariant();
                        var value = paramMatch.Groups["strval"].Success
                            ? paramMatch.Groups["strval"].Value
                            : paramMatch.Groups["val"].Value;

                        switch (key)
                        {
                            case "label":
                                field.Label = value;
                                break;
                            case "type":
                                field.InputType = value;
                                break;
                        }
                    }
                }

                return field;
            }

            // Plain type names (bool, string, int, etc.) — treat as a simple data field.
            if (IsSimpleTypeName(spec))
            {
                return new ViewField
                {
                    Name = fieldName.Trim(),
                    FieldType = spec.ToLowerInvariant()
                };
            }

            return null;
        }

        /// <summary>
        /// Attempts to parse a button type spec into a <see cref="ButtonDefinition"/>.
        /// Returns null if the spec is not a button declaration.
        /// </summary>
        public static ButtonDefinition? TryParseButton(string fieldName, string typeSpec)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(typeSpec))
                return null;

            var spec = typeSpec.Trim().TrimEnd(';').Trim();

            // button<ComponentType>
            var buttonGenericMatch = ButtonGenericRegex.Match(spec);
            if (buttonGenericMatch.Success)
            {
                return new ButtonDefinition
                {
                    Name = fieldName.Trim(),
                    ComponentType = buttonGenericMatch.Groups["component"].Value.Trim()
                };
            }

            // Plain "button" without component type
            if (string.Equals(spec, "button", StringComparison.OrdinalIgnoreCase))
            {
                return new ButtonDefinition
                {
                    Name = fieldName.Trim(),
                    ComponentType = null
                };
            }

            return null;
        }

        /// <summary>
        /// Determines whether a type spec refers to a button declaration.
        /// </summary>
        public static bool IsButtonSpec(string typeSpec)
        {
            if (string.IsNullOrWhiteSpace(typeSpec))
                return false;
            var spec = typeSpec.Trim().TrimEnd(';').Trim();
            return spec.StartsWith("button", StringComparison.OrdinalIgnoreCase) &&
                   (spec.Length == 6 ||
                    spec[6] == '<' ||
                    char.IsWhiteSpace(spec[6]));
        }

        private static readonly HashSet<string> SimpleTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bool", "boolean", "string", "int", "integer", "long", "float", "double", "decimal",
            "date", "datetime", "time", "any", "object", "void"
        };

        private static bool IsSimpleTypeName(string spec)
            => SimpleTypeNames.Contains(spec.Trim());
    }
}
