using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Space.OS.Terminal;

/// <summary>Подсветка текста AGIASM (директивы, опкоды, операнды, хвост // из исходника).</summary>
internal sealed class MagicAgiasmColorizer : DocumentColorizingTransformer
{
    private static readonly Brush PlainBrush = CreateBrush(0xC8, 0xE0, 0xD0);
    private static readonly Brush KeywordBrush = CreateBrush(0x6C, 0x9E, 0xFF);
    private static readonly Brush IdentifierBrush = CreateBrush(0xA8, 0xC5, 0xFF);
    private static readonly Brush StringBrush = CreateBrush(0x7E, 0xDC, 0x9A);
    private static readonly Brush NumberBrush = CreateBrush(0xF5, 0xA8, 0x7A);
    private static readonly Brush CommentBrush = CreateBrush(0x6B, 0x7A, 0x99);
    private static readonly Brush PunctuationBrush = CreateBrush(0x9A, 0xA8, 0xCC);

    private static readonly HashSet<string> StructureKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "program", "module", "procedure", "function", "entrypoint"
    };

    private static readonly HashSet<string> Opcodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nop", "ret", "def", "defgen", "awaitobj", "await", "streamwaitobj", "getobj", "setobj", "streamwait",
        "label", "je", "jmp", "cmp", "expr", "defexpr", "lambda", "equals", "not", "lt", "callobj",
        "addvertex", "addrelation", "addshape", "call", "acall", "push", "pop", "global"
    };

    private IReadOnlyList<(int start, int end, Brush brush)> _spans = [];

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void DocumentUpdated(TextDocument document)
    {
        var text = document.Text ?? string.Empty;
        var list = new List<(int, int, Brush)>();
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var nl = !atEnd && text[i] == '\n';
            var cr = !atEnd && text[i] == '\r';
            if (!atEnd && !nl && !cr)
                continue;

            var lineLen = i - lineStart;
            if (lineLen > 0)
                AddSpansForLine(text, lineStart, lineLen, list);

            if (atEnd)
                break;
            if (cr && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            lineStart = i + 1;
        }

        _spans = list;
    }

    private static void AddSpansForLine(string text, int lineOffset, int lineLen, List<(int, int, Brush)> list)
    {
        if (lineLen <= 0)
            return;
        var line = text.AsSpan(lineOffset, lineLen);
        if (line.Trim().IsEmpty)
            return;

        var commentIdx = FindTrailingCommentIndex(line);
        if (commentIdx >= 0)
            list.Add((lineOffset + commentIdx, lineOffset + lineLen, CommentBrush));

        var instrEnd = commentIdx >= 0 ? commentIdx : lineLen;
        var s = 0;
        while (s < instrEnd && char.IsWhiteSpace(line[s]))
            s++;
        var e = instrEnd;
        while (e > s && char.IsWhiteSpace(line[e - 1]))
            e--;
        if (e <= s)
            return;

        var baseOff = lineOffset + s;
        var instr = text.AsSpan(baseOff, e - s);
        var instrEndOff = baseOff + instr.Length;

        if (instr.StartsWith("@AGIASM".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            const int kwLen = 7;
            list.Add((baseOff, baseOff + kwLen, KeywordBrush));
            TokenizeOperands(text, baseOff + kwLen, instrEndOff, list);
            return;
        }

        if (instr.StartsWith("@AGI".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            const int kwLen = 4;
            list.Add((baseOff, baseOff + kwLen, KeywordBrush));
            TokenizeOperands(text, baseOff + kwLen, instrEndOff, list);
            return;
        }

        var firstWordEndRel = FindWordEnd(instr, 0);
        if (firstWordEndRel <= 0)
        {
            TokenizeOperands(text, baseOff, instrEndOff, list);
            return;
        }

        var firstWord = instr[..firstWordEndRel].ToString();
        if (StructureKeywords.Contains(firstWord))
        {
            list.Add((baseOff, baseOff + firstWordEndRel, KeywordBrush));
            TokenizeOperands(text, baseOff + firstWordEndRel, instrEndOff, list);
            return;
        }

        if (Opcodes.Contains(firstWord))
        {
            list.Add((baseOff, baseOff + firstWordEndRel, KeywordBrush));
            TokenizeOperands(text, baseOff + firstWordEndRel, instrEndOff, list);
            return;
        }

        TokenizeOperands(text, baseOff, instrEndOff, list);
    }

    /// <summary>Первый <c>//</c> вне строковых литералов.</summary>
    private static int FindTrailingCommentIndex(ReadOnlySpan<char> line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                var bs = 0;
                for (var k = i - 1; k >= 0 && line[k] == '\\'; k--)
                    bs++;
                if (bs % 2 == 0)
                    inString = !inString;
            }

            if (inString)
                continue;
            if (c == '/' && line[i + 1] == '/')
                return i;
        }

        return -1;
    }

    private static int FindWordEnd(ReadOnlySpan<char> s, int start)
    {
        var i = start;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
        var w = i;
        while (w < s.Length && !char.IsWhiteSpace(s[w]) && s[w] != ',' && s[w] != ':' && s[w] != '[' && s[w] != ']')
            w++;
        return w > i ? w : start;
    }

    private static void TokenizeOperands(string text, int start, int end, List<(int, int, Brush)> list)
    {
        var i = start;
        while (i < end)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                var j = i + 1;
                while (j < end)
                {
                    if (text[j] == '"')
                    {
                        var bs = 0;
                        for (var k = j - 1; k >= i && text[k] == '\\'; k--)
                            bs++;
                        if (bs % 2 == 0)
                            break;
                    }

                    j++;
                }

                if (j < end)
                    j++;
                list.Add((i, j, StringBrush));
                i = j;
                continue;
            }

            if (char.IsDigit(text[i]) || text[i] == '-' && i + 1 < end && char.IsDigit(text[i + 1]))
            {
                var j = i + 1;
                while (j < end && (char.IsDigit(text[j]) || text[j] == '.' || text[j] == 'e' || text[j] == 'E' || text[j] == '-' || text[j] == '+'))
                    j++;
                list.Add((i, j, NumberBrush));
                i = j;
                continue;
            }

            if (text[i] is '[' or ']' or ',' or ':' or '(' or ')')
            {
                list.Add((i, i + 1, PunctuationBrush));
                i++;
                continue;
            }

            var w0 = i;
            while (i < end && !char.IsWhiteSpace(text[i]) && text[i] != ',' && text[i] != ':' && text[i] != '[' && text[i] != ']' && text[i] != '(' && text[i] != ')')
                i++;
            if (i > w0)
            {
                var word = text.AsSpan(w0, i - w0).ToString();
                var brush = Opcodes.Contains(word) ? KeywordBrush : IdentifierBrush;
                list.Add((w0, i, brush));
            }
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var lineStart = line.Offset;
        var lineContentEnd = line.Offset + line.Length;
        foreach (var (start, end, brush) in _spans)
        {
            if (end <= lineStart || start >= lineContentEnd)
                continue;
            var s = Math.Max(start, lineStart);
            var e = Math.Min(end, lineContentEnd);
            if (s < e)
                ChangeLinePart(s, e, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
