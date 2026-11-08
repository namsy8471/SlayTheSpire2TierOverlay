using System.Globalization;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// Evaluates the small amount formulas in <c>events.json</c>, e.g. <c>"floor(0.25*maxHp)"</c> or <c>"-gold"</c>.
/// Grammar: numbers, identifiers, <c>+ - * /</c>, parentheses and the functions <c>floor ceil round abs min max pow</c>.
/// Identifiers are looked up case-insensitively in the variables passed in (run state + the event's <c>vars</c>).
/// </summary>
/// <remarks>
/// A recursive-descent parser: each grammar rule is one method (<c>Sum</c> → <c>Product</c> →
/// <c>Unary</c> → <c>Atom</c>), so operator precedence falls out of the call order.
/// </remarks>
public static class EventFormula
{
    /// <summary>Evaluates <paramref name="text"/>; false when it does not parse or names an unknown variable.</summary>
    public static bool TryEvaluate(string text, IReadOnlyDictionary<string, double> vars, out double value)
    {
        try
        {
            var p = new Parser(text, vars);
            value = p.Sum();
            p.SkipSpaces();
            if (!p.AtEnd) throw new FormatException("trailing input");
            return double.IsFinite(value);
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    private ref struct Parser(string text, IReadOnlyDictionary<string, double> vars)
    {
        private readonly string _s = text;
        private int _i;

        public readonly bool AtEnd => _i >= _s.Length;

        public void SkipSpaces()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private bool Eat(char c)
        {
            SkipSpaces();
            if (_i < _s.Length && _s[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        public double Sum()
        {
            var v = Product();
            while (true)
            {
                if (Eat('+')) v += Product();
                else if (Eat('-')) v -= Product();
                else return v;
            }
        }

        private double Product()
        {
            var v = Unary();
            while (true)
            {
                if (Eat('*')) v *= Unary();
                else if (Eat('/'))
                {
                    var d = Unary();
                    if (d == 0) throw new FormatException("division by zero");
                    v /= d;
                }
                else return v;
            }
        }

        private double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            return Atom();
        }

        private double Atom()
        {
            SkipSpaces();
            if (_i >= _s.Length) throw new FormatException("unexpected end");
            if (Eat('('))
            {
                var v = Sum();
                if (!Eat(')')) throw new FormatException("missing )");
                return v;
            }
            var c = _s[_i];
            if (char.IsDigit(c) || c == '.')
            {
                var start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                var name = _s[start.._i];
                if (Eat('(')) return Call(name);
                if (vars.TryGetValue(name, out var direct)) return direct;
                foreach (var kv in vars)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                }
                throw new FormatException($"unknown variable {name}");
            }
            throw new FormatException($"unexpected '{c}'");
        }

        private double Call(string name)
        {
            var args = new List<double> { Sum() };
            while (Eat(',')) args.Add(Sum());
            if (!Eat(')')) throw new FormatException("missing )");
            return name.ToLowerInvariant() switch
            {
                "floor" when args.Count == 1 => Math.Floor(args[0]),
                "ceil" when args.Count == 1 => Math.Ceiling(args[0]),
                "round" when args.Count == 1 => Math.Round(args[0], MidpointRounding.AwayFromZero),
                "abs" when args.Count == 1 => Math.Abs(args[0]),
                "min" when args.Count >= 1 => args.Min(),
                "max" when args.Count >= 1 => args.Max(),
                "pow" when args.Count == 2 => Math.Pow(args[0], args[1]),
                _ => throw new FormatException($"unknown function {name}"),
            };
        }
    }
}
