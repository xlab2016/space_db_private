using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Space.OS.Terminal2;

/// <summary>
/// Клик по узкой полосе слева от номеров строк (не по самим цифрам) — вкл/выкл breakpoint (1-based) для .agi.
/// В Avalonia без фона margin не получает hit-test; полоса намеренно шире 14px, чтобы попасть было проще.
/// </summary>
internal sealed class AgiBreakpointMargin : AbstractMargin
{
    private readonly Func<string> _getFilePath;
    private readonly Dictionary<string, HashSet<int>> _breakpointsByPath;
    private readonly Action? _onBreakpointsChanged;

    /// <summary>Alpha=1: невидимо, но даёт геометрию для hit-test (иначе Pointer* не приходят).</summary>
    private static readonly IBrush HitTestSlabBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0x5C, 0x5C)); // IndianRed
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00)), 1); // DarkRed

    /// <summary>Ширина «желоба» слева от колонки номеров (пиксели логические).</summary>
    private const double GutterWidth = 28;

    public AgiBreakpointMargin(Func<string> getFilePath, Dictionary<string, HashSet<int>> breakpointsByPath, Action? onBreakpointsChanged = null)
    {
        _getFilePath = getFilePath;
        _breakpointsByPath = breakpointsByPath;
        _onBreakpointsChanged = onBreakpointsChanged;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null)
            oldTextView.VisualLinesChanged -= OnLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
            newTextView.VisualLinesChanged += OnLinesChanged;
        InvalidateVisual();
    }

    private void OnLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid)
            return;

        var path = _getFilePath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Без заливки по bounds первый breakpoint поставить нельзя: для пустого набора раньше был return — hit-test пустой.
        drawingContext.FillRectangle(HitTestSlabBrush, new Rect(Bounds.Size));

        var key = Path.GetFullPath(path);
        if (!_breakpointsByPath.TryGetValue(key, out var bps) || bps.Count == 0)
            return;

        foreach (var line in textView.VisualLines)
        {
            var ln = line.FirstDocumentLine.LineNumber;
            if (!bps.Contains(ln))
                continue;

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
            var center = new Point(GutterWidth * 0.5, y + 9);
            drawingContext.DrawEllipse(FillBrush, BorderPen, center, 5, 5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView == null)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed)
            return;

        var path = _getFilePath();
        if (string.IsNullOrWhiteSpace(path))
            return;
        var key = Path.GetFullPath(path);

        var pos = e.GetPosition(TextView);
        pos = pos.WithY(pos.Y + TextView.VerticalOffset);
        var vl = TextView.GetVisualLineFromVisualTop(pos.Y);
        if (vl == null)
            return;

        var ln = vl.FirstDocumentLine.LineNumber;
        if (!_breakpointsByPath.TryGetValue(key, out var set))
        {
            set = new HashSet<int>();
            _breakpointsByPath[key] = set;
        }

        if (!set.Add(ln))
            set.Remove(ln);

        _onBreakpointsChanged?.Invoke();

        e.Handled = true;
        InvalidateVisual();
    }
}
