using System.Globalization;
using System.Text;
using Sts2Overlay.Vision;

namespace VisionBench;

/// <summary>
/// Scores <see cref="CardRemovalProbe"/> against hand labels: every shop capture, at its own size and resized to other
/// resolutions (the probe must give the same answer at FHD and 4K).
///
/// <para>Labels file (kept outside the repo, it names the user's captures): one line per capture,
/// <c>shop_YYYYMMDD_hhmmss_fff.png available|used|other</c>, <c>#</c> starts a comment.
/// <c>available</c> = coin visible, no red cross, price label shown (the overlay should point at it);
/// <c>used</c> = cross drawn, or price gone right after buying; <c>other</c> = covered, dimmed, or not the shop.</para>
/// </summary>
internal static class RemovalBench
{
    public sealed record Variant(string Name, Func<ScreenFrame, ScreenFrame> Make);

    public static int Run(string capturesDir, string labelsPath, IReadOnlyList<Variant> variants, Action<string> log)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(labelsPath))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            labels[parts[0]] = parts[1];
        }

        string[] files = Directory.GetFiles(capturesDir, "shop_*.png").Order(StringComparer.Ordinal).ToArray();
        int errors = 0;
        foreach (Variant v in variants)
        {
            // Confusion counts per label, plus the closest values to each threshold (how much room is left).
            var counts = new SortedDictionary<string, (int Total, int SaidAvailable)>(StringComparer.Ordinal);
            float minGoldAvail = 1, maxRedAvail = 0, maxGoldOther = 0;
            int minIconAvail = int.MaxValue, unlabeled = 0;
            var wrong = new List<string>();
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (!labels.TryGetValue(name, out string? truth))
                {
                    unlabeled++;
                    continue;
                }
                CardRemovalReading r = CardRemovalProbe.Read(v.Make(ImageLoader.Load(f)));
                var c = counts.GetValueOrDefault(truth);
                counts[truth] = (c.Total + 1, c.SaidAvailable + (r.Available ? 1 : 0));
                bool expected = truth == "available";
                if (expected)
                {
                    minGoldAvail = Math.Min(minGoldAvail, r.FaceGold);
                    maxRedAvail = Math.Max(maxRedAvail, r.FaceRed);
                    minIconAvail = Math.Min(minIconAvail, r.PriceIcon);
                }
                else if (r.FaceRed <= CardRemovalProbe.MaxFaceRed && r.PriceIcon >= CardRemovalProbe.MinPriceIcon)
                {
                    // A negative that only the gold test rejects: that is the margin that matters for it.
                    maxGoldOther = Math.Max(maxGoldOther, r.FaceGold);
                }
                if (r.Available != expected) wrong.Add($"{name} ({truth}): {r}");
            }

            int total = counts.Values.Sum(c => c.Total);
            int correct = counts.Sum(kv => kv.Key == "available" ? kv.Value.SaidAvailable : kv.Value.Total - kv.Value.SaidAvailable);
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"[{v.Name}] {correct}/{total} correct");
            foreach ((string k, (int t, int a)) in counts) sb.Append(CultureInfo.InvariantCulture, $"; {k}: {t} ({a} said available)");
            if (unlabeled > 0) sb.Append(CultureInfo.InvariantCulture, $"; {unlabeled} unlabelled skipped");
            log(sb.ToString());
            log(string.Create(CultureInfo.InvariantCulture,
                $"  margins: available gold >= {minGoldAvail:F3} (threshold {CardRemovalProbe.MinFaceGold}), red <= {maxRedAvail:F3} (threshold {CardRemovalProbe.MaxFaceRed}), " +
                $"icon >= {minIconAvail} (threshold {CardRemovalProbe.MinPriceIcon}); negatives passing red+icon have gold <= {maxGoldOther:F3}"));
            foreach (string w in wrong) log("  WRONG " + w);
            errors += wrong.Count;
        }

        // Extra negatives: no reward-screen capture may ever show an available removal service.
        string[] rewards = Directory.GetFiles(capturesDir, "reward_*.png");
        string[] falsePositives = rewards.Where(f => CardRemovalProbe.Read(ImageLoader.Load(f)).Available).ToArray();
        log(string.Create(CultureInfo.InvariantCulture, $"[reward captures] {rewards.Length - falsePositives.Length}/{rewards.Length} correctly absent"));
        foreach (string f in falsePositives) log("  WRONG " + Path.GetFileName(f));
        return errors + falsePositives.Length;
    }
}
