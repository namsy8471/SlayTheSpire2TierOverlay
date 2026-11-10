using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Sts2Overlay.App.Views;

/// <summary>
/// Keeps an ItemsControl's row elements when its rows are replaced by a new list. The 추천 tab gets a whole new row
/// list for every offer, and an ItemsControl given a new list throws its row elements away and builds new ones from the
/// template: ~35 elements and their bindings per row, 20-70 ms for 3-7 rows on the UI thread that also draws the badges.
/// Measuring and laying the same rows out again costs a tenth of that (tools/BadgeRenderBench --panel-profile), so
/// building them is what costs.
/// <para>Here the ItemsControl's items are long-lived <see cref="RowSlot"/>s, one per row position; a new list only
/// hands each slot its new row (each row element gets a new DataContext and its bindings update) and adds or removes
/// slots for a longer or shorter list. Same template, same elements, same look.</para>
/// <code>
/// &lt;ItemsControl views:ReusedRows.Template="{StaticResource OfferRowTemplate}" views:ReusedRows.Source="{Binding Rows}" /&gt;
/// </code>
/// </summary>
public static class ReusedRows
{
    /// <summary>The rows (any list); replaces the ItemsControl's ItemsSource.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(IEnumerable), typeof(ReusedRows), new PropertyMetadata(null, OnSourceChanged));

    /// <summary>The template of one row (bound to the row itself, as an ItemTemplate would be); replaces ItemTemplate.</summary>
    public static readonly DependencyProperty TemplateProperty = DependencyProperty.RegisterAttached(
        "Template", typeof(DataTemplate), typeof(ReusedRows), new PropertyMetadata(null, OnTemplateChanged));

    private static readonly DependencyProperty SlotsProperty = DependencyProperty.RegisterAttached(
        "Slots", typeof(ObservableCollection<RowSlot>), typeof(ReusedRows), new PropertyMetadata(null));

    public static IEnumerable? GetSource(DependencyObject d) => (IEnumerable?)d.GetValue(SourceProperty);

    public static void SetSource(DependencyObject d, IEnumerable? value) => d.SetValue(SourceProperty, value);

    public static DataTemplate? GetTemplate(DependencyObject d) => (DataTemplate?)d.GetValue(TemplateProperty);

    public static void SetTemplate(DependencyObject d, DataTemplate? value) => d.SetValue(TemplateProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control) return;
        if (control.GetValue(SlotsProperty) is not ObservableCollection<RowSlot> slots)
        {
            slots = [];
            control.SetValue(SlotsProperty, slots);
        }
        if (!ReferenceEquals(control.ItemsSource, slots)) control.ItemsSource = slots;
        Sync(slots, (e.NewValue as IEnumerable)?.Cast<object>().ToList() ?? []);
    }

    private static void OnTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control) return;
        control.ItemTemplate = e.NewValue is DataTemplate row ? SlotTemplate(row) : null;
    }

    /// <summary>A slot's template: the row template, shown for the slot's current row (kept when the row changes).</summary>
    private static DataTemplate SlotTemplate(DataTemplate row)
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(RowSlot.Item)));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty, row);
        var template = new DataTemplate(typeof(RowSlot)) { VisualTree = presenter };
        template.Seal();
        return template;
    }

    /// <summary>
    /// Slot <c>i</c> shows <paramref name="items"/>[i]: existing slots get their new row, missing ones are added at the
    /// end, extra ones removed from the end. Slots are never replaced, so their row elements stay.
    /// </summary>
    internal static void Sync(IList<RowSlot> slots, IReadOnlyList<object> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < slots.Count) slots[i].Item = items[i];
            else slots.Add(new RowSlot { Item = items[i] });
        }
        while (slots.Count > items.Count) slots.RemoveAt(slots.Count - 1);
    }
}

/// <summary>One row position of a <see cref="ReusedRows"/> list: the row it shows now.</summary>
public sealed class RowSlot : INotifyPropertyChanged
{
    private object? _item;

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? Item
    {
        get => _item;
        set
        {
            if (ReferenceEquals(_item, value)) return;
            _item = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Item)));
        }
    }
}
