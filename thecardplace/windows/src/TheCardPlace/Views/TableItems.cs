using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

/// <summary>
/// A row of a score table or a line of On the table: its cells for the grid, and its spoken
/// name. Updated in place, so the row a screen reader is on is never destroyed and rebuilt.
/// </summary>
public sealed class RowItem : Observable
{
    private IReadOnlyList<string> _cells = [];
    private string _name = "";

    public IReadOnlyList<string> Cells { get => _cells; private set => Set(ref _cells, value); }
    public string AccessibleName { get => _name; private set => Set(ref _name, value); }

    public void Update(IReadOnlyList<string> cells, string name)
    {
        if (!Cells.SequenceEqual(cells)) Cells = cells.ToList();
        AccessibleName = name;
    }

    public override string ToString() => AccessibleName;
}

/// <summary>
/// Lays the seats out around the table: the player at the bottom, and the others on an arc over
/// the top, in play order from the player's left round to their right (left, across, right for
/// four), the way they would sit. If chairs would overlap in a small window, every plate is
/// drawn smaller by the same amount until none do.
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
        var size = new Size(InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Width),
                            InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Height));
        var scale = 1.0;
        for (var step = 0; step < 12 && Overlaps(Layout(final, size, n, scale), scale, size); step++) scale *= 0.92;
        var centres = Layout(final, size, n, scale);
        for (var i = 0; i < n; i++)
        {
            var c = InternalChildren[i];
            c.RenderTransformOrigin = new Point(0.5, 0.5);
            c.RenderTransform = scale < 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
            var d = c.DesiredSize;
            c.Arrange(new Rect(new Point(centres[i].X - d.Width / 2, centres[i].Y - d.Height / 2), d));
        }
        return final;
    }

    /// <summary>The centre of each chair, with plates of the given size drawn at the given scale.</summary>
    private static Point[] Layout(Size area, Size plate, int n, double scale)
    {
        var w = plate.Width * scale;
        var h = plate.Height * scale;
        var cx = area.Width / 2;
        var cy = area.Height / 2;
        var rx = Math.Max(0, (area.Width - w) / 2);
        var ry = Math.Max(0, (area.Height - h) / 2);
        var points = new Point[n];
        points[0] = new Point(cx, cy + ry);
        // The others from the left (180°) over the top (270°) to the right (360°); with one
        // other player, straight across.
        for (var i = 1; i < n; i++)
        {
            var deg = n == 2 ? 270 : 180 + (i - 1) * 180.0 / (n - 2);
            var a = deg * Math.PI / 180;
            points[i] = new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
        }
        return points;
    }

    private static bool Overlaps(Point[] centres, double scale, Size plate)
    {
        var w = plate.Width * scale + 6;
        var h = plate.Height * scale + 6;
        for (var i = 0; i < centres.Length; i++)
            for (var j = i + 1; j < centres.Length; j++)
                if (Math.Abs(centres[i].X - centres[j].X) < w && Math.Abs(centres[i].Y - centres[j].Y) < h) return true;
        return false;
    }
}

/// <summary>
/// The hand, fanned. Cards sit side by side when there is room; when there is not, each covers
/// part of the one before, leaving at least its corner showing, so a full hand stays on one row
/// at any window width. Only when even that will not fit (a large zoom) does it wrap to a second
/// row. Later cards are drawn over earlier ones; the focused card is raised by its ZIndex.
/// </summary>
public sealed class FanPanel : Panel
{
    private const double Gap = 6, MinShown = 34;
    private double _step;
    private int _perRow;

    protected override Size MeasureOverride(Size available)
    {
        var n = InternalChildren.Count;
        if (n == 0) return new Size(0, 0);
        foreach (UIElement c in InternalChildren) c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Width);
        var h = InternalChildren.Cast<UIElement>().Max(c => c.DesiredSize.Height);
        var room = double.IsInfinity(available.Width) ? n * (w + Gap) : available.Width;
        _perRow = n;
        _step = w + Gap;
        if (n > 1 && n * (w + Gap) - Gap > room)
        {
            _step = (room - w) / (n - 1);
            if (_step < MinShown)
            {
                _step = MinShown;
                _perRow = Math.Max(1, (int)((room - w) / MinShown) + 1);
            }
        }
        var rows = (n + _perRow - 1) / _perRow;
        var cols = Math.Min(n, _perRow);
        return new Size(w + (cols - 1) * _step, rows * h + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            var row = i / _perRow;
            var col = i % _perRow;
            c.Arrange(new Rect(new Point(col * _step, row * (c.DesiredSize.Height + Gap)), c.DesiredSize));
        }
        return final;
    }
}
