using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary>
/// All tunable numbers of the scoring model (<c>weights.json</c>, PLAN §6.5).
/// Every property has a default so a partial JSON file only overrides what it names.
/// Dictionaries keyed by act use the act number (1..3); a missing act falls back to the highest defined act below it.
/// </summary>
public sealed class Weights
{
    // ---- Base (PLAN §7.1) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<Tier, double> TierValue { get; set; } = new()
    {
        [Tier.S] = 80, [Tier.A] = 65, [Tier.B] = 50, [Tier.C] = 35, [Tier.D] = 20,
    };

    /// <summary>Tier assumed for cards missing from the tier pack.</summary>
    public Tier UnknownTier { get; set; } = Tier.C;

    /// <summary>Confidence assigned to cards missing from the tier pack.</summary>
    public double UnknownConfidence { get; set; } = 0.3;

    /// <summary>Role supply multiplier for upgraded deck cards.</summary>
    public double UpgradedSupplyMultiplier { get; set; } = 1.2;

    // ---- Build / archetype ----
    public double WArch { get; set; } = 20;
    public double BuildCap { get; set; } = 25;

    /// <summary>Build cap used when a weak role (block/draw) is below <see cref="WeakThreshold"/> of its target (DECK_COMPASS rule 3).</summary>
    public double BuildCapWeak { get; set; } = 12;

    public double WeakThreshold { get; set; } = 0.5;
    public List<string> WeakRoles { get; set; } = [Roles.Block, Roles.Draw];
    public double OffBuildPenalty { get; set; } = 10;
    public double CommitThreshold { get; set; } = 0.7;

    /// <summary>Two archetypes within this commit distance are shown as a hybrid.</summary>
    public double HybridMargin { get; set; } = 0.15;

    /// <summary>Core cards first: bonus for a build's missing core card, its synergy locked until a core is owned, then essentials before support.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public CorePriorityWeights CorePriority { get; set; } = new();

    // ---- Combo ----
    public double ComboCap { get; set; } = 15;

    // ---- Need ----
    public double WNeed { get; set; } = 25;

    /// <summary>Role supply targets: character id (or "default") → act → role → target supply.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, Dictionary<int, Dictionary<string, double>>> Targets { get; set; } = new(StringComparer.Ordinal)
    {
        ["default"] = new()
        {
            [1] = new() { [Roles.Frontload] = 3.0, [Roles.Aoe] = 1.0, [Roles.Block] = 2.5, [Roles.Scaling] = 0.5, [Roles.Draw] = 1.0 },
            [2] = new() { [Roles.Frontload] = 3.5, [Roles.Aoe] = 1.5, [Roles.Block] = 3.5, [Roles.Scaling] = 1.5, [Roles.Draw] = 2.0 },
            [3] = new() { [Roles.Frontload] = 3.5, [Roles.Aoe] = 2.0, [Roles.Block] = 4.0, [Roles.Scaling] = 2.5, [Roles.Draw] = 3.0 },
        },
    };

    /// <summary>Urgency multiplier per act and role ("Jobs" framework: act 1 damage, act 2 block, later scaling/draw).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, Dictionary<string, double>> Urgency { get; set; } = new()
    {
        [1] = new() { [Roles.Frontload] = 1.4, [Roles.Aoe] = 1.0, [Roles.Block] = 0.8, [Roles.Scaling] = 0.5, [Roles.Draw] = 0.6 },
        [2] = new() { [Roles.Frontload] = 1.0, [Roles.Aoe] = 1.1, [Roles.Block] = 1.3, [Roles.Scaling] = 1.1, [Roles.Draw] = 0.9 },
        [3] = new() { [Roles.Frontload] = 0.8, [Roles.Aoe] = 1.0, [Roles.Block] = 1.2, [Roles.Scaling] = 1.2, [Roles.Draw] = 1.2 },
    };

    // ---- Dilution / duplicates ----
    public double LambdaDilution { get; set; } = 1.5;
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, int> IdealDeckSize { get; set; } = new() { [1] = 15, [2] = 20, [3] = 25 };
    public double RhoDuplicate { get; set; } = 15;

