using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.Tools.BadgeRenderBench;

/// <summary>
/// Why the 추천 tab's rows cost what they cost: the real PanelWindow content laid out OFFSCREEN (no HWND, nothing shown,
/// like <see cref="PanelBench"/>), with the offer row template swapped for variants that each leave one part out. The
/// variants are cut from PanelWindow.xaml's own text (embedded) and parsed at run time, so one build tries them all,
/// interleaved in random order in every round (the PC may be busy: compare variants, not absolute times).
/// Per sample: bind (setting the offer), measure, arrange; plus "remeasure" = the same rows measured again with no new
/// elements (what layout alone costs), and the number of visuals the rows make.
/// </summary>
internal static class PanelProfile
{
    private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

    public static int Run(string artDir, int rounds)
    {
        var app = new BenchApp(() => Task.FromResult(0)) { Resources = Program.AppResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var vm = new PanelViewModel { SelectedTab = PanelTab.Offer, IsCollapsed = false };
        // Never shown: no HWND, so the panel never saves its position into the user's settings.
        var panel = new PanelWindow(vm, new AppSettings(), new OverlayCaptureVisibility(false));
        var root = (FrameworkElement)panel.Content;
        Layout(root);
        // The loose XAML below looks its StaticResources up in the application's resources: give it the panel's too.
        var panelResources = new ResourceDictionary();
        foreach (var k in panel.Resources.Keys) panelResources[k] = panel.Resources[k];
        app.Resources.MergedDictionaries.Add(panelResources);

        var compiled = (DataTemplate)panel.Resources["OfferRowTemplate"];
        // The rows' ItemsControl: named OfferRows once the rows were kept between offers (Views/ReusedRows.cs), a plain
        // ItemsControl with the row template before.
        var host = panel.FindName("OfferRows") as ItemsControl
                   ?? Descendants(root).OfType<ItemsControl>().First(c => ReferenceEquals(c.ItemTemplate, compiled));
        var reused = typeof(PanelWindow).Assembly.GetType("Sts2Overlay.App.Views.ReusedRows");
        var reusedSource = (DependencyProperty?)reused?.GetField("SourceProperty")!.GetValue(null);
        var reusedTemplate = (DependencyProperty?)reused?.GetField("TemplateProperty")!.GetValue(null);
        var variants = Variants(compiled);
        var ids = Directory.Exists(artDir)
            ? Directory.EnumerateFiles(artDir, "*.webp", SearchOption.AllDirectories)
                .Where(f => !Path.GetDirectoryName(f)!.EndsWith("beta", StringComparison.Ordinal))
                .Select(f => "CARD." + Path.GetFileNameWithoutExtension(f).ToUpperInvariant()).Distinct().ToArray()
            : [];
        // One provider for the whole run, warmed up: portraits are decoded off the UI thread anyway (IsAsync), so the
        // decode is left out here; what is timed is the rows' elements, bindings and layout.
        var provider = new CardArtProvider(artDir, null);
        foreach (var id in ids.Take(40)) provider.Thumb(id);

        // "reuse": the row containers are kept and only told about their new row (a holder per row whose Row changes),
        // instead of the ItemsControl making a new container tree for every row of every new offer.
        var holders = new System.Collections.ObjectModel.ObservableCollection<RowHolder>();
        var holderTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate" + Namespaces + "><ContentPresenter Content=\"{Binding Row}\" ContentTemplate=\"{StaticResource OfferRowTemplate}\" /></DataTemplate>");
        variants["reuse"] = holderTemplate;
        // "app": what the panel itself does now (the rows kept by Views/ReusedRows.cs), when this build has it.
        if (reused is not null) variants["app"] = compiled;

        // Each sample sets the host up from scratch for its variant (outside the timing).
        void Configure(string variant)
        {
            if (reusedSource is not null) host.ClearValue(reusedSource);
            if (reusedTemplate is not null) host.ClearValue(reusedTemplate);
            BindingOperations.ClearBinding(host, ItemsControl.ItemsSourceProperty);
            host.ClearValue(ItemsControl.ItemsSourceProperty);
            host.ClearValue(ItemsControl.ItemTemplateProperty);
            switch (variant)
            {
                case "app":
                    host.SetValue(reusedTemplate!, compiled);
                    BindingOperations.SetBinding(host, reusedSource!, new Binding(nameof(OfferSectionVm.Rows)));
                    break;
                case "reuse":
                    holders.Clear();
                    host.ItemsSource = holders;
                    host.ItemTemplate = holderTemplate;
                    break;
                default:
                    BindingOperations.SetBinding(host, ItemsControl.ItemsSourceProperty, new Binding(nameof(OfferSectionVm.Rows)));
                    host.ItemTemplate = variants[variant];
                    break;
            }
        }

        var kinds = new[] { "reward", "shop" };
        var keys = variants.Keys.SelectMany(v => kinds.Select(k => (Variant: v, Kind: k))).ToArray();
        var bind = keys.ToDictionary(k => k, _ => new List<double>());
        var measure = keys.ToDictionary(k => k, _ => new List<double>());
        var arrange = keys.ToDictionary(k => k, _ => new List<double>());
        var total = keys.ToDictionary(k => k, _ => new List<double>());
        var remeasure = keys.ToDictionary(k => k, _ => new List<double>());
        var visuals = keys.ToDictionary(k => k, _ => 0);
        var rng = new Random(11);
        var started = Stopwatch.StartNew();
        var next = 0;
        for (var r = 0; r < rounds; r++)
        {
            foreach (var key in keys.OrderBy(_ => rng.Next()))
            {
                var reuse = key.Variant == "reuse";
                Configure(key.Variant);
                var n = key.Kind == "shop" ? 7 : 3;
                var previous = Offer(n, provider, ids, ref next); // the previous screen's offer (stale in the app)
                vm.Offer = previous;
                if (reuse) Sync(holders, previous.Rows);
                Layout(root);
                var offer = Offer(n, provider, ids, ref next);
                var t0 = Stopwatch.GetTimestamp();
                vm.Offer = offer;
                if (reuse) Sync(holders, offer.Rows);
                var t1 = Stopwatch.GetTimestamp();
                root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var t2 = Stopwatch.GetTimestamp();
                root.Arrange(new Rect(root.DesiredSize));
                root.UpdateLayout();
                var t3 = Stopwatch.GetTimestamp();
                bind[key].Add(Ms(t0, t1));
                measure[key].Add(Ms(t1, t2));
                arrange[key].Add(Ms(t2, t3));
                total[key].Add(Ms(t0, t3));

                // The same rows again, every element told to measure anew: layout and text only, nothing created.
                foreach (var e in Descendants(host).OfType<UIElement>()) e.InvalidateMeasure();
                var t4 = Stopwatch.GetTimestamp();
                Layout(root);
                remeasure[key].Add(Ms(t4, Stopwatch.GetTimestamp()));
                visuals[key] = Descendants(host).Count();
            }
            var done = r + 1;
            var eta = DateTime.Now.AddSeconds(started.Elapsed.TotalSeconds / done * (rounds - done));
            Console.WriteLine($"  {done}/{rounds} rounds ({100.0 * done / rounds:0}%), eta {eta:HH:mm:ss}");
        }

        Console.WriteLine();
        Console.WriteLine("panel 추천 tab, new offer -> layout done, offscreen (ms, medians; portraits pre-decoded):");
        // root.Measure returns at once (the change is queued deeper down); UpdateLayout does the work, so "layout" = arrange + update.
        Console.WriteLine($"  {"variant",-16} {"kind",-6} {"total",7} {"(p25",6} {"min)",6} {"bind",6} {"layout",7} {"remeasure",10} {"visuals",8}");
        foreach (var key in keys)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"  {key.Variant,-16} {key.Kind,-6} {Stats.Median(total[key]),7:0.0} {Stats.Pct(total[key], 0.25),6:0.0} {total[key].Min(),6:0.0} {Stats.Median(bind[key]),6:0.0} {Stats.Median(measure[key].Zip(arrange[key], (a, b) => a + b)),7:0.0} {Stats.Median(remeasure[key]),10:0.0} {visuals[key],8}"));
        }
        return 0;
    }

    private static void Sync(System.Collections.ObjectModel.ObservableCollection<RowHolder> holders, IReadOnlyList<OfferRowVm> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < holders.Count) holders[i].Row = rows[i];
            else holders.Add(new RowHolder { Row = rows[i] });
        }
        while (holders.Count > rows.Count) holders.RemoveAt(holders.Count - 1);
    }

    private static OfferSectionVm Offer(int n, CardArtProvider provider, string[] ids, ref int next)
    {
        var rows = new OfferRowVm[n];
        for (var i = 0; i < n; i++)
        {
            rows[i] = new OfferRowVm
            {
                Name = $"Card {next}",
                TierText = "SABCD"[next % 5].ToString(),
                ScoreText = (40 + next % 50).ToString(System.Globalization.CultureInfo.InvariantCulture),
                BarWidth = 30 + next % 60,
                IsBest = i == 0,
                SubText = $"{i + 1}위 · 데이터 신뢰도 {50 + next % 40}%",
                Chips = [new ChipVm("광역 부족 +24", true), new ChipVm("덱 크기 -6", false)],
                Art = ids.Length > 0 ? provider.For(ids[next % Math.Min(40, ids.Length)]) : null,
                PriceText = n == 7 ? (50 + next % 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            };
            next++;
        }
        return new OfferSectionVm { Title = "카드 보상", ConfidenceText = "보통", Rows = rows, Footer = $"추천: Card {next - n}" };
    }

    /// <summary>The compiled template, the same text parsed, and parsed copies that each leave one part out.</summary>
    private static Dictionary<string, DataTemplate> Variants(DataTemplate compiled)
    {
        var text = RowTemplateText();
        var v = new Dictionary<string, DataTemplate> { ["compiled"] = compiled, ["parsed"] = Parse(text) };
        void Add(string name, string changed)
        {
            if (changed == text) throw new InvalidOperationException($"variant {name}: the template text did not change");
            v[name] = Parse(changed);
        }
        Add("no-chips", Regex.Replace(text, @"<ItemsControl Grid\.Row=""2""[^>]*/>", ""));
        Add("no-thumb", Regex.Replace(text, @"<ContentControl Content=""\{Binding Art\}""[^>]*/>", ""));
        Add("thumb-no-tip", text.Replace("ContentTemplate=\"{StaticResource CardThumbTemplate}\"", "ContentTemplate=\"{StaticResource BenchThumbNoTip}\"", StringComparison.Ordinal));
        Add("no-relics", Regex.Replace(text, @"<ItemsControl ItemsSource=""\{Binding Relics\}"".*?</ItemsControl>", "", RegexOptions.Singleline));
        Add("no-tags", Regex.Replace(text, @"<Border Background=""(\{StaticResource EstimateBg\}|#1F58D68D)"".*?</Border>", "", RegexOptions.Singleline));
        Add("no-bar", Regex.Replace(text, @"<Border Width=""70"".*?</Border>", "", RegexOptions.Singleline));
        Add("no-triggers", Regex.Replace(text, @"<DataTemplate\.Triggers>.*?</DataTemplate\.Triggers>\s*(?=</DataTemplate>\s*$)", "", RegexOptions.Singleline));
        Add("no-subtext", Regex.Replace(text, @"<TextBlock Grid\.Row=""1""[^>]*/>", ""));
        return v;
    }

    private const string Namespaces =
        " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
        " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"" +
        " xmlns:vm=\"clr-namespace:Sts2Overlay.App.ViewModels;assembly=Sts2Overlay\"" +
        " xmlns:svc=\"clr-namespace:Sts2Overlay.App.Services;assembly=Sts2Overlay\"" +
        " xmlns:views=\"clr-namespace:Sts2Overlay.App.Views;assembly=Sts2Overlay\"";

    private static DataTemplate Parse(string template)
    {
        // A thumbnail template without the hover tooltip, for "thumb-no-tip" (otherwise CardThumbTemplate as written).
        if (!Application.Current.Resources.Contains("BenchThumbNoTip"))
        {
            var thumb = Block(EmbeddedXaml(), "CardThumbTemplate");
            thumb = Regex.Replace(thumb, @"<Border\.ToolTip>.*?</Border\.ToolTip>", "", RegexOptions.Singleline);
            Application.Current.Resources["BenchThumbNoTip"] = (DataTemplate)XamlReader.Parse(WithNamespaces(thumb));
        }
        return (DataTemplate)XamlReader.Parse(WithNamespaces(template));
    }

    private static string WithNamespaces(string block)
    {
        var withoutKey = Regex.Replace(block, @"\s+x:Key=""[^""]+""", "");
        return withoutKey.Insert("<DataTemplate".Length, Namespaces);
    }

    private static string RowTemplateText() => Block(EmbeddedXaml(), "OfferRowTemplate");

    private static string EmbeddedXaml()
    {
        using var stream = typeof(PanelProfile).Assembly.GetManifestResourceStream("PanelWindow.xaml")!;
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>The <c>&lt;DataTemplate x:Key="key"&gt;</c> element, nested templates included.</summary>
    private static string Block(string xaml, string key)
    {
        var start = xaml.IndexOf($"<DataTemplate x:Key=\"{key}\"", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{key} not found in PanelWindow.xaml");
        var depth = 0;
        for (var i = start; i < xaml.Length; i++)
        {
            if (string.CompareOrdinal(xaml, i, "<DataTemplate", 0, 13) == 0 && (xaml[i + 13] is ' ' or '>')) depth++;
            else if (string.CompareOrdinal(xaml, i, "</DataTemplate>", 0, 15) == 0 && --depth == 0) return xaml[start..(i + 15)];
        }
        throw new InvalidOperationException($"{key}: no closing tag");
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            var n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (var i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                yield return c;
                stack.Push(c);
            }
        }
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
    }
}

/// <summary>A kept row container's item: the row it shows now (the "reuse" variant).</summary>
public sealed class RowHolder : System.ComponentModel.INotifyPropertyChanged
{
    private OfferRowVm? _row;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public OfferRowVm? Row
    {
        get => _row;
        set
        {
            if (ReferenceEquals(_row, value)) return;
            _row = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Row)));
        }
    }
}
