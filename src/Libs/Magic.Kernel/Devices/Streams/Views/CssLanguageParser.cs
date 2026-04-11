namespace Magic.Kernel.Devices.Streams.Views
{
    /// <summary>
    /// Parses CSS block strings (from AGI view Render() methods) into a <see cref="CssBlock"/> model.
    /// Handles the <c>css: { selector { property: value; } }</c> sublanguage syntax.
    /// <para>
    /// Supported syntax:
    /// <list type="bullet">
    /// <item>#id { property: value; }</item>
    /// <item>.class { property: value; }</item>
    /// <item>element { property: value; }</item>
    /// <item>Conditional values: display: Error ? block : none;</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class CssLanguageParser
    {
        private readonly string _source;
        private int _pos;

        private CssLanguageParser(string source)
        {
            _source = source ?? "";
            _pos = 0;
        }

        /// <summary>
        /// Parses a CSS block string and returns a <see cref="CssBlock"/> model.
        /// The source may optionally contain the <c>css:</c> prefix (from AGI return statements).
        /// Returns null if the source is empty or contains no valid CSS.
        /// </summary>
        public static CssBlock? Parse(string? cssSource)
        {
            if (string.IsNullOrWhiteSpace(cssSource))
                return null;

            var src = cssSource.Trim();
            if (src.StartsWith("css:", StringComparison.OrdinalIgnoreCase))
                src = src.Substring(4).TrimStart();

            // Strip trailing semicolon.
            if (src.EndsWith(";"))
                src = src.Substring(0, src.Length - 1).TrimEnd();

            if (string.IsNullOrWhiteSpace(src))
                return null;

            // Strip outer braces if the entire block is wrapped in them.
            if (src.StartsWith("{") && src.EndsWith("}"))
                src = src.Substring(1, src.Length - 2).Trim();

            var parser = new CssLanguageParser(src);
            return parser.ParseBlock();
        }

        private CssBlock ParseBlock()
        {
            var block = new CssBlock();

            while (_pos < _source.Length)
            {
                SkipWhitespace();
                if (_pos >= _source.Length)
                    break;

                var rule = ParseRule();
                if (rule != null)
                    block.Rules.Add(rule);
            }

            return block;
        }

        private CssRule? ParseRule()
        {
            SkipWhitespace();
            if (_pos >= _source.Length)
                return null;

            // Read selector (everything up to '{').
            var selectorStart = _pos;
            while (_pos < _source.Length && _source[_pos] != '{')
                _pos++;

            if (_pos >= _source.Length)
                return null;

            var selector = _source.Substring(selectorStart, _pos - selectorStart).Trim();
            if (string.IsNullOrEmpty(selector))
                return null;

            _pos++; // skip '{'

            var rule = new CssRule { Selector = selector };

            // Parse declarations inside the rule block.
            while (_pos < _source.Length && _source[_pos] != '}')
            {
                SkipWhitespace();
                if (_pos >= _source.Length || _source[_pos] == '}')
                    break;

                var decl = ParseDeclaration();
                if (decl != null)
                    rule.Declarations.Add(decl);
            }

            if (_pos < _source.Length && _source[_pos] == '}')
                _pos++; // skip '}'

            return rule;
        }

        private CssDeclaration? ParseDeclaration()
        {
            SkipWhitespace();
            if (_pos >= _source.Length || _source[_pos] == '}')
                return null;

            // Read property name (up to ':').
            var propStart = _pos;
            while (_pos < _source.Length && _source[_pos] != ':' && _source[_pos] != '}' && _source[_pos] != '\n')
                _pos++;

            if (_pos >= _source.Length || _source[_pos] != ':')
                return null;

            var property = _source.Substring(propStart, _pos - propStart).Trim();
            if (string.IsNullOrEmpty(property))
                return null;

            _pos++; // skip ':'
            SkipWhitespace();

            // Read value (up to ';' or end of block), respecting nested braces.
            var valueStart = _pos;
            var depth = 0;
            while (_pos < _source.Length)
            {
                var c = _source[_pos];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (c == ';' && depth == 0)
                    break;
                _pos++;
            }

            var value = _source.Substring(valueStart, _pos - valueStart).Trim();

            if (_pos < _source.Length && _source[_pos] == ';')
                _pos++; // skip ';'

            if (string.IsNullOrEmpty(property))
                return null;

            return new CssDeclaration { Property = property, Value = value };
        }

        private void SkipWhitespace()
        {
            while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
                _pos++;
        }
    }

    /// <summary>A parsed CSS block containing one or more CSS rules.</summary>
    public sealed class CssBlock
    {
        /// <summary>List of CSS rules (selector + declarations).</summary>
        public List<CssRule> Rules { get; set; } = new();

        /// <summary>Renders the CSS block to a formatted CSS string suitable for embedding in a &lt;style&gt; tag.</summary>
        public string RenderToCss()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var rule in Rules)
            {
                sb.AppendLine(rule.RenderToCss());
            }
            return sb.ToString();
        }
    }

    /// <summary>A single CSS rule with a selector and a list of property declarations.</summary>
    public sealed class CssRule
    {
        /// <summary>CSS selector (e.g. "#login", ".container", "body").</summary>
        public string Selector { get; set; } = "";

        /// <summary>Property declarations within the rule.</summary>
        public List<CssDeclaration> Declarations { get; set; } = new();

        /// <summary>Renders the rule to a formatted CSS string.</summary>
        public string RenderToCss()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{Selector} {{");
            foreach (var decl in Declarations)
            {
                sb.AppendLine($"  {decl.Property}: {decl.Value};");
            }
            sb.Append('}');
            return sb.ToString();
        }
    }

    /// <summary>A single CSS property declaration (property: value).</summary>
    public sealed class CssDeclaration
    {
        /// <summary>CSS property name (e.g. "color", "display", "horizontal-position").</summary>
        public string Property { get; set; } = "";

        /// <summary>
        /// CSS value string (e.g. "red", "center", "Error ? block : none").
        /// May contain AGI conditional expressions that are preserved as-is.
        /// </summary>
        public string Value { get; set; } = "";
    }
}
