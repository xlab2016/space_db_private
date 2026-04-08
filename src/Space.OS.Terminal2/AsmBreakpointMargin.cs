using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Space.OS.Terminal2;

/// <summary>
/// Breakpoint по номеру строки листинга AGIASM (1-based). Клик — полоса слева от номеров строк, не сами цифры.
/// </summary>
internal sealed class AsmBreakpointMargin : AbstractMargin
{
    private readonly HashSet<int> _breakpoints;
    private readonly Action? _onBreakpointsChanged;

    private static readonly IBrush HitTestSlabBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0x5C, 0x5C)); // IndianRed
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00)), 1); // DarkRed

    private const double GutterWidth = 28;

    public AsmBreakpointMargin(HashSet<int> breakpoints, Action? onBreakpointsChanged = null)
    {
        _breakpoints = breakpoints;
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

        drawingContext.FillRectangle(HitTestSlabBrush, new Rect(Bounds.Size));

        foreach (var line in textView.VisualLines)
        {
            var ln = line.FirstDocumentLine.LineNumber;
            if (!_breakpoints.Contains(ln))
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

        var pos = e.GetPosition(TextView);
        pos = pos.WithY(pos.Y + TextView.VerticalOffset);
        var vl = TextView.GetVisualLineFromVisualTop(pos.Y);
        if (vl == null)
            return;

        var ln = vl.FirstDocumentLine.LineNumber;
        if (!_breakpoints.Add(ln))
            _breakpoints.Remove(ln);

        _onBreakpointsChanged?.Invoke();

        e.Handled = true;
        InvalidateVisual();
    }
}
