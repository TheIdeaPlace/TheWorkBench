using System.Windows;
using System.Windows.Controls;
using TheCardPlace.Play;

namespace TheCardPlace.App.Views;

/// <summary>Game ▸ Settings: the player's name, how play is spoken, and Screen Reader Announcements.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Dictionary<AnnouncementCategory, CheckBox> _boxes = [];

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        NameBox.Text = settings.PlayerName;
        MoveFocusBox.IsChecked = settings.MoveFocusOnTurn;
        EveryPlayBox.IsChecked = settings.SpeakEveryPlay;
        SpeakBox.IsChecked = settings.Announcements.Enabled;
        foreach (var c in AnnouncementCategories.Configurable)
        {
            var box = new CheckBox { Content = c.Description(), IsChecked = settings.Announcements.IsOn(c) };
            _boxes[c] = box;
            CategoryPanel.Children.Add(box);
        }
        UpdateCategories();
    }

    /// <summary>The categories stay reachable when speech is off, so they can be set first; they read as unavailable.</summary>
    private void UpdateCategories()
    {
        foreach (var b in _boxes.Values) b.Opacity = SpeakBox.IsChecked == true ? 1 : 0.6;
    }

    private void SpeakBox_Click(object sender, RoutedEventArgs e) => UpdateCategories();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        _settings.PlayerName = name.Length == 0 ? "Player" : name;
        _settings.MoveFocusOnTurn = MoveFocusBox.IsChecked == true;
        _settings.SpeakEveryPlay = EveryPlayBox.IsChecked == true;
        _settings.Announcements.Enabled = SpeakBox.IsChecked == true;
        foreach (var (c, box) in _boxes) _settings.Announcements.Set(c, box.IsChecked == true);
        DialogResult = true;
    }
}
