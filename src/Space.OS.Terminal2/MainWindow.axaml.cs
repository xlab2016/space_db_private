using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Magic.Kernel;
using Magic.Kernel.Compilation;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Runtime;
using Magic.Kernel.Terminal.Models;
using Magic.Kernel.Terminal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpaceDb.Services;

namespace Space.OS.Terminal2;

public sealed class DebuggerMemoryRow
{
    public string Layer { get; set; } = "";
    public long Slot { get; set; }
    public string TypeName { get; set; } = "";
    public string Value { get; set; } = "";
    public List<InterpreterInspectNode>? InspectTreeRoots { get; set; }
}

public sealed class DebuggerStackRow
{
    public int Index { get; set; }
    public string TopMark { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string Value { get; set; } = "";
    public List<InterpreterInspectNode>? InspectTreeRoots { get; set; }
}

public partial class MainWindow : Window
{
    private readonly TerminalWorkspaceService _workspace = new();
    private readonly TerminalMonitorService _monitor = new();
    private readonly SimulatorRunService _runService = new();
    private readonly string _appSettingsPath;
    private readonly string _spaceConfigPath;
    private readonly string _vaultPath;
    private bool _sidebarCollapsed;
    private readonly List<RuntimeExecutionRecord> _executionRecords = [];
    private IHost? _host;
    private MagicKernel? _kernel;

    private readonly List<ExecutionUnitItem> _executionUnits = [];
    private readonly List<VaultItem> _vaultItems = [];
    private readonly List<VaultStoreRow> _vaultRows = [];
    private readonly List<string> _consoleLines = [];
    private readonly List<string> _consoleIoLines = [];
    private readonly DispatcherTimer _consoleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly MagicAgiColorizer _codeColorizer = new();
    private readonly MagicAgiasmColorizer _asmColorizer = new();
    private readonly DispatcherTimer _codeHighlightTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, HashSet<int>> _agiBreakpointsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _asmBreakpoints = new();
    private InterpreterDebugSession? _activeDebugSession;
    private InterpreterDebugSession? _attachDebugSession;
    private Interpreter? _attachInterpreter;
    private Interpreter? _editorDebugInterpreter;
    private QueuedTextReader? _editorDebugStdin;
    private int _attachInstanceIndex;
    private int _codeEditorAttachInstanceCap = 8;
    private int _debugHighlightDocumentLine;
    private string? _debugHighlightAgiPath;
    private int _debugHighlightAsmLine;
    private int _monitorDebugRefreshThrottle;
    private readonly AgiDebugCurrentLineRenderer _debugCurrentLineRenderer;
    private string? _codeEditorPath;
    private int _asmRefreshGeneration;
    private readonly List<LinkedModuleEditorHost> _linkedModuleEditors = [];
    private bool _codeEditorDebuggerSplitOpen = true;
    private bool _codeEditorEnvironmentOpen = true;

    private enum DebuggerMemoryLayerKind { All, Global, Local, Frame }
    private DebuggerMemoryLayerKind _debuggerMemoryLayerKind = DebuggerMemoryLayerKind.All;
    private List<DebuggerMemoryRow>? _debuggerMemoryRowsFull;
    private readonly HttpClient _httpScratchClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private long _lastGlobalDebugFnKeyTick;
    private Key _lastGlobalDebugFnKey;

    private const string LinkedModuleMarkerPrefix = "// --- Space.Terminal linked module: ";
    private const string LinkedModuleMarkerSuffix = " ---";

    private sealed record LinkedModuleEditorHost(string FilePath, TextEditor Editor, MagicAgiColorizer Colorizer, TabItem Tab);

    public MainWindow()
    {
        InitializeComponent();

        HttpScratchEditor.Options.AllowScrollBelowDocument = false;
        HttpScratchEditor.Document.Text =
            "# Первый блок до ### — запрос по Send. @name = значение и {{name}} в URL/теле.\r\n" +
            "@host = http://localhost:8080/\r\n\r\n" +
            "GET {{host}}health\r\n";

        _debugCurrentLineRenderer = new AgiDebugCurrentLineRenderer(() => MainEditorDebugHighlightLine());
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_debugCurrentLineRenderer);
        CodeEditor.TextArea.LeftMargins.Insert(0, new AgiBreakpointMargin(() => _codeEditorPath ?? string.Empty, _agiBreakpointsByPath, SyncBreakpointsToActiveDebugSession));
        CodeAsmEditor.TextArea.LeftMargins.Insert(0, new AsmBreakpointMargin(_asmBreakpoints, SyncBreakpointsToActiveDebugSession));
        CodeAsmEditor.TextArea.TextView.BackgroundRenderers.Add(new AgiDebugCurrentLineRenderer(() => _debugHighlightAsmLine));
        CodeEditor.TextArea.TextView.LineTransformers.Add(_codeColorizer);
        CodeEditor.Options.AllowScrollBelowDocument = false;
        CodeAsmEditor.IsReadOnly = true;
        CodeAsmEditor.Options.AllowScrollBelowDocument = false;
        CodeAsmEditor.TextArea.TextView.LineTransformers.Add(_asmColorizer);
        _codeHighlightTimer.Tick += CodeHighlightTimer_Tick;
        CodeEditor.Document.TextChanged += OnAnyAgiSourceDocument_TextChanged;

        _appSettingsPath = ResolveProjectFile("src", "Space.OS.Simulator", "appsettings.json");
        _spaceConfigPath = SpaceEnvironment.GetFilePath("dev.json");
        _vaultPath = ResolveVaultPath();
        LoadAll();
        _consoleTimer.Tick += ConsoleTimer_Tick;
        _consoleTimer.Start();
        Loaded += MainWindow_Loaded;
    }

    private void LoadAll()
    {
        LoadExecutionUnits();
        LoadVaultItems();
        LoadStorageTree();
        RefreshMonitor("monitor-exes");
    }

