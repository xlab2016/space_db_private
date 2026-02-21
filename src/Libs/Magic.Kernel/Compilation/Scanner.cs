using System;
using System.Collections.Generic;

namespace Magic.Kernel.Compilation
{
    /// <summary>Делит исходный код на токены: ключевые слова, идентификаторы, числа, строки, разделители.</summary>
    public class Scanner
    {
        private readonly string _source;
        private readonly List<Token> _tokens = new List<Token>();
        private int _position;
        private int _index;

        public Scanner(string source)
        {
            _source = source ?? string.Empty;
            _index = 0;
            _position = 0;
            ScanAll();
        }

        /// <summary>Текущая позиция в потоке токенов (индекс следующего к потреблению).</summary>
        public int Position => _position;

        /// <summary>Получить следующий токен и сдвинуть позицию.</summary>
        public Token Scan()
        {
            if (_position >= _tokens.Count)
                return Token.Eof(_index);
            return _tokens[_position++];
        }

        /// <summary>Посмотреть токен относительно текущей позиции без потребления. Watch(0) — текущий, Watch(-1) — предыдущий.</summary>
        public Token? Watch(int offset)
        {
            var i = _position + offset;
            if (i < 0 || i >= _tokens.Count)
                return null;
            return _tokens[i];
        }

        /// <summary>Текущий токен (тот, который вернёт следующий Scan()).</summary>
        public Token Current => Watch(0) ?? Token.Eof(_index);

        /// <summary>Сохранить позицию для отката (BNF).</summary>
        public int Save() => _position;

        /// <summary>Восстановить позицию.</summary>
        public void Restore(int position) => _position = position;

        private void ScanAll()
        {
            _tokens.Clear();
            _index = 0;
            SkipWhitespace();
            while (_index < _source.Length)
            {
                var tok = ScanOne();
                _tokens.Add(tok);
                if (tok.Kind == TokenKind.EndOfInput)
                    break;
                SkipWhitespace();
            }
            _tokens.Add(Token.Eof(_index));
            _position = 0;
        }

        private void SkipWhitespace()
        {
            while (_index < _source.Length && char.IsWhiteSpace(_source[_index]))
                _index++;
        }

        private Token ScanOne()
        {
            if (_index >= _source.Length)
                return Token.Eof(_index);

            var start = _index;
            var ch = _source[_index];

            // Односимвольные разделители
            if (ch == ':') { _index++; return new Token(TokenKind.Colon, ":", start, _index); }
            if (ch == ',') { _index++; return new Token(TokenKind.Comma, ",", start, _index); }
            if (ch == '[') { _index++; return new Token(TokenKind.LBracket, "[", start, _index); }
            if (ch == ']') { _index++; return new Token(TokenKind.RBracket, "]", start, _index); }
            if (ch == '{') { _index++; return new Token(TokenKind.LBrace, "{", start, _index); }
            if (ch == '}') { _index++; return new Token(TokenKind.RBrace, "}", start, _index); }

            // Строка в кавычках
            if (ch == '"')
            {
                _index++;
                var valueStart = _index;
                while (_index < _source.Length && _source[_index] != '"')
                {
                    if (_source[_index] == '\\' && _index + 1 < _source.Length)
                        _index += 2;
                    else
                        _index++;
                }
                var value = _source.Substring(valueStart, _index - valueStart).Replace("\\\"", "\"");
                if (_index < _source.Length) _index++;
                return new Token(TokenKind.StringLiteral, value, start, _index);
            }

            // Число (включая отрицательное и float)
            if (ch == '-' || char.IsDigit(ch))
            {
                var hasDot = false;
                var hasExp = false;
                var i = _index;
                if (ch == '-') i++;
                while (i < _source.Length && (char.IsDigit(_source[i]) || _source[i] == '.' || _source[i] == 'e' || _source[i] == 'E' || _source[i] == '+'))
                {
                    if (_source[i] == '.') hasDot = true;
                    if (_source[i] == 'e' || _source[i] == 'E') hasExp = true;
                    i++;
                }
                var raw = _source.Substring(_index, i - _index);
                _index = i;
                var kind = (hasDot || hasExp) ? TokenKind.Float : TokenKind.Number;
                return new Token(kind, raw, start, _index);
            }

            // Идентификатор (буква или _ затем буквы/цифры/_)
            if (char.IsLetter(ch) || ch == '_')
            {
                var i = _index + 1;
                while (i < _source.Length && (char.IsLetterOrDigit(_source[i]) || _source[i] == '_'))
                    i++;
                var value = _source.Substring(_index, i - _index);
                _index = i;
                return new Token(TokenKind.Identifier, value, start, _index);
            }

            // Неизвестный символ — считаем концом (или можно один символ как идентификатор для ошибки)
            _index++;
            return new Token(TokenKind.Identifier, _source.Substring(start, 1), start, _index);
        }
    }
}
