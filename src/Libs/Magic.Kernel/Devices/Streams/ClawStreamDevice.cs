using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>
    /// Claw device: an HTTP socket server that listens on a configurable port.
    /// Protocol: receives JSON, returns JSON.
    /// POST /authenticate {user,password} => {token} with progressive lockout.
    /// POST /entrypoint (Bearer auth) {method, data:{command,...}} dispatches to registered AGI procedures.
    /// Bind AGI procedures with: claw1.methods.add("methodName", "&amp;procedureName").
    /// Inside the bound procedure: var socket1 := socket; provides access to connection context.
    /// </summary>
    public class ClawStreamDevice : DefStream
    {
        private int _port;
        private List<Credential> _credentials = new List<Credential>();
        private readonly ConcurrentDictionary<string, string> _tokens = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, UserLockout> _lockouts = new ConcurrentDictionary<string, UserLockout>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _methodMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;

        private KernelConfiguration? _kernelConfig;
        private ExecutableUnit? _unit;

        /// <summary>Methods registry: methodName => AGI procedure name.</summary>
        public ClawMethodsRegistry Methods { get; }

        public ClawStreamDevice()
        {
            Methods = new ClawMethodsRegistry(_methodMap);
        }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";

            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
                return await HandleOpenAsync(args).ConfigureAwait(false);

            if (string.Equals(name, "methods", StringComparison.OrdinalIgnoreCase))
                return Methods;

            throw new CallUnknownMethodException(name, this);
        }

        private async Task<DeviceOperationResult> HandleOpenAsync(object?[]? args)
        {
            if (args != null && args.Length > 0)
                ParseConfig(args[0]);

            // Capture kernel config and unit from execution context
            _kernelConfig = Magic.Kernel.Interpretation.ExecutionContext.CurrentInterpreter?.Configuration;
            _unit = Magic.Kernel.Interpretation.ExecutionContext.CurrentUnit;

            return await OpenAsync().ConfigureAwait(false);
        }

        private void ParseConfig(object? config)
        {
            if (config == null) return;

            if (config is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("port", out var portObj))
                    _port = ParseInt(portObj, 8080);

                if (dict.TryGetValue("authentication", out var authObj) && authObj is Dictionary<string, object> authDict)
                {
                    if (authDict.TryGetValue("credentials", out var credsObj))
                        _credentials = ParseCredentials(credsObj);
                }
            }
            else if (config is string jsonStr)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("port", out var portEl))
                        _port = portEl.ValueKind == JsonValueKind.Number ? portEl.GetInt32() : 8080;
                    if (root.TryGetProperty("authentication", out var authEl) && authEl.TryGetProperty("credentials", out var credsEl))
                        _credentials = ParseCredentialsFromJson(credsEl);
                }
                catch { }
            }

            if (_port <= 0) _port = 8080;
        }

        private static int ParseInt(object? obj, int defaultVal)
        {
            if (obj is long l) return (int)l;
            if (obj is int i) return i;
            if (obj is string s && int.TryParse(s, out var v)) return v;
            return defaultVal;
        }

        private static List<Credential> ParseCredentials(object? credsObj)
        {
            var result = new List<Credential>();
            if (credsObj is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object> d)
                    {
                        var user = d.TryGetValue("user", out var u) ? u?.ToString() ?? "" : "";
                        var password = d.TryGetValue("password", out var p) ? p?.ToString() ?? "" : "";
                        if (!string.IsNullOrEmpty(user))
                            result.Add(new Credential(user, password));
                    }
                }
            }
            return result;
        }

        private static List<Credential> ParseCredentialsFromJson(JsonElement el)
        {
            var result = new List<Credential>();
            if (el.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in el.EnumerateArray())
            {
                var user = item.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
                var password = item.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(user))
                    result.Add(new Credential(user, password));
            }
            return result;
        }

        public override Task<DeviceOperationResult> OpenAsync()
        {
            if (_port <= 0) _port = 8080;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.Failed, $"Failed to start Claw listener on port {_port}: {ex.Message}"));
            }

            _serverTask = RunServerAsync(_cts.Token);
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public override async Task<DeviceOperationResult> CloseAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_serverTask != null)
            {
                try { await _serverTask.ConfigureAwait(false); } catch { }
            }
            return DeviceOperationResult.Success;
        }

        private async Task RunServerAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener!.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch { continue; }

                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;
                resp.ContentType = "application/json";

                if (req.HttpMethod != "POST")
                {
                    await WriteJsonResponse(resp, 405, new Dictionary<string, object> { ["error"] = "Method Not Allowed" }).ConfigureAwait(false);
                    return;
                }

                var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";

                if (string.Equals(path, "/authenticate", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleAuthenticateAsync(req, resp).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/entrypoint", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleEntrypointAsync(req, resp).ConfigureAwait(false);
                    return;
                }

                await WriteJsonResponse(resp, 404, new Dictionary<string, object> { ["error"] = "Not Found" }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteJsonResponse(ctx.Response, 500, new Dictionary<string, object> { ["error"] = ex.Message }).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private async Task HandleAuthenticateAsync(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBodyAsync(req).ConfigureAwait(false);
            string? user = null;
            string? password = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("user", out var u)) user = u.GetString();
                if (root.TryGetProperty("password", out var p)) password = p.GetString();
            }
            catch
            {
                await WriteJsonResponse(resp, 400, new Dictionary<string, object> { ["error"] = "Invalid JSON" }).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(user))
            {
                await WriteJsonResponse(resp, 400, new Dictionary<string, object> { ["error"] = "user required" }).ConfigureAwait(false);
                return;
            }

            // Check lockout
            if (_lockouts.TryGetValue(user, out var lockout) && lockout.IsLocked())
            {
                var remaining = lockout.RemainingSeconds();
                await WriteJsonResponse(resp, 429, new Dictionary<string, object>
                {
                    ["error"] = "Too many failed attempts. Account locked.",
                    ["retry_after_seconds"] = (object)remaining
                }).ConfigureAwait(false);
                return;
            }

            // Validate credentials
            var found = _credentials.Find(c => string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase) && c.Password == password);
            if (found == null)
            {
                var lo = _lockouts.GetOrAdd(user, _ => new UserLockout());
                lo.RecordFailure();
                await WriteJsonResponse(resp, 401, new Dictionary<string, object> { ["error"] = "Invalid credentials" }).ConfigureAwait(false);
                return;
            }

            if (_lockouts.TryGetValue(user, out var existingLockout))
                existingLockout.Reset();

            var token = GenerateToken();
            _tokens[token] = user;

            await WriteJsonResponse(resp, 200, new Dictionary<string, object> { ["token"] = (object)token }).ConfigureAwait(false);
        }

        private async Task HandleEntrypointAsync(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var authHeader = req.Headers["Authorization"] ?? "";
            string? token = null;
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = authHeader.Substring(7).Trim();

            if (string.IsNullOrEmpty(token) || !_tokens.ContainsKey(token))
            {
                await WriteJsonResponse(resp, 401, new Dictionary<string, object> { ["error"] = "Unauthorized" }).ConfigureAwait(false);
                return;
            }

            var user = _tokens[token];
            var body = await ReadBodyAsync(req).ConfigureAwait(false);

            string? methodName = null;
            object? data = null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out var m)) methodName = m.GetString();
                if (root.TryGetProperty("data", out var d)) data = JsonElementToObject(d);
            }
            catch
            {
                await WriteJsonResponse(resp, 400, new Dictionary<string, object> { ["error"] = "Invalid JSON" }).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(methodName))
            {
                await WriteJsonResponse(resp, 400, new Dictionary<string, object> { ["error"] = "method required" }).ConfigureAwait(false);
                return;
            }

            if (!_methodMap.TryGetValue(methodName, out var procedureName))
            {
                await WriteJsonResponse(resp, 404, new Dictionary<string, object> { ["error"] = $"Method '{methodName}' not found" }).ConfigureAwait(false);
                return;
            }

            // Build socket context for this request
            var socketCtx = new ClawSocketContext(Name, user, token);

            // Build call data passed as procedure argument
            var callData = new Dictionary<string, object>
            {
                ["authentication"] = new Dictionary<string, object>
                {
                    ["isAuthenticated"] = (object)true,
                    ["user"] = (object)user,
                    ["token"] = (object)(token ?? "")
                },
                ["command"] = (object)(data is Dictionary<string, object> dataDict && dataDict.TryGetValue("command", out var cmd) ? cmd?.ToString() ?? "" : ""),
                ["data"] = data ?? (object)new Dictionary<string, object>()
            };

            object? result = null;
            try
            {
                result = await InvokeProcedureAsync(procedureName, callData, socketCtx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJsonResponse(resp, 500, new Dictionary<string, object> { ["error"] = (object)ex.Message }).ConfigureAwait(false);
                return;
            }

            var responseObj = result as Dictionary<string, object> ?? new Dictionary<string, object> { ["result"] = result ?? (object)"ok" };
            await WriteJsonResponse(resp, 200, responseObj).ConfigureAwait(false);
        }

        /// <summary>Invokes a registered AGI procedure in a new interpreter instance sharing the same compiled unit.</summary>
        private async Task<object?> InvokeProcedureAsync(string procedureName, object? data, ClawSocketContext socketCtx)
        {
            if (_unit == null)
                throw new InvalidOperationException("Claw device not opened or no execution unit available.");

            // Set socket context for access inside the procedure via 'socket' keyword
            ClawExecutionContext.CurrentSocket = socketCtx;

            try
            {
                // Create a new interpreter for each request (thread-safe, stateless per-request)
                var interpreter = new Interpreter();
                if (_kernelConfig != null)
                    interpreter.Configuration = _kernelConfig;

                var callInfo = new CallInfo { FunctionName = procedureName };
                callInfo.Parameters["0"] = data;

                var result = await interpreter.InterpreteFromEntryAsync(_unit, procedureName, callInfo).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException($"Procedure '{procedureName}' execution failed.");

                // Extract top-of-stack return value if available
                return interpreter.Stack.Count > 0 ? interpreter.Stack[interpreter.Stack.Count - 1] : null;
            }
            finally
            {
                ClawExecutionContext.CurrentSocket = null;
            }
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
        {
            using var ms = new System.IO.MemoryStream();
            await req.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static async Task WriteJsonResponse(HttpListenerResponse resp, int statusCode, object body)
        {
            resp.StatusCode = statusCode;
            var json = JsonSerializer.Serialize(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            resp.OutputStream.Close();
        }

        private static string GenerateToken()
            => Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "").Replace("+", "-").Replace("/", "_");

        private static object? JsonElementToObject(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.Object => JsonElementToDict(el),
                JsonValueKind.Array => JsonElementToList(el),
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : (object)el.GetDouble(),
                JsonValueKind.True => (object)true,
                JsonValueKind.False => (object)false,
                _ => (object?)null
            };
        }

        private static Dictionary<string, object> JsonElementToDict(JsonElement el)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var prop in el.EnumerateObject())
            {
                var v = JsonElementToObject(prop.Value);
                if (v != null) d[prop.Name] = v;
            }
            return d;
        }

        private static List<object> JsonElementToList(JsonElement el)
        {
            var list = new List<object>();
            foreach (var item in el.EnumerateArray())
            {
                var v = JsonElementToObject(item);
                if (v != null) list.Add(v);
            }
            return list;
        }

        // Unimplemented stream I/O — Claw is a server-mode device
        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"), Array.Empty<byte>()));

        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"));

        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.Success);

        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => Task.FromResult<(DeviceOperationResult, IStreamChunk?)>((DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"), null));

        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"));

        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.Success);

        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.Success, 0L));

        private sealed record Credential(string User, string Password);
    }

    /// <summary>Progressive lockout durations: 10s, 30s, 1m, 5m, 15m, 30m, 1h, 1d, permanent.</summary>
    internal sealed class UserLockout
    {
        private static readonly TimeSpan[] Durations =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(1),
            TimeSpan.MaxValue  // permanent
        };

        private int _failureCount;
        private DateTime _lockedUntil = DateTime.MinValue;

        public void RecordFailure()
        {
            var idx = Math.Min(_failureCount, Durations.Length - 1);
            var duration = Durations[idx];
            _lockedUntil = duration == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow.Add(duration);
            _failureCount++;
        }

        public bool IsLocked() => DateTime.UtcNow < _lockedUntil;

        public long RemainingSeconds()
        {
            if (_lockedUntil == DateTime.MaxValue) return long.MaxValue;
            var remaining = _lockedUntil - DateTime.UtcNow;
            return remaining.Ticks > 0 ? (long)remaining.TotalSeconds + 1 : 0;
        }

        public void Reset()
        {
            _failureCount = 0;
            _lockedUntil = DateTime.MinValue;
        }
    }

    /// <summary>Registry for claw method bindings: external method name => AGI procedure name.</summary>
    public sealed class ClawMethodsRegistry : IDefType
    {
        private readonly ConcurrentDictionary<string, string> _map;

        public long? Index { get; set; }
        public string Name { get; set; } = "methods";
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        public ClawMethodsRegistry(ConcurrentDictionary<string, string> map)
        {
            _map = map;
        }

        public Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";

            if (string.Equals(name, "add", StringComparison.OrdinalIgnoreCase))
            {
                // args[0] = external method name (string), args[1] = AGI procedure name (string, from &call)
                if (args == null || args.Length < 2)
                    throw new ArgumentException("methods.add requires (methodName, &procedureName)");

                var bindName = args[0]?.ToString() ?? "";
                // &call is passed as the string "&call" — strip the & prefix to get the procedure name
                var rawProcRef = args[1]?.ToString() ?? "";
                var procName = rawProcRef.StartsWith("&", StringComparison.Ordinal) ? rawProcRef.Substring(1) : rawProcRef;
                if (!string.IsNullOrEmpty(bindName) && !string.IsNullOrEmpty(procName))
                    _map[bindName] = procName;

                return Task.FromResult<object?>(this);
            }

            throw new CallUnknownMethodException(name, this);
        }

        public Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);
        public Task<object?> Await() => Task.FromResult<object?>(this);
        public Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult<(bool, object?, object?)>((true, null, null));
    }

    /// <summary>Context injected as the 'socket' variable inside a claw-called procedure.</summary>
    public sealed class ClawSocketContext : IDefType
    {
        public long? Index { get; set; }
        public string Name { get; set; }
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        /// <summary>Name of the authenticated user for this connection.</summary>
        public string User { get; }

        /// <summary>Bearer token for this session.</summary>
        public string Token { get; }

        public ClawSocketContext(string serverName, string user, string token)
        {
            Name = serverName;
            User = user;
            Token = token;
        }

        public Task<object?> CallObjAsync(string methodName, object?[] args)
            => throw new CallUnknownMethodException(methodName, this);

        public Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);
        public Task<object?> Await() => Task.FromResult<object?>(this);
        public Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult<(bool, object?, object?)>((true, null, null));
    }

    /// <summary>AsyncLocal context carrying the current socket for a claw request invocation.</summary>
    public static class ClawExecutionContext
    {
        private static readonly AsyncLocal<ClawSocketContext?> _currentSocket = new AsyncLocal<ClawSocketContext?>();

        public static ClawSocketContext? CurrentSocket
        {
            get => _currentSocket.Value;
            set => _currentSocket.Value = value;
        }
    }
}
