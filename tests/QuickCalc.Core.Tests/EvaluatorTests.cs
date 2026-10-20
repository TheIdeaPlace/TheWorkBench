using QuickCalc.Core;

namespace QuickCalc.Core.Tests;

public class EvaluatorTests
{
    private static string Calc(string expression, AngleMode mode = AngleMode.Degrees, double answer = 0) =>
        NumberFormatter.Format(Evaluator.Evaluate(expression, mode, answer));

    [Theory]
    [InlineData("1+2", "3")]
    [InlineData("2+3*4", "14")]
    [InlineData("(2+3)*4", "20")]
    [InlineData("10-4-3", "3")]
    [InlineData("100/10/2", "5")]
    [InlineData("7/2", "3.5")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData(".5*4", "2")]
    [InlineData("  12   +  3 ", "15")]
    [InlineData("1/3*3", "1")]
    [InlineData("5.+1", "6")]
    public void Arithmetic(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("12×3", "36")]
    [InlineData("12÷4", "3")]
    [InlineData("9−4", "5")]
    [InlineData("−5+2", "-3")]
    [InlineData("2·3", "6")]
    [InlineData("√9", "3")]
    [InlineData("2π", "6.28318530717959")]
    public void KeypadSymbols(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("2^10", "1024")]
    [InlineData("2**3", "8")]
    [InlineData("2^3^2", "512")] // right-associative
    [InlineData("-2^2", "-4")]
    [InlineData("(-2)^2", "4")]
    [InlineData("2^-1", "0.5")]
    [InlineData("4^0.5", "2")]
    [InlineData("--3", "3")]
    [InlineData("+3", "3")]
    [InlineData("3--2", "5")]
    public void PowersAndSigns(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("50%", "0.5")]
    [InlineData("80+15%", "92")]
    [InlineData("80-25%", "60")]
    [InlineData("200*15%", "30")]
    [InlineData("80+15%*2", "80.3")] // percent applies to the sum only when it stands alone
    [InlineData("5!", "120")]
    [InlineData("0!", "1")]
    [InlineData("3!!", "720")]
    [InlineData("2^3!", "64")]
    public void PercentAndFactorial(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("2pi", "6.28318530717959")]
    [InlineData("2(3+4)", "14")]
    [InlineData("(1+1)(2+2)", "8")]
    [InlineData("3sqrt(16)", "12")]
    [InlineData("2 e", "5.43656365691809")]
    [InlineData("2e", "5.43656365691809")]
    public void ImpliedMultiplication(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("1e3", "1000")]
    [InlineData("1.5e+20*2", "3e+20")]
    [InlineData("2E-3", "0.002")]
    [InlineData("1e300*1e5", "1e+305")]
    public void ScientificNotation(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("sqrt(16)", "4")]
    [InlineData("sqrt 16", "4")]
    [InlineData("sqrt 16 + 1", "5")]
    [InlineData("SQRT(16)", "4")]
    [InlineData("cbrt(-27)", "-3")]
    [InlineData("abs(-4.5)", "4.5")]
    [InlineData("ln(e)", "1")]
    [InlineData("log(1000)", "3")]
    [InlineData("exp(0)", "1")]
    [InlineData("round(2.5)", "3")]
    [InlineData("round(-2.5)", "-3")]
    [InlineData("floor(-2.5)", "-3")]
    [InlineData("ceil(2.1)", "3")]
    [InlineData("sqrt(16", "4")] // missing ")" is filled in
    [InlineData("((2+3", "5")]
    [InlineData("sin(30)^2", "0.25")]
    public void Functions(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("sin(30)", "0.5")]
    [InlineData("sin(90)", "1")]
    [InlineData("sin(180)", "0")]
    [InlineData("sin(270)", "-1")]
    [InlineData("sin(-90)", "-1")]
    [InlineData("sin(720)", "0")]
    [InlineData("cos(0)", "1")]
    [InlineData("cos(90)", "0")]
    [InlineData("cos(180)", "-1")]
    [InlineData("cos(60)", "0.5")]
    [InlineData("tan(45)", "1")]
    [InlineData("tan(180)", "0")]
    [InlineData("asin(1)", "90")]
    [InlineData("acos(0)", "90")]
    [InlineData("atan(1)", "45")]
    public void TrigInDegrees(string expression, string expected) => Assert.Equal(expected, Calc(expression));

