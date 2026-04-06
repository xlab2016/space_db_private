using System.ComponentModel;
using System.Runtime.CompilerServices;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Terminal.Models;

public sealed class ExecutionUnitItem
{
    public string Path { get; set; } = string.Empty;
    public int InstanceCount { get; set; } = 1;

    public string Id { get; set; } = string.Empty;
    public string? CodeDirectory { get; set; }
    public string? ProgramFile { get; set; }
    public string? VaultFile { get; set; }
    public string? RocksDbPath { get; set; }
    public string? SpaceName { get; set; }
}

public sealed class VaultItem
{
    public string Program { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int InstanceIndex { get; set; }
    public Dictionary<string, object?> Store { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VaultStoreRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _key = "";
    public string Key
    {
        get => _key;
        set
        {
            if (_key == value) return;
            _key = value ?? "";
            Notify();
        }
    }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value ?? "";
            Notify();
            Notify(nameof(ValueDisplay));
        }
    }

    private bool _isSensitive;
    public bool IsSensitive
    {
        get => _isSensitive;
        set
        {
            if (_isSensitive == value) return;
            _isSensitive = value;
            Notify();
            Notify(nameof(ValueDisplay));
            Notify(nameof(RevealButtonCaption));
        }
    }

    private bool _sensitiveRevealed;
    /// <summary>Только UI: реальное значение sensitive не показываем, пока не нажали «Показать».</summary>
    public bool SensitiveRevealed
    {
        get => _sensitiveRevealed;
        set
        {
            if (_sensitiveRevealed == value) return;
            _sensitiveRevealed = value;
            Notify();
            Notify(nameof(ValueDisplay));
            Notify(nameof(RevealButtonCaption));
        }
    }

    /// <summary>Текст в ячейке: маска для скрытого sensitive.</summary>
    public string ValueDisplay => IsSensitive && !SensitiveRevealed ? "••••••••" : Value;

    public string RevealButtonCaption => SensitiveRevealed ? "Скрыть" : "Показать";
}

public sealed class MonitorRow
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string State { get; set; } = "n/a";
    public string Details { get; set; } = string.Empty;
}

public sealed class MonitorSnapshot
{
    public List<MonitorRow> Exes { get; } = new();
    public List<MonitorRow> Streams { get; } = new();
    public List<MonitorRow> Drivers { get; } = new();
    public List<MonitorRow> Connections { get; } = new();
    public int TotalDevices { get; set; }
}

public sealed class RuntimeExecutionRecord
{
    public string Status { get; set; } = "starting";
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public bool Success { get; set; }
    public int InstanceCount { get; set; } = 1;
    public string? ErrorMessage { get; set; }
}

public static class DeviceClassification
{
    public static bool IsDriver(IDevice device)
        => device.GetType().Namespace?.Contains(".Drivers", StringComparison.OrdinalIgnoreCase) == true ||
           device.GetType().Name.EndsWith("Driver", StringComparison.OrdinalIgnoreCase);

    public static bool IsStream(IDevice device)
        => device.GetType().Name.Contains("Stream", StringComparison.OrdinalIgnoreCase);
}
