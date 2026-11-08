using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// The card tiles of one archetype in the 빌드 tab: every card the pack lists for it, grouped by role
/// (핵심 → 완성 → 조력 → 보조) and marked owned or not. Kept apart from <see cref="AppController"/> so it can be tested
/// without a window.
/// </summary>
internal static class BuildCardsPresenter
{
    private static readonly (string Role, string Title)[] RoleOrder =
    [
        (ArchetypeRoles.Core, "핵심"),
        (ArchetypeRoles.Payoff, "완성"),
        (ArchetypeRoles.Enabler, "조력"),
        (ArchetypeRoles.Support, "보조"),
    ];

    public static string RoleName(string role)
        => RoleOrder.FirstOrDefault(r => r.Role == role).Title ?? role;

    /// <param name="canonical">Alias → canonical card id (<see cref="DataPack.Canonical"/>); OwnedCards holds canonical ids.</param>
    /// <param name="card">Builds the tile for (canonical id, role title, owned).</param>
    public static IReadOnlyList<BuildCardGroupVm> Groups(
        ArchetypeStatus status, Func<string, string> canonical, Func<string, string, bool, BuildCardVm> card)
    {
        var owned = new HashSet<string>(status.OwnedCards, StringComparer.Ordinal);
        // A card listed under two roles is shown once, under the first (same rule as ArchetypeDetector).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<BuildCardGroupVm>();
        foreach (var (role, title) in RoleOrder)
        {
            var cards = ListFor(status.Archetype.Cards, role)
                .Select(canonical)
                .Where(seen.Add)
                .Select(id => card(id, title, owned.Contains(id)))
                .ToArray();
            if (cards.Length == 0) continue;
            groups.Add(new BuildCardGroupVm($"{title} {cards.Count(c => c.Owned)}/{cards.Length}", cards));
        }
        return groups;
    }

    /// <summary>"카드 9/15 보유".</summary>
    public static string Summary(IReadOnlyList<BuildCardGroupVm> groups)
    {
        var all = groups.SelectMany(g => g.Cards).ToArray();
        return $"카드 {all.Count(c => c.Owned)}/{all.Length} 보유";
    }

    private static List<string> ListFor(ArchetypeCards cards, string role) => role switch
    {
        ArchetypeRoles.Core => cards.Core,
        ArchetypeRoles.Payoff => cards.Payoff,
        ArchetypeRoles.Enabler => cards.Enabler,
        _ => cards.Support,
    };
}
