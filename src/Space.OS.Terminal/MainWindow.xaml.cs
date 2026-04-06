using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
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
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;

namespace Space.OS.Terminal;

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
    /// <summary>Интерпретатор при Debug из редактора (до завершения Run).</summary>
    private Interpreter? _editorDebugInterpreter;
    /// <summary>Stdin только для F5 из редактора — не делит глобальный <see cref="Console.In"/> с startup execution units.</summary>
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

    private enum DebuggerMemoryLayerKind
    {
        All,
        Global,
        Local,
        /// <summary>Inherited / Inherited@frameN — данные кадра из родителя.</summary>
        Frame
    }

    private DebuggerMemoryLayerKind _debuggerMemoryLayerKind = DebuggerMemoryLayerKind.All;
    private List<DebuggerMemoryRow>? _debuggerMemoryRowsFull;
    private readonly HttpClient _httpScratchClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private WinForms.NotifyIcon? _trayIcon;

    /// <summary>Маркер между главным файлом и подтянутым модулем (тот же путь, что в <c>use</c>).</summary>
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
        WireAgiTextAreaDebugKeys(CodeEditor);

        _appSettingsPath = ResolveProjectFile("src", "Space.OS.Simulator", "appsettings.json");
        _spaceConfigPath = SpaceEnvironment.GetFilePath("dev.json");
        _vaultPath = ResolveVaultPath();
        LoadAll();
        _consoleTimer.Tick += ConsoleTimer_Tick;
        _consoleTimer.Start();
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            return;

        Hide();
        ShowInTaskbar = false;
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null)
            return;

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "Space.OS.Terminal",
            Icon = TryLoadTrayIcon(),
            Visible = false
        };
        _trayIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                Dispatcher.Invoke(RestoreFromTray);
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Развернуть", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(Close));
        _trayIcon.ContextMenuStrip = menu;
    }

    private static System.Drawing.Icon TryLoadTrayIcon()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // fallback below
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void RestoreFromTray()
    {
        if (_trayIcon != null)
            _trayIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null)
            return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    /// <summary>F5 запуск отладки, F9 Continue. Attach/Asm: F10/F11 — инструкция. Code: F10 строка AGI, F11 заход в вызов.</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleGlobalDebugKeys(e))
            e.Handled = true;
    }

    /// <summary>AvalonEdit может перехватывать клавиши; дублируем F10/F11 на уровне TextArea (туннель после окна).</summary>
    private void WireAgiTextAreaDebugKeys(TextEditor editor)
    {
        editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (TryHandleGlobalDebugKeys(e))
                e.Handled = true;
        };
    }

    /// <returns>true если событие обработано (шаг отладки и т.д.).</returns>
    private bool TryHandleGlobalDebugKeys(KeyEventArgs e)
    {
        if (CodeEditorPanel.Visibility != Visibility.Visible)
            return false;

        if (Keyboard.Modifiers != ModifierKeys.None)
            return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var asmTab = IsAsmSourceTabSelected();
        var attachDebug = _attachDebugSession != null;

        switch (key)
        {
            case Key.F5:
                if (e.IsRepeat)
                    return false;
                CodeEditorDebug_Click(this, new RoutedEventArgs());
                return true;
            case Key.F9:
                if (e.IsRepeat || !DebugContinueButton.IsEnabled)
                    return false;
                DebugContinue_Click(this, new RoutedEventArgs());
                return true;
            case Key.F10:
                if (e.IsRepeat || !DebugStepButton.IsEnabled)
                    return false;
                if (attachDebug || asmTab)
                    DebugStepInto_Click(this, new RoutedEventArgs());
                else
                    DebugStep_Click(this, new RoutedEventArgs());
                return true;
            case Key.F11:
                if (e.IsRepeat || !DebugStepIntoButton.IsEnabled)
                    return false;
                DebugStepInto_Click(this, new RoutedEventArgs());
                return true;
            default:
                return false;
        }
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
            {
                return Path.GetFullPath(candidate);
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

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
            {
                return Path.GetFullPath(candidate);
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

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
        {
            VaultItemsGrid.SelectedIndex = 0;
        }
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
        {
            _vaultRows.AddRange(_workspace.ToRows(selected));
        }

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

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarColumn.Width = _sidebarCollapsed ? new GridLength(56) : new GridLength(290);
    }

    private void NavigationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (NavigationTree.SelectedItem is not TreeViewItem selected || selected.Tag is not string tag)
        {
            return;
        }

        ExecutionUnitsPanel.Visibility = tag == "space-executionunits" ? Visibility.Visible : Visibility.Collapsed;
        VaultPanel.Visibility = tag == "space-vault" ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = tag == "space-storage" ? Visibility.Visible : Visibility.Collapsed;
        CodeEditorPanel.Visibility = tag == "space-code-editor" ? Visibility.Visible : Visibility.Collapsed;
        MonitorPanel.Visibility = tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        ConsolePanel.Visibility = tag == "console-logs" ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = selected.Header?.ToString() ?? "Space.OS.Terminal";
        SectionMeta.Text = tag switch
        {
            "space-executionunits" => _spaceConfigPath,
            "space-vault" => _vaultPath,
            "space-code-editor" => _codeEditorPath ?? string.Empty,
            _ => "monitor"
        };

        if (tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase))
        {
            RefreshMonitor(tag);
        }
        else if (tag == "console-logs")
        {
            DrainConsole();
        }
        else if (tag == "space-storage")
        {
            LoadStorageTree();
        }
        else if (tag == "space-code-editor")
        {
            ReloadCodeEditor();
        }
    }

    private void ExecutionUnitsReload_Click(object sender, RoutedEventArgs e) => LoadExecutionUnits();

    private void ExecutionUnitsAdd_Click(object sender, RoutedEventArgs e)
    {
        _executionUnits.Add(new ExecutionUnitItem { Path = "samples/new_unit.agi", InstanceCount = 1 });
        ExecutionUnitsGrid.Items.Refresh();
    }

    private void ExecutionUnitsDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ExecutionUnitsGrid.SelectedItem is ExecutionUnitItem item)
        {
            _executionUnits.Remove(item);
            ExecutionUnitsGrid.Items.Refresh();
        }
    }

    private void ExecutionUnitsSave_Click(object sender, RoutedEventArgs e)
    {
        _workspace.SaveExecutionUnits(_spaceConfigPath, _executionUnits);
        SectionMeta.Text = $"saved: {_spaceConfigPath}";
    }

    private void ExecutionUnitsRowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ExecutionUnitItem item)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        var fullPath = SpaceEnvironment.GetFilePath(item.Path);
        OpenCodeEditor(fullPath, Math.Max(1, item.InstanceCount));
        SelectNavigationTag("space-code-editor");
    }

    private void StorageReload_Click(object sender, RoutedEventArgs e) => LoadStorageTree();
    private void CodeEditorReload_Click(object sender, RoutedEventArgs e) => ReloadCodeEditor();

    private void CodeEditorSave_Click(object sender, RoutedEventArgs e)
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

    /// <summary>Активная сессия копирует breakpoint только при старте — дублируем набор при клике по margin во время Run.</summary>
    private void SyncBreakpointsToActiveDebugSession()
    {
        _attachDebugSession?.ReplaceBreakpointsFrom(_agiBreakpointsByPath, _asmBreakpoints);
        _activeDebugSession?.ReplaceBreakpointsFrom(_agiBreakpointsByPath, _asmBreakpoints);
    }

    private InterpreterDebugSession? CurrentStepSession
        => _attachDebugSession ?? _activeDebugSession;

    /// <summary>Интерпретатор для панели Memory/Stack: пауза → активный; иначе attach или editor run.</summary>
    private Interpreter? GetInterpreterForDebugger()
        => CurrentStepSession?.PausedInterpreter ?? _attachInterpreter ?? _editorDebugInterpreter;

    private void CodeEditorDebugger_Click(object sender, RoutedEventArgs e)
    {
        ApplyCodeEditorDebuggerLayout(!_codeEditorDebuggerSplitOpen);
        if (_codeEditorDebuggerSplitOpen)
            RefreshCodeEditorDebuggerPanel();
    }

    private void CodeEditorEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (CodeEditorEnvironmentPanel.Visibility == Visibility.Visible)
            ApplyCodeEditorEnvironmentLayout(false);
        else
        {
            ApplyCodeEditorEnvironmentLayout(true);
            SyncEnvironmentPanelsFromBuffers();
        }
    }

    /// <summary>Колонки CodeEditorMainSplitGrid: 0 редактор, 1 splitter, 2 отладчик (справа).</summary>
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
            CodeEditorDebuggerColumnSplitter.Visibility = Visibility.Visible;
            CodeEditorDebuggerPanel.Visibility = Visibility.Visible;
        }
        else
        {
            cols[1].Width = new GridLength(0);
            cols[2].MinWidth = 0;
            cols[2].Width = new GridLength(0);
            CodeEditorDebuggerColumnSplitter.Visibility = Visibility.Collapsed;
            CodeEditorDebuggerPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Строки CodeEditorPanel: 2 splitter, 3 Environment снизу.</summary>
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
            CodeEditorEnvironmentSplitter.Visibility = Visibility.Visible;
            CodeEditorEnvironmentPanel.Visibility = Visibility.Visible;
        }
        else
        {
            rows[2].Height = new GridLength(0);
            rows[2].MinHeight = 0;
            rows[3].Height = new GridLength(0);
            rows[3].MinHeight = 0;
            rows[1].Height = new GridLength(1, GridUnitType.Star);
            CodeEditorEnvironmentSplitter.Visibility = Visibility.Collapsed;
            CodeEditorEnvironmentPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncEnvironmentPanelsFromBuffers()
    {
        CodeEditorEnvLogsTextBox.Text = string.Join(Environment.NewLine, _consoleLines);
        CodeEditorEnvLogsTextBox.CaretIndex = CodeEditorEnvLogsTextBox.Text.Length;
        CodeEditorEnvConsoleTextBox.Text = string.Join(Environment.NewLine, _consoleIoLines);
        CodeEditorEnvConsoleTextBox.CaretIndex = CodeEditorEnvConsoleTextBox.Text.Length;
        RebuildEnvironmentNetworksText();
    }

    private void RebuildEnvironmentNetworksText()
    {
        CodeEditorEnvNetworksTextBox.Text = string.Join(Environment.NewLine,
            _consoleLines.Where(IsDeviceNetworkLogLine));
        CodeEditorEnvNetworksTextBox.CaretIndex = CodeEditorEnvNetworksTextBox.Text.Length;
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

    private async void CodeEditorHttpSend_Click(object sender, RoutedEventArgs e)
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

    private void CodeEditorHttpClearResponse_Click(object sender, RoutedEventArgs e)
        => HttpScratchResponseTextBox.Clear();

    /// <summary>Упрощённый .http: блок до ###, строки @var = value, подстановка {{var}}, заголовки, тело после пустой строки.</summary>
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
        var mode = 0; // 0 = vars+preamble, 1 = request+headers, 2 = body
        string? method = null;
        string? url = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in rawLines)
        {
            var t = line.Trim();
            if (mode == 0)
            {
                if (string.IsNullOrEmpty(t))
                    continue;
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
                if (string.IsNullOrEmpty(t))
                {
                    mode = 2;
                    continue;
                }

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
            {
                contentType = kv.Value;
                continue;
            }

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
        if (CodeEditorDebuggerPanel.Visibility != Visibility.Visible)
            return;

        // RadioButton IsChecked может вызвать Filter_Changed до конструирования второго ребёнка DockPanel (DataGrid).
        if (DebuggerMemoryGrid == null || DebuggerStackGrid == null)
        {
            Dispatcher.BeginInvoke(new Action(RefreshCodeEditorDebuggerPanel), DispatcherPriority.Loaded);
            return;
        }

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
            if (DebuggerMemoryGrid != null)
                DebuggerMemoryGrid.ItemsSource = null;
            if (DebuggerStackGrid != null)
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
        if (DebuggerMemoryGrid == null)
            return;

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
                rows = rows.Where(r =>
                    r.Layer == "Local" || r.Layer.StartsWith("Local@", StringComparison.OrdinalIgnoreCase));
                break;
            case DebuggerMemoryLayerKind.Frame:
                rows = rows.Where(r =>
                    string.Equals(r.Layer, "Inherited", StringComparison.OrdinalIgnoreCase) ||
                    r.Layer.StartsWith("Inherited@", StringComparison.OrdinalIgnoreCase));
                break;
        }

        DebuggerMemoryGrid.ItemsSource = rows.ToList();
    }

    private void DebuggerMemoryLayerFilter_Changed(object sender, RoutedEventArgs e)
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

        if (DebuggerMemoryGrid == null)
            return;

        ApplyDebuggerMemoryLayerFilter();
    }

    private void DebuggerDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.DetailsVisibility = Visibility.Collapsed;
        // После виртуализации кнопка могла остаться с «▼» — синхронизируем с состоянием строки.
        e.Row.Dispatcher.BeginInvoke(() => SyncDebuggerExpandButtonGlyph(e.Row), DispatcherPriority.Loaded);
    }

    private void DebuggerExpandRowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag as string != "DebugInspectExpand")
            return;

        var row = FindAncestor<DataGridRow>(btn);
        if (row == null)
            return;

        var expand = row.DetailsVisibility != Visibility.Visible;
        row.DetailsVisibility = expand ? Visibility.Visible : Visibility.Collapsed;
        btn.Content = expand ? "▼" : "▶";
    }

    private static void SyncDebuggerExpandButtonGlyph(DataGridRow row)
    {
        var btn = FindVisualChild<Button>(row, b => b.Tag as string == "DebugInspectExpand");
        if (btn == null)
            return;
        btn.Content = row.DetailsVisibility == Visibility.Visible ? "▼" : "▶";
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (predicate == null || predicate(t)))
                return t;
            var nested = FindVisualChild(child, predicate);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null && source is not T)
            source = VisualTreeHelper.GetParent(source);
        return source as T;
    }

    /// <summary>Кнопки «развернуть всё» в той же строке Grid, что и TreeView — не предок; ищем TreeView среди детей предков-Grid.</summary>
    private static TreeView? FindInspectRowDetailsTreeView(DependencyObject? from)
    {
        while (from != null)
        {
            if (from is Grid)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
                {
                    if (VisualTreeHelper.GetChild(from, i) is TreeView tv)
                        return tv;
                }
            }

            from = VisualTreeHelper.GetParent(from);
        }

        return null;
    }

    private void DebuggerInspectExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject src && FindInspectRowDetailsTreeView(src) is { } tree)
        {
            tree.Dispatcher.BeginInvoke(() =>
            {
                tree.UpdateLayout();
                ExpandAllTreeViewItems(tree);
            }, DispatcherPriority.Loaded);
        }

        e.Handled = true;
    }

    private void DebuggerInspectCollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject src && FindInspectRowDetailsTreeView(src) is { } tree)
        {
            tree.Dispatcher.BeginInvoke(() =>
            {
                tree.UpdateLayout();
                CollapseAllTreeViewItems(tree);
            }, DispatcherPriority.Loaded);
        }

        e.Handled = true;
    }

    /// <summary>Несколько проходов + UpdateLayout: дочерние контейнеры TreeView появляются только после раскрытия родителя.</summary>
    private static void ExpandAllTreeViewItems(TreeView tree)
    {
        for (var pass = 0; pass < 64; pass++)
        {
            if (!ExpandOnePassAllCollapsedParents(tree))
                break;
            tree.UpdateLayout();
        }
    }

    private static bool ExpandOnePassAllCollapsedParents(ItemsControl parent)
    {
        var changed = false;
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi)
                continue;
            if (tvi.HasItems && !tvi.IsExpanded)
            {
                tvi.IsExpanded = true;
                changed = true;
            }

            if (ExpandOnePassAllCollapsedParents(tvi))
                changed = true;
        }

        return changed;
    }

    private static void CollapseAllTreeViewItems(TreeView tree)
    {
        foreach (var item in tree.Items)
        {
            if (tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                CollapseTreeItemRecursive(tvi);
        }
    }

    private static void CollapseTreeItemRecursive(TreeViewItem item)
    {
        foreach (var sub in item.Items)
        {
            if (item.ItemContainerGenerator.ContainerFromItem(sub) is TreeViewItem child)
                CollapseTreeItemRecursive(child);
        }

        item.IsExpanded = false;
    }

    private void RefreshCodeEditorAttachUi()
    {
        var hasPath = !string.IsNullOrWhiteSpace(_codeEditorPath) && File.Exists(_codeEditorPath);
        if (!hasPath)
        {
            CodeEditorRunningHint.Visibility = Visibility.Collapsed;
            DebugAttachButton.Visibility = Visibility.Collapsed;
            DebugRestartRunningExeButton.Visibility = Visibility.Collapsed;
            return;
        }

        var full = Path.GetFullPath(_codeEditorPath!);
        var running = _executionRecords.Exists(r =>
            string.Equals(Path.GetFullPath(r.SourcePath), full, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase));

        CodeEditorRunningHint.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        DebugAttachButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        DebugAttachButton.IsEnabled = running && _attachDebugSession == null;
        DebugRestartRunningExeButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        DebugRestartRunningExeButton.IsEnabled = running;
    }

    private void OnInterpreterAttachRegistryUnregistered(string? unitName, string? attachPath, int instanceIndex)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnInterpreterAttachRegistryUnregistered(unitName, attachPath, instanceIndex));
            return;
        }

        if (_attachDebugSession != null
            && !string.IsNullOrWhiteSpace(attachPath)
            && !string.IsNullOrWhiteSpace(_codeEditorPath)
            && string.Equals(Path.GetFullPath(attachPath), Path.GetFullPath(_codeEditorPath), StringComparison.OrdinalIgnoreCase)
            && instanceIndex == _attachInstanceIndex)
        {
            DetachFromRunningExe();
        }
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
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        SetDebugToolbarPaused(false);
        SectionMeta.Text = _codeEditorPath ?? string.Empty;
        RefreshCodeEditorAttachUi();
    }

    private void DebugAttach_Click(object sender, RoutedEventArgs e)
    {
        if (_kernel == null || string.IsNullOrWhiteSpace(_codeEditorPath) || !File.Exists(_codeEditorPath))
        {
            MessageBox.Show(this, "Файл не выбран.", "Attach");
            return;
        }

        if (_attachDebugSession != null)
            return;

        var max = Math.Max(1, _codeEditorAttachInstanceCap);
        if (!_kernel.AttachRegistry.TryGetBySourcePath(_codeEditorPath, max, out var interp, out var idx) || interp == null)
        {
            MessageBox.Show(this, "Нет запущенного интерпретатора для этого .agi (дождитесь running в Startup Exes).", "Attach");
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

    /// <summary>См. context.md: остановка корневых интерпретаторов по пути + перезапуск из dev.json / повтор F5.</summary>
    private async void DebugRestartRunningExe_Click(object sender, RoutedEventArgs e)
    {
        if (_kernel == null || string.IsNullOrWhiteSpace(_codeEditorPath) || !File.Exists(_codeEditorPath))
        {
            MessageBox.Show(this, "Файл не выбран.", "Restart");
            return;
        }

        var fullPath = Path.GetFullPath(_codeEditorPath);
        var runningUi = _executionRecords.Exists(r =>
            string.Equals(Path.GetFullPath(r.SourcePath), fullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase));
        if (!runningUi)
        {
            MessageBox.Show(this, "Нет записи running для этого файла.", "Restart");
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
                        MessageBox.Show(this, tabErr ?? "Save failed.", "Restart — save");
                        return;
                    }
                }
                else if (!TryPersistLinkedCodeEditorIfApplicable(out var linkErr))
                {
                    if (linkErr != null)
                    {
                        MessageBox.Show(this, linkErr, "Restart — save");
                        return;
                    }

                    File.WriteAllText(_codeEditorPath, CodeEditor.Document.Text);
                }

                SectionMeta.Text = $"saved + restart: {_codeEditorPath}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Restart — save");
                return;
            }

            foreach (var (_, interp) in _kernel.AttachRegistry.EnumerateBySourcePath(fullPath, scanCap))
                RequestInterpreterCooperativeStop(interp);

            _attachDebugSession?.RequestStop();
            _activeDebugSession?.RequestStop();

            await WaitForAgiRunSessionsEndedAsync(fullPath, scanCap).ConfigureAwait(true);

            if (_kernel.AttachRegistry.HasAnyForSourcePath(fullPath, scanCap))
            {
                MessageBox.Show(this, "Таймаут остановки интерпретаторов.", "Restart");
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
                MessageBox.Show(this, "Running startup для этого .agi, но путь не найден в executionUnits dev.json — новый процесс не запущен.", "Restart");
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

            // Корень часто висит в await claw (GetContextAsync) — RequestStop по инструкциям не доходит; Stop listener разблокирует.
            var elapsed = (int)sw.ElapsedMilliseconds;
            if (elapsed - lastForceCloseMs >= 400)
            {
                lastForceCloseMs = elapsed;
                foreach (var (_, interp) in _kernel.AttachRegistry.EnumerateBySourcePath(fullPath, scanCap))
                {
                    try
                    {
                        await interp.ForceCloseOwnedStreamsAsync().ConfigureAwait(true);
                    }
                    catch
                    {
                        // best-effort
                    }
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
        // Stop нужен и между паузами: после Continue на await (claw и т.д.) интерпретатор не в WaitPausedAsync, но сессия жива.
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
        void apply()
        {
            // Пока не выставили SelectedItem в Code — иначе IsAsmSourceTabSelected() всегда false.
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

            CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            CodeAsmEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            foreach (var h in _linkedModuleEditors)
                h.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

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
            catch
            {
                // ignore scroll/caret errors
            }

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
            catch
            {
                // ignore
            }

            if (stayOnAsmTab)
            {
                CodeAsmEditor.Focus();
                CodeAsmEditor.TextArea.Focus();
            }
            else if (agiFocus != null)
            {
                agiFocus.Focus();
                agiFocus.TextArea.Focus();
            }

            RefreshCodeEditorDebuggerPanel();
            RefreshMonitor(CurrentMonitorTag());
        }

        if (Dispatcher.CheckAccess())
            apply();
        else
            Dispatcher.Invoke(apply);
    }

    private void CodeEditorDebug_Click(object sender, RoutedEventArgs e)
    {
        _ = RunCodeEditorDebugAsync();
    }

    private async Task RunCodeEditorDebugAsync()
    {
        if (_kernel == null)
        {
            MessageBox.Show(this, "Kernel not ready.", "Debug");
            return;
        }

        if (_attachDebugSession != null)
            DetachFromRunningExe();

        FlushLinkedModuleFilesFromEditors();
        var source = GetCodeEditorMainSourceForCompile();
        var comp = await _kernel.CompileAsync(source, _codeEditorPath).ConfigureAwait(true);
        if (!comp.Success || comp.Result == null)
        {
            MessageBox.Show(this, comp.ErrorMessage ?? "Compilation failed.", "Debug");
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
            Dispatcher.Invoke(() =>
            {
                debugRecord.Success = false;
                debugRecord.Status = "error";
                debugRecord.EndedAtUtc = DateTimeOffset.UtcNow;
                debugRecord.ErrorMessage = ex.Message;
                RefreshMonitor(CurrentMonitorTag());
                SectionMeta.Text = $"debug: error {ex.Message}";
                MessageBox.Show(this, ex.Message, "Debug");
            });
        }
        finally
        {
            session.PausedAtDebugLocation -= OnInterpreterPausedAtDebugLocation;
            session.EndRun();
            _activeDebugSession = null;
            _editorDebugInterpreter = null;
            _editorDebugStdin = null;
            Dispatcher.Invoke(() =>
            {
                _debugHighlightDocumentLine = 0;
                _debugHighlightAgiPath = null;
                _debugHighlightAsmLine = 0;
                CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                CodeAsmEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                foreach (var h in _linkedModuleEditors)
                    h.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
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

    private void DebugContinue_Click(object sender, RoutedEventArgs e)
    {
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        CurrentStepSession?.RequestContinue();
        SetDebugToolbarPaused(false);
    }

    /// <summary>Следующая строка исходника (раньше была одна инструкция — на одной строке казалось, что «шаг» не работает).</summary>
    private void DebugStep_Click(object sender, RoutedEventArgs e)
    {
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        CurrentStepSession?.RequestStepOverLine();
        SetDebugToolbarPaused(false);
    }

    private void DebugStepInto_Click(object sender, RoutedEventArgs e)
    {
        _debugHighlightDocumentLine = 0;
        _debugHighlightAgiPath = null;
        _debugHighlightAsmLine = 0;
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        CodeAsmEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var h in _linkedModuleEditors)
            h.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        var asmTab = IsAsmSourceTabSelected();
        var attachDebug = _attachDebugSession != null;
        if (attachDebug || asmTab)
            CurrentStepSession?.RequestStepInstruction();
        else
            CurrentStepSession?.RequestStepInto();
        SetDebugToolbarPaused(false);
    }

    private void DebugStop_Click(object sender, RoutedEventArgs e)
    {
        if (_attachDebugSession != null)
        {
            // Нельзя сразу Detach: он обнуляет DebugSession до отмены — интерпретатор перестаёт видеть ContinueCancellationToken.
            // RequestStop → выход задачи → OnInterpreterAttachRegistryUnregistered → DetachFromRunningExe.
            _attachDebugSession.RequestStop();
            SetDebugToolbarPaused(false);
            return;
        }

        _activeDebugSession?.RequestStop();
        SetDebugToolbarPaused(false);
    }

    private void OnAnyAgiSourceDocument_TextChanged(object? sender, EventArgs e)
    {
        _codeHighlightTimer.Stop();
        _codeHighlightTimer.Start();
        if (IsAsmSourceTabSelected())
            RequestAsmViewRefresh();
    }

    private void CodeEditorSourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != CodeEditorSourceTabControl)
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

    private void VaultReload_Click(object sender, RoutedEventArgs e) => LoadVaultItems();

    private void VaultAdd_Click(object sender, RoutedEventArgs e)
    {
        _vaultItems.Add(new VaultItem { Program = "program", System = "system", Module = "module", InstanceIndex = 0 });
        VaultItemsGrid.Items.Refresh();
    }

    private void VaultDelete_Click(object sender, RoutedEventArgs e)
    {
        if (VaultItemsGrid.SelectedItem is VaultItem item)
        {
            _vaultItems.Remove(item);
            VaultItemsGrid.Items.Refresh();
            RefreshVaultRows();
        }
    }

    private void VaultSave_Click(object sender, RoutedEventArgs e)
    {
        if (VaultItemsGrid.SelectedItem is VaultItem selected)
        {
            _workspace.ApplyRows(selected, _vaultRows);
        }

        _workspace.SaveVaultItems(_vaultItems, _vaultPath);
        SectionMeta.Text = $"saved: {_vaultPath}";
    }

    private void VaultItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshVaultRows();

    private void VaultStoreGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not VaultStoreRow row)
            return;
        if (!string.Equals(e.Column.Header?.ToString(), "Value", StringComparison.Ordinal))
            return;
        if (row.IsSensitive && !row.SensitiveRevealed)
            e.Cancel = true;
    }

    private void VaultSensitiveReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is VaultStoreRow row && row.IsSensitive)
            row.SensitiveRevealed = !row.SensitiveRevealed;
    }

    private void VaultCopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not VaultStoreRow row)
            return;
        try
        {
            Clipboard.SetText(row.Value ?? string.Empty);
        }
        catch (Exception ex)
        {
            SectionMeta.Text = $"clipboard: {ex.Message}";
        }
    }

    private void DebuggerInspectSensitiveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is InterpreterInspectNode n)
            n.ToggleSensitiveReveal();
        e.Handled = true;
    }

    private void DebuggerInspectCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is InterpreterInspectNode n)
        {
            var t = n.GetCopyableText();
            if (string.IsNullOrEmpty(t))
                return;
            try
            {
                Clipboard.SetText(t);
            }
            catch (Exception ex)
            {
                SectionMeta.Text = $"clipboard: {ex.Message}";
            }
        }

        e.Handled = true;
    }

    private void DebuggerInspectViewPayload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is InterpreterInspectNode { BinaryPayload: { } payload })
        {
            var w = new BinaryPayloadViewerWindow(payload) { Owner = this };
            w.Show();
        }

        e.Handled = true;
    }

    private void MonitorRefresh_Click(object sender, RoutedEventArgs e)
    {
        var tag = (NavigationTree.SelectedItem as TreeViewItem)?.Tag?.ToString() ?? "monitor-exes";
        RefreshMonitor(tag);
    }

    private void ConsoleTimer_Tick(object? sender, EventArgs e)
    {
        DrainConsole();
        MaybeRefreshMonitorDuringActiveDebug();
    }

    /// <summary>
    /// При Run из executionUnits монитор дергается из фоновых задач; при Debug из редактора — нет,
    /// поэтому вкладка Streams не обновлялась, пока не переключить раздел или не нажать Refresh.
    /// </summary>
    private void MaybeRefreshMonitorDuringActiveDebug()
    {
        if (_activeDebugSession == null && _attachDebugSession == null)
        {
            _monitorDebugRefreshThrottle = 0;
            return;
        }

        if (MonitorPanel.Visibility != Visibility.Visible)
            return;

        var tag = CurrentMonitorTag();
        if (!tag.StartsWith("monitor-", StringComparison.OrdinalIgnoreCase))
            return;

        // ~2 s при интервале таймера 250 ms
        if (++_monitorDebugRefreshThrottle < 8)
            return;

        _monitorDebugRefreshThrottle = 0;
        RefreshMonitor(tag);
    }

    private void ConsoleRefresh_Click(object sender, RoutedEventArgs e) => DrainConsole();

    private void ConsoleClear_Click(object sender, RoutedEventArgs e)
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
        {
            return;
        }

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
        {
            ConsoleLogsTextBox.Text = string.Join(Environment.NewLine, _consoleLines);
        }
        else
        {
            foreach (var line in drained)
            {
                if (ConsoleLogsTextBox.Text.Length > 0)
                    ConsoleLogsTextBox.AppendText(Environment.NewLine);
                ConsoleLogsTextBox.AppendText(line);
            }
        }

        ConsoleLogsTextBox.CaretIndex = ConsoleLogsTextBox.Text.Length;

        var codeEditorVisible = CodeEditorPanel.Visibility == Visibility.Visible;
        var envVisible = CodeEditorEnvironmentPanel.Visibility == Visibility.Visible;
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
                if (CodeEditorEnvLogsTextBox.Text.Length > 0)
                    CodeEditorEnvLogsTextBox.AppendText(Environment.NewLine);
                CodeEditorEnvLogsTextBox.AppendText(line);
                AppendLineToTextBox(CodeEditorEnvConsoleTextBox, line);
                if (IsDeviceNetworkLogLine(line))
                {
                    if (CodeEditorEnvNetworksTextBox.Text.Length > 0)
                        CodeEditorEnvNetworksTextBox.AppendText(Environment.NewLine);
                    CodeEditorEnvNetworksTextBox.AppendText(line);
                }
            }
        }

        CodeEditorEnvLogsTextBox.CaretIndex = CodeEditorEnvLogsTextBox.Text.Length;
        CodeEditorEnvNetworksTextBox.CaretIndex = CodeEditorEnvNetworksTextBox.Text.Length;
        CodeEditorEnvConsoleTextBox.CaretIndex = CodeEditorEnvConsoleTextBox.Text.Length;
    }

    private static void AppendLineToTextBox(TextBox textBox, string line)
    {
        if (textBox.Text.Length > 0)
            textBox.AppendText(Environment.NewLine);
        textBox.AppendText(line);
    }

    private void CodeEditorEnvConsoleSend_Click(object sender, RoutedEventArgs e)
    {
        SendEnvironmentConsoleInput();
    }

    private void CodeEditorEnvConsoleInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
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

        if (CodeEditorPanel.Visibility == Visibility.Visible && CodeEditorEnvironmentPanel.Visibility == Visibility.Visible)
        {
            AppendLineToTextBox(CodeEditorEnvConsoleTextBox, echo);
            CodeEditorEnvConsoleTextBox.CaretIndex = CodeEditorEnvConsoleTextBox.Text.Length;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
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
        await Dispatcher.InvokeAsync(() =>
        {
            record.Status = "running";
            RefreshMonitor("monitor-exes");
            RefreshCodeEditorAttachUi();
        });

        try
        {
            var host = new ExecutionUnitHost(_kernel!);
            var ok = await _runService.RunConfiguredUnitAsync(host, unit).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                record.Success = ok;
                record.Status = ok ? "stopped" : "error";
                record.EndedAtUtc = DateTimeOffset.UtcNow;
                if (!ok && string.IsNullOrWhiteSpace(record.ErrorMessage))
                {
                    record.ErrorMessage = "Execution returned false.";
                }

                RefreshMonitor("monitor-exes");
                RefreshCodeEditorAttachUi();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
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
        DisposeTrayIcon();
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
            {
                rootNode.Items.Add(child);
            }
        }

        foreach (var file in dirInfo.GetFiles("*.agi").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            rootNode.Items.Add(CreateAgiFileNode(file.FullName));
        }

        return rootNode;
    }

    private TreeViewItem CreateAgiFileNode(string fullPath)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(fullPath),
            VerticalAlignment = VerticalAlignment.Center
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

        return new TreeViewItem
        {
            Header = stack,
            Tag = fullPath
        };
    }

    private void StorageEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path)
        {
            return;
        }

        OpenCodeEditor(path);
        SelectNavigationTag("space-code-editor");
    }

    private void SelectNavigationTag(string tag)
    {
        foreach (var item in NavigationTree.Items)
        {
            if (item is TreeViewItem root && TrySelectByTag(root, tag))
            {
                return;
            }
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
            try
            {
                File.WriteAllText(h.FilePath, h.Editor.Document.Text ?? string.Empty);
            }
            catch
            {
                // компилятор прочитает последнее удачное состояние с диска
            }
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
            HorizontalScrollBarVisibility = CodeEditor.HorizontalScrollBarVisibility,
            VerticalScrollBarVisibility = CodeEditor.VerticalScrollBarVisibility,
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

    /// <summary>Вкладки с модулями по <c>use</c> (перед Asm). Порядок как в исходнике.</summary>
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
            WireAgiTextAreaDebugKeys(editor);
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

    /// <returns>True если сохранён bundle (main + модули). False и error=null — обычный один файл; false и error — ошибка разбора/IO.</returns>
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
}
