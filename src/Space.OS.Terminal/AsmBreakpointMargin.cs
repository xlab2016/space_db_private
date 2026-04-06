using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Space.OS.Terminal;

/// <summary>Breakpoint по номеру строки листинга AGIASM (1-based).</summary>
internal sealed class AsmBreakpointMargin : AbstractMargin
{
    private readonly HashSet<int> _breakpoints;
    private readonly Action? _onBreakpointsChanged;

    public AsmBreakpointMargin(HashSet<int> breakpoints, Action? onBreakpointsChanged = null)
    {
        _breakpoints = breakpoints;
        _onBreakpointsChanged = onBreakpointsChanged;
        ClipToBounds = false;
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override Size MeasureOverride(Size availableSize) => new(14, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null)
            oldTextView.VisualLinesChanged -= OnLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
            newTextView.VisualLinesChanged += OnLinesChanged;
        InvalidateVisual();
    }

    private void OnLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid)
            return;

        foreach (var line in textView.VisualLines)
        {
            var ln = line.FirstDocumentLine.LineNumber;
            if (!_breakpoints.Contains(ln))
                continue;

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
            var center = new Point(7, y + 9);
            drawingContext.DrawEllipse(Brushes.IndianRed, new Pen(Brushes.DarkRed, 1), center, 5, 5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TextView == null)
            return;

        var pos = e.GetPosition(TextView);
        pos.Y += TextView.VerticalOffset;
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
