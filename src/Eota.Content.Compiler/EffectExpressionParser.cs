using System.Globalization;
using Eota.Kernel.Effects;

namespace Eota.Content.Compiler;

internal sealed class EffectCompileException(string code, string path, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}

internal sealed class EffectExpressionParser(string text, string path, Action<string> validateVariable)
{
    private int _position;
    private int _nodes;

    public IntExpression Parse()
    {
        if (text.Length > 512) { Fail("Expression exceeds 512 characters."); }
        var result = Additive(0);
        Skip();
        if (_position != text.Length) { Fail("Unexpected expression token."); }
        return result;
    }

    private IntExpression Additive(int depth)
    {
        var value = Multiplicative(depth + 1);
        while (true)
        {
            Skip();
            if (Take('+')) { value = Binary(ArithmeticOperation.Add, value, Multiplicative(depth + 1)); }
            else if (Take('-')) { value = Binary(ArithmeticOperation.Subtract, value, Multiplicative(depth + 1)); }
            else { return value; }
        }
    }

    private IntExpression Multiplicative(int depth)
    {
        var value = Primary(depth + 1);
        while (true)
        {
            Skip();
            if (Take('*')) { value = Binary(ArithmeticOperation.Multiply, value, Primary(depth + 1)); }
            else if (Take('/')) { value = Binary(ArithmeticOperation.Divide, value, Primary(depth + 1)); }
            else { return value; }
        }
    }

    private IntExpression Primary(int depth)
    {
        if (depth > 24 || ++_nodes > 64) { Fail("Expression node/depth budget exceeded."); }
        Skip();
        if (Take('('))
        {
            var inner = Additive(depth + 1);
            Skip();
            if (!Take(')')) { Fail("Expected closing parenthesis."); }
            return inner;
        }
        var negative = Take('-');
        Skip();
        var start = _position;
        while (_position < text.Length && char.IsAsciiDigit(text[_position])) { _position++; }
        if (_position > start)
        {
            var digits = (negative ? "-" : string.Empty) + text[start.._position];
            if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) { Fail("Int64 literal out of range."); }
            return new ConstantExpression(number);
        }
        if (negative) { return Binary(ArithmeticOperation.Subtract, new ConstantExpression(0), Primary(depth + 1)); }
        while (_position < text.Length && (char.IsAsciiLetter(text[_position]) || text[_position] == '.')) { _position++; }
        if (_position == start) { Fail("Expected integer, variable or parenthesized expression."); }
        var name = text[start.._position];
        validateVariable(name);
        return new VariableExpression(name);
    }

    private IntExpression Binary(ArithmeticOperation operation, IntExpression left, IntExpression right)
    {
        if (++_nodes > 64) { Fail("Expression node budget exceeded."); }
        if (operation == ArithmeticOperation.Divide && right is ConstantExpression { Value: 0 }) { Fail("Division by zero."); }
        if (left is ConstantExpression a && right is ConstantExpression b)
        {
            try
            {
                return new ConstantExpression(operation switch
                {
                    ArithmeticOperation.Add => checked(a.Value + b.Value),
                    ArithmeticOperation.Subtract => checked(a.Value - b.Value),
                    ArithmeticOperation.Multiply => checked(a.Value * b.Value),
                    ArithmeticOperation.Divide => checked(a.Value / b.Value),
                    _ => throw new InvalidOperationException()
                });
            }
            catch (ArithmeticException) { Fail("Constant expression overflow or invalid arithmetic."); }
        }
        return new BinaryExpression(operation, left, right);
    }

    private bool Take(char token)
    {
        if (_position >= text.Length || text[_position] != token) { return false; }
        _position++; return true;
    }
    private void Skip() { while (_position < text.Length && char.IsWhiteSpace(text[_position])) { _position++; } }
    private void Fail(string message) => throw new EffectCompileException("invalid-expression", path, message);
}
