using System.Collections.Concurrent;
using System.Diagnostics;
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
/// These send real keystrokes, so they take focus while they run.
/// </summary>
[Trait("Category", "UI")]
public sealed class MainWindowTests : IDisposable
{
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
            EnsureForeground();
            WaitFor(() => FocusedId() == "ExpressionBox", "expression box focused at start");
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
            while (!_app.HasExited && timer.Elapsed < TimeSpan.FromSeconds(5))
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

    /// <summary>Keystrokes go to whatever window is in front, so never send any unless QuickCalc has focus.</summary>
    private void EnsureForeground()
    {
        var timer = Stopwatch.StartNew();
        while (_automation.FocusedElement()?.Properties.ProcessId.ValueOrDefault != _app.ProcessId)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("QuickCalc could not take focus; not sending keystrokes to another window");
            _window.SetForeground();
            Thread.Sleep(100);
        }
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

    private AutomationElement ById(string id) =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? throw new InvalidOperationException($"No element {id}");

    private AutomationElement Keypad =>
        _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Group).And(cf.ByName("Keypad"))) ?? throw new InvalidOperationException("No Keypad group");

    private TextBox Expression => ById("ExpressionBox").AsTextBox();

    private TextBox Result => ById("ResultBox").AsTextBox();

    private ListBox History => ById("HistoryList").AsListBox();

    private Button Key(string name) =>
        Keypad.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)))?.AsButton() ?? throw new InvalidOperationException($"No key {name}");

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

    private static void WaitFor(Func<bool> condition, string what)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(50);
        }
    }

    private void WaitForAnnouncement(string text) =>
        WaitFor(() => _announcements.Contains(text), $"announcement \"{text}\" (heard: {string.Join(" | ", _announcements)})");

    private void Type(string text)
    {
        EnsureForeground();
        Keyboard.Type(text);
        Thread.Sleep(150);
    }

    private void Press(VirtualKeyShort key)
    {
        EnsureForeground();
        Keyboard.Type(key);
        Thread.Sleep(150);
    }

    private void PressWith(VirtualKeyShort modifier, VirtualKeyShort key)
    {
        EnsureForeground();
        Keyboard.TypeSimultaneously(modifier, key);
        Thread.Sleep(150);
    }

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

        WaitForAnnouncement("equals 36");
        Assert.Equal("36", Result.Text);
        Assert.Equal("", Expression.Text);
        var item = Assert.Single(History.Items);
        Assert.Equal("12 × 3 = 36", item.Name);

        // Starting with an operator continues from the answer.
        Type("*2=");
        WaitForAnnouncement("equals 72");
        Assert.Equal("72", Result.Text);
        Assert.Equal("ans × 2 = 72", History.Items[0].Name);
    }

    [Fact]
    public void ErrorIsAnnouncedAndExpressionIsKept()
    {
        Type("5+1/0");
        Press(VirtualKeyShort.ENTER);

        WaitForAnnouncement("Error: Cannot divide by zero");
        Assert.Equal("Error: Cannot divide by zero", Result.Text);
        Assert.Equal("5+1/0", Expression.Text);
        Assert.Empty(History.Items);
    }

    [Fact]
    public void KeypadButtonsBuildAndCalculate()
    {
        Key("7").Invoke();
        WaitForAnnouncement("7");
        Key("Multiply").Invoke();
        WaitForAnnouncement("7 times");
        Key("6").Invoke();
        Key("Equals").Invoke();
        WaitForAnnouncement("equals 42");
        Assert.Equal("42", Result.Text);

        Key("Square root").Invoke();
        Key("9").Invoke();
        Key("Close paren").Invoke();
        WaitForAnnouncement("square root of open paren 9 close paren");
        Key("Backspace").Invoke();
        WaitForAnnouncement("square root of open paren 9");
        Key("Clear").Invoke();
        WaitForAnnouncement("Cleared");
        Assert.Equal("", Expression.Text);
    }

    [Fact]
    public void UpAndDownRecallHistory()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        Type("2+2");
        Press(VirtualKeyShort.ENTER);

        Press(VirtualKeyShort.UP);
        Assert.Equal("2 + 2", Expression.Text);
        WaitForAnnouncement("2 plus 2");
        Press(VirtualKeyShort.UP);
        Assert.Equal("1 + 1", Expression.Text);
        Press(VirtualKeyShort.UP);
        WaitForAnnouncement("No earlier calculations");
        Press(VirtualKeyShort.DOWN);
        Assert.Equal("2 + 2", Expression.Text);
        Press(VirtualKeyShort.DOWN);
        Assert.Equal("", Expression.Text);
        WaitForAnnouncement("Empty");
    }

    [Fact]
    public void EscapeClearsTheExpression()
    {
        Type("123");
        Press(VirtualKeyShort.ESCAPE);
        Assert.Equal("", Expression.Text);
        WaitForAnnouncement("Cleared");
    }

    [Fact]
    public void TabVisitsEachAreaOnceAndKeypadIsOneStop()
    {
        Press(VirtualKeyShort.TAB);
        Assert.Equal("ResultBox", FocusedId());
        Press(VirtualKeyShort.TAB);
        Assert.Equal("AngleUnitBox", FocusedId());
        Press(VirtualKeyShort.TAB);
        Assert.Equal("Sine", FocusedName());
        Press(VirtualKeyShort.TAB);
        Assert.Equal("HistoryList", FocusedId()); // empty, but still reachable
        Press(VirtualKeyShort.TAB);
        Assert.Equal("ExpressionBox", FocusedId()); // wraps around without stopping on the menu bar
        PressWith(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Assert.Equal("HistoryList", FocusedId());
        PressWith(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Assert.Equal("Sine", FocusedName());
    }

    [Fact]
    public void HistoryWithItemsIsOneTabStop()
    {
        Type("1+1");
        Press(VirtualKeyShort.ENTER);
        Type("2+2");
        Press(VirtualKeyShort.ENTER);
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        Press(VirtualKeyShort.TAB);
        Assert.Equal("2 + 2 = 4", FocusedName());
        Press(VirtualKeyShort.TAB);
        Assert.Equal("ExpressionBox", FocusedId());
    }

    [Fact]
    public void AltOpensTheMenu()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        WaitFor(() => FocusedName() == "Exit", $"Exit menu item focused (focus: {FocusedId()})");
        Press(VirtualKeyShort.ESCAPE);
        Press(VirtualKeyShort.ESCAPE);
        WaitFor(() => FocusedId() == "ExpressionBox", "focus back in the expression box");
    }

    [Fact]
    public void ArrowKeysMoveAroundTheKeypad()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        Assert.Equal("Sine", FocusedName());
        Press(VirtualKeyShort.RIGHT);
        Assert.Equal("Cosine", FocusedName());
        Press(VirtualKeyShort.DOWN);
        Assert.Equal("Squared", FocusedName());
        Press(VirtualKeyShort.LEFT);
        Press(VirtualKeyShort.LEFT); // stays inside the keypad at the edge
        Assert.Equal("Square root", FocusedName());

        // Tab out and back in returns to the last key used.
        Press(VirtualKeyShort.TAB);
        PressWith(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Assert.Equal("Square root", FocusedName());

        Press(VirtualKeyShort.SPACE);
        WaitForAnnouncement("square root of open paren");
        Assert.Equal("√(", Expression.Text);
    }

    [Fact]
    public void TypingOnTheKeypadGoesToTheExpression()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
        Assert.Equal("Sine", FocusedName());
        Type("4+4");
        Assert.Equal("ExpressionBox", FocusedId());
        Assert.Equal("4+4", Expression.Text);
    }

    [Fact]
    public void AccessKeysReachEachArea()
    {
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_R);
        Assert.Equal("ResultBox", FocusedId());
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
        Assert.Equal("ExpressionBox", FocusedId());
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        Assert.Equal("HistoryList", FocusedId());
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_A);
        Assert.Equal("AngleUnitBox", FocusedId());
    }

    [Fact]
    public void RadiansOptionChangesTrig()
    {
        Type("cos(pi)");
        Press(VirtualKeyShort.ENTER);
        WaitFor(() => Result.Text.StartsWith("0.998"), "cos(pi) in degrees is cos(3.14°)");

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_A);
        Press(VirtualKeyShort.DOWN);
        Assert.Equal("Radians", ById("AngleUnitBox").AsComboBox().SelectedItem?.Name);
        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
        Type("cos(pi)");
        Press(VirtualKeyShort.ENTER);
        WaitForAnnouncement("equals minus 1");
        Assert.Equal("-1", Result.Text);
    }

    [Fact]
    public void HistoryEnterRecallsAndDeleteRemoves()
    {
        Type("1+2");
        Press(VirtualKeyShort.ENTER);
        Type("3+4");
        Press(VirtualKeyShort.ENTER);

        PressWith(VirtualKeyShort.ALT, VirtualKeyShort.KEY_Y);
        Press(VirtualKeyShort.HOME);
        Assert.Equal("3 + 4 = 7", FocusedName());
        Press(VirtualKeyShort.DELETE);
        WaitForAnnouncement("Removed");
        Assert.Equal("1 + 2 = 3", FocusedName());
        Press(VirtualKeyShort.ENTER);
        Assert.Equal("ExpressionBox", FocusedId());
        Assert.Equal("1 + 2", Expression.Text);
    }

    [Fact]
    public void ZoomIsAnnounced()
    {
        PressWith(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_PLUS);
        WaitForAnnouncement("Zoom 110 percent");
        PressWith(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_0);
        WaitForAnnouncement("Zoom 100 percent");
    }
}
