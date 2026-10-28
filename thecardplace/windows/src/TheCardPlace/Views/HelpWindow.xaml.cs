using System.Windows;
using System.Windows.Controls;
using TheCardPlace.Play;

namespace TheCardPlace.App.Views;

/// <summary>How to play a game (its rules, from the engine), or, with no game, the keyboard.</summary>
public partial class HelpWindow : Window
{
    private readonly IReadOnlyList<(string Heading, string Body)> _sections;

    public HelpWindow(GameKind? kind)
    {
        InitializeComponent();
        _sections = kind is { } k ? [.. GameFactory.Rules(k), ("Keyboard", KeyboardHelp.Text)] : [("Keyboard", KeyboardHelp.Text)];
        Title = kind is { } g ? $"How to play {g.Title()}" : "Keyboard shortcuts";
        SectionList.ItemsSource = _sections.Select(s => s.Heading).ToList();
        SectionList.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            // With one section there is nothing to choose: start in the text.
            if (_sections.Count == 1) BodyText.Focus();
            else (SectionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        };
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedIndex < 0) return;
        BodyText.Text = _sections[SectionList.SelectedIndex].Body;
        BodyText.CaretIndex = 0;
        BodyText.ScrollToHome();
    }
}

public static class KeyboardHelp
{
    public const string Text =
        """
        Tab and Shift+Tab move between the parts of the table: the status line, the table, your hand, the controls, the scores, the hands played and the game log. Each list is one stop; the arrow keys move within it.

        In your hand
        Left and Right arrow: the previous or next card. Home and End: the first or last card.
        1 to 9, and 0 for the tenth: jump straight to that card.
        Enter or Space: play the card, or choose it for a pass, a discard or a bury. A card you cannot play says why.

        Anywhere at the table
        N: continue, or do what the first button says (deal the next hand, pass the cards).
        R: repeat the last message.
        G: go to the game log, newest first.
        H, T, L, S and the other review letters: hear your hand, the trick, the last trick, the scores and more. The Review menu lists the ones each game has.
        ?: this list.

        Everywhere
        Alt or F10: the menus.
        Alt+U status line, Alt+B table, Alt+Y your hand, Alt+S scores, Alt+P hands played, Alt+L game log.
        Ctrl+N: new game. Ctrl+W: leave the table.
        Ctrl+Comma: settings.
        Ctrl+F9: read the status bar again. Ctrl+Shift+F9: move to it; Escape comes back.
        Ctrl+Plus, Ctrl+Minus and Ctrl+0: zoom in, zoom out, reset.
        F1: how to play.
        """;
}
