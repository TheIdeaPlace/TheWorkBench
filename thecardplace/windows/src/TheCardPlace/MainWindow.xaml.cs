using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TheCardPlace.App.Views;
using TheCardPlace.Play;

namespace TheCardPlace.App;

/// <summary>
/// The one window. It shows the hub or a table, owns the menus, the status bar and the announcer,
/// and handles the keys that work everywhere (the Ctrl ones and the function keys). The single
/// letter game keys belong to the table view.
/// </summary>
public partial class MainWindow : Window
{
    private const double MinZoom = 0.8, MaxZoom = 2.0, ZoomStep = 0.1;

    private readonly Announcer _announcer;
    private HubView? _hub;
    private GameView? _game;
    private IInputElement? _beforeStatus;

    public MainWindow()
    {
        InitializeComponent();
        _announcer = new Announcer(App.Settings.Announcements, new UiaSpeaker(this));
        _announcer.Announced += a => StatusText.Text = _announcer.LastText;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += (_, e) =>
        {
            if (_game is not null && !ConfirmLeave("Close The Card Place?")) e.Cancel = true;
        };
        Closed += (_, _) => _game?.Session.Dispose();
        ShowHub();
    }

    internal Announcer Announcer => _announcer;

    // ---- pages ----

    public void ShowHub()
    {
        _game?.Session.Dispose();
        _game = null;
        _hub ??= new HubView(this);
        _hub.Refresh();
        Page.Content = _hub;
        Title = "The Card Place";
        ApplyZoom();
        BuildMenus();
        Dispatcher.BeginInvoke(() => _hub.FocusGames(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Play(GameKind kind)
    {
        App.Settings.LastGame = kind.Key();
        SettingsStore.Save(App.Settings);
        var session = GameFactory.Create(kind, App.Settings, _announcer);
        _game = new GameView(this, session);
        Page.Content = _game;
        Title = $"{kind.Title()} - The Card Place";
        ApplyZoom();
        BuildMenus();
        _game.Begin();
    }

    // ---- menus ----

    /// <summary>
    /// The menus for the page on screen. Nothing in a menu is ever disabled or hidden while it is
    /// shown: someone arrowing through a menu should meet everything, and an item that cannot act
    /// says why when chosen.
    /// </summary>
    internal void BuildMenus()
    {
        MainMenu.Items.Clear();
        var game = MenuTop("_Game");
        if (_game is { } g)
        {
            Add(game, "_New game", "Ctrl+N", () => g.NewGame());
            Add(game, "_Leave this table", "Ctrl+W", LeaveTable);
            game.Items.Add(new Separator());
        }
        else
        {
            Add(game, "_Play the selected game", "Enter", () => _hub?.PlaySelected());
            game.Items.Add(new Separator());
        }
        Add(game, "_Settings…", "Ctrl+Comma", OpenSettings);
        game.Items.Add(new Separator());
        Add(game, "E_xit", "Alt+F4", Close);

        if (_game is { } gv)
        {
            var review = MenuTop("_Review");
            foreach (var r in gv.Session.Reviews)
                Add(review, Escape(r.Title), r.Key is { } k ? char.ToUpperInvariant(k).ToString() : "", () => _announcer.Request(r.Text()));
            review.Items.Add(new Separator());
            Add(review, "_Continue or deal", "N", () => gv.Session.Advance());
            Add(review, "R_epeat the last message", "R", () => _announcer.RepeatLast());
        }

        var view = MenuTop("_View");
        if (_game is { } gl)
        {
            Add(view, "Game _log", "G", () => gl.FocusLog());
            Add(view, "Your _hand", "Alt+Y", () => gl.FocusHand());
            view.Items.Add(new Separator());
        }
        Add(view, "_Read the status bar", "Ctrl+F9", ReadStatusBar);
        Add(view, "_Go to the status bar", "Ctrl+Shift+F9", GoToStatusBar);
        view.Items.Add(new Separator());
        Add(view, "Zoom _in", "Ctrl+Plus", () => Zoom(ZoomStep));
        Add(view, "Zoom _out", "Ctrl+Minus", () => Zoom(-ZoomStep));
        Add(view, "Reset _zoom", "Ctrl+0", () => Zoom(0));

        var help = MenuTop("_Help");
        Add(help, _game is null ? "_How to play the selected game" : "_How to play", "F1", ShowRules);
        Add(help, "_Keyboard shortcuts", "?", ShowKeys);
        help.Items.Add(new Separator());
        Add(help, "_About The Card Place", "", () => new AboutWindow { Owner = this }.ShowDialog());
    }

    private MenuItem MenuTop(string header)
    {
        var m = new MenuItem { Header = header };
        MainMenu.Items.Add(m);
        return m;
    }

    private static void Add(MenuItem parent, string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }

    /// <summary>A review title as a menu header: its first letter is the access key, and any underscore is literal.</summary>
    private static string Escape(string title) => "_" + title.Replace("_", "__");

    // ---- commands ----

    private void LeaveTable()
    {
        if (_game is null) return;
        if (!ConfirmLeave("Leave this table?")) return;
        ShowHub();
        _announcer.Say("Back at the games.", AnnouncementCategory.Result);
    }

    private bool ConfirmLeave(string question)
    {
        if (_game is null || !_game.Session.InProgress) return true;
        return MessageBox.Show(this, "The game in progress will be lost.", question,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void OpenSettings()
    {
        var w = new SettingsWindow(App.Settings) { Owner = this };
        if (w.ShowDialog() == true)
        {
            SettingsStore.Save(App.Settings);
            _hub?.Refresh();
            _game?.Refresh();
            _announcer.Say("Settings saved.", AnnouncementCategory.Result);
        }
    }

    private void ShowRules()
    {
        var kind = _game?.Session.Kind ?? _hub?.SelectedKind ?? GameKind.Hearts;
        new HelpWindow(kind) { Owner = this }.ShowDialog();
    }

    internal void ShowKeys() => new HelpWindow(null) { Owner = this }.ShowDialog();

    private void ReadStatusBar()
    {
        if (StatusText.Text.Length == 0) _announcer.Request("The status bar is empty.");
        else _announcer.Request(StatusText.Text);
    }

    private void GoToStatusBar()
    {
        _beforeStatus = Keyboard.FocusedElement;
        StatusText.Focusable = true;
        StatusText.Focus();
        StatusText.SelectAll();
    }

    private void StatusText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F6)
        {
            e.Handled = true;
            if (_beforeStatus is UIElement { IsVisible: true } back) back.Focus();
            else if (_game is not null) _game.FocusHand();
            else _hub?.FocusGames();
        }
    }

    private void Zoom(double step)
    {
        App.Settings.Zoom = step == 0 ? 1.0 : Math.Clamp(Math.Round(App.Settings.Zoom + step, 1), MinZoom, MaxZoom);
        ApplyZoom();
        SettingsStore.Save(App.Settings);
        _announcer.Say($"Zoom {App.Settings.Zoom * 100:0} percent", AnnouncementCategory.Result);
    }

    private void ApplyZoom()
    {
        if (Page.Content is FrameworkElement fe)
            fe.LayoutTransform = new System.Windows.Media.ScaleTransform(App.Settings.Zoom, App.Settings.Zoom);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        Action? run = (mods, key) switch
        {
            (ModifierKeys.Control, Key.N) when _game is not null => () => _game.NewGame(),
            (ModifierKeys.Control, Key.W) when _game is not null => LeaveTable,
            (ModifierKeys.Control, Key.OemComma) => OpenSettings,
            (ModifierKeys.Control, Key.F9) => ReadStatusBar,
            (ModifierKeys.Control | ModifierKeys.Shift, Key.F9) => GoToStatusBar,
            (ModifierKeys.Control, Key.OemPlus or Key.Add) => () => Zoom(ZoomStep),
            (ModifierKeys.Control, Key.OemMinus or Key.Subtract) => () => Zoom(-ZoomStep),
            (ModifierKeys.Control, Key.D0 or Key.NumPad0) => () => Zoom(0),
            (ModifierKeys.None, Key.F1) => ShowRules,
            _ => null,
        };
        if (run is null) return;
        e.Handled = true;
        run();
    }
}
