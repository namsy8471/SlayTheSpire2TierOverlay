using System.Globalization;
using System.Text;
using System.Text.Json;
using Sts2Overlay.Vision;

namespace VisionBench;

/// <summary>
/// Hand-labelled shop prices (data/local/price/labels.json): one entry per shop visit with the 7 slot prices.
/// Captures whose timestamp falls in [from, to] show those prices (sold slots simply have no card).
/// </summary>
sealed class PriceLabels
{
    public sealed record Visit(int Index, string From, string To, IReadOnlyList<int> Prices);

    public IReadOnlyList<Visit> Visits { get; }

    private PriceLabels(IReadOnlyList<Visit> visits) => Visits = visits;

    public static PriceLabels Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new PriceLabels([]);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var visits = new List<Visit>();
        foreach (JsonElement v in doc.RootElement.GetProperty("visits").EnumerateArray())
            visits.Add(new Visit(visits.Count, v.GetProperty("from").GetString()!, v.GetProperty("to").GetString()!,
                v.GetProperty("prices").EnumerateArray().Select(p => p.GetInt32()).ToList()));
        return new PriceLabels(visits);
    }

    /// <summary>The labelled visit a capture timestamp (yyyyMMdd_HHmmss_fff) belongs to.</summary>
    public Visit? At(string timestamp) =>
        Visits.FirstOrDefault(v => string.CompareOrdinal(timestamp, v.From) >= 0 && string.CompareOrdinal(timestamp, v.To) <= 0);
}

/// <summary>Price accuracy over the bench (per recognised shop card whose visit is labelled).</summary>
sealed class PriceScore
{
    public int Correct, Wrong, Rejected, Missing;
    public int RejectedButRight; // digits were right but the reading was not trusted
    public readonly List<string> Errors = [];
    public readonly Dictionary<PriceTint, int> Tints = [];

    public int Total => Correct + Wrong + Rejected + Missing;

    public void Add(string capture, RecognizedCard card, int truth)
    {
        PriceReading? r = card.PriceReading;
        if (r == null)
        {
            Missing++;
            Errors.Add($"{capture} #{card.Slot} {card.CardId}: no digits found (truth {truth})");
            return;
        }
        Tints[r.Tint] = Tints.GetValueOrDefault(r.Tint) + 1;
        if (r.Value == truth) Correct++;
        else if (r.Value == null)
        {
            Rejected++;
            if (r.Text == truth.ToString(CultureInfo.InvariantCulture)) RejectedButRight++;
            Errors.Add($"{capture} #{card.Slot} {card.CardId}: rejected {r} (truth {truth})");
        }
        else
        {
            Wrong++;
            Errors.Add($"{capture} #{card.Slot} {card.CardId}: WRONG {r} (truth {truth})");
        }
    }

    public string Report(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine("| scored cards | correct | wrong | rejected (null) | no digits found |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine($"| {Total} | {Correct} ({Pct(Correct)}) | {Wrong} | {Rejected} (digits right: {RejectedButRight}) | {Missing} |");
        sb.AppendLine();
        sb.AppendLine($"Label colours seen: {string.Join(", ", Tints.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}");
        sb.AppendLine();
        foreach (string e in Errors) sb.AppendLine($"- {e}");
        sb.AppendLine();
        return sb.ToString();
    }

    private string Pct(int n) => Total == 0 ? "-" : (100.0 * n / Total).ToString("F1", CultureInfo.InvariantCulture) + " %";
}
