using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TheCardPlace.UiTests;

/// <summary>
/// Drives the real app through UI Automation, the interface JAWS, NVDA and Narrator use, and
/// listens for the notifications they would speak. Each test starts the app with its own empty
/// settings folder and the Immediate pace, so the computer players move at once. Before every
/// keystroke the harness checks the app is the foreground window, and stops rather than type
/// into anything else.
/// </summary>
[Trait("Category", "UI")]
public sealed partial class AppTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly Application _app;
    private readonly UIA3Automation _automation = new();
    private readonly ConcurrentQueue<string> _heard = new();
    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), "tcp-ui-" + Guid.NewGuid().ToString("N"));
    private Window _window;

    public AppTests()
    {
        Directory.CreateDirectory(_settingsDir);
        // Immediate pace in every game, and a name, so speech is predictable.
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), """
            {
              "PlayerName": "Kelly",
              "Games": {
                "hearts": { "Pace": "Immediate" }, "spades": { "Pace": "Immediate" }, "euchre": { "Pace": "Immediate" },
                "cribbage": { "Pace": "Immediate" }, "sheephead": { "Pace": "Immediate" }
              },
              "LastGame": "hearts"
            }
            """);
        var start = new ProcessStartInfo(FindExe()) { UseShellExecute = false };
        start.Environment["TCP_SETTINGS_DIR"] = _settingsDir;
        _app = Application.Launch(start);
        try
        {
            _window = WaitForMainWindow();
            _window.RegisterNotificationEvent(TreeScope.Subtree, (_, _, _, text, _) => _heard.Enqueue(text));
            BringToFront();
            WaitForFocus(e => e.Name == "Hearts, four players", "the games list");
        }
        catch
        {
            CloseApp();
            throw;
        }
    }

    /// <summary>
    /// The window, found by its process and title. FlaUI's own lookup gives up early on a slow
    /// first start (a cold VM, an emulated runtime), so this polls, and says so if the app died.
    /// </summary>
    private Window WaitForMainWindow()
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (_app.HasExited) throw new InvalidOperationException($"The Card Place exited at start with code {_app.ExitCode}");
            try
            {
                var w = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(2));
                if (w is not null && w.Title.Contains("The Card Place", StringComparison.Ordinal)) return w;
            }
            catch (COMException) { }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("The Card Place did not open a window in 45 seconds");
    }

    public void Dispose()
    {
        CloseApp();
        try { Directory.Delete(_settingsDir, true); } catch (IOException) { }
    }

    private void CloseApp()
    {
        try
        {
            if (!_app.HasExited) _app.Kill();
        }
        catch (ArgumentException) { }
        _app.Dispose();
        _automation.Dispose();
    }

    private static string FindExe()
    {
        // tests\TheCardPlace.UiTests\bin\<Configuration>\net10.0-windows10.0.19041.0 -> src\TheCardPlace\bin\<Configuration>\net10.0-windows10.0.19041.0
        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = testDir.Parent!.Name;
        var root = testDir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var bin = Path.Combine(root, "src", "TheCardPlace", "bin", configuration);
        var exe = Directory.Exists(bin) ? Directory.GetFiles(bin, "TheCardPlace.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        Assert.True(exe is not null, $"App not built under {bin}");
        return exe!;
    }

    // ---- keeping keystrokes inside the app ----

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private bool AppIsInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == (uint)_app.ProcessId;
    }

    private void BringToFront()
    {
        var timer = Stopwatch.StartNew();
        var tries = 0;
        while (!AppIsInFront())
        {
            if (timer.Elapsed > Timeout) throw new InvalidOperationException("The app could not come to the front; not sending keys elsewhere");
            // Windows only lets a process that has had input bring another window forward. A test
            // runner started from a script has had none, so after the first try it taps Alt, which
            // counts as input and changes nothing in any window, then asks again.
            if (tries++ > 0)
            {
                Keyboard.Press(VirtualKeyShort.ALT);
                Keyboard.Release(VirtualKeyShort.ALT);
            }
            _window.SetForeground();
            Thread.Sleep(150);
        }
    }

    private void Send(params VirtualKeyShort[] keys)
    {
        if (!AppIsInFront()) throw new InvalidOperationException("Another window came to the front; stopped sending keys");
        if (keys.Length == 1) Keyboard.Press(keys[0]);
        else Keyboard.TypeSimultaneously(keys);
        if (keys.Length == 1) Keyboard.Release(keys[0]);
        Thread.Sleep(120);
    }

    private void Type(char c)
    {
        if (!AppIsInFront()) throw new InvalidOperationException("Another window came to the front; stopped sending keys");
        Keyboard.Type(c);
        Thread.Sleep(120);
    }

    private AutomationElement Focused() => _automation.FocusedElement();

    private AutomationElement WaitForFocus(Func<AutomationElement, bool> test, string what)
    {
        var timer = Stopwatch.StartNew();
        AutomationElement? f = null;
        while (timer.Elapsed < Timeout)
        {
            try
            {
                f = Focused();
                if (test(f)) return f;
            }
            catch (COMException) { }
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"Focus never reached {what}; it is on {Describe(f)}");
    }

    private string WaitToHear(Func<string, bool> test, string what)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < Timeout)
        {
            var hit = _heard.FirstOrDefault(test);
            if (hit is not null) return hit;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"Never heard {what}. Heard: {string.Join(" | ", _heard)}");
    }

    private static string Describe(AutomationElement? e)
    {
        if (e is null) return "nothing";
        try { return $"{e.ControlType} '{e.Name}'"; }
        catch (COMException) { return "an element that went away"; }
    }

    private static readonly string[] Games =
        ["Hearts, four players", "Euchre, four players", "Spades, four players", "Cribbage, two players", "Sheephead, three to six players"];

    /// <summary>Picks a game the way a person does: Home, Down to it, Enter.</summary>
    private void StartGame(string listName, string windowTitle)
    {
        Send(VirtualKeyShort.HOME);
        WaitForFocus(e => e.Name == Games[0], "the first game");
        for (var i = 0; i < Array.IndexOf(Games, listName); i++) Send(VirtualKeyShort.DOWN);
        WaitForFocus(e => e.Name == listName, listName);
        Send(VirtualKeyShort.ENTER);
        var timer = Stopwatch.StartNew();
        while (!_window.Title.StartsWith(windowTitle, StringComparison.Ordinal) && timer.Elapsed < Timeout) Thread.Sleep(100);
        Assert.StartsWith(windowTitle, _window.Title, StringComparison.Ordinal);
    }

    /// <summary>Tab round the window once and return what each stop was.</summary>
    private List<AutomationElement> TabRing(int max = 25)
    {
        var first = Focused();
        var firstKey = Describe(first);
        var stops = new List<AutomationElement> { first };
        for (var i = 0; i < max; i++)
        {
            Send(VirtualKeyShort.TAB);
            var f = Focused();
            if (Describe(f) == firstKey) return stops;
            stops.Add(f);
        }
        throw new Xunit.Sdk.XunitException("Tab never came back round: " + string.Join(", ", stops.Select(Describe)));
    }

    // ---- the hub ----

    [Fact]
    public void Hub_tab_ring_holds_each_control_once_and_no_menu()
    {
        var ring = TabRing();
        var names = ring.Select(Describe).ToList();
        Assert.DoesNotContain(ring, e => e.ControlType == ControlType.MenuItem || e.ControlType == ControlType.MenuBar);
        Assert.Equal(names.Count, names.Distinct().Count());
        // The games list is one stop, however many games it holds.
        Assert.Single(ring, e => e.ControlType == ControlType.ListItem);
        Assert.Contains("Edit 'Your name'", names);
        Assert.Contains(ring, e => e.ControlType == ControlType.Button && e.Name == "Play Hearts");
    }

    [Fact]
    public void Arrows_move_through_the_games_and_the_details_follow()
    {
        Send(VirtualKeyShort.DOWN);
        WaitForFocus(e => e.Name == "Euchre, four players", "Euchre");
        Assert.NotNull(_window.FindFirstDescendant(cf => cf.ByName("Play Euchre")));
        Send(VirtualKeyShort.END);
        WaitForFocus(e => e.Name == "Sheephead, three to six players", "Sheephead");
        Assert.NotNull(_window.FindFirstDescendant(cf => cf.ByName("Players")));
    }

    // ---- a table ----

    [Fact]
    public void The_hand_is_one_tab_stop_and_arrows_move_card_by_card()
    {
        StartGame("Hearts, four players", "Hearts");
        var first = WaitForFocus(e => e.ControlType == ControlType.ListItem && e.Parent.Name == "Your hand", "a card");
        Send(VirtualKeyShort.RIGHT);
        var second = WaitForFocus(e => e.Name != first.Name && e.Parent.Name == "Your hand", "the next card");
        Send(VirtualKeyShort.LEFT);
        WaitForFocus(e => e.Name == first.Name, "the first card again");
        Send(VirtualKeyShort.END);
        Send(VirtualKeyShort.HOME);
        WaitForFocus(e => e.Name == first.Name, "the first card after Home");
        Assert.NotEqual(first.Name, second.Name);

        var ring = TabRing();
        Assert.Single(ring, e => e.ControlType == ControlType.ListItem && e.Parent.Name == "Your hand");
        Assert.Single(ring, e => e.ControlType == ControlType.ListItem && e.Parent.Name == "Table");
        Assert.DoesNotContain(ring, e => e.ControlType == ControlType.MenuItem);
        Assert.DoesNotContain(ring, e => e.Name == "Last message");
        // Top to bottom: the status line, the table, the hand, then the button that moves the game on.
        var order = ring.Select(e => e.Name).ToList();
        Assert.True(order.IndexOf("Status") < ring.FindIndex(e => e.Parent.Name == "Table"));
    }

    [Fact]
    public void Choosing_three_cards_and_passing_gets_the_game_going()
    {
        StartGame("Hearts, four players", "Hearts");
        WaitForFocus(e => e.Parent.Name == "Your hand", "a card");
        _heard.Clear();
        for (var i = 0; i < 3; i++)
        {
            Send(VirtualKeyShort.SPACE);
            WaitForFocus(e => e.Name.EndsWith(", chosen", StringComparison.Ordinal), "a chosen card");
            Send(VirtualKeyShort.RIGHT);
        }
        WaitToHear(t => t.Contains("Three of three chosen", StringComparison.Ordinal), "the third card chosen");
        // N never passes; the Pass button is the next Tab stop after the hand.
        Type('n');
        Assert.NotNull(_window.FindFirstDescendant(cf => cf.ByName("Pass 3 of 3")));
        Send(VirtualKeyShort.TAB);
        WaitForFocus(e => e.Name == "Pass 3 of 3", "the Pass button");
        Send(VirtualKeyShort.ENTER);
        WaitToHear(t => t.Contains("Your turn", StringComparison.Ordinal) || t.Contains("passed you", StringComparison.Ordinal), "the pass and the play");
        WaitForFocus(e => e.Parent.Name == "Your hand", "the hand after passing");
    }

    [Fact]
    public void A_card_that_cannot_be_played_is_named_so_and_refused_aloud()
    {
        StartGame("Hearts, four players", "Hearts");
        WaitForFocus(e => e.Parent.Name == "Your hand", "a card");
        for (var i = 0; i < 3; i++) { Send(VirtualKeyShort.SPACE); Send(VirtualKeyShort.RIGHT); }
        Send(VirtualKeyShort.TAB);
        WaitForFocus(e => e.Name == "Pass 3 of 3", "the Pass button");
        Send(VirtualKeyShort.ENTER);
        // Play until a card is marked, looking along the hand with the arrows as a person would.
        // At Immediate pace each of our plays brings the turn straight back.
        for (var trick = 0; trick < 13; trick++)
        {
            WaitForFocus(e => e.Parent?.Name == "Your hand", "the hand");
            Send(VirtualKeyShort.HOME);
            for (var i = 0; i < 13; i++)
            {
                var f = Focused();
                if (f.Name.Contains("cannot be played", StringComparison.Ordinal))
                {
                    var name = f.Name;
                    _heard.Clear();
                    Send(VirtualKeyShort.ENTER);
                    WaitToHear(t => t.Contains("cannot be played, ", StringComparison.Ordinal), "the refusal");
                    Assert.Equal(name, Focused().Name);
                    return;
                }
                Send(VirtualKeyShort.RIGHT);
            }
            // Nothing marked: every card is playable, so play the first.
            Send(VirtualKeyShort.HOME);
            Send(VirtualKeyShort.ENTER);
            Thread.Sleep(400);
        }
        throw new Xunit.Sdk.XunitException("no unplayable card came up in a whole hand");
    }

    /// <summary>
    /// Playing the focused card takes it out of the hand. Focus must go to another card first:
    /// if it fell to the window even for a moment, JAWS would read the whole window.
    /// </summary>
    [Fact]
    public void Focus_never_falls_to_the_window_while_cards_are_played()
    {
        StartGame("Hearts, four players", "Hearts");
        WaitForFocus(e => e.Parent.Name == "Your hand", "a card");
        var landed = new ConcurrentQueue<string>();
        var handler = _automation.RegisterFocusChangedEvent(e =>
        {
            try { landed.Enqueue($"{e.ControlType}|{e.Name}"); } catch (COMException) { }
        });
        try
        {
            for (var i = 0; i < 3; i++) { Send(VirtualKeyShort.SPACE); Send(VirtualKeyShort.RIGHT); }
            Send(VirtualKeyShort.TAB);
            WaitForFocus(e => e.Name == "Pass 3 of 3", "the Pass button");
            Send(VirtualKeyShort.ENTER);
            for (var trick = 0; trick < 6; trick++)
            {
                WaitForFocus(e => e.Parent?.Name == "Your hand", "the hand");
                Send(VirtualKeyShort.HOME);
                for (var i = 0; i < 13 && Focused().Name.Contains("cannot be played", StringComparison.Ordinal); i++)
                    Send(VirtualKeyShort.RIGHT);
                Send(VirtualKeyShort.ENTER);
                Thread.Sleep(500);
            }
            Thread.Sleep(500);
        }
        finally
        {
            handler.Dispose();
        }
        // Only ever a card or a control: never the window, the page or a pane round the lists.
        var allowed = new[] { ControlType.ListItem, ControlType.Button, ControlType.ComboBox, ControlType.CheckBox };
        var stray = landed.Where(l => !allowed.Any(t => l.StartsWith($"{t}|", StringComparison.Ordinal))).ToList();
        Assert.True(stray.Count == 0, "Focus landed on " + string.Join(" ; ", stray) + "\nAll: " + string.Join(" ; ", landed));
        Assert.Contains(landed, l => l.StartsWith($"{ControlType.ListItem}|", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_keys_and_the_status_bar_keys_speak_without_moving_focus()
    {
        StartGame("Hearts, four players", "Hearts");
        var card = WaitForFocus(e => e.Parent.Name == "Your hand", "a card");
        _heard.Clear();
        Type('h');
        WaitToHear(t => t.Contains("clubs, ", StringComparison.OrdinalIgnoreCase) || t.Contains("club, ", StringComparison.OrdinalIgnoreCase), "the hand read out");
        Assert.Equal(card.Name, Focused().Name);
        _heard.Clear();
        Send(VirtualKeyShort.CONTROL, VirtualKeyShort.F9);
        var statusBar = (_window.FindFirstDescendant(cf => cf.ByName("Last message")) ?? throw new Xunit.Sdk.XunitException("no status bar")).AsTextBox();
        WaitToHear(t => t == statusBar.Text, "the status bar read again");
        Send(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.F9);
        WaitForFocus(e => e.Name == "Last message", "the status bar");
        Send(VirtualKeyShort.ESCAPE);
        WaitForFocus(e => e.Name == card.Name, "back where we were");
    }

    [Fact]
    public void Leaving_a_table_asks_and_no_keeps_the_game()
    {
        StartGame("Hearts, four players", "Hearts");
        WaitForFocus(e => e.Parent.Name == "Your hand", "a card");
        Send(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        var dialog = Retry.WhileNull(() => _window.ModalWindows.FirstOrDefault(), Timeout).Result
                     ?? throw new Xunit.Sdk.XunitException("no question was asked");
        Assert.Equal("Leave this table?", dialog.Title);
        WaitForFocus(e => e.Name == "No", "No, the safe default");
        Send(VirtualKeyShort.ENTER);
        WaitForFocus(e => e.Parent?.Name == "Your hand", "the hand again");
        Assert.StartsWith("Hearts", _window.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hearts, four players", "Hearts")]
    [InlineData("Euchre, four players", "Euchre")]
    [InlineData("Spades, four players", "Spades")]
    [InlineData("Cribbage, two players", "Cribbage")]
    [InlineData("Sheephead, three to six players", "Sheephead")]
    public void Every_game_opens_with_focus_on_something_to_do(string listName, string title)
    {
        StartGame(listName, title);
        var f = WaitForFocus(e => e.Parent?.Name == "Your hand" || e.ControlType is ControlType.Button or ControlType.ComboBox,
            "the hand, a choice or the button");
        Assert.NotEqual(string.Empty, f.Name);
        var status = (_window.FindFirstDescendant(cf => cf.ByAutomationId("StatusLine")) ?? throw new Xunit.Sdk.XunitException("no status line")).AsTextBox();
        Assert.False(string.IsNullOrWhiteSpace(status.Text));
        var ring = TabRing();
        Assert.DoesNotContain(ring, e => e.ControlType == ControlType.MenuItem);
        // The hand is one stop: a card, or before the deal (cribbage starts with the cut) the empty list.
        Assert.Equal(1, ring.Count(e => (e.ControlType == ControlType.ListItem && e.Parent?.Name == "Your hand")
                                        || (e.ControlType == ControlType.List && e.Name == "Your hand")));
        Assert.All(ring, e => Assert.False(string.IsNullOrEmpty(e.Name), $"a {e.ControlType} with no name"));
    }
}
