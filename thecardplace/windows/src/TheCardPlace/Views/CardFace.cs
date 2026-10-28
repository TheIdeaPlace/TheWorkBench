using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using TheCardPlace.Core;

namespace TheCardPlace.App.Views;

/// <summary>
/// A card as it is drawn: rank and suit in the corners, a large suit in the middle, and an
/// optional word underneath ("trump", "13"). Purely visual. The control that holds a card (a hand
/// item, a seat) carries its name for the screen reader, so this face is hidden from UI
/// Automation rather than read a second time as loose text.
/// </summary>
public sealed class CardFace : Border
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(Card?), typeof(CardFace), new PropertyMetadata(null, (d, _) => ((CardFace)d).Render()));

    public static readonly DependencyProperty IsOffProperty = DependencyProperty.Register(
        nameof(IsOff), typeof(bool), typeof(CardFace), new PropertyMetadata(false, (d, _) => ((CardFace)d).Render()));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(CardFace), new PropertyMetadata(null, (d, _) => ((CardFace)d).Render()));

    private readonly TextBlock _corner = new() { FontWeight = FontWeights.Bold, FontSize = 17, LineHeight = 17, Margin = new Thickness(6, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _pip = new() { FontSize = 36, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 0) };
    private readonly TextBlock _badge = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 2, 4), TextWrapping = TextWrapping.Wrap, MaxWidth = 44 };
    private readonly TextBlock _off = new() { Text = "✕", FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 6, 0) };

    public CardFace()
    {
        Width = 72;
        Height = 100;
        CornerRadius = new CornerRadius(7);
        BorderThickness = new Thickness(1.5);
        SetResourceReference(BorderBrushProperty, "Brush.CardEdge");
        var grid = new Grid();
        grid.Children.Add(_corner);
        grid.Children.Add(_pip);
        grid.Children.Add(_badge);
        grid.Children.Add(_off);
        Child = grid;
        Render();
    }

    public Card? Card
    {
        get => (Card?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>Drawn greyed with a cross: a card that cannot be played now.</summary>
    public bool IsOff
    {
        get => (bool)GetValue(IsOffProperty);
        set => SetValue(IsOffProperty, value);
    }

    public string? Badge
    {
        get => (string?)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    private void Render()
    {
        if (Card is not { } c)
        {
            // An empty place: an outline on the felt where a card will go.
            Background = Brushes.Transparent;
            SetResourceReference(BorderBrushProperty, "Brush.Slot");
            _corner.Text = _pip.Text = _badge.Text = "";
            _off.Visibility = Visibility.Collapsed;
            return;
        }
        SetResourceReference(BorderBrushProperty, "Brush.CardEdge");
        SetResourceReference(BackgroundProperty, IsOff ? "Brush.CardFaceOff" : "Brush.CardFace");
        var ink = c.IsRed ? "Brush.CardRed" : "Brush.CardBlack";
        foreach (var t in new[] { _corner, _pip, _badge, _off }) t.SetResourceReference(TextBlock.ForegroundProperty, ink);
        _corner.Text = c.Rank.ShortText() + "\n" + c.Suit.Symbol();
        _pip.Text = c.Suit.Symbol();
        _badge.Text = Badge ?? "";
        _off.Visibility = IsOff ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HiddenPeer(this);

    private sealed class HiddenPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