    // ---- DECK_COMPASS lessons ----
    /// <summary>Penalty when a card's <c>requires</c> axis is missing from the deck ("엔진 없음").</summary>
    public double RequiresPenalty { get; set; } = 15;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]

    public SaturationWeights Saturation { get; set; } = new();
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PowerSaturationWeights PowerSaturation { get; set; } = new();

    // ---- Situation ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SituationWeights Situation { get; set; } = new();

    // ---- Skip (PLAN §7.2) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SkipWeights Skip { get; set; } = new();

    // ---- Confidence UX (PLAN §7.4) ----
    public double ConfidenceSureGap { get; set; } = 10;
    public double ConfidenceCloseGap { get; set; } = 5;

    // ---- Shop (PLAN §7.3) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ShopWeights Shop { get; set; } = new();

    // ---- Card generation odds (SPIKE §5, code-confirmed) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public CardOddsWeights CardOdds { get; set; } = new();

    // ---- Archetype completion / probability (PLAN §7.5) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public CompletionWeights Completion { get; set; } = new();

    // ---- Map path advisor (PLAN §7.6) ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Map.PathWeights Path { get; set; } = new();

    // ---- Event advisor (weights.json "events") ----
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Events.EventWeights Events { get; set; } = new();

    public static Weights Default => new();

    /// <summary>Looks up an act-keyed value, falling back to the closest lower act, then the lowest act.</summary>
    public static T ForAct<T>(IReadOnlyDictionary<int, T> byAct, int act, T fallback)
    {
        if (byAct.Count == 0) return fallback;
        if (byAct.TryGetValue(act, out var v)) return v;
        var lower = byAct.Keys.Where(k => k <= act).DefaultIfEmpty(int.MinValue).Max();
        if (lower != int.MinValue) return byAct[lower];
        return byAct[byAct.Keys.Min()];
    }
}

/// <summary>Diminishing returns when a resource is over-produced (e.g. Regent stars).</summary>
public sealed class SaturationWeights
{
    /// <summary>Producers above <c>Ratio × max(1, consumers)</c> count as excess.</summary>
    public double Ratio { get; set; } = 2.0;

    /// <summary>Each excess producer removes this fraction of positive build/need/combo value.</summary>
    public double PerExcess { get; set; } = 0.15;

    /// <summary>Positive synergy never drops below this fraction.</summary>
    public double MinFactor { get; set; } = 0.3;
}

/// <summary>
/// "코어 먼저, 그다음 필수 시너지" rule (all card roles come from the archetype JSON; no card ids in code).
/// <list type="number">
/// <item><b>Synergy locked until core.</b> <c>coreFactor(A) = min(1, ownedCore / coreTarget)</c>. A non-core card's build value
/// from A is multiplied by <c>gate = floor + (1 − floor) × coreFactor</c>, <c>floor = max(noCoreFactor[role], unlock(A))</c>
/// (see <see cref="NoCoreMaxUnlock"/>; default noCoreFactor 0, so with no core gate = unlock, 0 while the core is still likely).
/// The lost value is the negative <c>core_missing</c> component.</item>
/// <item><b>No core → no commitment.</b> In the scorer a build counts as committed (off-build penalty) only when it is core-secured.</item>
/// <item><b>Core bonus.</b> A missing core card of A gets
/// <c>core_priority = (starter + (Bonus − starter) × ramp) × (1 − coreFactor) × weak</c>, where <c>starter = StarterBonus</c> up to
/// <see cref="StarterMaxAct"/> (else 0), <c>ramp = clamp(commit / LeanFull, 0, 1)</c> while A is leaning (commit ≥ <see cref="LeanMin"/>
/// and top or within <see cref="Weights.HybridMargin"/> of the top), else 0, and <c>weak = WeakFactor</c> on a weak deck. Max over
/// builds, never summed; 0 when the card's <c>requires</c> axis is missing.</item>
/// <item><b>Staged synergy after core.</b> Essential = payoff + enabler, sub = support. Once A owns a core, its support cards count only
/// <see cref="SubBeforeEssential"/> of their role weight until A owns <see cref="EssentialTarget"/> distinct essential cards
/// (per-archetype <see cref="Archetype.EssentialTarget"/> overrides it).</item>
/// </list>
/// </summary>
public sealed class CorePriorityWeights
{
    /// <summary>Largest core-priority bonus (score points) = one tier step (15), reached at commit ≥ <see cref="LeanFull"/>.</summary>
    public double Bonus { get; set; } = 15;

    /// <summary>Commit at which a build starts to count as leaning.</summary>
    public double LeanMin { get; set; } = 0.15;

