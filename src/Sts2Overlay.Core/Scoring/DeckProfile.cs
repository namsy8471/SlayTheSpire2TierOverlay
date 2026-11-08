using Sts2Overlay.Core.Archetypes;

namespace Sts2Overlay.Core.Scoring;

/// <summary>What the deck has and lacks right now. Computed once per decision by <see cref="DeckAnalyzer"/>.</summary>
public sealed record DeckProfile
{
    public int DeckSize { get; init; }

    /// <summary>Σ role strength over deck cards (upgraded cards × <c>UpgradedSupplyMultiplier</c>).</summary>
    public IReadOnlyDictionary<string, double> RoleSupply { get; init; } = new Dictionary<string, double>();

    /// <summary>Target supply for the current character and act.</summary>
    public IReadOnlyDictionary<string, double> RoleTarget { get; init; } = new Dictionary<string, double>();

    /// <summary><c>max(0, target − supply) / target</c>, 0..1, for roles that have a target.</summary>
    public IReadOnlyDictionary<string, double> RoleGap { get; init; } = new Dictionary<string, double>();

    /// <summary>How many deck cards provide each axis.</summary>
    public IReadOnlyDictionary<string, int> Provides { get; init; } = new Dictionary<string, int>();

    /// <summary>How many deck cards consume each axis.</summary>
    public IReadOnlyDictionary<string, int> Consumes { get; init; } = new Dictionary<string, int>();

    /// <summary>Copies per card id already in the deck.</summary>
    public IReadOnlyDictionary<string, int> Copies { get; init; } = new Dictionary<string, int>();

    public int PowerCount { get; init; }
    public int BasicStrikes { get; init; }
    public int BasicDefends { get; init; }
    public int Curses { get; init; }

    /// <summary>Archetype statuses, highest commit first.</summary>
    public IReadOnlyList<ArchetypeStatus> Archetypes { get; init; } = [];

    public ArchetypeStatus? Primary => Archetypes.Count > 0 ? Archetypes[0] : null;
    public double MaxCommit => Primary?.Commit ?? 0.0;

    /// <summary>Weak roles (block/draw by default) whose supply is below <c>WeakThreshold × target</c>.</summary>
    public IReadOnlyList<string> WeakRoles { get; init; } = [];

    public bool IsWeak => WeakRoles.Count > 0;
}
