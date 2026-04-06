using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Magic.Kernel.Compilation;

namespace Space.OS.Terminal;

/// <summary>Подсветка .agi по токенам <see cref="Scanner"/> (тот же лексер, что и компилятор).</summary>
internal sealed class MagicAgiColorizer : DocumentColorizingTransformer
{
    private static readonly Brush PlainBrush = CreateBrush(0xDD, 0xE8, 0xFF);
    private static readonly Brush KeywordBrush = CreateBrush(0x6C, 0x9E, 0xFF);
    private static readonly Brush IdentifierBrush = CreateBrush(0xA8, 0xC5, 0xFF);
    /// <summary>Имя слева от «:» (поле, именованный аргумент, объявляемый тип).</summary>
    private static readonly Brush DeclNameBrush = CreateBrush(0x9A, 0xD8, 0xE8);
    /// <summary>Ссылка на тип / встроенный тип (справа от «:» в аннотации, после is, …).</summary>
    private static readonly Brush TypeBrush = CreateBrush(0xE5, 0xB6, 0x5C);
    private static readonly Brush StringBrush = CreateBrush(0x7E, 0xDC, 0x9A);
    private static readonly Brush NumberBrush = CreateBrush(0xF5, 0xA8, 0x7A);
    private static readonly Brush CommentBrush = CreateBrush(0x6B, 0x7A, 0x99);
    private static readonly Brush PunctuationBrush = CreateBrush(0x9A, 0xA8, 0xCC);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "@", "AGI", "program", "module", "system", "procedure", "function", "entrypoint", "asm",
        "table", "database", "await", "call", "print", "ret", "global", "lambda", "address",
        "index", "indices", "var", "if", "else", "while", "loop", "for",
        "type", "class", "method", "constructor", "public", "private", "protected", "internal",
        "switch", "is", "println", "case", "procedure", "function", "use", "entrypoint", "execution", "as",
    };

    private static readonly HashSet<string> BuiltinTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "string", "bool", "void", "char", "float", "double", "decimal", "object", "long", "short", "byte",
    };

    private IReadOnlyList<(int start, int end, Brush brush)> _spans = [];

    private static SolidColorBrush CreateBrush(byte r, byte g, byte bl)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, bl));
        brush.Freeze();
        return brush;
    }

    public void DocumentUpdated(TextDocument document)
    {
        var text = document.Text ?? string.Empty;
        var scanner = new Scanner(text, emitLineComments: true);
        var tokenList = new List<Token>();
        while (true)
        {
            var t = scanner.Scan();
            if (t.IsEndOfInput)
            {
                break;
            }

            tokenList.Add(t);
        }

        ClassifyNameAndTypeIndices(tokenList, out var typeIndices, out var declNameIndices);
        var list = new List<(int, int, Brush)>(tokenList.Count);
        for (var i = 0; i < tokenList.Count; i++)
        {
            var t = tokenList[i];
            list.Add((t.Start, t.End, BrushFor(t, typeIndices.Contains(i), declNameIndices.Contains(i))));
        }

        _spans = list;
    }

    private static Brush BrushFor(Token t, bool isTypeIdentifier, bool isDeclName)
    {
        return t.Kind switch
        {
            TokenKind.LineComment => CommentBrush,
            TokenKind.StringLiteral => StringBrush,
            TokenKind.Number or TokenKind.Float => NumberBrush,
            TokenKind.Identifier when isTypeIdentifier => TypeBrush,
            TokenKind.Identifier when isDeclName => DeclNameBrush,
            TokenKind.Identifier => Keywords.Contains(t.Value) ? KeywordBrush : IdentifierBrush,
            TokenKind.Newline => PlainBrush,
            _ when t.Kind is TokenKind.Colon or TokenKind.Comma or TokenKind.LBracket or TokenKind.RBracket
                or TokenKind.LParen or TokenKind.RParen or TokenKind.LBrace or TokenKind.RBrace
                or TokenKind.Dot or TokenKind.Assign or TokenKind.LessThan or TokenKind.GreaterThan
                or TokenKind.Semicolon => PunctuationBrush,
            _ => PlainBrush
        };
    }

    /// <summary>Имя (слева от «:») и тип (аннотации, is, наследование, дженерики вида <c>float&lt;decimal&gt;</c>).</summary>
    private static void ClassifyNameAndTypeIndices(List<Token> tokens, out HashSet<int> typeIndices, out HashSet<int> declNameIndices)
    {
        typeIndices = new HashSet<int>();
        declNameIndices = new HashSet<int>();
        MarkGenericTypeParameterIdentifiers(tokens, typeIndices);

        var braceDepth = 0;
        var parenDepth = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            // Объявление на верхнем уровне: имя слева; базы/типы справа — только в typeIndices
            if (t.Kind == TokenKind.Identifier && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon
                && braceDepth == 0 && parenDepth == 0)
            {
                declNameIndices.Add(i);
                var j = i + 2;
                if (j < tokens.Count
                    && !IsKeywordToken(tokens[j], "type")
                    && !IsKeywordToken(tokens[j], "class"))
                {
                    while (j < tokens.Count)
                    {
                        if (tokens[j].Kind != TokenKind.Identifier)
                            break;
                        typeIndices.Add(j);
                        j++;
                        ConsumeArraySuffix(tokens, ref j);
                        if (j < tokens.Count && tokens[j].Kind == TokenKind.Comma)
                        {
                            j++;
                            continue;
                        }

                        break;
                    }
                }
            }

            // Имя при «name: value» (не «:=»): X: 1, Board: Board(…), Surface: Point[]
            if (t.Kind == TokenKind.Identifier && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon
                && (i + 2 >= tokens.Count || tokens[i + 2].Kind != TokenKind.Assign))
            {
                if (!(i > 0 && IsKeywordToken(tokens[i - 1], "is")))
                    declNameIndices.Add(i);
            }

            if (t.Kind == TokenKind.Identifier)
            {
                var v = t.Value;
                if (BuiltinTypes.Contains(v))
                    typeIndices.Add(i);
                else if (i > 0 && IsKeywordToken(tokens[i - 1], "constructor"))
                    typeIndices.Add(i);
                else if (i > 0 && IsKeywordToken(tokens[i - 1], "is"))
                    typeIndices.Add(i);
                else if (i > 0 && tokens[i - 1].Kind == TokenKind.Colon)
                {
                    // … is Circle: circle — «circle» метка, не тип
                    var isPatternLabel = i >= 4
                        && tokens[i - 2].Kind == TokenKind.Identifier
                        && IsKeywordToken(tokens[i - 3], "is");
                    // use modularity: module1 as { — module1 не тип
                    var isUseAliasRhs = i >= 3
                        && IsKeywordToken(tokens[i - 3], "use")
                        && tokens[i - 2].Kind == TokenKind.Identifier;
                    if (!isPatternLabel && !isUseAliasRhs)
                        typeIndices.Add(i);
                }
            }

            switch (t.Kind)
            {
                case TokenKind.LBrace:
                    braceDepth++;
                    break;
                case TokenKind.RBrace:
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case TokenKind.LParen:
                    parenDepth++;
                    break;
                case TokenKind.RParen:
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
            }
        }
    }

    /// <summary>
    /// Только настоящие угловые дженерики: не путать с <c>i &lt; length(…)</c> (там после &lt; идёт id и «(»).
    /// Закрытие по парным <c>&lt;&gt;</c>; обрыв на «;» при вложенности 1 и наружных () — не дженерик.
    /// </summary>
    private static void MarkGenericTypeParameterIdentifiers(List<Token> tokens, HashSet<int> typeIndices)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.LessThan)
                continue;
            if (i == 0 || tokens[i - 1].Kind != TokenKind.Identifier)
                continue;
            // Сравнение: id &lt; id(
            if (i + 2 < tokens.Count
                && tokens[i + 1].Kind == TokenKind.Identifier
                && tokens[i + 2].Kind == TokenKind.LParen)
                continue;

            var angle = 1;
            var outerParen = 0;
            var start = i + 1;
            for (var k = i + 1; k < tokens.Count && angle > 0; k++)
            {
                switch (tokens[k].Kind)
                {
                    case TokenKind.LParen:
                        outerParen++;
                        break;
                    case TokenKind.RParen:
                        outerParen = Math.Max(0, outerParen - 1);
                        break;
                    case TokenKind.LessThan:
                        if (outerParen == 0)
                            angle++;
                        break;
                    case TokenKind.GreaterThan:
                        if (outerParen == 0)
                        {
                            angle--;
                            if (angle == 0)
                            {
                                for (var m = start; m < k; m++)
                                {
                                    if (tokens[m].Kind == TokenKind.Identifier)
                                        typeIndices.Add(m);
                                }
                            }
                        }

                        break;
                    case TokenKind.Semicolon:
                        if (angle == 1 && outerParen == 0)
                            goto NextLt;
                        break;
                }
            }

            NextLt:
            ;
        }
    }

    private static void ConsumeArraySuffix(List<Token> tokens, ref int j)
    {
        while (j + 1 < tokens.Count
               && tokens[j].Kind == TokenKind.LBracket
               && tokens[j + 1].Kind == TokenKind.RBracket)
            j += 2;
    }

    private static bool IsKeywordToken(Token t, string word)
        => t.Kind == TokenKind.Identifier && t.Value.Equals(word, StringComparison.OrdinalIgnoreCase);

    protected override void ColorizeLine(DocumentLine line)
    {
        // ChangeLinePart допускает только смещения по *телу* строки (без \r\n). См. DocumentColorizingTransformer.
        var lineStart = line.Offset;
        var lineContentEnd = line.Offset + line.Length;
        foreach (var (start, end, brush) in _spans)
        {
            if (end <= lineStart || start >= lineContentEnd)
            {
                continue;
            }

            var s = System.Math.Max(start, lineStart);
            var e = System.Math.Min(end, lineContentEnd);
            if (s < e)
            {
                ChangeLinePart(s, e, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
