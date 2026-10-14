using System.Globalization;
using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Payroll;

/// <summary>
/// A tiny safe expression language for salary formulas (doc 03 R3): decimal numbers, component codes,
/// <c>+ - * /</c>, unary minus and parentheses. Nothing else is accepted, so a formula can never run code.
/// </summary>
public static class FormulaEvaluator
{
    public const int MaxLength = 300;

    private enum TokenType
    {
        Number,
        Code,
        Plus,
        Minus,
        Star,
        Slash,
        Open,
        Close,
        End,
    }

    private readonly record struct Token(TokenType Type, string Text, decimal Value, int Position);

    /// <summary>The component codes used by a formula, or an error message.</summary>
    public static Result<IReadOnlySet<string>> Parse(string? formula, IReadOnlySet<string> knownCodes)
    {
        ArgumentNullException.ThrowIfNull(knownCodes);
        if (string.IsNullOrWhiteSpace(formula))
        {
            return Error.Validation("Formula", "Enter a formula, for example BASIC * 0.1.");
        }

        if (formula.Length > MaxLength)
        {
            return Error.Validation("Formula", $"A formula can have at most {MaxLength} characters.");
        }

        try
        {
            var codes = new HashSet<string>(StringComparer.Ordinal);
            var parser = new Parser(Tokenize(formula), code =>
            {
                if (!knownCodes.Contains(code))
                {
                    throw new FormatException($"Unknown component code {code}.");
                }

                codes.Add(code);
                return 1m;
            });
            parser.ParseAll();
            return codes;
        }
        catch (FormatException ex)
        {
            return Error.Validation("Formula", ex.Message);
        }
    }

    /// <summary>Evaluates a formula. Division by zero gives 0 (an empty component should not break a payslip).</summary>
    public static decimal Evaluate(string formula, Func<string, decimal> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);
        return new Parser(Tokenize(formula), valueOf).ParseAll();
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(c) || c == '.')
            {
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                var number = text[start..i];
                if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                {
                    throw new FormatException($"\"{number}\" is not a number.");
                }

                tokens.Add(new Token(TokenType.Number, number, value, start));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenType.Code, text[start..i].ToUpperInvariant(), 0, start));
                continue;
            }

            var type = c switch
            {
                '+' => TokenType.Plus,
                '-' => TokenType.Minus,
                '*' => TokenType.Star,
                '/' => TokenType.Slash,
                '(' => TokenType.Open,
                ')' => TokenType.Close,
                _ => throw new FormatException($"\"{c}\" is not allowed. Use numbers, component codes, + - * / and brackets."),
            };
            tokens.Add(new Token(type, c.ToString(), 0, start));
            i++;
        }

        tokens.Add(new Token(TokenType.End, string.Empty, 0, text.Length));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens, Func<string, decimal> valueOf)
    {
        private const int MaxDepth = 50;
        private int _index;
        private int _depth;

        private Token Current => tokens[_index];

        public decimal ParseAll()
        {
            var value = Expression();
            if (Current.Type != TokenType.End)
            {
                throw new FormatException($"Unexpected \"{Current.Text}\" at position {Current.Position + 1}.");
            }

            return value;
        }

        private decimal Expression()
        {
            var value = Term();
            while (Current.Type is TokenType.Plus or TokenType.Minus)
            {
                var op = Current.Type;
                _index++;
                var right = Term();
                value = op == TokenType.Plus ? value + right : value - right;
            }

            return value;
        }

        private decimal Term()
        {
            var value = Factor();
            while (Current.Type is TokenType.Star or TokenType.Slash)
            {
                var op = Current.Type;
                _index++;
                var right = Factor();
                value = op == TokenType.Star ? value * right : right == 0 ? 0 : value / right;
            }

            return value;
        }

        private decimal Factor()
        {
            var token = Current;
            switch (token.Type)
            {
                case TokenType.Number:
                    _index++;
                    return token.Value;
                case TokenType.Code:
                    _index++;
                    return valueOf(token.Text);
                case TokenType.Minus:
                    _index++;
                    return -Factor();
                case TokenType.Plus:
                    _index++;
                    return Factor();
                case TokenType.Open:
                    if (++_depth > MaxDepth)
                    {
                        throw new FormatException("Too many brackets.");
                    }

                    _index++;
                    var inner = Expression();
                    if (Current.Type != TokenType.Close)
                    {
                        throw new FormatException("A closing bracket is missing.");
                    }

                    _index++;
                    _depth--;
                    return inner;
                case TokenType.End:
                    throw new FormatException("The formula ends too early.");
                default:
                    throw new FormatException($"Unexpected \"{token.Text}\" at position {token.Position + 1}.");
            }
        }
    }
}
