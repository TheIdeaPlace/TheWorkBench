namespace TheCardPlace.Play;

/// <summary>
/// Puts a list on screen into a new order in place, without ever moving the item that has focus.
/// Moving an item takes its element out of the window for a moment, and if that one has focus,
/// focus falls to the window and JAWS reads the whole window. So when the focused item is out of
/// place, the items in front of it go to the back instead, and later passes put them right.
/// </summary>
public static class ListOrder
{
    /// <summary>
    /// Brings the list to <paramref name="target"/>'s order. Anything not in the target must
    /// already be gone; anything in it but not in the list is inserted.
    /// </summary>
    /// <param name="move">Moves an item from one index to another, as ObservableCollection.Move does.</param>
    /// <param name="focused">The id of the item with focus, if any.</param>
    public static void InOrder(IReadOnlyList<string> target, Func<int> count, Func<int, string> idAt,
        Action<int, int> move, Action<int, string> insert, string? focused)
    {
        for (var i = 0; i < target.Count; i++)
        {
            // Everything before i is already in place.
            var at = -1;
            for (var j = i; j < count(); j++)
            {
                if (idAt(j) == target[i]) { at = j; break; }
            }
            if (at < 0) insert(i, target[i]);
            else if (at != i && target[i] == focused)
            {
                for (; at > i; at--) move(i, count() - 1);
            }
            else if (at != i) move(at, i);
        }
    }
}
