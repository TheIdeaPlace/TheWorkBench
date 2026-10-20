using QuickCalc.Core;

namespace QuickCalc.Core.Tests;

public class TextTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1234567.0, "1234567")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1e20, "1e+20")]
    [InlineData(1.5e-12, "1.5e-12")]
    [InlineData(123456789012345678.0, "1.23456789012346e+17")]
    public void FormatsNumbers(double value, string expected) => Assert.Equal(expected, NumberFormatter.Format(value));

    [Fact]
    public void NegativeZeroShowsAsZero() => Assert.Equal("0", NumberFormatter.Format(-0.0));

    [Theory]
    [InlineData("15", "15")]
    [InlineData("-2.5", "minus 2.5")]
    [InlineData("1e+20", "1 times 10 to the power 20")]
    [InlineData("-1.5e-12", "minus 1.5 times 10 to the power minus 12")]
    public void SpeaksNumbers(string formatted, string expected) => Assert.Equal(expected, NumberFormatter.Speak(formatted));

    [Theory]
    [InlineData("12*3", "12 × 3")]
    [InlineData("12/(3-1)", "12 ÷ (3 − 1)")]
    [InlineData("-5+2", "−5 + 2")]
    [InlineData("2^-1", "2 ^ −1")]
    [InlineData("2pi", "2π")]
    [InlineData("pi e", "π e")]
    [InlineData("sqrt(16)+sin 30", "sqrt(16) + sin 30")]
    [InlineData("80+15%", "80 + 15%")]
    [InlineData("5!*2", "5! × 2")]
    [InlineData("ans*2", "ans × 2")]
    [InlineData("2 e", "2 e")]
    [InlineData("1.5e+20*2", "1.5e+20 × 2")]
    [InlineData("2 # 3", "2 # 3")] // unreadable input is left alone
    public void BuildsDisplayText(string expression, string expected) => Assert.Equal(expected, ExpressionText.ToDisplay(expression));

    [Theory]
    [InlineData("12*3", "36")]
    [InlineData("12/(3-1)", "6")]
    [InlineData("2^-1", "0.5")]
    [InlineData("2pi", "6.28318530717959")]
    [InlineData("2 e", "5.43656365691809")]
    [InlineData("sqrt 16+sin 30", "4.5")]
    [InlineData("80+15%", "92")]
    [InlineData("1.5e+20*2", "3e+20")]
    public void DisplayTextCalculatesTheSame(string expression, string expected)
    {
        string display = ExpressionText.ToDisplay(expression);
        Assert.Equal(expected, NumberFormatter.Format(Evaluator.Evaluate(display)));
    }

    [Theory]
    [InlineData("", "Empty")]
    [InlineData("12*3", "12 times 3")]
    [InlineData("12÷(3−1)", "12 divided by open paren 3 minus 1 close paren")]
    [InlineData("-5+2", "negative 5 plus 2")]
    [InlineData("2^-1", "2 to the power negative 1")]
    [InlineData("sqrt(16)", "square root of open paren 16 close paren")]
    [InlineData("80+15%", "80 plus 15 percent")]
    [InlineData("5!", "5 factorial")]
    [InlineData("ans*2", "answer times 2")]
    [InlineData("2π", "2 pi")]
    [InlineData("1e+20", "1 times 10 to the power 20")]
    [InlineData("2 # 3", "2 # 3")]
    public void BuildsSpeech(string expression, string expected) => Assert.Equal(expected, ExpressionText.ToSpeech(expression));
}
