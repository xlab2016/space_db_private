using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Magic.Kernel.Interpretation;
using Microsoft.Win32;

namespace Space.OS.Terminal;

/// <summary>Hex-просмотр больших <see cref="BinaryDebugPayload"/> вне основного дерева (без зависания UI).</summary>
public sealed class BinaryPayloadViewerWindow : Window
{
    private const int MaxInitialHexBytes = 48 * 1024;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly TextBlock _status;
    private readonly TextBox _body;

    public BinaryPayloadViewerWindow(BinaryDebugPayload payload)
    {
        _data = payload.Data;
        Title = "Двоичные данные · отладчик";
        Width = 780;
        Height = 560;
        MinWidth = 480;
        MinHeight = 320;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 12, 28));

        var root = new DockPanel { Margin = new Thickness(10) };

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.LightSteelBlue,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var copyBtn = new Button { Content = "Копировать видимое", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        copyBtn.Click += (_, _) => CopyVisible();
        buttons.Children.Add(copyBtn);
        var saveBtn = new Button { Content = "Сохранить в файл…", Padding = new Thickness(12, 4, 12, 4) };
        saveBtn.Click += (_, _) => SaveToFile();
        buttons.Children.Add(saveBtn);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _body = new TextBox
        {
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gainsboro,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(12, 19, 50)),
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true
        };
        root.Children.Add(_body);

        Content = root;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var len = _data.Length;
        var take = (int)Math.Min(MaxInitialHexBytes, len);
        _status.Text = len == 0
            ? "Пустой буфер."
            : take < len
                ? $"Всего {len} байт. Ниже — первые {take} в hex (остальное через «Сохранить в файл»)."
                : $"Всего {len} байт (hex).";

        if (len == 0)
        {
            _body.Text = "";
            return;
        }

        try
        {
            var bytes = _data.Slice(0, take).ToArray();
            var dump = await System.Threading.Tasks.Task.Run(() => BuildHexDump(bytes)).ConfigureAwait(true);
            _body.Text = dump;
        }
        catch (Exception ex)
        {
            _body.Text = $"Ошибка построения дампа: {ex.Message}";
        }
    }

    private void CopyVisible()
    {
        try
        {
            Clipboard.SetText(_body.Text ?? "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveToFile()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Binary|*.bin|All|*.*",
            FileName = "payload.bin"
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllBytes(dlg.FileName, _data.ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Сохранение", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string BuildHexDump(byte[] bytes)
    {
        const int row = 16;
        var sb = new StringBuilder(bytes.Length * 4 + 64);
        for (var off = 0; off < bytes.Length; off += row)
        {
            sb.Append(off.ToString("X8"));
            sb.Append("  ");
            var lineLen = Math.Min(row, bytes.Length - off);
            for (var i = 0; i < lineLen; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[off + i].ToString("X2"));
            }

            for (var pad = lineLen; pad < row; pad++)
                sb.Append("   ");
            sb.Append("  │ ");
            for (var i = 0; i < lineLen; i++)
            {
                var b = bytes[off + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
