using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TheCardPlace.Core;
using TheCardPlace.Play;

namespace TheCardPlace.App.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// A card in the hand as the list holds it. Updated in place when only its state changes, so
/// the item the screen reader is on is not destroyed and rebuilt under it.
/// </summary>
public sealed class HandItem(string id) : Observable
{
    private Card? _card;
    private string _name = "";
    private bool _off, _chosen;
    private string? _badge;

    public string Id { get; } = id;
    public Card? Card { get => _card; private set => Set(ref _card, value); }
    public string AccessibleName { get => _name; private set => Set(ref _name, value); }
    public bool IsOff { get => _off; private set => Set(ref _off, value); }
    public bool Chosen { get => _chosen; private set => Set(ref _chosen, value); }
    public string? Badge { get => _badge; private set => Set(ref _badge, value); }
    public HandCard Source { get; private set; } = null!;

    public void Update(HandCard c)
    {
        Source = c;
        Card = c.Card;
        AccessibleName = c.AccessibleName;
        IsOff = !c.Playable;
        Chosen = c.Chosen;
        Badge = c.Badge;
    }
}

public sealed class SeatItem(int seat) : Observable
{
    private string _title = "", _detail = "", _name = "";
    private Card? _played;
    private string? _note;
    private bool _turn;

    public int Seat { get; } = seat;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public Card? Played { get => _played; private set => Set(ref _played, value); }
    public string? PlayedNote { get => _note; private set => Set(ref _note, value); }
    public bool IsTurn { get => _turn; private set => Set(ref _turn, value); }
    public string AccessibleName { get => _name; private set => Set(ref _name, value); }

    public void Update(SeatInfo s)
    {
        Title = s.IsMe ? s.Name + " (you)" : s.Name;
        Detail = string.Join(" · ", new[] { s.Position, s.Detail }.Where(x => x.Length > 0));
        Played = s.Played;
        PlayedNote = s.PlayedNote;
        IsTurn = s.IsTurn;
        AccessibleName = s.AccessibleName;
    }
}

/// <summary>A row of a score table, with its cells for the grid and its spoken name.</summary>
public sealed record TableRow(IReadOnlyList<string> Cells, string AccessibleName);

/// <summary>
/// Lays the seats out around the table: the player at the bottom, the others clockwise in play
/// order (left, across, right for four), the way they would sit.
/// </summary>
public sealed class TablePanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement c in InternalChildren) c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = double.IsInfinity(available.Width) ? 700 : available.Width;
        var h = double.IsInfinity(available.Height) ? 420 : available.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var n = InternalChildren.Count;
        if (n == 0) return final;
        var maxW = InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Width);
        var maxH = InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Height);
        var cx = final.Width / 2;
        var cy = final.Height / 2;
        var rx = Math.Max(0, (final.Width - maxW) / 2);
        var ry = Math.Max(0, (final.Height - maxH) / 2);
        for (var i = 0; i < n; i++)
        {
            var c = InternalChildren[i];
            var angle = (90 + i * 360.0 / n) * Math.PI / 180;
            var x = cx + rx * Math.Cos(angle) - c.DesiredSize.Width / 2;
            var y = cy + ry * Math.Sin(angle) - c.DesiredSize.Height / 2;
            c.Arrange(new Rect(new Point(x, y), c.DesiredSize));
        }
        return final;
    }
}
