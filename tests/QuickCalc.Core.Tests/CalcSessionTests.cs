using QuickCalc.Core;

namespace QuickCalc.Core.Tests;

public class CalcSessionTests
{
    [Fact]
    public void SuccessfulCalculationUpdatesAnswerAndHistory()
    {
        var session = new CalcSession();
        var result = session.Evaluate("12*3");

        Assert.True(result.Success);
        Assert.Equal("36", result.DisplayText);
        Assert.Equal("equals 36", result.SpokenText);
        Assert.Equal(36, session.Answer);
        var entry = Assert.Single(session.History);
        Assert.Equal("12 × 3 = 36", entry.DisplayText);
        Assert.Equal("12 × 3 = 36", entry.ToString());
    }

    [Fact]
    public void ErrorLeavesAnswerAndHistoryAlone()
    {
        var session = new CalcSession();
        session.Evaluate("5");
        var result = session.Evaluate("1/0");

        Assert.False(result.Success);
        Assert.Equal("Error: Cannot divide by zero", result.DisplayText);
        Assert.Equal("Error: Cannot divide by zero", result.SpokenText);
        Assert.Equal(1, result.ErrorPosition);
        Assert.Equal(5, session.Answer);
        Assert.Single(session.History);
    }

    [Fact]
    public void NegativeResultIsSpokenAsMinus()
    {
        var result = new CalcSession().Evaluate("2-5");
        Assert.Equal("-3", result.DisplayText);
        Assert.Equal("equals minus 3", result.SpokenText);
    }

    [Theory]
    [InlineData("*2", "20")]
    [InlineData("×2", "20")]
    [InlineData(" /4", "2.5")]
    [InlineData("+1", "11")]
    [InlineData("^2", "100")]
    [InlineData("-1", "-1")] // a leading minus is a negative number, not "ans − 1"
    [InlineData("2", "2")]
    public void LeadingOperatorContinuesFromAnswer(string expression, string expected)
    {
        var session = new CalcSession();
        session.Evaluate("10");
        Assert.Equal(expected, session.Evaluate(expression).DisplayText);
    }

    [Fact]
    public void LeadingOperatorWithNoHistoryIsAnError()
    {
        var result = new CalcSession().Evaluate("*2");
        Assert.False(result.Success);
        Assert.Equal(0, result.ErrorPosition);
    }

    [Fact]
    public void ErrorPositionIgnoresTheAddedAns()
    {
        var session = new CalcSession();
        session.Evaluate("10");
        var result = session.Evaluate("/0");
        Assert.Equal("Error: Cannot divide by zero", result.DisplayText);
        Assert.Equal(0, result.ErrorPosition);
    }

    [Fact]
    public void HistoryRecordsTheAnsItAdded()
    {
        var session = new CalcSession();
        session.Evaluate("10");
        session.Evaluate("*2");
        Assert.Equal("ans × 2 = 20", session.History[0].DisplayText);
    }

    [Fact]
    public void AngleModeIsUsed()
    {
        var session = new CalcSession { AngleMode = AngleMode.Radians };
        Assert.Equal("-1", session.Evaluate("cos(pi)").DisplayText);
        session.AngleMode = AngleMode.Degrees;
        Assert.Equal("-1", session.Evaluate("cos(180)").DisplayText);
    }

    [Fact]
    public void HistoryIsCapped()
    {
        var session = new CalcSession();
        for (int i = 0; i < CalcSession.MaxHistory + 5; i++)
            session.Evaluate(i.ToString());
        Assert.Equal(CalcSession.MaxHistory, session.History.Count);
        Assert.Equal("204", session.History[0].Expression);
    }

    [Fact]
    public void UpAndDownBrowseHistoryAndRestoreTheDraft()
    {
        var session = new CalcSession();
        session.Evaluate("1+1");
        session.Evaluate("2+2");

        Assert.Equal("2 + 2", session.RecallOlder("draft"));
        Assert.Equal("1 + 1", session.RecallOlder("2 + 2"));
        Assert.Null(session.RecallOlder("1 + 1"));
        Assert.Equal("2 + 2", session.RecallNewer());
        Assert.Equal("draft", session.RecallNewer());
        Assert.Null(session.RecallNewer());
    }

    [Fact]
    public void RecallWithEmptyHistoryReturnsNull()
    {
        var session = new CalcSession();
        Assert.Null(session.RecallOlder(""));
        Assert.Null(session.RecallNewer());
    }

    [Fact]
    public void EvaluatingResetsHistoryBrowsing()
    {
        var session = new CalcSession();
        session.Evaluate("1");
        session.Evaluate("2");
        session.RecallOlder("");
        session.RecallOlder("");
        session.Evaluate("3");
        Assert.Equal("3", session.RecallOlder(""));
    }

    [Fact]
    public void ResetHistoryCursorStartsBrowsingFromNewest()
    {
        var session = new CalcSession();
        session.Evaluate("1");
        session.Evaluate("2");
        session.RecallOlder("x");
        session.RecallOlder("x");
        session.ResetHistoryCursor();
        Assert.Equal("2", session.RecallOlder("y"));
        Assert.Equal("y", session.RecallNewer());
    }

    [Fact]
    public void ClearHistoryEmptiesTheList()
    {
        var session = new CalcSession();
        session.Evaluate("1");
        session.ClearHistory();
        Assert.Empty(session.History);
        Assert.Null(session.RecallOlder(""));
        Assert.Equal(1, session.Answer); // ans survives clearing the list
    }

    [Theory]
    [InlineData("12", 2, 0, "+", "12+", 3)]
    [InlineData("12", 1, 0, "×", "1×2", 2)]
    [InlineData("12", 0, 2, "7", "7", 1)]
    [InlineData("", 0, 0, "sin(", "sin(", 4)]
    [InlineData("12", 99, 5, "3", "123", 3)] // out-of-range caret is clamped
    public void InsertsAtCaret(string text, int start, int length, string insertion, string expectedText, int expectedCaret)
    {
        var (newText, caret) = CalcSession.Insert(text, start, length, insertion);
        Assert.Equal(expectedText, newText);
        Assert.Equal(expectedCaret, caret);
    }

    [Theory]
    [InlineData("123", 3, 0, "12", 2)]
    [InlineData("123", 1, 0, "23", 0)]
    [InlineData("123", 0, 0, "123", 0)]
    [InlineData("123", 0, 2, "3", 0)]
    [InlineData("", 0, 0, "", 0)]
    public void Backspaces(string text, int start, int length, string expectedText, int expectedCaret)
    {
        var (newText, caret) = CalcSession.Backspace(text, start, length);
        Assert.Equal(expectedText, newText);
        Assert.Equal(expectedCaret, caret);
    }
}
