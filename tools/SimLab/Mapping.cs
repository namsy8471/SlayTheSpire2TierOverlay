using System.Text.Json;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Rooms;

namespace SimTrial;

/// <summary>One line of fights.jsonl.</summary>
public sealed class FightRow
{
    public string run_id { get; set; } = "";
    public string character { get; set; } = "";
    public string build_id { get; set; } = "";
    public int ascension { get; set; }
    public bool win { get; set; }
    public int node_index { get; set; }
    public int act { get; set; }
    public int floor { get; set; }
    public string room_type { get; set; } = "";
    public string encounter { get; set; } = "";
    public List<string> monsters { get; set; } = new();
    public int turns { get; set; }
    public int hp_before { get; set; }
    public int max_hp_before { get; set; }
    public int damage_taken { get; set; }
    public int hp_after { get; set; }
    public bool died { get; set; }
    public List<string> deck { get; set; } = new();
    public List<string> relics { get; set; } = new();
    /// <summary>Potions the player used in this fight (from the .run file; null = unknown).</summary>
    public List<string>? potions { get; set; }
    /// <summary>Run-long relic state at this fight (see enrich_fights.py), keyed by relic id without "RELIC.".</summary>
    public Dictionary<string, int>? relic_state { get; set; }
}

public sealed record CardSpec(CardModel Canonical, int Upgrades, EnchantmentModel? Enchantment, int EnchantAmount);

public sealed record EncounterInfo(EncounterDefinition Definition, Type ActType, int ActIndex, RoomType RoomType);

/// <summary>A fight translated into simulator models.</summary>
public sealed class FightSpec
{
    public required FightRow Row { get; init; }
    public CharacterModel? Character { get; set; }
    public EncounterInfo? Encounter { get; set; }
    public List<CardSpec> Deck { get; } = new();
    public List<RelicModel> Relics { get; } = new();
    public List<PotionModel> Potions { get; } = new();
    public List<string> Unmapped { get; } = new();          // "kind:id" entries
    public bool EncounterMapped => Encounter is not null;
    public bool CharacterMapped => Character is not null;
    public bool AllCardsMapped { get; set; } = true;
    public bool AllRelicsMapped { get; set; } = true;
    public bool FullyMapped => EncounterMapped && CharacterMapped && AllCardsMapped && AllRelicsMapped;
    /// <summary>Enough to simulate: relics may be skipped, nothing else.</summary>
    public bool Simulatable => EncounterMapped && CharacterMapped && AllCardsMapped;
}

public static class Mapper
{
    private static Dictionary<string, EncounterInfo>? _encounters;

    /// <summary>Indexes every encounter of the four acts by its game id ("ENCOUNTER.&lt;IdEntry&gt;").</summary>
    public static IReadOnlyDictionary<string, EncounterInfo> Encounters
    {
        get
        {
            if (_encounters is not null) return _encounters;
            var map = new Dictionary<string, EncounterInfo>(StringComparer.Ordinal);
            foreach (ActDefinition act in new ActDefinition[] { new Overgrowth(), new Underdocks(), new Hive(), new Glory() })
            {
                void Add(IEnumerable<EncounterDefinition> defs, RoomType rt)
                {
                    foreach (EncounterDefinition e in defs)
                        map.TryAdd("ENCOUNTER." + e.IdEntry, new EncounterInfo(e, act.GetType(), act.Index, rt));
                }
                Add(act.MonsterEncounterCandidates, RoomType.Monster);
                Add(act.EliteEncounterCandidates, RoomType.Elite);
                Add(act.BossEncounterCandidates, RoomType.Boss);
            }
            _encounters = map;
            return map;
        }
    }

    public static FightSpec Map(FightRow row)
    {
        var spec = new FightSpec { Row = row };
        try { spec.Character = ModelDb.GetByIdOrNull<CharacterModel>(ModelId.Deserialize(row.character)); }
        catch (Exception) { spec.Character = null; }
        if (spec.Character is null) spec.Unmapped.Add("character:" + row.character);

        if (Encounters.TryGetValue(row.encounter, out EncounterInfo? enc)) spec.Encounter = enc;
        else spec.Unmapped.Add("encounter:" + row.encounter);

        foreach (string token in row.deck)
        {
            CardSpec? card = ParseCard(token, out string? problem);
            if (card is null)
            {
                spec.AllCardsMapped = false;
                spec.Unmapped.Add(problem!);
            }
            else spec.Deck.Add(card);
        }

        foreach (string relicId in row.relics)
        {
            RelicModel? relic = null;
            try { relic = ModelDb.GetByIdOrNull<RelicModel>(ModelId.Deserialize(relicId)); }
            catch (Exception) { }
            if (relic is null)
            {
                spec.AllRelicsMapped = false;
                spec.Unmapped.Add("relic:" + relicId);
            }
            else spec.Relics.Add(relic);
        }
        foreach (string potionId in row.potions ?? new List<string>())
        {
            PotionModel? potion = null;
            try { potion = ModelDb.GetByIdOrNull<PotionModel>(ModelId.Deserialize(potionId)); }
            catch (Exception) { }
            if (potion is null) spec.Unmapped.Add("potion:" + potionId);
            else spec.Potions.Add(potion);
        }
        return spec;
    }

    /// <summary>"CARD.X", "CARD.X+1", "CARD.X+1#ENCHANTMENT.Y:3".</summary>
    public static CardSpec? ParseCard(string token, out string? problem)
    {
        problem = null;
        string cardPart = token;
        string? enchPart = null;
        int hash = token.IndexOf('#');
        if (hash >= 0)
        {
            cardPart = token[..hash];
            enchPart = token[(hash + 1)..];
        }
        int upgrades = 0;
        int plus = cardPart.IndexOf('+');
        string id = cardPart;
        if (plus >= 0)
        {
            id = cardPart[..plus];
            if (!int.TryParse(cardPart[(plus + 1)..], out upgrades)) upgrades = 1;
        }

        CardModel? canonical = null;
        try { canonical = ModelDb.GetByIdOrNull<CardModel>(ModelId.Deserialize(id)); }
        catch (Exception) { }
        if (canonical is null)
        {
            problem = "card:" + id;
            return null;
        }
        if (upgrades > canonical.MaxUpgradeLevel)
        {
            problem = "card_upgrade:" + id + "+" + upgrades;
            return null;
        }

        EnchantmentModel? ench = null;
        int amount = 0;
        if (enchPart is not null)
        {
            string enchId = enchPart;
            int colon = enchPart.IndexOf(':');
            if (colon >= 0)
            {
                enchId = enchPart[..colon];
                if (!int.TryParse(enchPart[(colon + 1)..], out amount)) amount = 1;
            }
            else amount = 1;
            try { ench = ModelDb.GetByIdOrNull<EnchantmentModel>(ModelId.Deserialize(enchId)); }
            catch (Exception) { }
            if (ench is null)
            {
                problem = "enchantment:" + enchId;
                return null;
            }
        }
        return new CardSpec(canonical, upgrades, ench, amount);
    }

    public static IEnumerable<FightRow> ReadFights(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            FightRow? row = JsonSerializer.Deserialize<FightRow>(line);
            if (row is not null) yield return row;
        }
    }
}
