using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Relics;

namespace VisionBench;

/// <summary>
/// Shop relic bench: <see cref="RelicRecognizer"/> on every capture vs the run history.
///
/// <para>Ground truth: <c>map_point_history[act][i].player_stats[me].relic_choices</c> of each shop floor lists every
/// relic the merchant displayed (<c>was_picked</c> = bought). A capture is matched to its run by time
/// (<see cref="RunTruth.At"/>) and to the shop floor by the cards the card recogniser read (fallback: the floor
/// whose relics overlap most).</para>
///
/// <para>Per slot of a shop frame: correct (id on the floor's list), WRONG (id not on it), empty (nothing drawn),
/// missed (something there but not identified, with the cover evidence: tooltip / hand share of the icon square).
/// False positives: relics identified on frames the card recogniser does not call a shop, checked on ALL captures
/// with the relic reader running ungated.</para>
/// </summary>
internal static class RelicBench
{
    private sealed record ShopFloor(string Name, IReadOnlyList<string> Relics, IReadOnlyList<string> Bought, IReadOnlyList<string> Cards);

    public static int Run(string capturesDir, string savesDir, string? player, CardRecognizer cards, RelicRecognizer relics,
        Func<ScreenFrame, ScreenFrame>? transform, string outFile, Action<string> log, int limit = int.MaxValue)
    {
        var tsRegex = new Regex(@"(\d{8}_\d{6}_\d{3})");
        var files = Directory.GetFiles(capturesDir, "*.png")
            .Select(f => (Path: f, Ts: tsRegex.Match(Path.GetFileName(f)).Groups[1].Value))
            .Where(f => f.Ts.Length > 0).OrderBy(f => f.Ts, StringComparer.Ordinal).Take(limit).ToList();
        static DateTime TimeOf(string ts) => DateTime.ParseExact(ts[..15], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        List<RunTruth> runs = files.Count == 0 ? [] : RunTruth.LoadAll(Path.Combine(savesDir, "current_run.save"), TimeOf(files[0].Ts));
        var floorsByRun = runs.ToDictionary(r => r.Path, r => LoadShopFloors(r.Path, player));

        int missedOpen = 0, shopFrames = 0, shopByRelics = 0, otherFrames = 0, correct = 0, wrong = 0, empty = 0, missed = 0, fp = 0, emptyUnexplained = 0, slotOrderMismatch = 0;
        var missedCovered = new List<string>();
        var errors = new List<string>();
        var times = new List<double>();
        var scoresRight = new List<float>();
        var marginsRight = new List<float>();
        float bestRejectedScore = 0;
        var unknownDetails = new List<string>();
        bool warmed = false;
        var csv = new StringBuilder("file,kind,slot,state,relic,score,margin,shape,shapeMargin,agrees,scale,priceText,price,tip,hand,overlay,rug,verdict\n");
        foreach ((string path, string ts) in files)
        {
            string name = Path.GetFileName(path);
            ScreenFrame frame = ImageLoader.Load(path);
            if (transform != null) frame = transform(frame);
            if (!warmed)
            {
                relics.Recognize(frame);
                warmed = true;
            }
            IReadOnlyList<RecognizedRelic> got = relics.Recognize(frame, out double ms);
            times.Add(ms);
            ScreenRecognition rc = cards.Recognize(frame);
            bool isShop = rc.Kind == ScreenKind.Shop;
            RunTruth? run = RunTruth.At(runs, TimeOf(ts));
            List<ShopFloor> floors = run != null ? floorsByRun[run.Path] : [];

            void Row(RecognizedRelic r, string verdict) =>
                csv.AppendLine(FormattableString.Invariant($"{name},{rc.Kind},{r.Slot},{r.State},{r.RelicId},{r.Score:F3},{r.Margin:F3},{r.ShapeScore:F3},{r.ShapeMargin:F3},{r.ShapeAgrees},{r.NodeScale:F2},{r.PriceReading?.Text},{r.PriceReading?.Value},{r.TooltipCover:F2},{r.HandCover:F2},{r.OverlayCover:F2},{r.RugShare:F2},{verdict}"));
            if (!isShop)
            {
                // The card recogniser misses some real shop frames (a hovered curse, the merchant's hand over the
                // cards). Such a frame still counts as a shop when its relics are a shop floor's own: two or more of
                // them, or one whose price label was read and fits its rarity. Anything else accepted is a false positive.
                var acc = got.Where(r => r.Accepted).ToList();
                ShopFloor? byRelics = acc.Count == 0 ? null
                    : floors.FirstOrDefault(f => acc.All(a => f.Relics.Contains(a.RelicId!)) && (acc.Count >= 2 || acc[0].Price != null));
                if (byRelics == null)
                {
                    foreach (RecognizedRelic r in got) Row(r, r.Accepted ? "fp" : "other");
                    otherFrames++;
                    foreach (RecognizedRelic r in acc)
                    {
                        fp++;
                        errors.Add($"FALSE-POS {name} ({rc.Kind}): {r}");
                    }
                    continue;
                }
                shopByRelics++;
            }
            shopFrames++;
            // Which shop floor: by the recognised cards first (independent of the relic reader), else by relic overlap.
            var cardIds = rc.Cards.Select(c => c.CardId!).ToHashSet();
            int RelicHits(ShopFloor f) => got.Count(g => g.Accepted && f.Relics.Contains(g.RelicId!));
            ShopFloor? floor = floors.OrderByDescending(f => f.Cards.Count(cardIds.Contains)).ThenByDescending(RelicHits).FirstOrDefault(f => f.Cards.Any(cardIds.Contains))
                ?? floors.OrderByDescending(RelicHits).FirstOrDefault();
            if (floor == null)
            {
                errors.Add($"NO-TRUTH {name}: {string.Join(" | ", got)}");
                continue;
            }
            foreach (RecognizedRelic r in got)
            {
                Row(r, r.State switch
                {
                    RelicSlotState.Empty => "empty",
                    _ when r.RelicId != null && floor.Relics.Contains(r.RelicId) => r.Accepted ? "correct" : "missed-top-ok",
                    _ => r.Accepted ? "wrong" : "missed-top-bad",
                });
                switch (r.State)
                {
                    case RelicSlotState.Identified when floor.Relics.Contains(r.RelicId!):
                        correct++;
                        scoresRight.Add(r.Score);
                        marginsRight.Add(r.Margin);
                        // The save lists the displayed relics in slot order (restocks appended): a cross-check only.
                        if (r.Slot < floor.Relics.Count && floor.Relics[r.Slot] != r.RelicId) slotOrderMismatch++;
                        break;
                    case RelicSlotState.Identified:
                        wrong++;
                        errors.Add($"WRONG {name} {run!.Tag} {floor.Name}: {r} (floor: {string.Join(", ", floor.Relics)})");
                        break;
                    case RelicSlotState.Empty:
                        empty++;
                        // An empty slot needs a bought relic on this floor.
                        if (floor.Bought.Count == 0)
                        {
                            emptyUnexplained++;
                            errors.Add($"EMPTY-UNEXPLAINED {name} {floor.Name}: {r}");
                        }
                        break;
                    default:
                        missed++;
                        // Covered: a hover tooltip, the merchant's hand or a hovered card over the icon square.
                        bool covered = r.TooltipCover >= 0.1f || r.HandCover >= 0.1f || r.OverlayCover >= 0.2f;
                        if (covered) missedCovered.Add($"{name} slot {r.Slot}: tip={r.TooltipCover:F2} hand={r.HandCover:F2} overlay={r.OverlayCover:F2} rug={r.RugShare:F2}");
                        else missedOpen++;
                        bestRejectedScore = Math.Max(bestRejectedScore, r.Candidates.Count > 0 && !floor.Relics.Contains(r.Candidates[0].Id) ? r.Score : 0);
                        string cand = string.Join(", ", r.Candidates.Take(3).Select(c => $"{c.Id} {c.Score:F2}"));
                        string inFloor = r.Candidates.Count > 0 && floor.Relics.Contains(r.Candidates[0].Id) ? "top in floor" : "top NOT in floor";
                        unknownDetails.Add($"{name} slot {r.Slot}: s={r.Score:F3} m={r.Margin:F3} scale={r.NodeScale:F2} tip={r.TooltipCover:F2} hand={r.HandCover:F2} [{cand}] {inFloor}");
                        break;
                }
            }
            log($"{name,-34} shop  {ms,5:F1} ms  {string.Join(" | ", got.Select(g => g.State == RelicSlotState.Identified ? $"{g.RelicId} {g.Price?.ToString(CultureInfo.InvariantCulture) ?? "?"}" : g.State.ToString()))}");
        }

        times.Sort();
        var sb = new StringBuilder();
        sb.AppendLine($"# Relic bench {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"- captures: {files.Count} ({shopFrames} shop frames: {shopFrames - shopByRelics} by the card recogniser + {shopByRelics} by their relics matching a shop floor; {otherFrames} other){(transform != null ? ", transformed" : "")}");
        sb.AppendLine($"- relic database: {relics.Database.Count} shop-sellable relics; runs: {runs.Count}");
        sb.AppendLine();
        sb.AppendLine("| slots on shop frames | correct | WRONG | empty | missed: covered | missed: uncovered | false positives on other frames |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        sb.AppendLine($"| {correct + wrong + empty + missed} | {correct} | {wrong} | {empty} (unexplained {emptyUnexplained}) | {missed - missedOpen} | {missedOpen} | {fp} |");
        sb.AppendLine();
        if (scoresRight.Count > 0)
            sb.AppendLine($"Correct: score min {scoresRight.Min():F3}, margin min {marginsRight.Min():F3}; best score of a rejected slot whose top relic was NOT on the floor: {bestRejectedScore:F3}");
        sb.AppendLine($"Slot order differs from relic_choices order: {slotOrderMismatch}");
        sb.AppendLine($"Relic reader time per frame: median {times[times.Count / 2]:F2} ms, p95 {times[times.Count * 95 / 100]:F2} ms, max {times[^1]:F2} ms");
        sb.AppendLine();
        sb.AppendLine("## Errors");
        foreach (string e in errors) sb.AppendLine("- " + e);
        sb.AppendLine();
        sb.AppendLine("## Covered slots (evidence: share of the icon square in tooltip slate / merchant hand / overlay (dark / card body) / bare rug)");
        foreach (string m in missedCovered) sb.AppendLine("- " + m);
        sb.AppendLine();
        sb.AppendLine("## Not identified (missed), all");
        foreach (string u in unknownDetails) sb.AppendLine("- " + u);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, sb.ToString());
        File.WriteAllText(Path.ChangeExtension(outFile, ".csv"), csv.ToString());
        log(sb.ToString()[..Math.Min(sb.Length, 2500)]);
        log($"report: {outFile}");
        return wrong + fp + missedOpen + emptyUnexplained;
    }

    /// <summary>Shop floors of one run: displayed relics (in save order), bought ones, and the offered/bought cards.</summary>
    private static List<ShopFloor> LoadShopFloors(string path, string? player)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument doc = JsonDocument.Parse(fs);
        var list = new List<ShopFloor>();
        if (!doc.RootElement.TryGetProperty("map_point_history", out JsonElement history)) return list;
        int act = 0;
        foreach (JsonElement points in history.EnumerateArray())
        {
            int idx = 0;
            foreach (JsonElement point in points.EnumerateArray())
            {
                // A "?" room can turn out to be a shop: map_point_type "unknown" with rooms[].room_type "shop".
                bool shop = (point.TryGetProperty("map_point_type", out JsonElement t) && t.GetString() == "shop")
                            || (point.TryGetProperty("rooms", out JsonElement rooms) && rooms.ValueKind == JsonValueKind.Array
                                && rooms.EnumerateArray().Any(r => r.TryGetProperty("room_type", out JsonElement rt) && rt.GetString() == "shop"));
                if (shop && point.TryGetProperty("player_stats", out JsonElement stats))
                {
                    JsonElement ps = stats.EnumerateArray().FirstOrDefault(p => player == null || (p.TryGetProperty("player_id", out JsonElement id) && id.ToString() == player));
                    if (ps.ValueKind != JsonValueKind.Object) ps = stats.EnumerateArray().FirstOrDefault();
                    var relics = new List<string>();
                    var bought = new List<string>();
                    var cards = new List<string>();
                    if (ps.ValueKind == JsonValueKind.Object)
                    {
                        if (ps.TryGetProperty("relic_choices", out JsonElement rcs))
                            foreach (JsonElement c in rcs.EnumerateArray())
                            {
                                string id = c.GetProperty("choice").GetString()!;
                                relics.Add(id);
                                if (c.TryGetProperty("was_picked", out JsonElement wp) && wp.GetBoolean()) bought.Add(id);
                            }
                        if (ps.TryGetProperty("card_choices", out JsonElement cc))
                            foreach (JsonElement c in cc.EnumerateArray()) cards.Add(c.GetProperty("card").GetProperty("id").GetString()!);
                        if (ps.TryGetProperty("cards_gained", out JsonElement cg))
                            foreach (JsonElement c in cg.EnumerateArray()) cards.Add(c.GetProperty("id").GetString()!);
                    }
                    list.Add(new ShopFloor($"act{act + 1} #{idx}", relics, bought, cards));
                }
                idx++;
            }
            act++;
        }
        return list;
    }
}
