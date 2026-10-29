using TheCardPlace.Core;

namespace TheCardPlace.Play.Tests;

public class ListOrderTests
{
    /// <summary>
    /// Random hands, new orders, cards added and the focus anywhere: the list always ends in the
    /// new order, and the focused item is never the one moved.
    /// </summary>
    [Fact]
    public void Any_order_is_reached_without_moving_the_focused_item()
    {
        var random = new RandomSource(42);
        for (var round = 0; round < 3000; round++)
        {
            var size = random.NextInt(9);
            var list = Enumerable.Range(0, size).Select(i => "c" + i).ToList();
            // The target keeps some of the list, in any order, and brings some new ones.
            var target = list.Where(_ => random.NextInt(4) > 0).Concat(Enumerable.Range(0, random.NextInt(4)).Select(i => "n" + i)).ToList();
            Shuffle(target, random);
            // As the views do, what has gone is removed first.
            list.RemoveAll(id => !target.Contains(id));
            var focused = list.Count > 0 && random.NextInt(3) > 0 ? list[random.NextInt(list.Count)] : null;

            ListOrder.InOrder(target, () => list.Count, i => list[i],
                (from, to) =>
                {
                    Assert.NotEqual(focused, list[from]);
                    var id = list[from];
                    list.RemoveAt(from);
                    list.Insert(to, id);
                },
                (i, id) => list.Insert(i, id), focused);

            Assert.Equal(target, list);
        }
    }

    [Fact]
    public void A_list_already_in_order_is_left_alone()
    {
        var list = new List<string> { "a", "b", "c" };
        var changes = 0;
        ListOrder.InOrder(["a", "b", "c"], () => list.Count, i => list[i], (_, _) => changes++, (_, _) => changes++, "b");
        Assert.Equal(0, changes);
    }

    private static void Shuffle(List<string> items, RandomSource random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