    /// <summary>Commit at which the lean part reaches <c>Bonus − starter</c> (linear from 0 below).</summary>
    public double LeanFull { get; set; } = 0.4;

    /// <summary>Multiplier on the bonus when a weak role (block/draw) is short — fix weaknesses before builds (DECK_COMPASS rule 3).</summary>
    public double WeakFactor { get; set; } = 0.5;

    /// <summary>Part of the bonus every missing core card gets in the early acts, leaning or not.</summary>
    public double StarterBonus { get; set; } = 6;

    /// <summary>Last act in which <see cref="StarterBonus"/> applies.</summary>
    public int StarterMaxAct { get; set; } = 2;

    /// <summary>
    /// Share of a non-core role weight that still counts toward the build while the build owns no core card (default 0 for every role:
    /// before a core, cards are judged on their own). Rises linearly to 1 as the build reaches its <see cref="Archetype.CoreTarget"/>.
    /// A role without a key is not gated.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, double> NoCoreFactor { get; set; } = new(StringComparer.Ordinal)
    {
        [ArchetypeRoles.Payoff] = 0,
        [ArchetypeRoles.Support] = 0,
        [ArchetypeRoles.Enabler] = 0,
    };

    /// <summary>
    /// Largest share of a non-core card's synergy that counts while its build owns no core ("코어 전: 시너지 n% 반영").
    /// <c>unlock = clamp(NoCoreMaxUnlock − P, 0, NoCoreMaxUnlock)</c>, P = chance that a missing core of the build is still offered
    /// (<see cref="Archetypes.ArchetypeStatus.CoreChance"/>): 0 while P ≥ NoCoreMaxUnlock, up to NoCoreMaxUnlock when the core is unlikely to come.
    /// Owning more of the build's synergy cards never raises it.
    /// </summary>
    public double NoCoreMaxUnlock { get; set; } = 0.5;

    /// <summary>Fallback unlock per act when P is unknown (status built without the detector); capped by <see cref="NoCoreMaxUnlock"/>.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> NoCoreUnlockByAct { get; set; } = new() { [1] = 0, [2] = 0.2, [3] = 0.4 };

    /// <summary>[추정] Floors per act for the remaining-rewards estimate behind P (same rule as the build tab: half the floors give a card reward).</summary>
    public int FloorsPerAct { get; set; } = 15;

    /// <summary>[추정] Normal card rewards per remaining floor.</summary>
    public double RewardsPerFloor { get; set; } = 0.5;

    /// <summary>Acts in a run; each act not yet finished still has a boss card reward (3 rares).</summary>
    public int RunActs { get; set; } = 3;

    /// <summary>Distinct essential (payoff + enabler) cards a build needs before its support cards count fully.</summary>
    public int EssentialTarget { get; set; } = 2;

    /// <summary>Share of a support card's role weight while its build owns a core but fewer than the essential target.</summary>
    public double SubBeforeEssential { get; set; } = 0.4;

    // ---- After the core: the build's essentials beat generic cards ("빌드 필수 카드") ----

    /// <summary>
    /// Least synergy (score points) an essential card (payoff/enabler) counts for once its build owns its core target and is the deck's
    /// build (top commit or within the hybrid margin), whatever the commit:
    /// <c>secured_essential = max(0, floor − synergy)</c>, <c>floor = SecuredEssentialFloor × taper × emergency</c>.
    /// 27 = one tier step (15) + a typical role need (~6) + the dilution edge of a card that draws (~6), so an essential beats a generic card
    /// one tier higher; it stays below two tier steps (30), so a card two tiers higher still wins. 0 turns the rule off.
    /// </summary>
    public double SecuredEssentialFloor { get; set; } = 27;

    /// <summary>Owned distinct essentials above the build's essential target at which the floor starts to taper (the build is done).</summary>
    public int SecuredEssentialExtra { get; set; } = 2;

    /// <summary>Share of the floor removed per owned essential beyond <c>essentialTarget + SecuredEssentialExtra</c>.</summary>
    public double SecuredEssentialTaper { get; set; } = 0.5;

