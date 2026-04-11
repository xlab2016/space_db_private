using System.Net;
using System.Text;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Devices.Streams.Views;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>
    /// HTTP Site server: listens on a configurable port and serves HTML views by route.
    /// Routes requests to registered views by path (case-insensitive).
    /// The first registered view is also served at the root '/' path.
    /// Implements the rendering pipeline:
    ///   DefType (view) → ViewDefinition → HtmlNode AST → RenderDriver → HTML response
    /// </summary>
    public sealed class SiteDriver : IStreamDevice
    {
        private int _port;
        private string _serverName = "site";

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Registered views keyed by lowercase view name (e.g. "login", "dashboard").</summary>
        private readonly Dictionary<string, ViewDefinition> _views = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The default view served at the root '/' path (typically the first registered view).</summary>
        private ViewDefinition? _defaultView;

        private static readonly byte[] EmptyHtmlBytes = Encoding.UTF8.GetBytes("<html></html>");

        public int Port => _port;

        public bool IsListening => _listener?.IsListening == true && _serverTask != null && !_serverTask.IsCompleted;

        public void SetServerName(string name)
        {
            _serverName = string.IsNullOrWhiteSpace(name) ? "site" : name.Trim();
        }

        /// <summary>
        /// Registers view definitions from compiled DefType objects in the executable unit.
        /// View types are identified by having a <see cref="RenderDevice"/> generalization.
        /// When viewTypeNames is specified, only those type names are registered (in order).
        /// Otherwise all types with RenderDevice generalizations are registered.
        /// </summary>
        public void RegisterViewsFromUnit(ExecutableUnit unit, IReadOnlyList<string>? viewTypeNames = null)
        {
            if (unit?.Types == null)
                return;

            IEnumerable<DefType> candidates;
            if (viewTypeNames != null && viewTypeNames.Count > 0)
            {
                // Preserve order from site definition.
                candidates = viewTypeNames
                    .SelectMany(name => unit.Types.Where(t =>
                        string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)))
                    .Distinct();
            }
            else
            {
                // Fall back: any type with a RenderDevice generalization.
                candidates = unit.Types.Where(t =>
                    t.Generalizations.Any(g => g is RenderDevice));
            }

            foreach (var defType in candidates)
            {
                var viewDef = BuildViewDefinition(defType);
                if (viewDef == null)
                    continue;

                _views[viewDef.Name] = viewDef;

                // First view becomes the default (served at '/').
                _defaultView ??= viewDef;

                Console.WriteLine($"[{_serverName}] [site] Registered view '{viewDef.Name}' → /{viewDef.Name.ToLowerInvariant()}");
            }
        }

        /// <summary>
        /// Registers a view definition directly (used when views are provided programmatically
        /// or extracted from rendered method output).
        /// </summary>
        public void RegisterView(ViewDefinition view)
        {
            if (view == null || string.IsNullOrWhiteSpace(view.Name))
                return;
            _views[view.Name] = view;
            _defaultView ??= view;
        }

        /// <summary>Builds a <see cref="ViewDefinition"/> from a compiled <see cref="DefType"/> view type.</summary>
        private static ViewDefinition? BuildViewDefinition(DefType defType)
        {
            if (defType == null)
                return null;

            var viewName = defType.Name?.Trim();
            if (string.IsNullOrEmpty(viewName))
                return null;

            var view = new ViewDefinition { Name = viewName };

            // Extract RenderDevice (if present) to get pre-rendered HTML/CSS.
            var renderDevice = defType.Generalizations.OfType<RenderDevice>().FirstOrDefault();
            if (renderDevice?.ViewDefinition != null)
            {
                view.RenderResult = renderDevice.ViewDefinition.RenderResult;
                view.RawHtml = renderDevice.ViewDefinition.RawHtml;
                view.CssResult = renderDevice.ViewDefinition.CssResult;
                view.Fields = renderDevice.ViewDefinition.Fields;
                view.Buttons = renderDevice.ViewDefinition.Buttons;
                return view;
            }

            // Extract fields and buttons from the type schema using ViewFieldParser.
            foreach (var field in defType.Fields)
            {
                var fieldName = (field.Name ?? "").Trim();
                var typeSpec = (field.Type ?? "").Trim();

                if (string.IsNullOrEmpty(fieldName))
                    continue;

                // Try to parse as a button declaration.
                if (ViewFieldParser.IsButtonSpec(typeSpec))
                {
                    var button = ViewFieldParser.TryParseButton(fieldName, typeSpec);
                    if (button != null)
                    {
                        view.Buttons.Add(button);
                        continue;
                    }
                }

                // Try to parse as a typed view field.
                var viewField = ViewFieldParser.TryParseField(fieldName, typeSpec);
                if (viewField != null)
                {
                    view.Fields.Add(viewField);
                    continue;
                }

                // Fallback: store as raw field.
                view.Fields.Add(new ViewField
                {
                    Name = fieldName,
                    FieldType = string.IsNullOrEmpty(typeSpec) ? "string" : typeSpec
                });
            }

            // Extract Render() method body to build HTML/CSS render result.
            ExtractRenderMethodResult(defType, view);

            return view;
        }

        /// <summary>
        /// Attempts to extract the HTML/CSS render result from the view type's Render() method body.
        /// The Render() method's return value is stored in the type's method metadata; here we look
        /// for the return value string and parse it using <see cref="ViewRenderResult"/>.
        /// </summary>
        private static void ExtractRenderMethodResult(DefType defType, ViewDefinition view)
        {
            // Find the Render() method in the type's method registry.
            var renderMethod = defType.Methods.FirstOrDefault(m =>
                string.Equals(m.Name, "Render", StringComparison.OrdinalIgnoreCase));

            if (renderMethod == null)
                return;

            // The return value string is embedded in the method's ReturnType field
            // when it contains an html:/css: projection literal (V1 compiler convention).
            var returnType = renderMethod.ReturnType ?? "";
            if (!string.IsNullOrWhiteSpace(returnType) &&
                (returnType.StartsWith("html:", StringComparison.OrdinalIgnoreCase) ||
                 returnType.StartsWith("css:", StringComparison.OrdinalIgnoreCase)))
            {
                var renderResult = ViewRenderResult.Parse(returnType);
                if (renderResult != null)
                {
                    view.RenderResult = renderResult.HtmlNode;
                    view.RawHtml = renderResult.RawHtml;
                    view.CssResult = renderResult.CssBlock;
                }
            }
        }

        public void ParseAndApplyConfig(object? config)
        {
            if (config == null) return;

            if (config is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("port", out var portObj))
                    _port = ParseInt(portObj, 8080);
            }

            if (_port <= 0) _port = 8080;
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            if (_port <= 0) _port = 8080;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _listener?.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{_serverName}] [site] Failed to start listener on port {_port}: {ex.Message}");
                return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.IOError, ex.Message));
            }

            Console.WriteLine($"[{_serverName}] [site] Listening on port {_port}");
            if (_defaultView != null)
                Console.WriteLine($"[{_serverName}] [site] Default view: '{_defaultView.Name}' → http://localhost:{_port}/{_defaultView.Name.ToLowerInvariant()}");

            _serverTask = Task.Run(() => ServeLoopAsync(token), CancellationToken.None);

            return Task.FromResult(DeviceOperationResult.Success);
        }

        private async Task ServeLoopAsync(CancellationToken token)
        {
            var listener = _listener;
            if (listener == null) return;

            try
            {
                while (!token.IsCancellationRequested && listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) when (token.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleRequestAsync(ctx), CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[{_serverName}] [site] Server loop error: {ex.Message}");
            }
            finally
            {
                _stoppedTcs.TrySetResult();
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var html = ResolveViewHtml(path);

                var htmlBytes = Encoding.UTF8.GetBytes(html);
                var response = ctx.Response;
                response.StatusCode = 200;
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = htmlBytes.Length;

                await response.OutputStream.WriteAsync(htmlBytes, 0, htmlBytes.Length).ConfigureAwait(false);
                response.OutputStream.Close();
            }
            catch
            {
                // ignore per-request errors
            }
        }

        /// <summary>
        /// Resolves the HTML to serve for a given request path.
        /// Routing rules:
        ///   '/' or '/login' (first view name)  → default view
        ///   '/{viewName}' (case-insensitive)    → matching named view
        ///   No match                            → empty HTML page
        /// </summary>
        private string ResolveViewHtml(string path)
        {
            var normalizedPath = (path ?? "/").Trim('/').Trim();

            // Root path → default (first) view.
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return _defaultView?.RenderHtml() ?? Encoding.UTF8.GetString(EmptyHtmlBytes);
            }

            // Case-insensitive lookup by view name.
            if (_views.TryGetValue(normalizedPath, out var namedView))
                return namedView.RenderHtml();

            // No match.
            return Encoding.UTF8.GetString(EmptyHtmlBytes);
        }

        /// <summary>Waits until the server stops (listener closed or cancelled).</summary>
        public Task AwaitUntilStoppedAsync() => _stoppedTcs.Task;

        public async Task<DeviceOperationResult> CloseAsync()
        {
            _cts?.Cancel();

            try
            {
                _listener?.Close();
            }
            catch { }

            if (_serverTask != null)
            {
                try
                {
                    await _serverTask.ConfigureAwait(false);
                }
                catch { }
            }

            _stoppedTcs.TrySetResult();

            Console.WriteLine($"[{_serverName}] [site] Stopped.");
            return DeviceOperationResult.Success;
        }

        public Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.NotSupported("SiteDriver does not support Read"), Array.Empty<byte>()));

        public Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support Write"));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support Control"));

        public Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => Task.FromResult<(DeviceOperationResult, IStreamChunk?)>((DeviceOperationResult.NotSupported("SiteDriver does not support ReadChunk"), null));

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support WriteChunk"));

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.NotSupported("SiteDriver does not support Move"));

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.NotSupported("SiteDriver does not support Length"), 0L));

        private static int ParseInt(object? obj, int defaultVal)
        {
            if (obj is long l) return (int)l;
            if (obj is int i) return i;
            if (obj is string s && int.TryParse(s, out var v)) return v;
            return defaultVal;
        }
    }
}
