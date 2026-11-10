using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The 추천 tab keeps its row elements from offer to offer (Views/ReusedRows.cs): a new row list must only hand the
/// existing slots their new rows, never replace a slot (that would throw its row elements away).
/// </summary>
public class ReusedRowsTests
{
    [Fact]
    public void Same_length_list_keeps_every_slot_and_hands_it_the_new_row()
    {
        var slots = new ObservableCollection<RowSlot>();
        ReusedRows.Sync(slots, ["a", "b", "c"]);
        var before = slots.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        slots.CollectionChanged += (_, e) => changes.Add(e.Action);

        ReusedRows.Sync(slots, ["x", "y", "z"]);

        Assert.Equal(before, slots); // the same slot objects (reference equality: RowSlot has no value equality)
        Assert.Equal(["x", "y", "z"], slots.Select(s => s.Item));
        Assert.Empty(changes); // no add / remove / replace / reset: the ItemsControl keeps every container
    }

    [Fact]
    public void Longer_and_shorter_lists_only_add_or_remove_at_the_end()
    {
        var slots = new ObservableCollection<RowSlot>();
        ReusedRows.Sync(slots, ["a", "b"]);
        var first = slots[0];
        var changes = new List<NotifyCollectionChangedAction>();
        slots.CollectionChanged += (_, e) => changes.Add(e.Action);

        ReusedRows.Sync(slots, ["a2", "b2", "c2", "d2"]); // reward (3 + skip) after a 1-card choice + skip
        Assert.Same(first, slots[0]);
        Assert.Equal(["a2", "b2", "c2", "d2"], slots.Select(s => s.Item));
        ReusedRows.Sync(slots, ["e"]);
        Assert.Same(first, slots[0]);
        Assert.Equal(["e"], slots.Select(s => s.Item));
        Assert.Equal([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add,
            NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove], changes);
    }

    [Fact]
    public void Empty_list_removes_every_slot()
    {
        var slots = new ObservableCollection<RowSlot>();
        ReusedRows.Sync(slots, ["a", "b"]);
        ReusedRows.Sync(slots, []);
        Assert.Empty(slots);
    }

    [Fact]
    public void A_slot_tells_its_row_element_only_when_its_row_changes()
    {
        var slot = new RowSlot { Item = "a" };
        var raised = 0;
        slot.PropertyChanged += (_, e) =>
        {
            Assert.Equal(nameof(RowSlot.Item), e.PropertyName);
            raised++;
        };
        slot.Item = "a"; // same object: the stale-marked offer keeps its rows list, nothing to redo
        slot.Item = "b";
        Assert.Equal(1, raised);
    }
}
