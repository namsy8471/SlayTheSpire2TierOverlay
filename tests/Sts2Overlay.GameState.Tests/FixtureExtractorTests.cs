using System.Text.RegularExpressions;
using Sts2Overlay.GameState.Diagnostics;
using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Tests;

/// <summary>FixtureExtractor keeps ids and drops localized game text in every language (synthetic logs, made-up titles).</summary>
public class FixtureExtractorTests
{
    private const string EnglishEvent =
        "[VERYDEBUG] [EventSynchronizer] Event EVENT.TEST_BREW began for player 76561190000000777 with options: " +
        "EventOption title: Drink It, Then Regret It textKey: TEST_BREW.pages.INITIAL.options.DRINK," +
        "EventOption title: Walk Away textKey: TEST_BREW.pages.INITIAL.options.LEAVE";

    private const string KoreanEvent =
        "[VERYDEBUG] [EventSynchronizer] Event EVENT.TEST_BREW began for player 76561190000000888 with options: " +
        "EventOption title: 마신다, 그리고 후회한다 textKey: TEST_BREW.pages.INITIAL.options.DRINK," +
        "EventOption title: 떠난다 textKey: TEST_BREW.pages.INITIAL.options.LEAVE";

    [Fact]
    public void Redact_keeps_the_event_and_its_option_keys_but_no_titles()
    {
        foreach (string line in new[] { EnglishEvent, KoreanEvent })
        {
            string? clean = FixtureExtractor.Redact(line);
            Assert.NotNull(clean);
            Assert.DoesNotContain("title", clean, StringComparison.Ordinal);
            Assert.DoesNotContain("Drink It", clean, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"[\uAC00-\uD7A3]"), clean);
            var before = Assert.IsType<EventStartedEvent>(LogLineParser.Parse(line));
            var after = Assert.IsType<EventStartedEvent>(LogLineParser.Parse(clean));
            Assert.Equal(before.EventId, after.EventId);
            Assert.Equal(before.PlayerId, after.PlayerId);
            Assert.Equal(["TEST_BREW.pages.INITIAL.options.DRINK", "TEST_BREW.pages.INITIAL.options.LEAVE"], after.OptionKeys);
        }
        const string ids = "[DEBUG] [EventSynchronizer] Option index 0 chosen for player 76561190000000777 in event EVENT.TEST_BREW. Choice key: TEST_BREW.pages.INITIAL.options.DRINK";
        Assert.Equal(ids, FixtureExtractor.Redact(ids));
    }

    [Fact]
    public void Extracted_fixture_has_ids_only_in_any_game_language()
    {
        string root = Path.Combine(Path.GetTempPath(), "sts2-fixture-test-" + Guid.NewGuid().ToString("N"));
        string logs = Path.Combine(root, "logs"), history = Path.Combine(root, "history"), output = Path.Combine(root, "out");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(history);
        try
        {
            File.WriteAllLines(Path.Combine(logs, "godot.log"),
            [
                "[INFO] Our player id: 76561190000000777",
                "[INFO] Embarking on a multiplayer run. Players: Player 76561190000000777, DEFECT,Player 76561190000000888, SILENT. Ascension: 0 Seed: TESTSEED01",
                EnglishEvent,
                KoreanEvent,
                "[DEBUG] [EventSynchronizer] Option index 0 chosen for player 76561190000000777 in event EVENT.TEST_BREW. Choice key: TEST_BREW.pages.INITIAL.options.DRINK",
                "[INFO] Some unparsed English line with the game's text in it",
                "[INFO] Saved run history: 1700000000.run",
            ]);
            File.WriteAllText(Path.Combine(history, "1700000000.run"), "{\"players\":[{\"id\":76561190000000777},{\"id\":76561190000000888}]}");

            var report = new StringWriter();
            Assert.True(FixtureExtractor.Extract(logs, history, "1700000000.run", output, report), report.ToString());

            string[] lines = File.ReadAllLines(Path.Combine(output, "1700000000.log"));
            string all = string.Join("\n", lines);
            Assert.DoesNotContain("title", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Walk Away", all, StringComparison.Ordinal);
            Assert.DoesNotContain("unparsed", all, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"[\uAC00-\uD7A3]"), all);
            Assert.DoesNotContain("76561190000000777", all, StringComparison.Ordinal);
            // Both event lines survive as ids (player ids anonymised in lobby order: 777 -> ...001, 888 -> ...002).
            var events = lines.Select(LogLineParser.Parse).OfType<EventStartedEvent>().ToList();
            Assert.Equal([76561190000000001UL, 76561190000000002UL], events.Select(e => e.PlayerId));
            Assert.All(events, e => Assert.Equal(2, e.OptionKeys.Count));
            Assert.Single(lines.Select(LogLineParser.Parse).OfType<EventOptionChosenEvent>());
            Assert.DoesNotContain("76561190000000888", File.ReadAllText(Path.Combine(output, "1700000000.run")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
