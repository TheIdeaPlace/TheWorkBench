using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace TheCardPlace.App.Views;

/// <summary>
/// A list whose selection is only where focus is, so it doesn't tell UI Automation it has one.
/// The hand, the table, On the table and the log select the item you arrow to, and Narrator
/// said "selected" on every card because of it. Their items still have their names and their
/// "3 of 13"; only the selection pattern is gone. A card chosen for a pass says "chosen" in its
/// name, which is the state that matters.
/// </summary>
public sealed class QuietListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ListBox owner) : ListBoxAutomationPeer(owner)
    {
        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new ItemPeer(item, this);

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? null : base.GetPattern(patternInterface);
    }

    private sealed class ItemPeer(object item, SelectorAutomationPeer owner) : ListBoxItemAutomationPeer(item, owner)
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.SelectionItem ? null : base.GetPattern(patternInterface);
    }
}
