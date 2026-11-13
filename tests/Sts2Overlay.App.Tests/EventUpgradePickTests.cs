using System.IO;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Vision.Events;
using PixelRect = Sts2Overlay.Vision.PixelRect;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// <see cref="EventUpgradePick"/>: the deck grid that opens after an event option such as Sapphire Seed's "Eat" is the
/// upgrade pick, so it gets the rest site's upgrade marks; other grids in an event room do not.
/// </summary>
public class EventUpgradePickTests
{
    [Theory]
    [InlineData(1, "B")]
    [InlineData(2, "A,B")]
    [InlineData(4, "B,A,A,B")]
    public void Baseline_takes_the_requested_suffix_of_the_legal_saved_cards(int count, string expected)
    {
        var catalog = EventCatalog.Parse(Json.Replace("\"count\": 1, \"how\": \"choose\"", $"\"count\": {count}, \"how\": \"choose\""));
        var pick = new EventUpgradePick(catalog);
        var pack = new Core.Data.DataPack { Catalog = new() { Cards =
            [new() { Id = "A", MaxUpgrade = 1 }, new() { Id = "B", MaxUpgrade = 1 }, new() { Id = "CURSE", MaxUpgrade = 0 }] } };
        var run = new Core.Model.RunState { Character = "CHARACTER.IRONCLAD",
            Deck = [new(new("B")), new(new("A")), new(new("A")), new(new("A", 1)), new(new("B")), new(new("CURSE"))] };
        Assert.Equal(expected, string.Join(",", pick.BaselineCards(Eat, run, pack, true).Select(c => c.Id)));
        Assert.Empty(pick.BaselineCards(Eat, run, pack, false));
        Assert.Empty(pick.BaselineCards(Eat, run with { Deck = [.. run.Deck, new(new("UNKNOWN"))] }, pack, true));
    }

    private const string Json = """
    {
      "events": [
        {
          "id": "EVENT.TEST_SEED",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_SEED.pages.INITIAL.options.EAT", "effects": [
                { "type": "heal", "amount": 9 },
                { "type": "cardUpgrade", "count": 1, "how": "choose" } ] },
              { "key": "TEST_SEED.pages.INITIAL.options.PLANT", "effects": [
                { "type": "enchant", "enchantment": "ENCHANTMENT.SOWN", "count": 1, "how": "choose" } ] },
              { "key": "TEST_SEED.pages.INITIAL.options.CHANGE", "effects": [
                { "type": "cardTransform", "count": 1, "how": "choose" } ] },
              { "key": "TEST_SEED.pages.INITIAL.options.LUCK", "effects": [
                { "type": "cardUpgrade", "count": 2, "how": "random" } ] },
              { "key": "TEST_SEED.pages.INITIAL.options.BOTH", "effects": [
                { "type": "cardUpgrade", "count": 1, "how": "choose" },
                { "type": "cardTransform", "count": 1, "how": "choose" } ] }
            ]}
          ]
        }
      ]
    }
    """;

    private const string Eat = "TEST_SEED.pages.INITIAL.options.EAT";
    private static readonly EventCatalog Catalog = EventCatalog.Parse(Json);

    private static bool Opens(string shortName) => EventUpgradePick.OpensUpgradeGrid(Catalog.FindOption($"TEST_SEED.pages.INITIAL.options.{shortName}")!.Value.Option);

    [Fact]
    public void Only_an_option_that_upgrades_a_chosen_card_opens_the_upgrade_grid()
    {
        Assert.True(Opens("EAT"));
        Assert.False(Opens("PLANT"));  // enchant grid
        Assert.False(Opens("CHANGE")); // transform grid: looks the same to the grid probe
        Assert.False(Opens("LUCK"));   // random upgrades: no grid
        Assert.False(Opens("BOTH"));   // two grids in a row: which one is up is unknown
    }

    [Fact]
    public void The_grid_after_the_click_is_the_pick_until_the_events_buttons_come_back()
    {
        var p = new EventUpgradePick(Catalog);
        Assert.False(p.Update(Eat, gridOpen: false, buttonsOnScreen: false)); // clicked, the grid is still sliding in
        Assert.True(p.Expects(Eat));
        Assert.True(p.Update(Eat, gridOpen: true, buttonsOnScreen: false));
        Assert.False(p.Update(Eat, gridOpen: false, buttonsOnScreen: false)); // a frame the probe missed (card hover tip)
        Assert.True(p.Update(Eat, gridOpen: true, buttonsOnScreen: false));   // the "before ▶ after" preview
        Assert.False(p.Update(Eat, gridOpen: false, buttonsOnScreen: true));  // picked: the result page's button
        // The deck view opened later in the same room is not an upgrade pick.
        Assert.False(p.Update(Eat, gridOpen: true, buttonsOnScreen: false));
        Assert.False(p.Expects(Eat));
    }

