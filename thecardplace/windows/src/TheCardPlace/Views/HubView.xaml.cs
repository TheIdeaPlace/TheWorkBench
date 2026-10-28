using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TheCardPlace.Core;
using TheCardPlace.Play;

namespace TheCardPlace.App.Views;

/// <summary>The opening page: pick a game, set your name and the game's options, and deal.</summary>
public partial class HubView : UserControl
{
    private readonly MainWindow _window;
    private bool _loading;

    public sealed record GameItem(GameKind Kind)
    {
        public string Title => Kind.Title();
        public string Players => Kind.PlayersDescription();
        public string Symbol => Kind.Symbol();
        public bool IsRed => Kind is GameKind.Hearts or GameKind.Sheephead;
        public string AccessibleName => $"{Title}, {Players.ToLowerInvariant()}";
    }

    public HubView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        GamesList.ItemsSource = GameKinds.All.Select(k => new GameItem(k)).ToList();
        var last = GameKinds.FromKey(App.Settings.LastGame) ?? GameKind.Hearts;
        GamesList.SelectedIndex = Array.IndexOf(GameKinds.All, last);
    }

    public GameKind SelectedKind => (GamesList.SelectedItem as GameItem)?.Kind ?? GameKind.Hearts;

    public void Refresh()
    {
        _loading = true;
        NameBox.Text = App.Settings.PlayerName;
        _loading = false;
        ShowDetails();
    }

    public void FocusGames()
    {
        if (GamesList.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, GamesList.SelectedIndex)) is ListBoxItem item)
            item.Focus();
        else
            GamesList.Focus();
    }

    public void PlaySelected() => _window.Play(SelectedKind);

    private void ShowDetails()
    {
        var kind = SelectedKind;
        var gs = App.Settings.For(kind);
        GameTitle.Text = kind.Title();
        GameTagline.Text = kind.Tagline();
        PlayButton.Content = $"_Play {kind.Title()}";
        OptionsPanel.Children.Clear();

        if (kind.HasDifficulty())
        {
            AddChoice("_Difficulty", DifficultyExtensions.All.Select(d => d.Label()).ToList(),
                Array.IndexOf(DifficultyExtensions.All, gs.Difficulty), i => gs.Difficulty = DifficultyExtensions.All[i]);
        }
        AddChoice("P_ace of the computer players", PaceExtensions.All.Select(p => p.Label()).ToList(),
            Math.Max(0, Array.IndexOf(PaceExtensions.All, gs.Pace)), i => gs.Pace = PaceExtensions.All[i]);

        foreach (var rule in kind.Rules())
        {
            if (rule.IsToggle)
            {
                var box = new CheckBox { Content = rule.Label, IsChecked = gs.RuleBool(rule), Margin = new Thickness(0, 10, 0, 0) };
                box.Click += (_, _) => Save(() => gs.Rules[rule.Key] = box.IsChecked == true ? "true" : "false");
                OptionsPanel.Children.Add(box);
            }
            else
            {
                var current = gs.Rule(rule);
                var index = rule.Choices.ToList().FindIndex(c => c.Value == current);
                AddChoice(rule.Label, rule.Choices.Select(c => c.Label).ToList(), Math.Max(0, index),
                    i => gs.Rules[rule.Key] = rule.Choices[i].Value);
            }
        }
    }

    private void AddChoice(string label, IReadOnlyList<string> options, int selected, Action<int> set)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        AutomationPropertiesHelper.SetName(combo, label.Replace("_", ""));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) Save(() => set(combo.SelectedIndex));
        };
        OptionsPanel.Children.Add(new Label { Content = label, Target = combo, Style = (Style)FindResource("PanelLabel") });
        OptionsPanel.Children.Add(combo);
    }

    private void Save(Action change)
    {
        if (_loading) return;
        change();
        SettingsStore.Save(App.Settings);
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    private void GamesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            PlaySelected();
        }
    }

    private void GamesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GamesList.SelectedItem is not null) PlaySelected();
    }

    /// <summary>A Label's access key focuses the list itself; put focus on the selected game instead.</summary>
    private void GamesList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(e.NewFocus, GamesList)) Dispatcher.BeginInvoke(FocusGames);
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.PlayerName = NameBox.Text.Trim().Length == 0 ? "Player" : NameBox.Text.Trim();
        SettingsStore.Save(App.Settings);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlaySelected();

    private void RulesButton_Click(object sender, RoutedEventArgs e) =>
        new HelpWindow(SelectedKind) { Owner = _window }.ShowDialog();
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);
}
