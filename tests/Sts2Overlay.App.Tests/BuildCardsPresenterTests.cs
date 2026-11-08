using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.App.Tests;

/// <summary>Card tiles of an archetype in the 빌드 tab.</summary>
public class BuildCardsPresenterTests
{
    private static BuildCardVm Tile(string id, string role, bool owned) => new(id, role, null, "?", owned, null);

    private static ArchetypeStatus Status(ArchetypeCards cards, params string[] owned) => new()
    {
        Archetype = new Archetype { Id = "orbs", Cards = cards },
        OwnedCards = owned,
    };

    [Fact]
    public void Groups_follow_role_order_and_skip_empty_roles()
    {
        var st = Status(new ArchetypeCards
        {
            Support = ["CARD.S1"],
            Enabler = ["CARD.E1", "CARD.E2"],
            Core = ["CARD.C1", "CARD.C2"],
        }, "CARD.C2", "CARD.E1");

        var groups = BuildCardsPresenter.Groups(st, id => id, Tile);

        Assert.Equal(["핵심 1/2", "조력 1/2", "보조 0/1"], groups.Select(g => g.Title));
        Assert.Equal(["CARD.C1", "CARD.C2"], groups[0].Cards.Select(c => c.Name));
        Assert.Equal([false, true], groups[0].Cards.Select(c => c.Owned));
        Assert.Equal("핵심", groups[0].Cards[0].RoleText);
        Assert.Equal("카드 2/5 보유", BuildCardsPresenter.Summary(groups));
    }

    [Fact]
    public void Aliases_are_canonicalised_and_a_card_in_two_roles_is_shown_once()
    {
        // OwnedCards holds canonical ids; the archetype list may still use an old alias.
        var st = Status(new ArchetypeCards { Core = ["CARD.OLD_NAME"], Payoff = ["CARD.NEW_NAME", "CARD.P1"] }, "CARD.NEW_NAME");

        var groups = BuildCardsPresenter.Groups(st, id => id == "CARD.OLD_NAME" ? "CARD.NEW_NAME" : id, Tile);

        Assert.Equal(["핵심 1/1", "완성 0/1"], groups.Select(g => g.Title));
        Assert.True(groups[0].Cards.Single().Owned);
        Assert.Equal("CARD.P1", groups[1].Cards.Single().Name);
    }

    [Fact]
    public void Tooltip_detail_leaves_out_unknown_rarity()
    {
        Assert.Equal("핵심 · 희귀 · 티어 A", new BuildCardVm("x", "핵심", "희귀", "A", true, null).DetailText);
        Assert.Equal("보조 · 티어 ?", new BuildCardVm("x", "보조", null, "?", false, null).DetailText);
    }
}