    /// <summary>
    /// Roles whose shortage is an emergency: gap ≥ <see cref="EmergencyGap"/> (supply below 30% of target by default). In an emergency
    /// (or HP below <see cref="SituationWeights.LowHpThreshold"/>) the floor is multiplied by <see cref="EmergencyFactor"/> and the weak build cap
    /// applies again, so the role need can win.
    /// </summary>
    public List<string> EmergencyRoles { get; set; } = [Roles.Block];

    /// <summary>Role gap (0..1, 1 = no supply) at and above which an <see cref="EmergencyRoles"/> role is an emergency.</summary>
    public double EmergencyGap { get; set; } = 0.7;

    /// <summary>Multiplier on the secured-essential floor in an emergency.</summary>
    public double EmergencyFactor { get; set; } = 0.5;
}

public sealed class PowerSaturationWeights
{
    /// <summary>Power cards in deck at or above which another power is penalised.</summary>
    public int Threshold { get; set; } = 6;

    public double PenaltyPerExtra { get; set; } = 4;
}

public sealed class SituationWeights
{
    public double BossRewardBuildBonus { get; set; } = 8;
    public double LowHpThreshold { get; set; } = 0.3;

    /// <summary>Extra fraction of the block need added when HP is below the threshold.</summary>
    public double LowHpBlockBoost { get; set; } = 0.5;
}

public sealed class SkipWeights
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> T0 { get; set; } = new() { [1] = 45, [2] = 52, [3] = 58 };
    public double LambdaSize { get; set; } = 1.5;
    public double LambdaCommit { get; set; } = 5;

    /// <summary>A card must beat Skip by at least this margin to be recommended.</summary>
    public double Margin { get; set; } = 0.5;

    /// <summary>Cards with confidence below this cannot beat Skip (unless they beat it by <see cref="LowConfidenceStep"/> more).</summary>
    public double LowConfidence { get; set; } = 0.5;

    /// <summary>
    /// A low-confidence card still beats Skip when it clears <c>skip + margin + LowConfidenceStep</c> and no confident card beats Skip:
    /// even rated one tier lower (one tier step = 15) it would win. Only flips a skip into a pick, never changes which card is best.
    /// </summary>
    public double LowConfidenceStep { get; set; } = 15;

    /// <summary>
    /// "초반엔 어떤 카드든 타격·수비보다 낫다": per act, how much Skip drops when the deck is basic cards
    /// (<c>skip_basics = −BasicPickBias[act] × min(1, basicShare / BasicShareFull)</c>, basicShare = <see cref="Scoring.DeckProfile.BasicShare"/>).
    /// Act 1 = 30 puts a fresh starting deck's skip at 15, below a D-tier card (20), so early act 1 skips only cards with real penalties.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, double> BasicPickBias { get; set; } = new() { [1] = 30, [2] = 10, [3] = 0 };

    /// <summary>Basic share at and above which <see cref="BasicPickBias"/> applies in full (linear below it).</summary>
    public double BasicShareFull { get; set; } = 0.5;
}

public sealed class ShopWeights
{
    // Removal price: 75 + 25 × removals, ascension ≥ 6: 100 + 50 × removals. [코드 확인] SPIKE §5.
    public int RemovalBase { get; set; } = 75;
    public int RemovalStep { get; set; } = 25;
    public int RemovalBaseHighAscension { get; set; } = 100;
    public int RemovalStepHighAscension { get; set; } = 50;
    public int HighAscension { get; set; } = 6;

    /// <summary>Score-equivalent value of removing a curse.</summary>
    public double CurseRemovalValue { get; set; } = 30;

    /// <summary>Score-equivalent value of removing a basic Strike/Defend.</summary>
    public double BasicRemovalValue { get; set; } = 14;

    /// <summary>Base card price per lower-case rarity before the random factor. [코드 확인] SPIKE §5.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> CardPrice { get; set; } = new(StringComparer.Ordinal)
    {
        ["common"] = 50, ["uncommon"] = 75, ["rare"] = 150,
    };

    /// <summary>Colorless cards cost this multiple of the base price. [코드 확인]</summary>
    public double ColorlessPriceMultiplier { get; set; } = 1.15;

    /// <summary>Lower bound of the random price factor. [코드 확인]</summary>
    public double PriceJitterMin { get; set; } = 0.95;

    /// <summary>Upper bound of the random price factor. [코드 확인]</summary>
    public double PriceJitterMax { get; set; } = 1.05;

    /// <summary>Price factor of the one card on sale. [코드 확인]</summary>
    public double SaleMultiplier { get; set; } = 0.5;