    [Fact]
    public void No_click_or_another_kind_of_option_never_marks_a_grid()
    {
        var p = new EventUpgradePick(Catalog);
        Assert.False(p.Update(null, gridOpen: true, buttonsOnScreen: false));
        Assert.False(p.Update("TEST_SEED.pages.INITIAL.options.CHANGE", gridOpen: true, buttonsOnScreen: false));
        Assert.False(p.Update("TEST_SEED.pages.INITIAL.options.PLANT", gridOpen: true, buttonsOnScreen: false));
        Assert.False(p.Update("NOT_IN_THE_DATA", gridOpen: true, buttonsOnScreen: false));
        Assert.False(p.Expects(null));
    }

    [Fact]
    public void A_new_room_with_the_same_option_starts_over()
    {
        var p = new EventUpgradePick(Catalog);
        p.Update(Eat, gridOpen: true, buttonsOnScreen: false);
        p.Update(Eat, gridOpen: false, buttonsOnScreen: true);
        p.Update(null, gridOpen: false, buttonsOnScreen: false); // the room closed
        Assert.True(p.Update(Eat, gridOpen: true, buttonsOnScreen: false));
    }

    // ---- with the screen tracker: the click it sees is what arms the pick ----------------------------------------

    private static EventScreen Screen(int count, int hover = -1, int text = 0)
        => new(Enumerable.Range(0, count).Select(i =>
        {
            var sig = new float[96];
            sig[(text * 7 + i * 13) % 96] = 1;
            return new EventButton(i, new PixelRect(930, 604 + 104 * i, 784, 84), i == hover ? EventButtonState.Hovered : EventButtonState.Idle, sig);
        }).ToArray());

    [Fact]
    public void The_tracker_names_the_clicked_option_and_forgets_it_in_the_next_room()
    {
        var t = new EventScreenTracker(Catalog);
        t.SetEvent("EVENT.TEST_SEED");
        t.OnScreen(Screen(5));
        Assert.Null(t.Clicked);
        t.OnScreen(Screen(5, hover: 0)); // mouse on EAT
        t.OnScreen(null);
        Assert.Null(t.Clicked);          // one frame without buttons is not a click yet
        t.OnScreen(null);
        Assert.Equal(Eat, t.Clicked);
        t.OnScreen(Screen(1, text: 3));  // the result page's button: no page to line up, the click stays known
        Assert.Equal(Eat, t.Clicked);
        t.SetEvent(null);
        Assert.Null(t.Clicked);
    }

    [Fact]
    public void An_unseen_click_names_no_option()
    {
        var t = new EventScreenTracker(Catalog);
        t.SetEvent("EVENT.TEST_SEED");
        t.OnScreen(Screen(5)); // never hovered
        t.OnScreen(null);
        t.OnScreen(null);
        Assert.True(t.Lost);
        Assert.Null(t.Clicked);
    }

    [Fact]
    public void The_co_op_log_names_the_choice_too()
    {
        var t = new EventScreenTracker(Catalog);
        t.SetEvent("EVENT.TEST_SEED");
        t.SetLog(null, [Eat]);
        Assert.Equal(Eat, t.Clicked);
    }

    // ---- the shipped packs: exactly these options open the upgrade pick ------------------------------------------

    [Theory]
    [InlineData("v0.107.1")]
    [InlineData("v0.111.0")]
    public void Real_pack_options_that_open_the_upgrade_grid(string pack)
    {
        var catalog = EventCatalog.LoadDirectory(Path.Combine(RepoRoot(), "data", "packs", pack));
        var keys = catalog.Events.SelectMany(e => e.Pages).SelectMany(p => p.Options)
            .Where(EventUpgradePick.OpensUpgradeGrid).Select(o => o.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
        [
            "AROMA_OF_CHAOS.pages.INITIAL.options.MAINTAIN_CONTROL",
            "NEOW.pages.INITIAL.options.POMANDER",
            "SAPPHIRE_SEED.pages.INITIAL.options.EAT",
            "SPIRIT_GRAFTER.pages.INITIAL.options.REJECTION",
            "TEZCATARA.pages.INITIAL.options.YUMMY_COOKIE",
            "TRIAL.pages.MERCHANT.options.INNOCENT",
        ], keys);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("repo root (SlayTheSpire2TierOverlay.sln) not found above " + AppContext.BaseDirectory);
    }
}
