using Magic.Kernel.Devices.Streams.Drivers;

namespace Magic.Kernel.Devices.Streams.Views
{
    /// <summary>
    /// Represents a view definition parsed from an AGI view type.
    /// A view is a named page served by a site stream, with optional fields, buttons, and a Render() method.
    /// Supports multi-projection returns: <c>return html: &lt;html&gt;...&lt;/html&gt;, css: { ... };</c>
    /// Example AGI:
    /// <code>
    /// Login{} : view {
    ///   Username: field&lt;string&gt;(label: "Username");
    ///   Password: field&lt;string&gt;(type: "password");
    ///   Logon: button&lt;Login_logon&gt;;
    ///   Error: bool;
    ///
    ///   method Render() {
    ///     return html: &lt;html&gt;...&lt;/html&gt;, css: {
    ///       #login { horizontal-position: center; }
    ///     };
    ///   }
    /// }
    /// </code>
    /// </summary>
    public sealed class ViewDefinition
    {
        /// <summary>View name (e.g. "Login"). Used for URL routing: /login, /Login.</summary>
        public string Name { get; set; } = "";

        /// <summary>HTML content returned by the view's Render() method, pre-parsed to an AST.</summary>
        public HtmlNode? RenderResult { get; set; }

        /// <summary>Raw HTML string from Render() if AST parsing is bypassed.</summary>
        public string? RawHtml { get; set; }

        /// <summary>
        /// CSS block returned by the view's Render() method alongside HTML.
        /// When present, the CSS is injected into a &lt;style&gt; tag inside the rendered &lt;head&gt;.
        /// </summary>
        public CssBlock? CssResult { get; set; }

        /// <summary>Fields declared in the view (Username, Password, Error, etc.).</summary>
        public List<ViewField> Fields { get; set; } = new();

        /// <summary>
        /// Button components declared in the view.
        /// Each entry holds the button field name and the optional component type reference.
        /// Example: <c>Logon: button&lt;Login_logon&gt;</c> → ButtonDefinition { Name="Logon", ComponentType="Login_logon" }
        /// </summary>
        public List<ButtonDefinition> Buttons { get; set; } = new();

        /// <summary>Returns the rendered HTML for this view using the <see cref="RenderDriver"/>.</summary>
        public string RenderHtml()
        {
            string htmlBody;
            if (RenderResult != null)
                htmlBody = RenderDriver.RenderToHtml(RenderResult);
            else if (!string.IsNullOrEmpty(RawHtml))
                htmlBody = RawHtml!;
            else
                htmlBody = $"<html><body><h1>{Name}</h1></body></html>";

            // Inject CSS as a <style> block inside <head> if CSS is present.
            if (CssResult != null && CssResult.Rules.Count > 0)
                htmlBody = InjectCss(htmlBody, CssResult.RenderToCss());

            return htmlBody;
        }

        /// <summary>
        /// Injects a CSS &lt;style&gt; block into the HTML document.
        /// If a &lt;head&gt; tag exists, the style is inserted inside it.
        /// Otherwise a &lt;style&gt; tag is prepended to the document.
        /// </summary>
        private static string InjectCss(string html, string css)
        {
            if (string.IsNullOrWhiteSpace(css))
                return html;

            var styleBlock = $"<style>\n{css}</style>";

            // Try to inject inside existing <head>.
            var headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (headIdx >= 0)
            {
                var insertPos = headIdx + 6; // after <head>
                return html.Substring(0, insertPos) + "\n" + styleBlock + "\n" + html.Substring(insertPos);
            }

            // Try to inject before <body>.
            var bodyIdx = html.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
            if (bodyIdx >= 0)
            {
                return html.Substring(0, bodyIdx) + styleBlock + "\n" + html.Substring(bodyIdx);
            }

            // Fallback: prepend to the document.
            return styleBlock + "\n" + html;
        }
    }

    /// <summary>A field declared inside a view (e.g. <c>Username: field&lt;string&gt;(label: "Username")</c>).</summary>
    public sealed class ViewField
    {
        /// <summary>Field name (e.g. "Username", "Password").</summary>
        public string Name { get; set; } = "";

        /// <summary>Field data type (e.g. "string", "bool", "int").</summary>
        public string FieldType { get; set; } = "string";

        /// <summary>Optional display label.</summary>
        public string? Label { get; set; }

        /// <summary>Optional HTML input type override (e.g. "password", "email", "number").</summary>
        public string? InputType { get; set; }
    }

    /// <summary>
    /// A button component declared inside a view.
    /// Buttons may reference a named component type for behavior (click handlers, rendering).
    /// Example: <c>Logon: button&lt;Login_logon&gt;</c>
    /// </summary>
    public sealed class ButtonDefinition
    {
        /// <summary>Button field name in the view (e.g. "Logon").</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Optional component type name that defines this button's behavior.
        /// E.g. "Login_logon" for <c>button&lt;Login_logon&gt;</c>.
        /// Null for simple buttons without a component type.
        /// </summary>
        public string? ComponentType { get; set; }
    }
}