    /// <summary>Character cards on offer per lower-case card type (2 attack, 2 skill, 1 power). [코드 확인]</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> CharacterCardSlots { get; set; } = new(StringComparer.Ordinal)
    {
        [CardTypes.Attack] = 2, [CardTypes.Skill] = 2, [CardTypes.Power] = 1,
    };

    /// <summary>Colorless cards on offer per lower-case rarity (1 uncommon, 1 rare). [코드 확인]</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> ColorlessCardSlots { get; set; } = new(StringComparer.Ordinal)
    {
        ["uncommon"] = 1, ["rare"] = 1,
    };
}

public sealed class CompletionWeights
{
    /// <summary>Completion % weight of core and payoff cards (reference site "must" = 100).</summary>
    public double CoreWeight { get; set; } = 100;

    /// <summary>Completion % weight of enabler and support cards (reference site "rec" = 56).</summary>
    public double SupportWeight { get; set; } = 56;

    /// <summary>
    /// Fallback pool size per rarity for one character when no catalog is loaded. Defaults are the per-character counts of the
    /// v0.107.1 catalog in single player (every character: 20 common, 35 uncommon, 25 rare).
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> PoolSize { get; set; } = new(StringComparer.Ordinal)
    {
        ["common"] = 20, ["uncommon"] = 35, ["rare"] = 25,
    };

    public int CardsPerReward { get; set; } = 3;
}

/// <summary>
/// How the game rolls card rarity and reward upgrades (<c>Odds/CardRarityOdds.cs</c>, v0.107.1). [코드 확인] SPIKE §5.
/// Use <see cref="CardOdds"/> to evaluate them.
/// </summary>
public sealed class CardOddsWeights
{
    /// <summary>Ascension level from which the <c>*HighAscension</c> values apply (rare odds halved, smaller pity step and upgrade chance).</summary>
    public int HighAscension { get; set; } = 7;

    /// <summary>Multiplier for <see cref="SourceRarityOdds.Rare"/> at high ascension when a source has no explicit <see cref="SourceRarityOdds.RareHighAscension"/>.</summary>
    public double RareMultiplierHighAscension { get; set; } = 0.5;

    /// <summary>Normal combat card reward (also used for <see cref="Model.OfferContext.Event"/> offers).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SourceRarityOdds CardReward { get; set; } = new() { Rare = 0.03, Uncommon = 0.37, RareHighAscension = 0.0149 };

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SourceRarityOdds EliteReward { get; set; } = new() { Rare = 0.10, Uncommon = 0.40, RareHighAscension = 0.05 };

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SourceRarityOdds BossReward { get; set; } = new() { Rare = 1.0, Uncommon = 0.0, RareHighAscension = 1.0 };

    /// <summary>Merchant character cards.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SourceRarityOdds Shop { get; set; } = new() { Rare = 0.09, Uncommon = 0.37, RareHighAscension = 0.045 };

    /// <summary>Rare "pity" offset added to the rare threshold.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public RarePityWeights Pity { get; set; } = new();

    /// <summary>Chance per act that a non-rare reward card is upgraded: <c>act × value</c>. Shop cards are never upgraded.</summary>
    public double UpgradeChancePerAct { get; set; } = 0.25;

    public double UpgradeChancePerActHighAscension { get; set; } = 0.125;
}

/// <summary>Rarity odds of one card source. Common is the rest (1 − rare − uncommon).</summary>
public sealed class SourceRarityOdds
{
    public double Rare { get; set; }
    public double Uncommon { get; set; }

    /// <summary>Rare odds at high ascension; null → <see cref="Rare"/> × <see cref="CardOddsWeights.RareMultiplierHighAscension"/>.</summary>
    public double? RareHighAscension { get; set; }
}

/// <summary>
/// Rare pity offset: starts at <see cref="Start"/>, rises by <see cref="Step"/> for every non-rare card rolled, is capped at
/// <see cref="Cap"/> and resets to <see cref="Start"/> when a rare is rolled. The game rolls rare when <c>roll &lt; rare + offset</c>.
/// </summary>
public sealed class RarePityWeights
{
    public double Start { get; set; } = -0.05;
    public double Step { get; set; } = 0.01;
    public double StepHighAscension { get; set; } = 0.005;
    public double Cap { get; set; } = 0.40;
}
