namespace Magic.Kernel.Devices.Streams.Views
{
    /// <summary>
    /// Holds the result of parsing a view Render() method's return value.
    /// Supports multi-projection returns: <c>return html: &lt;html&gt;...&lt;/html&gt;, css: { ... };</c>
    /// <para>
    /// The AGI syntax allows returning a comma-separated list of named projections:
    /// <code>
    /// return html: &lt;html&gt;...&lt;/html&gt;, css: {
    ///   #login { horizontal-position: center; }
    /// };
    /// </code>
    /// </para>
    /// </summary>
    public sealed class ViewRenderResult
    {
        /// <summary>Parsed HTML AST from the <c>html:</c> projection.</summary>
        public HtmlNode? HtmlNode { get; set; }

        /// <summary>Raw HTML string if AST parsing was bypassed.</summary>
        public string? RawHtml { get; set; }

        /// <summary>Parsed CSS block from the <c>css:</c> projection (if present).</summary>
        public CssBlock? CssBlock { get; set; }

        /// <summary>
        /// Parses a return value string from a view Render() method.
        /// Handles single projection (<c>html: &lt;html&gt;...&lt;/html&gt;</c>) and
        /// multi-projection (<c>html: ..., css: { ... }</c>) return values.
        /// </summary>
        public static ViewRenderResult? Parse(string? returnValue)
        {
            if (string.IsNullOrWhiteSpace(returnValue))
                return null;

            var src = returnValue.Trim();

            // Strip trailing semicolon.
            if (src.EndsWith(";"))
                src = src.Substring(0, src.Length - 1).TrimEnd();

            if (string.IsNullOrWhiteSpace(src))
                return null;

            var result = new ViewRenderResult();

            // Split into projections by "html:", "css:", etc. prefixes.
            // Use a state machine to split at top-level commas between projections.
            var projections = SplitProjections(src);

            foreach (var proj in projections)
            {
                var p = proj.Trim();
                if (p.StartsWith("html:", StringComparison.OrdinalIgnoreCase))
                {
                    var htmlContent = p.Substring(5).TrimStart();
                    result.HtmlNode = HtmlLanguageParser.Parse(htmlContent);
                    if (result.HtmlNode == null)
                        result.RawHtml = htmlContent;
                }
                else if (p.StartsWith("css:", StringComparison.OrdinalIgnoreCase))
                {
                    var cssContent = p.Substring(4).TrimStart();
                    result.CssBlock = CssLanguageParser.Parse(cssContent);
                }
            }

            if (result.HtmlNode == null && result.RawHtml == null && result.CssBlock == null)
                return null;

            return result;
        }

        /// <summary>
        /// Splits a multi-projection return value into individual projections.
        /// Splits at top-level commas that are immediately followed by a projection keyword
        /// (html:, css:, json:, etc.), respecting nested braces and angle brackets.
        /// </summary>
        private static List<string> SplitProjections(string src)
        {
            var projections = new List<string>();
            var depth = 0; // brace depth
            var start = 0;

            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                if (c == '{' || c == '<') depth++;
                else if (c == '}' || c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    // Check if the next non-whitespace content starts with a projection keyword.
                    var rest = src.Substring(i + 1).TrimStart();
                    if (IsProjectionKeyword(rest))
                    {
                        var chunk = src.Substring(start, i - start).Trim();
                        if (!string.IsNullOrWhiteSpace(chunk))
                            projections.Add(chunk);
                        start = i + 1;
                    }
                }
            }

            // Add the last projection.
            var last = src.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(last))
                projections.Add(last);

            // If no split occurred and the source starts with a projection keyword, treat it as a single projection.
            if (projections.Count == 0)
                projections.Add(src);

            return projections;
        }

        private static readonly string[] ProjectionKeywords = { "html:", "css:", "json:", "xml:", "text:" };

        private static bool IsProjectionKeyword(string src)
        {
            foreach (var keyword in ProjectionKeywords)
            {
                if (src.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
