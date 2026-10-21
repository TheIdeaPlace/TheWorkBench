using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuickCalc.UiTests;

/// <summary>
/// Drives the real app through UI Automation, the same interface JAWS, NVDA and Narrator use.
/// These send real keystrokes, so they take focus while they run. Before every keystroke the harness checks that
/// QuickCalc is the foreground window, and stops the test rather than type into anything else.
/// </summary>
[Trait("Category", "UI")]
public sealed partial class MainWindowTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Application _app;
    private readonly UIA3Automation _automation = new();
    private readonly Window _window;
    private readonly ConcurrentQueue<string> _announcements = new();

    public MainWindowTests()
    {
        _app = Application.Launch(FindExe());
        try
        {
            _window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(15))
                      ?? throw new InvalidOperationException("QuickCalc window did not appear");
            _window.RegisterNotificationEvent(TreeScope.Subtree, (_, _, _, text, _) => _announcements.Enqueue(text));
            BringToFront();
            ExpectFocusId("ExpressionBox");
        }
        catch
        {
            // xUnit doesn't call Dispose when the constructor throws, and a leftover app keeps the test run open.
            CloseApp();
            throw;
        }
    }

    public void Dispose() => CloseApp();

    private void CloseApp()
    {
        try
        {
            if (!_app.HasExited)
                _app.Close();
            var timer = Stopwatch.StartNew();
            while (!_app.HasExited && timer.Elapsed < Timeout)
                Thread.Sleep(50);
            if (!_app.HasExited)
                _app.Kill();
        }
        catch (ArgumentException)
        {
            // The process is already gone.
        }
        _app.Dispose();
        _automation.Dispose();
    }

    private static string FindExe()
    {
        // tests\QuickCalc.UiTests\bin\<Configuration>\net10.0-windows -> src\QuickCalc\bin\<Configuration>\net10.0-windows
        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string tfm = testDir.Name;
        string configuration = testDir.Parent!.Name;
        string root = testDir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        string exe = Path.Combine(root, "src", "QuickCalc", "bin", configuration, tfm, "QuickCalc.exe");
        Assert.True(File.Exists(exe), $"App not built at {exe}");
        return exe;
    }

    // ---- Keeping keystrokes inside QuickCalc ----

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private bool QuickCalcIsInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        return processId == (uint)_app.ProcessId;
    }

    private void BringToFront()
    {
        var timer = Stopwatch.StartNew();
        while (!QuickCalcIsInFront())
        {
            if (timer.Elapsed > Timeout)
                throw new InvalidOperationException("QuickCalc could not come to the front; not sending keystrokes to another window");
            _window.SetForeground();
            Thread.Sleep(100);
        }
    }

    /// <summary>Every keystroke goes through here. It refuses to send anything unless QuickCalc is the foreground window.</summary>
    private void Send(Action keystroke)
    {
        if (!QuickCalcIsInFront())
            throw new InvalidOperationException("Another window came to the front during the test; stopped sending keystrokes");
        keystroke();
        Thread.Sleep(60);
    }

    /// <summary>Starts a new step: forgets earlier announcements so a wait can only be satisfied by what this step says.</summary>
    private void NewStep() => _announcements.Clear();

    private void Type(string text)
    {
        NewStep();
        foreach (char c in text)
            Send(() => Keyboard.Type(c));
    }

    private void Press(VirtualKeyShort key)
    {
        NewStep();
        Send(() => Keyboard.Type(key));
    }

    private void PressWith(VirtualKeyShort modifier, VirtualKeyShort key)
    {
        NewStep();
        Send(() => Keyboard.TypeSimultaneously(modifier, key));
    }

    private void Invoke(string keyName)
    {
        NewStep();
        Key(keyName).Invoke();
    }

    // ---- Finding things and waiting for them ----

    private AutomationElement ById(string id) =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? throw new InvalidOperationException($"No element {id}");

    private AutomationElement Keypad =>
        _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Group).And(cf.ByName("Keypad"))) ?? throw new InvalidOperationException("No Keypad group");

    private TextBox Expression => ById("ExpressionBox").AsTextBox();

    private TextBox Result => ById("ResultBox").AsTextBox();

    private ListBox History => ById("HistoryList").AsListBox();

    private Button Key(string name) =>
        Keypad.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)))?.AsButton()
        ?? throw new InvalidOperationException($"No key {name}");

    /// <summary>The focused element's automation id, or a description of it when it has none (useful in failure messages).</summary>
    private string? FocusedId()
    {
        var focused = _automation.FocusedElement();
        if (focused is null)
            return null;
        string? id = focused.Properties.AutomationId.ValueOrDefault;
        return string.IsNullOrEmpty(id)
            ? $"[{focused.Properties.ControlType.ValueOrDefault} \"{focused.Properties.Name.ValueOrDefault}\" in process {focused.Properties.ProcessId.ValueOrDefault}]"
            : id;
    }

    private string? FocusedName() => _automation.FocusedElement()?.Properties.Name.ValueOrDefault;

    private static void WaitFor(Func<bool> condition, Func<string> describe)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > Timeout)
                Assert.Fail($"Timed out waiting for {describe()}");
            Thread.Sleep(30);
        }
    }

    private void ExpectFocusId(string id) => WaitFor(() => FocusedId() == id, () => $"focus on {id}; focus is on {FocusedId()}");

    private void ExpectFocusName(string name) => WaitFor(() => FocusedName() == name, () => $"focus on \"{name}\"; focus is on \"{FocusedName()}\"");

    private void ExpectExpression(string text) => WaitFor(() => Expression.Text == text, () => $"expression \"{text}\"; it is \"{Expression.Text}\"");

    private void ExpectResult(string text) => WaitFor(() => Result.Text == text, () => $"result \"{text}\"; it is \"{Result.Text}\"");

    private void ExpectAnnouncement(string text) =>
        WaitFor(() => _announcements.Contains(text), () => $"announcement \"{text}\"; heard: {string.Join(" | ", _announcements)}");

    /// <summary>Waits a moment, then checks this step announced nothing at all.</summary>
    private void ExpectSilence()
    {
        Thread.Sleep(300);
        Assert.True(_announcements.IsEmpty, $"Expected no announcement; heard: {string.Join(" | ", _announcements)}");
    }

    // ---- Tests ----

    [Fact]
    public void EveryControlHasAName()
    {
        Assert.Equal("Expression", Expression.Name);
        Assert.Equal("Result", Result.Name);
        Assert.Equal("History", History.Name);
        Assert.Equal("Angle unit", ById("AngleUnitBox").Name);

        var keys = Keypad.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        Assert.Equal(35, keys.Length);
        var names = keys.Select(k => k.Name).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("Divide", names);
        Assert.Contains("Square root", names);
        Assert.Contains("7", names);
    }

    [Fact]
    public void TypingAndEnterCalculatesAnnouncesAndRecordsHistory()
    {
        Type("12*3");
        Press(VirtualKeyShort.ENTER);
        ExpectAnnouncement("equals 36");
        ExpectResult("36");
        ExpectExpression("");
        Assert.Equal("12 × 3 = 36", Assert.Single(History.Items).Name);

        // Starting with an operator continues from the answer; history shows the number it used.
        Type("*2=");
        ExpectAnnouncement("equals 72");
        ExpectResult("72");
        Assert.Equal("36 × 2 = 72", History.Items[0].Name);
    }

    [Fact]
    public void EnterOnAnEmptyExpressionRepeatsTheAnswer()
    {
        Press(VirtualKeyShort.ENTER);
        ExpectAnnouncement("Nothing to calculate");
        ExpectResult("0");

        Type("2+2");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("4");
        Press(VirtualKeyShort.ENTER);
        ExpectAnnouncement("equals 4");
        ExpectResult("4");
        Assert.Single(History.Items);
    }

    [Fact]
    public void ErrorIsAnnouncedWithItsPlaceAndExpressionIsKept()
    {
        Type("5+1/0");
        Press(VirtualKeyShort.ENTER);
        ExpectAnnouncement("Error: Cannot divide by zero, at character 4");
        ExpectResult("Error: Cannot divide by zero");
        ExpectExpression("5+1/0");
        Assert.Empty(History.Items);
    }

    [Fact]
    public void KeypadButtonsBuildAndCalculate()
    {
        Invoke("7");
        ExpectAnnouncement("7");
        Invoke("Multiply");
        ExpectAnnouncement("7 times");
        Invoke("6");
        Invoke("Equals");
        ExpectAnnouncement("equals 42");
        ExpectResult("42");

        Invoke("Square root");
        Invoke("9");
        Invoke("Close paren");
        ExpectAnnouncement("square root of open paren 9 close paren");
        Invoke("Backspace");
        ExpectAnnouncement("square root of open paren 9");
        Invoke("Clear");
        ExpectAnnouncement("Cleared");
        ExpectExpression("");
    }

    [Fact]
    public void KeypadEAfterADigitStaysTheConstant()
    {
        Invoke("2");
        Invoke("e");
        Invoke("3");
        ExpectExpression("2×e3");
        Invoke("Clear");
        Invoke("2");
        Invoke("e");
        Invoke("Plus");
        Invoke("3");
        Invoke("Equals");
        ExpectResult("8.43656365691809");
    }

    [Fact]
    public void KeypadNamesDoNotRunTogether()
    {
        Invoke("3");
        Invoke("Equals");
        ExpectResult("3");
        Invoke("Previous answer");
        Invoke("e");
        ExpectExpression("ans e");
        Invoke("Natural log");
        ExpectExpression("ans e ln(");
        Invoke("e");
        Invoke("Equals");
        ExpectResult("8.15484548537714"); // 3 × e × ln(e)
    }

    [Fact]
    public void EnterOnAnEmptyExpressionAfterAnErrorShowsTheAnswerAgain()
    {
        Type("6*6");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("36");
        Type("1/0");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("Error: Cannot divide by zero");
        Press(VirtualKeyShort.ESCAPE);
        Press(VirtualKeyShort.ENTER);
        ExpectAnnouncement("equals 36");
        ExpectResult("36");
    }

    [Fact]
    public void NumberPadDecimalTypesAPoint()
    {
        Type("1");
        Press(VirtualKeyShort.DECIMAL);
        Type("5");
        ExpectExpression("1.5");
    }

    [Fact]
    public void UpAndDownRecallHistory()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("2");
        Type("2+2");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("4");

        Press(VirtualKeyShort.UP);
        ExpectExpression("2 + 2");
        ExpectAnnouncement("2 plus 2");
        Press(VirtualKeyShort.UP);
        ExpectExpression("1 + 1");
        Press(VirtualKeyShort.UP);
        ExpectAnnouncement("No earlier calculations");
        Press(VirtualKeyShort.DOWN);
        ExpectExpression("2 + 2");
        Press(VirtualKeyShort.DOWN);
        ExpectExpression("");
        ExpectAnnouncement("Empty");
    }

    [Fact]
    public void EscapeClearsTheExpression()
    {
        Type("123");
        Press(VirtualKeyShort.ESCAPE);
        ExpectExpression("");
        ExpectAnnouncement("Cleared");
    }

    [Fact]
    public void TabVisitsEachAreaOnceAndKeypadIsOneStop()
    {
        Press(VirtualKeyShort.TAB);
        ExpectFocusId("ResultBox");
        Press(VirtualKeyShort.TAB);
        ExpectFocusId("AngleUnitBox");
        Press(VirtualKeyShort.TAB);
        ExpectFocusName("Sine");
        Press(VirtualKeyShort.TAB);
        ExpectFocusId("HistoryList"); // empty, but still reachable
        Press(VirtualKeyShort.TAB);
        ExpectFocusId("ExpressionBox"); // wraps around without stopping on the menu bar
        PressWith(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        ExpectFocusId("HistoryList");
        PressWith(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        ExpectFocusName("Sine");
    }

    [Fact]
    public void HistoryWithItemsIsOneTabStopLandingOnTheNewest()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("2");
        Type("2+2");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("4");
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        ExpectFocusName("Sine");
        Press(VirtualKeyShort.TAB);
        ExpectFocusName("2 + 2 = 4");
        Press(VirtualKeyShort.TAB);
        ExpectFocusId("ExpressionBox");
    }

    [Fact]
    public void TabIntoHistoryLandsOnTheNewestEvenAfterVisitingAnOlderOne()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("2");
        Type("2+2");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("4");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        ExpectFocusName("2 + 2 = 4");
        Press(VirtualKeyShort.DOWN);
        ExpectFocusName("1 + 1 = 2");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
        ExpectFocusId("ExpressionBox");
        Type("3+3");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("6");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        ExpectFocusName("Sine");
        Press(VirtualKeyShort.TAB);
        ExpectFocusName("3 + 3 = 6");
    }

    [Fact]
    public void ClearHistoryFromTheMenuMovesFocusToTheExpression()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("2");
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        ExpectFocusName("1 + 1 = 2");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_E);
        ExpectFocusName("Copy result");
        NewStep();
        Send(() => Keyboard.Type(VirtualKeyShort.KEY_H));
        ExpectFocusId("ExpressionBox");
        ExpectAnnouncement("History cleared");
        Assert.Empty(History.Items);
    }

    [Fact]
    public void ArrowKeysMoveAroundTheKeypad()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        ExpectFocusName("Sine");
        Press(VirtualKeyShort.RIGHT);
        ExpectFocusName("Cosine");
        Press(VirtualKeyShort.DOWN);
        ExpectFocusName("Squared");
        Press(VirtualKeyShort.LEFT);
        ExpectFocusName("Square root");
        Press(VirtualKeyShort.LEFT); // stays inside the keypad at the edge
        ExpectFocusName("Square root");

        // Tab out and back in returns to the last key used.
        Press(VirtualKeyShort.TAB);
        ExpectFocusId("HistoryList");
        PressWith(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        ExpectFocusName("Square root");

        Press(VirtualKeyShort.SPACE);
        ExpectAnnouncement("square root of open paren");
        ExpectExpression("√(");
    }

    [Fact]
    public void TypingOnTheKeypadGoesToTheExpression()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        ExpectFocusName("Sine");
        Type("4+4");
        ExpectFocusId("ExpressionBox");
        ExpectExpression("4+4");
    }

    [Fact]
    public void AccessKeysReachEachArea()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_R);
        ExpectFocusId("ResultBox");
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
        ExpectFocusId("ExpressionBox");
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        ExpectFocusId("HistoryList");
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_A);
        ExpectFocusId("AngleUnitBox");
    }

    [Fact]
    public void AltOpensTheMenu()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        ExpectFocusName("Exit");
        Press(VirtualKeyShort.ESCAPE);
        Press(VirtualKeyShort.ESCAPE);
        ExpectFocusId("ExpressionBox");
    }

    [Fact]
    public void RadiansOptionChangesTrig()
    {
        Type("cos(pi)");
        Press(VirtualKeyShort.ENTER);
        WaitFor(() => Result.Text.StartsWith("0.998"), () => $"cos(3.14°); result is {Result.Text}");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_A);
        ExpectFocusId("AngleUnitBox");
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => ById("AngleUnitBox").AsComboBox().SelectedItem?.Name == "Radians", () => "Radians selected");
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
        ExpectFocusId("ExpressionBox");
        Type("cos(pi)");
        Press(VirtualKeyShort.ENTER);
        ExpectAnnouncement("equals minus 1");
        ExpectResult("-1");
    }

    [Fact]
    public void HistoryKeysActOnTheFocusedEntry()
    {
        Type("1+2");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("3");
        Type("3+4");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("7");

        // Straight in with Alt+Y and Enter, without arrowing first.
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        ExpectFocusName("3 + 4 = 7");
        Press(VirtualKeyShort.ENTER);
        ExpectFocusId("ExpressionBox");
        ExpectExpression("3 + 4");

        // Delete moves to the next entry and lets the screen reader read it, with no announcement on top.
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        ExpectFocusName("3 + 4 = 7");
        Press(VirtualKeyShort.DELETE);
        ExpectFocusName("1 + 2 = 3");
        ExpectSilence();
        Assert.Single(History.Items);

        Press(VirtualKeyShort.DELETE);
        ExpectFocusId("ExpressionBox");
        ExpectAnnouncement("Removed. History is empty");
        Assert.Empty(History.Items);
    }

    [Fact]
    public void IdenticalCalculationsAreSeparateEntries()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        ExpectResult("2");
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        WaitFor(() => History.Items.Length == 2, () => "two history entries");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => History.Items[1].AsListBoxItem().IsSelected, () => "second entry selected");
        Press(VirtualKeyShort.DELETE);
        WaitFor(() => History.Items.Length == 1, () => "one history entry");
        ExpectFocusName("1 + 1 = 2");
    }

    [Fact]
    public void ZoomIsAnnounced()
    {
        PressWith(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_PLUS);
        ExpectAnnouncement("Zoom 110 percent");
        PressWith(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_0);
        ExpectAnnouncement("Zoom 100 percent");
    }
}