    private static string ResolveProjectFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, Path.Combine(parts));
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
    }

    private static string ResolveVaultPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "design", "Space", "vault.json");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "vault.json"));
    }

    private void LoadExecutionUnits()
    {
        _executionUnits.Clear();
        _executionUnits.AddRange(_workspace.LoadExecutionUnits(_spaceConfigPath));
        ExecutionUnitsGrid.ItemsSource = null;
        ExecutionUnitsGrid.ItemsSource = _executionUnits;
    }

    private void LoadVaultItems()
    {
        _vaultItems.Clear();
        _vaultItems.AddRange(_workspace.LoadVaultItems(_vaultPath));
        VaultItemsGrid.ItemsSource = null;
        VaultItemsGrid.ItemsSource = _vaultItems;
        if (_vaultItems.Count > 0)
            VaultItemsGrid.SelectedIndex = 0;
        else
        {
            _vaultRows.Clear();
            VaultStoreGrid.ItemsSource = null;
        }
    }

    private void RefreshVaultRows()
    {
        _vaultRows.Clear();
        if (VaultItemsGrid.SelectedItem is VaultItem selected)
            _vaultRows.AddRange(_workspace.ToRows(selected));
        VaultStoreGrid.ItemsSource = null;
        VaultStoreGrid.ItemsSource = _vaultRows;
    }

    private string CurrentMonitorTag()
    {
        if (NavigationTree.SelectedItem is TreeViewItem t && t.Tag is string tag && tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase))
            return tag;
        return "monitor-exes";
    }

    private void RefreshMonitor(string tag)
    {
        var snapshot = _monitor.CreateSnapshot(_kernel, _executionRecords);
        List<MonitorRow> rows = tag switch
        {
            "monitor-streams" => snapshot.Streams,
            "monitor-drivers" => snapshot.Drivers,
            "monitor-connections" => snapshot.Connections,
            _ => snapshot.Exes
        };

        MonitorGrid.ItemsSource = null;
        MonitorGrid.ItemsSource = rows;
        MonitorStatsText.Text = $"exes={snapshot.Exes.Count}; streams={snapshot.Streams.Count}; drivers={snapshot.Drivers.Count}; connections={snapshot.Connections.Count}; devices={snapshot.TotalDevices}";
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        RootShellGrid.ColumnDefinitions[0].Width = _sidebarCollapsed ? new GridLength(56) : new GridLength(290);
    }

    private void NavigationTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationTree.SelectedItem is not TreeViewItem selected || selected.Tag is not string tag)
            return;

        ExecutionUnitsPanel.IsVisible = tag == "space-executionunits";
        VaultPanel.IsVisible = tag == "space-vault";
        StoragePanel.IsVisible = tag == "space-storage";
        CodeEditorPanel.IsVisible = tag == "space-code-editor";
        MonitorPanel.IsVisible = tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase);
        ConsolePanel.IsVisible = tag == "console-logs";

        SectionTitle.Text = selected.Header?.ToString() ?? "Space.OS.Terminal2";
        SectionMeta.Text = tag switch
        {
            "space-executionunits" => _spaceConfigPath,
            "space-vault" => _vaultPath,
            "space-code-editor" => _codeEditorPath ?? string.Empty,
            _ => "monitor"
        };

        if (tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase))
            RefreshMonitor(tag);
        else if (tag == "console-logs")
            DrainConsole();
        else if (tag == "space-storage")
            LoadStorageTree();
        else if (tag == "space-code-editor")
            ReloadCodeEditor();
    }

    private void ExecutionUnitsReload_Click(object? sender, RoutedEventArgs e) => LoadExecutionUnits();

    private void ExecutionUnitsAdd_Click(object? sender, RoutedEventArgs e)
    {
        _executionUnits.Add(new ExecutionUnitItem { Path = "samples/new_unit.agi", InstanceCount = 1 });
        ExecutionUnitsGrid.ItemsSource = null;
        ExecutionUnitsGrid.ItemsSource = _executionUnits;
    }

    private void ExecutionUnitsDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (ExecutionUnitsGrid.SelectedItem is ExecutionUnitItem item)
        {
            _executionUnits.Remove(item);
            ExecutionUnitsGrid.ItemsSource = null;
            ExecutionUnitsGrid.ItemsSource = _executionUnits;
        }
    }

    private void ExecutionUnitsSave_Click(object? sender, RoutedEventArgs e)
    {
        _workspace.SaveExecutionUnits(_spaceConfigPath, _executionUnits);
        SectionMeta.Text = $"saved: {_spaceConfigPath}";
    }

    private void ExecutionUnitsRowEdit_Click(object? sender, RoutedEventArgs e)
    {
        // DataGrid cell template controls often have no DataContext in Avalonia; walk to row/cell.
        if (!TryFindDataContextInVisualAncestors<ExecutionUnitItem>(sender, out var item) || item == null)
            return;
        if (string.IsNullOrWhiteSpace(item.Path))
            return;
        var fullPath = SpaceEnvironment.GetFilePath(item.Path);
        OpenCodeEditor(fullPath, Math.Max(1, item.InstanceCount));
        SelectNavigationTag("space-code-editor");
    }

    private static bool TryFindDataContextInVisualAncestors<T>(object? sender, out T? value) where T : class
    {
        value = null;
        if (sender is not Visual visual)
            return false;
        foreach (var v in visual.GetVisualAncestors().Prepend(visual))
        {
            if (v is StyledElement se && se.DataContext is T found)
            {
                value = found;
                return true;
            }
        }

        return false;
    }

    private void StorageReload_Click(object? sender, RoutedEventArgs e) => LoadStorageTree();
    private void CodeEditorReload_Click(object? sender, RoutedEventArgs e) => ReloadCodeEditor();

    private void CodeEditorSave_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_codeEditorPath))
        {
            SectionMeta.Text = "no file path";
            return;
        }

        try
        {
            if (_linkedModuleEditors.Count > 0)
            {
                if (!TrySaveAllOpenAgiFiles(out var tabErr))
                {
                    SectionMeta.Text = $"save failed: {tabErr}";
                    return;
                }
                SectionMeta.Text = $"saved (tabs): {_codeEditorPath}";
                return;
            }

            if (TryPersistLinkedCodeEditorIfApplicable(out var linkErr))
            {
                SectionMeta.Text = $"saved (bundle): {_codeEditorPath}";
                return;
            }

            if (linkErr != null)
            {
                SectionMeta.Text = $"save failed: {linkErr}";
                return;
            }

            File.WriteAllText(_codeEditorPath, CodeEditor.Document.Text);
            SectionMeta.Text = $"saved: {_codeEditorPath}";
        }
        catch (Exception ex)
        {
            SectionMeta.Text = $"save failed: {ex.Message}";
        }
    }

    private void SyncBreakpointsToActiveDebugSession()
    {
        _attachDebugSession?.ReplaceBreakpointsFrom(_agiBreakpointsByPath, _asmBreakpoints);
        _activeDebugSession?.ReplaceBreakpointsFrom(_agiBreakpointsByPath, _asmBreakpoints);
    }

    private InterpreterDebugSession? CurrentStepSession => _attachDebugSession ?? _activeDebugSession;

    private Interpreter? GetInterpreterForDebugger()
        => CurrentStepSession?.PausedInterpreter ?? _attachInterpreter ?? _editorDebugInterpreter;

    private void CodeEditorDebugger_Click(object? sender, RoutedEventArgs e)
    {
        ApplyCodeEditorDebuggerLayout(!_codeEditorDebuggerSplitOpen);
        if (_codeEditorDebuggerSplitOpen)
            RefreshCodeEditorDebuggerPanel();
    }

    private void CodeEditorEnvironment_Click(object? sender, RoutedEventArgs e)
    {
        if (CodeEditorEnvironmentPanel.IsVisible)
            ApplyCodeEditorEnvironmentLayout(false);
        else
        {
            ApplyCodeEditorEnvironmentLayout(true);
            SyncEnvironmentPanelsFromBuffers();
        }
    }

    private void ApplyCodeEditorDebuggerLayout(bool open)
    {
        _codeEditorDebuggerSplitOpen = open;
        var cols = CodeEditorMainSplitGrid.ColumnDefinitions;
        if (open)
        {
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[1].Width = new GridLength(6);
            cols[2].MinWidth = 180;
            if (cols[2].Width.Value <= 0.01)
                cols[2].Width = new GridLength(280);
            CodeEditorDebuggerColumnSplitter.IsVisible = true;
            CodeEditorDebuggerPanel.IsVisible = true;
        }
        else
        {
            cols[1].Width = new GridLength(0);
            cols[2].MinWidth = 0;
            cols[2].Width = new GridLength(0);
            CodeEditorDebuggerColumnSplitter.IsVisible = false;
            CodeEditorDebuggerPanel.IsVisible = false;
        }
    }

    private void ApplyCodeEditorEnvironmentLayout(bool open)
    {
        _codeEditorEnvironmentOpen = open;
        var rows = CodeEditorPanel.RowDefinitions;
        if (open)
        {
            rows[1].Height = new GridLength(2, GridUnitType.Star);
            rows[1].MinHeight = 120;
            rows[2].Height = new GridLength(6);
            rows[2].MinHeight = 6;
            rows[3].MinHeight = 140;
            rows[3].Height = new GridLength(1, GridUnitType.Star);
            CodeEditorEnvironmentSplitter.IsVisible = true;
            CodeEditorEnvironmentPanel.IsVisible = true;
        }
        else
        {
            rows[2].Height = new GridLength(0);
            rows[2].MinHeight = 0;
            rows[3].Height = new GridLength(0);
            rows[3].MinHeight = 0;
            rows[1].Height = new GridLength(1, GridUnitType.Star);
            CodeEditorEnvironmentSplitter.IsVisible = false;
            CodeEditorEnvironmentPanel.IsVisible = false;
        }
    }

    private void SyncEnvironmentPanelsFromBuffers()
    {
        CodeEditorEnvLogsTextBox.Text = string.Join(Environment.NewLine, _consoleLines);
        CodeEditorEnvConsoleTextBox.Text = string.Join(Environment.NewLine, _consoleIoLines);
        RebuildEnvironmentNetworksText();
    }

    private void RebuildEnvironmentNetworksText()
    {
        CodeEditorEnvNetworksTextBox.Text = string.Join(Environment.NewLine,
            _consoleLines.Where(IsDeviceNetworkLogLine));
    }

    private static bool IsDeviceNetworkLogLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        if (!line.Contains("[claw]", StringComparison.OrdinalIgnoreCase))
            return false;
        return line.Contains("request", StringComparison.OrdinalIgnoreCase)
               || line.Contains("/entrypoint", StringComparison.OrdinalIgnoreCase)
               || line.Contains("/authenticate", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Listening on", StringComparison.OrdinalIgnoreCase)
               || line.Contains("started.", StringComparison.OrdinalIgnoreCase)
               || line.Contains("stopped.", StringComparison.OrdinalIgnoreCase);
    }

    private async void CodeEditorHttpSend_Click(object? sender, RoutedEventArgs e)
    {
        var scratch = HttpScratchEditor.Document.Text ?? string.Empty;
        if (!TryBuildHttpRequestFromScratch(scratch, out var request, out var error))
        {
            HttpScratchResponseTextBox.Text = error ?? "Parse error.";
            return;
        }

        using (request)
        {
            try
            {
                using var response = await _httpScratchClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(true);
                var sb = new StringBuilder();
                sb.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                foreach (var h in response.Headers)
                    sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                if (response.Content is { } c)
                {
                    foreach (var h in c.Headers)
                        sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                    var body = await c.ReadAsStringAsync().ConfigureAwait(true);
                    sb.AppendLine();
                    sb.AppendLine(body);
                }
                HttpScratchResponseTextBox.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                HttpScratchResponseTextBox.Text = ex.ToString();
            }
        }
    }

    private void CodeEditorHttpClearResponse_Click(object? sender, RoutedEventArgs e)
        => HttpScratchResponseTextBox.Clear();

    private static bool TryBuildHttpRequestFromScratch(string scratch, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HttpRequestMessage? request, out string? error)
    {
        request = null;
        error = null;
        var blocks = Regex.Split(scratch ?? "", @"^\s*###\s*$", RegexOptions.Multiline);
        string? picked = null;
        foreach (var b in blocks)
        {
            var cleaned = ReplaceLineComments(b).Trim();
            if (!string.IsNullOrEmpty(cleaned))
            {
                picked = cleaned;
                break;
            }
        }

        var block = picked ?? ReplaceLineComments(scratch ?? "").Trim();
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawLines = block.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var bodyLines = new List<string>();
        var mode = 0;
        string? method = null;
        string? url = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in rawLines)
        {
            var t = line.Trim();
            if (mode == 0)
            {
                if (string.IsNullOrEmpty(t)) continue;
                var vm = Regex.Match(t, @"^\s*@(\w+)\s*=\s*(.+)$");
                if (vm.Success)
                {
                    vars[vm.Groups[1].Value] = vm.Groups[2].Value.Trim();
                    continue;
                }
                var rm = Regex.Match(t, @"^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+(\S+)(\s+HTTP/[\d.]+)?$", RegexOptions.IgnoreCase);
                if (rm.Success)
                {
                    method = rm.Groups[1].Value.ToUpperInvariant();
                    url = rm.Groups[2].Value;
                    mode = 1;
                    continue;
                }
                error = $"Ожидается @name = значение или строка METHOD url: {t}";
                return false;
            }
            else if (mode == 1)
            {
                if (string.IsNullOrEmpty(t)) { mode = 2; continue; }
                var hm = Regex.Match(t, @"^([^:\s]+)\s*:\s*(.*)$");
                if (hm.Success && !Regex.IsMatch(t, @"^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+", RegexOptions.IgnoreCase))
                {
                    headers[hm.Groups[1].Value.Trim()] = hm.Groups[2].Value.TrimEnd();
                    continue;
                }
                error = $"Не ожиданная строка в заголовках: {t}";
                return false;
            }
            else
                bodyLines.Add(line);
        }

        if (method == null || string.IsNullOrWhiteSpace(url))
        {
            error = "Укажите строку запроса: METHOD url (например GET http://localhost:8080/health).";
            return false;
        }

        url = ApplyScratchVars(url, vars);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = $"Некорректный URL после подстановок: {url}";
            return false;
        }

        var bodyText = string.Join(Environment.NewLine, bodyLines).Trim();
        bodyText = ApplyScratchVars(bodyText, vars);
        foreach (var kv in headers.Keys.ToList())
            headers[kv] = ApplyScratchVars(headers[kv], vars);

        request = new HttpRequestMessage(new HttpMethod(method), uri);
        string? contentType = null;
        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var kv in headers)
        {
            if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            { contentType = kv.Value; continue; }
            headerPairs.Add(kv);
        }
        if (!string.IsNullOrEmpty(bodyText))
        {
            var ct = string.IsNullOrEmpty(contentType) ? "application/json" : contentType;
            request.Content = new StringContent(bodyText, Encoding.UTF8, ct);
        }
        foreach (var kv in headerPairs)
        {
            if (!request.Headers.TryAddWithoutValidation(kv.Key, kv.Value) && request.Content != null)
                request.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        return true;
    }

    private static string ReplaceLineComments(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            var t = l.TrimStart();
            if (t.StartsWith("#", StringComparison.Ordinal))
                lines[i] = "";
        }
        return string.Join("\n", lines);
    }

    private static string ApplyScratchVars(string s, Dictionary<string, string> vars)
        => Regex.Replace(s, @"\{\{\s*(\w+)\s*\}\}", m =>
        {
            var key = m.Groups[1].Value;
            return vars.TryGetValue(key, out var v) ? v : m.Value;
        });

    private void RefreshCodeEditorDebuggerPanel()
    {
        if (!CodeEditorDebuggerPanel.IsVisible)
            return;

        var interp = GetInterpreterForDebugger();
        if (interp == null)
        {
            DebuggerUnitSummaryText.Text = "Нет интерпретатора: запустите Debug, Attach или остановитесь на breakpoint.";
            _debuggerMemoryRowsFull = null;
            DebuggerMemoryGrid.ItemsSource = null;
            DebuggerStackGrid.ItemsSource = null;
            return;
        }

        InterpreterDebugSnapshot snap;
        try
        {
            snap = interp.CaptureDebugSnapshot();
        }
        catch (Exception ex)
        {
            DebuggerUnitSummaryText.Text = $"Снимок не удался: {ex.Message}";
            _debuggerMemoryRowsFull = null;
            DebuggerMemoryGrid.ItemsSource = null;
            DebuggerStackGrid.ItemsSource = null;
            return;
        }

        DebuggerUnitSummaryText.Text =
            $"{snap.UnitDisplay} · IP={snap.InstructionPointer} · eval depth={snap.EvaluationStack.Count} · frames={snap.CallStack.Count}";

        var memRows = new List<DebuggerMemoryRow>();
        foreach (var layer in snap.MemoryLayers)
        {
            foreach (var cell in layer.Cells)
            {
                memRows.Add(new DebuggerMemoryRow
                {
                    Layer = layer.Name,
                    Slot = cell.Slot,
                    TypeName = cell.TypeName,
                    Value = cell.Value,
                    InspectTreeRoots = cell.InspectRoot != null
                        ? new List<InterpreterInspectNode> { cell.InspectRoot }
                        : null
                });
            }
        }

        _debuggerMemoryRowsFull = memRows;
        ApplyDebuggerMemoryLayerFilter();

        var topIndex = snap.EvaluationStack.Count > 0 ? snap.EvaluationStack.Count - 1 : -1;
        var stackRows = new List<DebuggerStackRow>();
        foreach (var slot in snap.EvaluationStack)
        {
            stackRows.Add(new DebuggerStackRow
            {
                Index = slot.Index,
                TopMark = slot.Index == topIndex ? "←" : "",
                TypeName = slot.TypeName,
                Value = slot.Value,
                InspectTreeRoots = slot.InspectRoot != null
                    ? new List<InterpreterInspectNode> { slot.InspectRoot }
                    : null
            });
        }

        DebuggerStackGrid.ItemsSource = stackRows;

        var callHint = snap.CallStack.Count == 0
            ? ""
            : " · calls: " + string.Join(" → ", snap.CallStack.Select(f => f.Name ?? "?"));
        DebuggerUnitSummaryText.Text += callHint;
    }

    private void ApplyDebuggerMemoryLayerFilter()
    {
        if (_debuggerMemoryRowsFull == null)
        {
            DebuggerMemoryGrid.ItemsSource = null;
            return;
        }

        IEnumerable<DebuggerMemoryRow> rows = _debuggerMemoryRowsFull;
        switch (_debuggerMemoryLayerKind)
        {
            case DebuggerMemoryLayerKind.Global:
                rows = rows.Where(r => string.Equals(r.Layer, "Global", StringComparison.OrdinalIgnoreCase));
                break;
            case DebuggerMemoryLayerKind.Local:
                rows = rows.Where(r => r.Layer == "Local" || r.Layer.StartsWith("Local@", StringComparison.OrdinalIgnoreCase));
                break;
            case DebuggerMemoryLayerKind.Frame:
                rows = rows.Where(r =>
                    string.Equals(r.Layer, "Inherited", StringComparison.OrdinalIgnoreCase) ||
                    r.Layer.StartsWith("Inherited@", StringComparison.OrdinalIgnoreCase));
                break;
        }

        DebuggerMemoryGrid.ItemsSource = rows.ToList();
    }

    private void DebuggerMemoryLayerFilter_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true)
            return;
        if (ReferenceEquals(rb, DebuggerMemFilterGlobal))
            _debuggerMemoryLayerKind = DebuggerMemoryLayerKind.Global;
        else if (ReferenceEquals(rb, DebuggerMemFilterLocal))
            _debuggerMemoryLayerKind = DebuggerMemoryLayerKind.Local;
        else if (ReferenceEquals(rb, DebuggerMemFilterFrame))
            _debuggerMemoryLayerKind = DebuggerMemoryLayerKind.Frame;
        else
            _debuggerMemoryLayerKind = DebuggerMemoryLayerKind.All;

        ApplyDebuggerMemoryLayerFilter();
    }

    private void DebuggerInspectSensitiveToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control ctrl && ctrl.DataContext is InterpreterInspectNode n)
            n.ToggleSensitiveReveal();
        e.Handled = true;
    }

    private async void DebuggerInspectCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control ctrl && ctrl.DataContext is InterpreterInspectNode n)
        {
            var t = n.GetCopyableText();
            if (!string.IsNullOrEmpty(t) && Clipboard != null)
            {
                try { await Clipboard.SetTextAsync(t); }
                catch (Exception ex) { SectionMeta.Text = $"clipboard: {ex.Message}"; }
            }
        }
        e.Handled = true;
    }

    private void DebuggerInspectViewPayload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control ctrl && ctrl.DataContext is InterpreterInspectNode { BinaryPayload: { } payload })
        {
            var w = new BinaryPayloadViewerWindow(payload);
            w.ShowDialog(this);
        }
        e.Handled = true;
    }

    private void RefreshCodeEditorAttachUi()
    {
        var hasPath = !string.IsNullOrWhiteSpace(_codeEditorPath) && File.Exists(_codeEditorPath);
        if (!hasPath)
        {
            CodeEditorRunningHint.IsVisible = false;
            DebugAttachButton.IsVisible = false;
            DebugRestartRunningExeButton.IsVisible = false;
            return;
        }

        var full = Path.GetFullPath(_codeEditorPath!);
        var running = _executionRecords.Exists(r =>
            string.Equals(Path.GetFullPath(r.SourcePath), full, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase));

        CodeEditorRunningHint.IsVisible = running;
        DebugAttachButton.IsVisible = running;
        DebugAttachButton.IsEnabled = running && _attachDebugSession == null;
        DebugRestartRunningExeButton.IsVisible = running;
        DebugRestartRunningExeButton.IsEnabled = running;
    }

    private void OnInterpreterAttachRegistryUnregistered(string? unitName, string? attachPath, int instanceIndex)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => OnInterpreterAttachRegistryUnregistered(unitName, attachPath, instanceIndex));
            return;
        }

        if (_attachDebugSession != null
            && !string.IsNullOrWhiteSpace(attachPath)
            && !string.IsNullOrWhiteSpace(_codeEditorPath)
            && string.Equals(Path.GetFullPath(attachPath), Path.GetFullPath(_codeEditorPath), StringComparison.OrdinalIgnoreCase)
            && instanceIndex == _attachInstanceIndex)
            DetachFromRunningExe();
        else
            RefreshCodeEditorAttachUi();
    }

    private void DetachFromRunningExe()
    {
        if (_attachInterpreter != null)
            _attachInterpreter.DebugSession = null;
        if (_attachDebugSession != null)
        {
            _attachDebugSession.PausedAtDebugLocation -= OnInterpreterPausedAtDebugLocation;
            _attachDebugSession.EndRun();
        }

        _attachDebugSession = null;
        _attachInterpreter = null;
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        SetDebugToolbarPaused(false);
        SectionMeta.Text = _codeEditorPath ?? string.Empty;
        RefreshCodeEditorAttachUi();
    }

    private async void DebugAttach_Click(object? sender, RoutedEventArgs e)
    {
        if (_kernel == null || string.IsNullOrWhiteSpace(_codeEditorPath) || !File.Exists(_codeEditorPath))
        {
            await ShowMessageAsync("Файл не выбран.", "Attach");
            return;
        }

        if (_attachDebugSession != null)
            return;

        var max = Math.Max(1, _codeEditorAttachInstanceCap);
        if (!_kernel.AttachRegistry.TryGetBySourcePath(_codeEditorPath, max, out var interp, out var idx) || interp == null)
        {
            await ShowMessageAsync("Нет запущенного интерпретатора для этого .agi (дождитесь running в Startup Exes).", "Attach");
            return;
        }

        var session = new InterpreterDebugSession();
        session.ReplaceBreakpointsFrom(_agiBreakpointsByPath, _asmBreakpoints);
        session.PausedAtDebugLocation += OnInterpreterPausedAtDebugLocation;
        session.BeginRun();
        interp.DebugSession = session;
        _attachDebugSession = session;
        _attachInterpreter = interp;
        _attachInstanceIndex = idx;

        SetDebugToolbarPaused(false);
        DebugAttachButton.IsEnabled = false;
        SectionMeta.Text = $"attach: instance {idx} — breakpoints active";
        RefreshCodeEditorAttachUi();
    }

    private async void DebugRestartRunningExe_Click(object? sender, RoutedEventArgs e)
    {
        if (_kernel == null || string.IsNullOrWhiteSpace(_codeEditorPath) || !File.Exists(_codeEditorPath))
        {
            await ShowMessageAsync("Файл не выбран.", "Restart");
            return;
        }

        var fullPath = Path.GetFullPath(_codeEditorPath);
        var runningUi = _executionRecords.Exists(r =>
            string.Equals(Path.GetFullPath(r.SourcePath), fullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase));
        if (!runningUi)
        {
            await ShowMessageAsync("Нет записи running для этого файла.", "Restart");
            return;
        }

        var hadEditorDebug = _activeDebugSession != null;
        var hadStartupExeRunning = _executionRecords.Exists(r =>
            string.Equals(Path.GetFullPath(r.SourcePath), fullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase)
            && !r.Name.StartsWith("debug:", StringComparison.OrdinalIgnoreCase));
        var unitCfg = FindExecutionUnitConfigForAgiPath(fullPath);
        var scanCap = Math.Max(Math.Max(1, _codeEditorAttachInstanceCap), unitCfg?.InstanceCount ?? 1);

        DebugRestartRunningExeButton.IsEnabled = false;
        DebugAttachButton.IsEnabled = false;
        try
        {
            try
            {
                if (_linkedModuleEditors.Count > 0)
                {
                    if (!TrySaveAllOpenAgiFiles(out var tabErr))
                    {
                        await ShowMessageAsync(tabErr ?? "Save failed.", "Restart — save");
                        return;
                    }
                }
                else if (!TryPersistLinkedCodeEditorIfApplicable(out var linkErr))
                {
                    if (linkErr != null)
                    {
                        await ShowMessageAsync(linkErr, "Restart — save");
                        return;
                    }
                    File.WriteAllText(_codeEditorPath, CodeEditor.Document.Text);
                }
                SectionMeta.Text = $"saved + restart: {_codeEditorPath}";
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(ex.Message, "Restart — save");
                return;
            }

            foreach (var (_, interp) in _kernel.AttachRegistry.EnumerateBySourcePath(fullPath, scanCap))
                RequestInterpreterCooperativeStop(interp);

            _attachDebugSession?.RequestStop();
            _activeDebugSession?.RequestStop();

            await WaitForAgiRunSessionsEndedAsync(fullPath, scanCap).ConfigureAwait(true);

            if (_kernel.AttachRegistry.HasAnyForSourcePath(fullPath, scanCap))
            {
                await ShowMessageAsync("Таймаут остановки интерпретаторов.", "Restart");
                return;
            }

            if (hadStartupExeRunning && unitCfg != null)
            {
                var record = new RuntimeExecutionRecord
                {
                    Name = Path.GetFileNameWithoutExtension(unitCfg.Path) ?? unitCfg.Path,
                    SourcePath = SpaceEnvironment.GetFilePath(unitCfg.Path),
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Status = "starting",
                    Success = false,
                    InstanceCount = unitCfg.InstanceCount
                };
                _executionRecords.Add(record);
                RefreshMonitor(CurrentMonitorTag());
                _ = RunUnitInBackgroundAsync(record, unitCfg);
            }
            else if (hadStartupExeRunning && unitCfg == null)
            {
                await ShowMessageAsync("Running startup для этого .agi, но путь не найден в executionUnits dev.json — новый процесс не запущен.", "Restart");
            }

            if (hadEditorDebug)
                await RunCodeEditorDebugAsync().ConfigureAwait(true);

            RefreshCodeEditorAttachUi();
            RefreshMonitor(CurrentMonitorTag());
        }
        finally
        {
            RefreshCodeEditorAttachUi();
        }
    }

    private static SpaceEnvironment.ExecutionUnitConfig? FindExecutionUnitConfigForAgiPath(string fullPathAgi)
    {
        var full = Path.GetFullPath(fullPathAgi);
        foreach (var u in SpaceEnvironment.ExecutionUnits)
        {
            var cfgAbs = Path.GetFullPath(SpaceEnvironment.GetFilePath(u.Path));
            if (string.Equals(cfgAbs, full, StringComparison.OrdinalIgnoreCase))
                return u;
        }
        return null;
    }

    private static void RequestInterpreterCooperativeStop(Interpreter interp)
    {
        if (interp.DebugSession is { } existing)
        {
            existing.RequestStop();
            return;
        }

        var kill = new InterpreterDebugSession();
        kill.BeginRun();
        interp.DebugSession = kill;
        kill.RequestStop();
    }

    private async Task WaitForAgiRunSessionsEndedAsync(string fullPath, int scanCap)
    {
        var sw = Stopwatch.StartNew();
        var lastForceCloseMs = -500;
        while (sw.ElapsedMilliseconds < 30_000)
        {
            var registryBusy = _kernel!.AttachRegistry.HasAnyForSourcePath(fullPath, scanCap);
            if (!registryBusy && _activeDebugSession == null && _attachDebugSession == null)
                return;

            var elapsed = (int)sw.ElapsedMilliseconds;
            if (elapsed - lastForceCloseMs >= 400)
            {
                lastForceCloseMs = elapsed;
                foreach (var (_, interp) in _kernel.AttachRegistry.EnumerateBySourcePath(fullPath, scanCap))
                {
                    try { await interp.ForceCloseOwnedStreamsAsync().ConfigureAwait(true); }
                    catch { }
                }
            }

            await Task.Delay(40).ConfigureAwait(true);
        }
    }

    private void SetDebugToolbarPaused(bool paused)
    {
        DebugContinueButton.IsEnabled = paused;
        DebugStepButton.IsEnabled = paused;
        DebugStepIntoButton.IsEnabled = paused;
        DebugStopButton.IsEnabled = paused || _attachDebugSession != null || _activeDebugSession != null;
    }

    private static bool PathsEqualAgi(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    private int MainEditorDebugHighlightLine()
    {
        if (_debugHighlightDocumentLine <= 0)
            return 0;
        if (string.IsNullOrEmpty(_codeEditorPath))
            return _debugHighlightDocumentLine;
        if (string.IsNullOrEmpty(_debugHighlightAgiPath))
            return _debugHighlightDocumentLine;
        return PathsEqualAgi(_debugHighlightAgiPath, _codeEditorPath) ? _debugHighlightDocumentLine : 0;
    }

    private int ModuleDebugHighlightLine(string moduleFullPath)
    {
        if (_debugHighlightDocumentLine <= 0 || string.IsNullOrEmpty(_debugHighlightAgiPath))
            return 0;
        return PathsEqualAgi(_debugHighlightAgiPath, moduleFullPath) ? _debugHighlightDocumentLine : 0;
    }

    private void OnInterpreterPausedAtDebugLocation(int sourceLine, int asmListingLine, string? agiSourcePath)
    {
        void Apply()
        {
            var stayOnAsmTab = IsAsmSourceTabSelected();

            SectionMeta.Text = asmListingLine > 0
                ? $"debug: AGI line {sourceLine}, ASM line {asmListingLine}"
                : $"debug: paused at line {sourceLine}";
            SetDebugToolbarPaused(true);

            _debugHighlightDocumentLine = sourceLine;
            _debugHighlightAgiPath = string.IsNullOrWhiteSpace(agiSourcePath)
                ? (string.IsNullOrEmpty(_codeEditorPath) ? null : Path.GetFullPath(_codeEditorPath))
                : Path.GetFullPath(agiSourcePath);
            _debugHighlightAsmLine = asmListingLine;

            TextEditor? agiFocus = CodeEditor;
            TabItem? selectTab = CodeEditorMainTab;
            if (!string.IsNullOrEmpty(_debugHighlightAgiPath) && !string.IsNullOrEmpty(_codeEditorPath) &&
                PathsEqualAgi(_debugHighlightAgiPath, _codeEditorPath))
            {
                agiFocus = CodeEditor;
                selectTab = CodeEditorMainTab;
            }
            else
            {
                var mod = _linkedModuleEditors.Find(h => PathsEqualAgi(h.FilePath, _debugHighlightAgiPath));
                if (mod != null)
                {
                    agiFocus = mod.Editor;
                    selectTab = mod.Tab;
                }
            }

            if (!stayOnAsmTab && selectTab != null)
                CodeEditorSourceTabControl.SelectedItem = selectTab;

            CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
            CodeAsmEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
            foreach (var h in _linkedModuleEditors)
                h.Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);

            try
            {
                if (!stayOnAsmTab && agiFocus != null && sourceLine >= 1 && sourceLine <= agiFocus.Document.LineCount)
                {
                    var dl = agiFocus.Document.GetLineByNumber(sourceLine);
                    agiFocus.TextArea.Caret.Offset = dl.Offset;
                    agiFocus.ScrollTo(sourceLine, 1);
                    agiFocus.TextArea.Caret.BringCaretToView();
                }
            }
            catch { }

            try
            {
                if (asmListingLine >= 1 && asmListingLine <= CodeAsmEditor.Document.LineCount)
                {
                    var al = CodeAsmEditor.Document.GetLineByNumber(asmListingLine);
                    CodeAsmEditor.TextArea.Caret.Offset = al.Offset;
                    CodeAsmEditor.ScrollTo(asmListingLine, 1);
                    CodeAsmEditor.TextArea.Caret.BringCaretToView();
                }
            }
            catch { }

            if (stayOnAsmTab)
                CodeAsmEditor.Focus();
            else
                agiFocus?.Focus();

            RefreshCodeEditorDebuggerPanel();
            RefreshMonitor(CurrentMonitorTag());
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.InvokeAsync(Apply);
    }

    private void CodeEditorDebug_Click(object? sender, RoutedEventArgs e)
    {
        _ = RunCodeEditorDebugAsync();
    }

    private async Task RunCodeEditorDebugAsync()
    {
        if (_kernel == null)
        {
            await ShowMessageAsync("Kernel not ready.", "Debug");
            return;
        }

        if (_attachDebugSession != null)
            DetachFromRunningExe();

        FlushLinkedModuleFilesFromEditors();
        var source = GetCodeEditorMainSourceForCompile();
        var comp = await _kernel.CompileAsync(source, _codeEditorPath).ConfigureAwait(true);
        if (!comp.Success || comp.Result == null)
        {
            await ShowMessageAsync(comp.ErrorMessage ?? "Compilation failed.", "Debug");
            return;
        }

        var debugName = !string.IsNullOrWhiteSpace(_codeEditorPath)
            ? $"debug:{Path.GetFileNameWithoutExtension(_codeEditorPath)}"
            : "debug:editor";
        var debugSource = !string.IsNullOrWhiteSpace(_codeEditorPath) ? _codeEditorPath! : "(unsaved editor buffer)";

        var debugRecord = new RuntimeExecutionRecord
        {
            Name = debugName,
            SourcePath = debugSource,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = "running",
            Success = false,
            InstanceCount = 1
        };
        _executionRecords.Add(debugRecord);
        RefreshMonitor(CurrentMonitorTag());

        var session = new InterpreterDebugSession();
        session.ReplaceBreakpointsFrom(_agiBreakpointsByPath, _asmBreakpoints);
        session.PausedAtDebugLocation += OnInterpreterPausedAtDebugLocation;
        _activeDebugSession = session;
        session.BeginRun();

        _editorDebugStdin = new QueuedTextReader();
        var interpreter = new Interpreter
        {
            Configuration = _kernel.Configuration,
            DebugSession = session,
            BypassRuntimeSpawn = true,
            StandardInput = _editorDebugStdin
        };

        _editorDebugInterpreter = interpreter;
        SetDebugToolbarPaused(false);
        SectionMeta.Text = "debug: running…";

        InterpretationResult? runResult = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_codeEditorPath))
                comp.Result!.AttachSourcePath = Path.GetFullPath(_codeEditorPath);
            runResult = await Task.Run(async () => await interpreter.InterpreteAsync(comp.Result!).ConfigureAwait(false))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                debugRecord.Success = false;
                debugRecord.Status = "error";
                debugRecord.EndedAtUtc = DateTimeOffset.UtcNow;
                debugRecord.ErrorMessage = ex.Message;
                RefreshMonitor(CurrentMonitorTag());
                SectionMeta.Text = $"debug: error {ex.Message}";
            });
        }
        finally
        {
            session.PausedAtDebugLocation -= OnInterpreterPausedAtDebugLocation;
            session.EndRun();
            _activeDebugSession = null;
            _editorDebugInterpreter = null;
            _editorDebugStdin = null;
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                _debugHighlightDocumentLine = 0;
                _debugHighlightAgiPath = null;
                _debugHighlightAsmLine = 0;
                CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
                CodeAsmEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
                foreach (var h in _linkedModuleEditors)
                    h.Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
                SetDebugToolbarPaused(false);
                if (!debugRecord.EndedAtUtc.HasValue)
                {
                    debugRecord.Success = runResult?.Success ?? false;
                    debugRecord.Status = debugRecord.Success ? "stopped" : "error";
                    debugRecord.EndedAtUtc = DateTimeOffset.UtcNow;
                    if (!debugRecord.Success && string.IsNullOrWhiteSpace(debugRecord.ErrorMessage))
                        debugRecord.ErrorMessage = "Interpretation returned failure.";
                    RefreshMonitor(CurrentMonitorTag());
                }
                SectionMeta.Text = runResult?.Success == true ? "debug: finished" : "debug: failed or interrupted";
                RefreshCodeEditorDebuggerPanel();
            });
        }
    }

    private void DebugContinue_Click(object? sender, RoutedEventArgs e)
    {
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        CurrentStepSession?.RequestContinue();
        SetDebugToolbarPaused(false);
    }

    private void DebugStep_Click(object? sender, RoutedEventArgs e)
    {
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        CurrentStepSession?.RequestStepOverLine();
        SetDebugToolbarPaused(false);
    }

    private void DebugStepInto_Click(object? sender, RoutedEventArgs e)
    {
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        var asmTab = IsAsmSourceTabSelected();
        if (asmTab)
            CurrentStepSession?.RequestStepInstruction();
        else
            CurrentStepSession?.RequestStepInto();
        SetDebugToolbarPaused(false);
    }

    private void DebugStop_Click(object? sender, RoutedEventArgs e)
    {
        if (_attachDebugSession != null)
        {
            _attachDebugSession.RequestStop();
            SetDebugToolbarPaused(false);
            return;
        }
        _activeDebugSession?.RequestStop();
        SetDebugToolbarPaused(false);
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandleGlobalDebugKeys(e))
            e.Handled = true;
    }

    private bool IsLikelyFunctionKeyAutoRepeat(Key key)
    {
        var now = Environment.TickCount64;
        if (key == _lastGlobalDebugFnKey && now - _lastGlobalDebugFnKeyTick < 45)
            return true;
        _lastGlobalDebugFnKey = key;
        _lastGlobalDebugFnKeyTick = now;
        return false;
    }

    private bool TryHandleGlobalDebugKeys(KeyEventArgs e)
    {
        if (!CodeEditorPanel.IsVisible)
            return false;

        if (e.KeyModifiers != KeyModifiers.None)
            return false;

        var asmTab = IsAsmSourceTabSelected();

        switch (e.Key)
        {
            case Key.F5:
                if (IsLikelyFunctionKeyAutoRepeat(e.Key))
                    return false;
                CodeEditorDebug_Click(this, e);
                return true;
            case Key.F9:
                if (IsLikelyFunctionKeyAutoRepeat(e.Key) || !DebugContinueButton.IsEnabled)
                    return false;
                DebugContinue_Click(this, e);
                return true;
            case Key.F10:
                if (IsLikelyFunctionKeyAutoRepeat(e.Key) || !DebugStepButton.IsEnabled)
                    return false;
                if (asmTab)
                    DebugStepInto_Click(this, e);
                else
                    DebugStep_Click(this, e);
                return true;
            case Key.F11:
                if (IsLikelyFunctionKeyAutoRepeat(e.Key) || !DebugStepIntoButton.IsEnabled)
                    return false;
                DebugStepInto_Click(this, e);
                return true;
            default:
                return false;
        }
    }

    private void OnAnyAgiSourceDocument_TextChanged(object? sender, EventArgs e)
    {
        _codeHighlightTimer.Stop();
        _codeHighlightTimer.Start();
        if (IsAsmSourceTabSelected())
            RequestAsmViewRefresh();
    }

    private void CodeEditorSourceTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CodeEditorSourceTabControl))
            return;
        if (IsAsmSourceTabSelected())
            RequestAsmViewRefresh();
    }

    private bool IsAsmSourceTabSelected()
        => ReferenceEquals(CodeEditorSourceTabControl.SelectedItem, CodeEditorAsmTab);

    private void RequestAsmViewRefresh()
    {
        FlushLinkedModuleFilesFromEditors();
        var gen = ++_asmRefreshGeneration;
        var source = GetCodeEditorMainSourceForCompile();
        var kernel = _kernel;
        _ = RunAsmViewRefreshAsync(gen, source, kernel);
    }

    private async Task RunAsmViewRefreshAsync(int generation, string source, MagicKernel? kernel)
    {
        if (kernel == null)
        {
            ApplyAsmTextIfCurrentGeneration(generation, "// Kernel not ready.");
            return;
        }

        CompilationResult comp;
        try
        {
            comp = await kernel.CompileAsync(source, _codeEditorPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var msg = "// " + ex.Message.Replace("\r\n", "\n").Replace("\n", "\n// ");
            ApplyAsmTextIfCurrentGeneration(generation, msg);
            return;
        }

        if (generation != _asmRefreshGeneration)
            return;

        if (!comp.Success || comp.Result == null)
        {
            var err = comp.ErrorMessage ?? "Compilation failed.";
            CodeAsmEditor.Document.Text = "// " + err.Replace("\r\n", "\n").Replace("\n", "\n// ");
            RefreshAsmSyntaxHighlight();
            return;
        }

        CodeAsmEditor.Document.Text = comp.Result.ToAgiasmText(source);
        RefreshAsmSyntaxHighlight();
    }

    private void RefreshAsmSyntaxHighlight()
    {
        _asmColorizer.DocumentUpdated(CodeAsmEditor.Document);
        CodeAsmEditor.TextArea.TextView.Redraw();
    }

    private void ApplyAsmTextIfCurrentGeneration(int generation, string text)
    {
        if (generation != _asmRefreshGeneration)
            return;
        CodeAsmEditor.Document.Text = text;
        RefreshAsmSyntaxHighlight();
    }

    private void CodeHighlightTimer_Tick(object? sender, EventArgs e)
    {
        _codeHighlightTimer.Stop();
        _codeColorizer.DocumentUpdated(CodeEditor.Document);
        CodeEditor.TextArea.TextView.Redraw();
        foreach (var h in _linkedModuleEditors)
        {
            h.Colorizer.DocumentUpdated(h.Editor.Document);
            h.Editor.TextArea.TextView.Redraw();
        }
    }

    private void VaultReload_Click(object? sender, RoutedEventArgs e) => LoadVaultItems();

    private void VaultAdd_Click(object? sender, RoutedEventArgs e)
    {
        _vaultItems.Add(new VaultItem { Program = "program", System = "system", Module = "module", InstanceIndex = 0 });
        VaultItemsGrid.ItemsSource = null;
        VaultItemsGrid.ItemsSource = _vaultItems;
        RefreshVaultRows();
    }

    private void VaultDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (VaultItemsGrid.SelectedItem is VaultItem item)
        {
            _vaultItems.Remove(item);
            VaultItemsGrid.ItemsSource = null;
            VaultItemsGrid.ItemsSource = _vaultItems;
            RefreshVaultRows();
        }
    }

    private void VaultSave_Click(object? sender, RoutedEventArgs e)
    {
        if (VaultItemsGrid.SelectedItem is VaultItem selected)
            _workspace.ApplyRows(selected, _vaultRows);
        _workspace.SaveVaultItems(_vaultItems, _vaultPath);
        SectionMeta.Text = $"saved: {_vaultPath}";
    }

    private void VaultItemsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshVaultRows();

    private void MonitorRefresh_Click(object? sender, RoutedEventArgs e)
    {
        var tag = (NavigationTree.SelectedItem as TreeViewItem)?.Tag?.ToString() ?? "monitor-exes";
        RefreshMonitor(tag);
    }

    private void ConsoleTimer_Tick(object? sender, EventArgs e)
    {
        DrainConsole();
        MaybeRefreshMonitorDuringActiveDebug();
    }

    private void MaybeRefreshMonitorDuringActiveDebug()
    {
        if (_activeDebugSession == null && _attachDebugSession == null)
        {
            _monitorDebugRefreshThrottle = 0;
            return;
        }

        if (!MonitorPanel.IsVisible)
            return;

        var tag = CurrentMonitorTag();
        if (!tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase))
            return;

        if (++_monitorDebugRefreshThrottle < 8)
            return;

        _monitorDebugRefreshThrottle = 0;
        RefreshMonitor(tag);
    }

    private void ConsoleRefresh_Click(object? sender, RoutedEventArgs e) => DrainConsole();

    private void ConsoleClear_Click(object? sender, RoutedEventArgs e)
    {
        _consoleLines.Clear();
        _consoleIoLines.Clear();
        ConsoleLogsTextBox.Clear();
        CodeEditorEnvLogsTextBox.Clear();
        CodeEditorEnvNetworksTextBox.Clear();
        CodeEditorEnvConsoleTextBox.Clear();
    }

    private void DrainConsole()
    {
        var drained = ConsoleLogCapture.Drain();
        if (drained.Count == 0)
            return;

        var trimmedHead = false;
        foreach (var line in drained)
        {
            _consoleLines.Add(line);
            _consoleIoLines.Add(line);
        }

        while (_consoleLines.Count > 2500)
        {
            _consoleLines.RemoveAt(0);
            trimmedHead = true;
        }
        while (_consoleIoLines.Count > 2500)
        {
            _consoleIoLines.RemoveAt(0);
            trimmedHead = true;
        }

        if (trimmedHead)
            ConsoleLogsTextBox.Text = string.Join(Environment.NewLine, _consoleLines);
        else
        {
            foreach (var line in drained)
                AppendLineToTextBox(ConsoleLogsTextBox, line);
        }

        var codeEditorVisible = CodeEditorPanel.IsVisible;
        var envVisible = CodeEditorEnvironmentPanel.IsVisible;
        if (!codeEditorVisible || !envVisible)
            return;

        if (trimmedHead)
        {
            CodeEditorEnvLogsTextBox.Text = string.Join(Environment.NewLine, _consoleLines);
            CodeEditorEnvConsoleTextBox.Text = string.Join(Environment.NewLine, _consoleIoLines);
            RebuildEnvironmentNetworksText();
        }
        else
        {
            foreach (var line in drained)
            {
                AppendLineToTextBox(CodeEditorEnvLogsTextBox, line);
                AppendLineToTextBox(CodeEditorEnvConsoleTextBox, line);
                if (IsDeviceNetworkLogLine(line))
                    AppendLineToTextBox(CodeEditorEnvNetworksTextBox, line);
            }
        }
    }

    private static void AppendLineToTextBox(TextBox textBox, string line)
    {
        var existing = textBox.Text ?? string.Empty;
        textBox.Text = existing.Length > 0
            ? existing + Environment.NewLine + line
            : line;
    }

    private void CodeEditorEnvConsoleSend_Click(object? sender, RoutedEventArgs e)
        => SendEnvironmentConsoleInput();

    private void CodeEditorEnvConsoleInputTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        SendEnvironmentConsoleInput();
    }

    private void SendEnvironmentConsoleInput()
    {
        var input = CodeEditorEnvConsoleInputTextBox.Text ?? string.Empty;
        CodeEditorEnvConsoleInputTextBox.Clear();
        if (_editorDebugStdin != null)
            _editorDebugStdin.EnqueueLine(input);
        else
            ConsoleLogCapture.SubmitInput(input);

        var echo = $"> {input}";
        _consoleIoLines.Add(echo);
        while (_consoleIoLines.Count > 2500)
            _consoleIoLines.RemoveAt(0);

        if (CodeEditorPanel.IsVisible && CodeEditorEnvironmentPanel.IsVisible)
            AppendLineToTextBox(CodeEditorEnvConsoleTextBox, echo);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await BootstrapAndRunInitialAsync();
    }

    private async Task BootstrapAndRunInitialAsync()
    {
        try
        {
            OSSystem.StartKernel();
            var builder = Host.CreateApplicationBuilder([]);
            builder.Configuration.AddJsonFile(_appSettingsPath, optional: true, reloadOnChange: true);
            builder.Configuration.AddEnvironmentVariables();
            builder.Services.AddSingleton<MagicKernel>();
            builder.Services.AddRocksDb(builder.Configuration);

            _host = builder.Build();
            using var scope = _host.Services.CreateScope();
            _kernel = scope.ServiceProvider.GetRequiredService<MagicKernel>();
            _kernel.AttachRegistry.InterpreterUnregistered += OnInterpreterAttachRegistryUnregistered;
            var defaultDisk = scope.ServiceProvider.GetRequiredService<RocksDbSpaceDisk>();
            _kernel.Devices.Add(defaultDisk);
            await _kernel.StartKernel();

            var executionUnits = SpaceEnvironment.ExecutionUnits;
            if (executionUnits.Count == 0)
            {
                SectionMeta.Text = "kernel started; no executionUnits in Space/dev.json";
                RefreshMonitor("monitor-exes");
                return;
            }

            foreach (var unit in executionUnits)
            {
                var record = new RuntimeExecutionRecord
                {
                    Name = Path.GetFileNameWithoutExtension(unit.Path) ?? unit.Path,
                    SourcePath = SpaceEnvironment.GetFilePath(unit.Path),
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Status = "starting",
                    Success = false,
                    InstanceCount = unit.InstanceCount
                };
                _executionRecords.Add(record);
                _ = RunUnitInBackgroundAsync(record, unit);
            }

            SectionMeta.Text = $"startup executionUnits launched: {executionUnits.Count}";
        }
        catch (Exception ex)
        {
            _executionRecords.Add(new RuntimeExecutionRecord
            {
                Name = "startup-executionUnits",
                SourcePath = "Space/dev.json:executionUnits",
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = "error",
                Success = false,
                InstanceCount = 0,
                EndedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message
            });
            SectionMeta.Text = $"startup error: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            RefreshMonitor("monitor-exes");
        }
    }

    private async Task RunUnitInBackgroundAsync(RuntimeExecutionRecord record, SpaceEnvironment.ExecutionUnitConfig unit)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            record.Status = "running";
            RefreshMonitor("monitor-exes");
            RefreshCodeEditorAttachUi();
        });

        try
        {
            var host = new ExecutionUnitHost(_kernel!);
            var ok = await _runService.RunConfiguredUnitAsync(host, unit).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                record.Success = ok;
                record.Status = ok ? "stopped" : "error";
                record.EndedAtUtc = DateTimeOffset.UtcNow;
                if (!ok && string.IsNullOrWhiteSpace(record.ErrorMessage))
                    record.ErrorMessage = "Execution returned false.";
                RefreshMonitor("monitor-exes");
                RefreshCodeEditorAttachUi();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                record.Success = false;
                record.Status = "error";
                record.EndedAtUtc = DateTimeOffset.UtcNow;
                record.ErrorMessage = ex.Message;
                RefreshMonitor("monitor-exes");
                RefreshCodeEditorAttachUi();
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_kernel != null)
            _kernel.AttachRegistry.InterpreterUnregistered -= OnInterpreterAttachRegistryUnregistered;
        DetachFromRunningExe();
        _consoleTimer.Stop();
        _httpScratchClient.Dispose();
        _host?.Dispose();
        base.OnClosed(e);
    }

    private void LoadStorageTree()
    {
        StorageTree.Items.Clear();
        var rootPath = SpaceEnvironment.Path;
        if (!Directory.Exists(rootPath))
        {
            StorageTree.Items.Add(new TreeViewItem { Header = $"SPACE_PATH not found: {rootPath}" });
            return;
        }

        var root = BuildAgiTree(rootPath);
        StorageTree.Items.Add(root);
        root.IsExpanded = true;
    }

    private TreeViewItem BuildAgiTree(string directory)
    {
        var dirInfo = new DirectoryInfo(directory);
        var rootNode = new TreeViewItem { Header = dirInfo.Name, Tag = dirInfo.FullName };

        foreach (var subDir in dirInfo.GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var child = BuildAgiTree(subDir.FullName);
            if (child.Items.Count > 0)
                rootNode.Items.Add(child);
        }

        foreach (var file in dirInfo.GetFiles("*.agi").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            rootNode.Items.Add(CreateAgiFileNode(file.FullName));

        return rootNode;
    }

    private TreeViewItem CreateAgiFileNode(string fullPath)
    {
        var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(fullPath),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var button = new Button
        {
            Content = "Edit",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Tag = fullPath,
            FontSize = 11
        };
        button.Click += StorageEdit_Click;
        stack.Children.Add(button);
        return new TreeViewItem { Header = stack, Tag = fullPath };
    }

    private void StorageEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path)
            return;
        OpenCodeEditor(path);
        SelectNavigationTag("space-code-editor");
    }

    private void SelectNavigationTag(string tag)
    {
        foreach (var item in NavigationTree.Items)
        {
            if (item is TreeViewItem root && TrySelectByTag(root, tag))
                return;
        }
    }

    private static bool TrySelectByTag(TreeViewItem node, string tag)
    {
        if (string.Equals(node.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
        {
            node.IsSelected = true;
            return true;
        }

        foreach (var child in node.Items)
        {
            if (child is TreeViewItem childNode)
            {
                if (TrySelectByTag(childNode, tag))
                {
                    node.IsExpanded = true;
                    return true;
                }
            }
        }

        return false;
    }

    private void OpenCodeEditor(string path, int attachInstanceCap = 8)
    {
        _codeEditorPath = path;
        _codeEditorAttachInstanceCap = Math.Max(1, attachInstanceCap);
        ReloadCodeEditor();
    }

    private string GetCodeEditorMainSourceForCompile()
    {
        var t = CodeEditor.Document.Text ?? string.Empty;
        var i = t.IndexOf(LinkedModuleMarkerPrefix, StringComparison.Ordinal);
        return i >= 0 ? t[..i].TrimEnd() : t;
    }

    private static string StripLinkedMarkersFromMainText(string text)
    {
        var i = text.IndexOf(LinkedModuleMarkerPrefix, StringComparison.Ordinal);
        return i >= 0 ? text[..i].TrimEnd() : text;
    }

    private void FlushLinkedModuleFilesFromEditors()
    {
        foreach (var h in _linkedModuleEditors)
        {
            try { File.WriteAllText(h.FilePath, h.Editor.Document.Text ?? string.Empty); }
            catch { }
        }
    }

    private bool TrySaveAllOpenAgiFiles(out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(_codeEditorPath))
        {
            errorMessage = "no file path";
            return false;
        }

        try
        {
            File.WriteAllText(_codeEditorPath, CodeEditor.Document.Text ?? string.Empty);
            foreach (var h in _linkedModuleEditors)
                File.WriteAllText(h.FilePath, h.Editor.Document.Text ?? string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private void ClearLinkedModuleTabs()
    {
        foreach (var h in _linkedModuleEditors)
        {
            h.Editor.Document.TextChanged -= OnAnyAgiSourceDocument_TextChanged;
            CodeEditorSourceTabControl.Items.Remove(h.Tab);
        }
        _linkedModuleEditors.Clear();
    }

    private (TextEditor Editor, MagicAgiColorizer Colorizer) CreateLinkedModuleTextEditorPair()
    {
        var editor = new TextEditor
        {
            FontFamily = CodeEditor.FontFamily,
            FontSize = CodeEditor.FontSize,
            ShowLineNumbers = true,
            Foreground = CodeEditor.Foreground,
            Background = CodeEditor.Background,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            WordWrap = false
        };
        editor.Options.AllowScrollBelowDocument = false;
        var colorizer = new MagicAgiColorizer();
        editor.TextArea.TextView.LineTransformers.Add(colorizer);
        editor.Document.TextChanged += OnAnyAgiSourceDocument_TextChanged;
        return (editor, colorizer);
    }

    private static string EscapeTabHeaderAccessText(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("_", "__", StringComparison.Ordinal);

    private bool TryPopulateLinkedModuleTabsFromUses(string mainPath, string mainText, out string labelBar)
    {
        labelBar = string.Empty;
        var structure = new Parser().ParseProgram(mainText);
        if (structure.UseDirectives.Count == 0)
            return false;

        var headerParts = new List<string> { Path.GetFileName(mainPath) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var any = false;
        foreach (var use in structure.UseDirectives)
        {
            string resolved;
            try
            {
                resolved = Compiler.ResolveUseModuleFilePath(mainPath, use.ModulePath);
            }
            catch (CompilationException)
            {
                continue;
            }

            if (!File.Exists(resolved) || !seen.Add(resolved))
                continue;

            any = true;
            headerParts.Add(Path.GetFileName(resolved));
            var (editor, colorizer) = CreateLinkedModuleTextEditorPair();
            var modFull = Path.GetFullPath(resolved);
            editor.TextArea.LeftMargins.Insert(0, new AgiBreakpointMargin(() => modFull, _agiBreakpointsByPath, SyncBreakpointsToActiveDebugSession));
            editor.TextArea.TextView.BackgroundRenderers.Add(new AgiDebugCurrentLineRenderer(() => ModuleDebugHighlightLine(modFull)));
            editor.Document.Text = File.ReadAllText(resolved);
            colorizer.DocumentUpdated(editor.Document);
            var tab = new TabItem
            {
                Header = EscapeTabHeaderAccessText(Path.GetFileName(resolved)),
                Content = editor
            };
            var asmIdx = CodeEditorSourceTabControl.Items.IndexOf(CodeEditorAsmTab);
            if (asmIdx < 0)
                return false;
            CodeEditorSourceTabControl.Items.Insert(asmIdx, tab);
            _linkedModuleEditors.Add(new LinkedModuleEditorHost(resolved, editor, colorizer, tab));
        }

        if (!any)
            return false;

        labelBar = string.Join(" | ", headerParts);
        return true;
    }

    private bool TrySplitLinkedEditorDocument(string document, out string mainText, out List<(string path, string body)> modules, out string? errorMessage)
    {
        mainText = string.Empty;
        modules = [];
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(_codeEditorPath))
        {
            errorMessage = "No main path for linked save.";
            return false;
        }

        var first = document.IndexOf(LinkedModuleMarkerPrefix, StringComparison.Ordinal);
        if (first < 0)
            return false;

        mainText = document[..first].TrimEnd();
        var pos = first;
        while (pos < document.Length)
        {
            if (!document.AsSpan(pos).StartsWith(LinkedModuleMarkerPrefix))
            {
                errorMessage = "Broken linked layout (unexpected text).";
                return false;
            }

            var lineBreak = document.IndexOf('\n', pos);
            if (lineBreak < 0)
            {
                errorMessage = "Incomplete linked marker line.";
                return false;
            }

            var line = document.Substring(pos, lineBreak - pos).TrimEnd();
            if (!line.EndsWith(LinkedModuleMarkerSuffix, StringComparison.Ordinal))
            {
                errorMessage = "Invalid linked marker line.";
                return false;
            }

            var innerLen = line.Length - LinkedModuleMarkerPrefix.Length - LinkedModuleMarkerSuffix.Length;
            if (innerLen < 1)
            {
                errorMessage = "Empty module path in linked marker.";
                return false;
            }

            var moduleSpec = line.Substring(LinkedModuleMarkerPrefix.Length, innerLen).Trim();
            string resolved;
            try
            {
                resolved = Compiler.ResolveUseModuleFilePath(_codeEditorPath, moduleSpec);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }

            var contentStart = lineBreak + 1;
            var next = document.IndexOf(LinkedModuleMarkerPrefix, contentStart, StringComparison.Ordinal);
            var body = (next < 0 ? document[contentStart..] : document[contentStart..next]).TrimEnd();
            modules.Add((resolved, body));
            pos = next >= 0 ? next : document.Length;
        }

        return true;
    }

    private bool TryPersistLinkedCodeEditorIfApplicable(out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(_codeEditorPath))
            return false;

        var doc = CodeEditor.Document.Text ?? string.Empty;
        if (doc.IndexOf(LinkedModuleMarkerPrefix, StringComparison.Ordinal) < 0)
            return false;

        if (!TrySplitLinkedEditorDocument(doc, out var mainText, out var modules, out errorMessage))
            return false;

        try
        {
            File.WriteAllText(_codeEditorPath, mainText);
            foreach (var (path, body) in modules)
                File.WriteAllText(path, body);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        return true;
    }

    private void ReloadCodeEditor()
    {
        ClearLinkedModuleTabs();
        CodeEditorMainTab.Header = "Code";

        if (string.IsNullOrWhiteSpace(_codeEditorPath))
        {
            CodeEditorFileText.Text = "No file selected";
            CodeEditor.Document.Text = string.Empty;
            _codeColorizer.DocumentUpdated(CodeEditor.Document);
            CodeEditor.TextArea.TextView.Redraw();
            RefreshCodeEditorAttachUi();
            if (IsAsmSourceTabSelected())
                RequestAsmViewRefresh();
            FinishCodeEditorLayoutRefresh();
            return;
        }

        if (!File.Exists(_codeEditorPath))
        {
            CodeEditorFileText.Text = $"Not found: {_codeEditorPath}";
            CodeEditor.Document.Text = string.Empty;
            _codeColorizer.DocumentUpdated(CodeEditor.Document);
            CodeEditor.TextArea.TextView.Redraw();
            RefreshCodeEditorAttachUi();
            if (IsAsmSourceTabSelected())
                RequestAsmViewRefresh();
            FinishCodeEditorLayoutRefresh();
            return;
        }

        var raw = File.ReadAllText(_codeEditorPath);
        var mainText = StripLinkedMarkersFromMainText(raw);
        CodeEditorMainTab.Header = EscapeTabHeaderAccessText(Path.GetFileName(_codeEditorPath));

        if (TryPopulateLinkedModuleTabsFromUses(_codeEditorPath, mainText, out var labelBar))
            CodeEditorFileText.Text = labelBar;
        else
            CodeEditorFileText.Text = _codeEditorPath;

        CodeEditor.Document.Text = mainText;
        _codeColorizer.DocumentUpdated(CodeEditor.Document);
        CodeEditor.TextArea.TextView.Redraw();
        foreach (var h in _linkedModuleEditors)
        {
            h.Colorizer.DocumentUpdated(h.Editor.Document);
            h.Editor.TextArea.TextView.Redraw();
        }

        RefreshCodeEditorAttachUi();
        if (IsAsmSourceTabSelected())
            RequestAsmViewRefresh();
        FinishCodeEditorLayoutRefresh();
    }

    private void FinishCodeEditorLayoutRefresh()
    {
        ApplyCodeEditorDebuggerLayout(_codeEditorDebuggerSplitOpen);
        if (_codeEditorEnvironmentOpen)
        {
            ApplyCodeEditorEnvironmentLayout(true);
            SyncEnvironmentPanelsFromBuffers();
        }
        RefreshCodeEditorDebuggerPanel();
    }

    private Task ShowMessageAsync(string message, string title)
    {
        // Simple dialog for Avalonia - show a message window
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK",
                        Margin = new Thickness(0, 12, 0, 0),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
                    }
                }
            }
        };
        // Wire OK button to close
        if (dialog.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is Button btn)
            btn.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(this);
    }
}