    [Theory]
    [InlineData("sin(pi/2)", "1")]
    [InlineData("cos(pi)", "-1")]
    [InlineData("tan(pi/4)", "1")]
    [InlineData("asin(1)", "1.5707963267949")]
    public void TrigInRadians(string expression, string expected) => Assert.Equal(expected, Calc(expression, AngleMode.Radians));

    [Fact]
    public void AnsUsesThePreviousAnswer() => Assert.Equal("84", Calc("ans*2", answer: 42));

    [Theory]
    [InlineData("1/0", "Cannot divide by zero", 1)]
    [InlineData("5/(2-2)", "Cannot divide by zero", 1)]
    [InlineData("0^-1", "Cannot divide by zero", 1)]
    [InlineData("sqrt(-4)", "Cannot take the square root of a negative number", 0)]
    [InlineData("ln(0)", "Logarithms need a number greater than zero", 0)]
    [InlineData("log(-1)", "Logarithms need a number greater than zero", 0)]
    [InlineData("asin(2)", "The inverse sine needs a number from -1 to 1", 0)]
    [InlineData("tan(90)", "The tangent of 90 degrees is undefined", 0)]
    [InlineData("tan(-270)", "The tangent of -270 degrees is undefined", 0)]
    [InlineData("(-8)^(1/3)", "A negative number cannot be raised to a fractional power", 4)]
    [InlineData("2.5!", "Factorial needs a whole number from 0 to 170", 3)]
    [InlineData("171!", "Factorial needs a whole number from 0 to 170", 3)]
    [InlineData("(-1)!", "Factorial needs a whole number from 0 to 170", 4)]
    [InlineData("10^400", "The result is too large", 2)]
    [InlineData("1e308*10", "The result is too large", 5)]
    [InlineData("1e400", "That number is too large", 0)]
    [InlineData("", "Nothing to calculate", 0)]
    [InlineData("   ", "Nothing to calculate", 0)]
    [InlineData("2+", "The expression ends before it is complete", 2)]
    [InlineData("*3", "Expected a number where \"*\" is", 0)]
    [InlineData("2+)", "Expected a number before the closing parenthesis", 2)]
    [InlineData("2)", "There is a closing parenthesis without an opening one", 1)]
    [InlineData("()", "Expected a number before the closing parenthesis", 1)]
    [InlineData("2 3", "Two numbers need an operator between them", 2)]
    [InlineData("(2)3", "Two numbers need an operator between them", 3)]
    [InlineData("1.2.3", "A number can only have one decimal point", 3)]
    [InlineData("5+.", "A decimal point needs a digit after it", 2)]
    [InlineData("foo(2)", "\"foo\" is not a name QuickCalc knows", 0)]
    [InlineData("2 # 3", "\"#\" is not something QuickCalc understands", 2)]
    [InlineData("sqrt", "Square root needs a number after it", 4)]
    [InlineData("sin(30 4)", "Two numbers need an operator between them", 7)]
    public void Errors(string expression, string message, int position)
    {
        var ex = Assert.Throws<CalcException>(() => Evaluator.Evaluate(expression));
        Assert.Equal(message, ex.Message);
        Assert.Equal(position, ex.Position);
    }

    [Fact]
    public void DeeplyNestedParenthesesWork()
    {
        string expression = new string('(', 200) + "1" + new string(')', 200);
        Assert.Equal("1", Calc(expression));
    }
}
