using System.Globalization;
using Glint.Core;

namespace Glint.CommandBar;

/// <summary>Inline arithmetic: a small recursive-descent parser, never an evaluator of arbitrary code.</summary>
public sealed class Calculator : ICommandProvider
{
    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        if (!TryEvaluate(query, out var value)) return Array.Empty<CommandResult>();
        var text = Format(value);
        return new[]
        {
            new CommandResult
            {
                Title = "= " + text,
                Subtitle = "Enter — скопировать",
                Hint = "Калькулятор",
                Glyph = Glyphs.Calculator,
                Score = 1000,
                Execute = () => Copy(text),
            },
        };
    }

    /// <summary>
    /// True only for something that looks like a calculation: a lone number or constant is not one,
    /// unless the query starts with "=".
    /// </summary>
    public static bool TryEvaluate(string input, out double value)
    {
        value = 0;
        var s = input.Trim();
        var forced = s.StartsWith('=');
        if (forced) s = s[1..];
        if (s.Length == 0) return false;
        try
        {
            var parser = new Parser(s);
            value = parser.ParseAll();
            return (forced || parser.Tokens > 1) && double.IsFinite(value);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Format(double v)
    {
        if (v == 0) v = 0; // drop negative zero
        var abs = Math.Abs(v);
        if (abs >= 1e15 || (abs > 0 && abs < 1e-9)) return v.ToString("G12", CultureInfo.InvariantCulture);
        return Math.Round(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    static void Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            AppServices.Notifier.Flash("Скопировано", text, Glyphs.Copy);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process for a moment.
            App.LogError(ex);
            AppServices.Notifier.Flash("Буфер обмена занят", "Попробуйте ещё раз", Glyphs.Info);
        }
    }

    /// <summary>
    /// expr  := term (('+' | '-') term)*
    /// term  := unary (('*' | '/' | '%') unary)*
    /// unary := ('-' | '+') unary | power
    /// power := primary ('^' unary)?
    /// primary := number | '(' expr ')' | constant | function ( '(' expr ')' | power )
    /// </summary>
    sealed class Parser
    {
        const int MaxDepth = 64;

        readonly string _s;
        int _pos;
        int _depth;

        public int Tokens { get; private set; }

        public Parser(string s) => _s = s;

        public double ParseAll()
        {
            var v = ParseExpr();
            SkipWhitespace();
            if (_pos != _s.Length) throw new FormatException();
            return v;
        }

        double ParseExpr()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-') || Eat('−')) v -= ParseTerm();
                else return v;
            }
        }

        double ParseTerm()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Eat('*') || Eat('×')) v *= ParseUnary();
                else if (Eat('/') || Eat('÷')) v /= ParseUnary();
                else if (Eat('%')) v %= ParseUnary();
                else return v;
            }
        }

        double ParseUnary()
        {
            // Deep nesting would overflow the stack, which can't be caught.
            if (++_depth > MaxDepth) throw new FormatException();
            try
            {
                if (Eat('-') || Eat('−')) return -ParseUnary();
                if (Eat('+')) return ParseUnary();
                return ParsePower();
            }
            finally
            {
                _depth--;
            }
        }

        double ParsePower()
        {
            var b = ParsePrimary();
            return Eat('^') ? Math.Pow(b, ParseUnary()) : b;
        }

        double ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _s.Length) throw new FormatException();
            var c = _s[_pos];

            if (c == '(')
            {
                _pos++;
                Tokens++;
                var v = ParseExpr();
                if (!Eat(')')) throw new FormatException();
                return v;
            }

            if (char.IsDigit(c) || c == '.' || c == ',') return ParseNumber();

            if (char.IsLetter(c))
            {
                var start = _pos;
                while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
                var name = _s[start.._pos].ToLowerInvariant();
                Tokens++;

                switch (name)
                {
                    case "pi":
                    case "π":
                        return Math.PI;
                    case "e":
                        return Math.E;
                }

                Func<double, double>? fn = name switch
                {
                    "sqrt" => Math.Sqrt,
                    "sin" => Math.Sin,
                    "cos" => Math.Cos,
                    "tan" or "tg" => Math.Tan,
                    "abs" => Math.Abs,
                    "round" => x => Math.Round(x, MidpointRounding.AwayFromZero),
                    "ln" => Math.Log,
                    "log" or "lg" => Math.Log10,
                    _ => null,
                };
                if (fn is null) throw new FormatException();

                if (++_depth > MaxDepth) throw new FormatException();
                try
                {
                    if (Eat('('))
                    {
                        var arg = ParseExpr();
                        if (!Eat(')')) throw new FormatException();
                        return fn(arg);
                    }
                    // "sqrt 16" without parentheses binds tightly, like a unary operator.
                    return fn(ParsePower());
                }
                finally
                {
                    _depth--;
                }
            }

            throw new FormatException();
        }

        double ParseNumber()
        {
            var start = _pos;
            var separators = 0;
            while (_pos < _s.Length)
            {
                var c = _s[_pos];
                if (char.IsDigit(c)) { _pos++; continue; }
                if ((c == '.' || c == ',') && separators == 0) { separators++; _pos++; continue; }
                break;
            }
            Tokens++;
            var text = _s[start.._pos].Replace(',', '.');
            if (text == ".") throw new FormatException();
            return double.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }

        bool Eat(char c)
        {
            SkipWhitespace();
            if (_pos < _s.Length && _s[_pos] == c)
            {
                _pos++;
                Tokens++;
                return true;
            }
            return false;
        }

        void SkipWhitespace()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
        }
    }
}
