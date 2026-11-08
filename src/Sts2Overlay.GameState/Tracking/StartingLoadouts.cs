using System.Text.Json;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>A character's run-start inventory (CharacterModel.StartingDeck / StartingRelics / StartingHp / StartingGold).</summary>
public sealed record StartingLoadout(
    string Character, int? StartingHp, int? StartingGold, int? MaxEnergy,
    IReadOnlyList<string> Deck, IReadOnlyList<string> Relics);

/// <summary>
/// Starting decks per character, from <c>data/catalog/starting-decks.v0.107.1.json</c> (generated from sts2.dll by
/// <c>tools/CatalogBuilder --starting-decks</c>, embedded into this assembly). Used to rebuild a co-op guest's
/// deck, because a guest has no save file at all (RunSaveManager.cs:79 only saves for single player / host).
/// </summary>
public sealed class StartingLoadouts
{
    /// <summary>Ascension 5 ("AscendersBane") adds this curse at floor 1 (AscensionManager.ApplyEffectsTo).</summary>
    public const string AscendersBane = "CARD.ASCENDERS_BANE";
    public const int AscendersBaneLevel = 5;

    private readonly Dictionary<string, StartingLoadout> _byCharacter;

    private StartingLoadouts(IEnumerable<StartingLoadout> loadouts) =>
        _byCharacter = loadouts.ToDictionary(l => l.Character, StringComparer.Ordinal);

    private static readonly Lazy<StartingLoadouts> s_embedded = new(() =>
    {
        using Stream s = typeof(StartingLoadouts).Assembly.GetManifestResourceStream("starting-decks.json")
                         ?? throw new InvalidOperationException("embedded starting-decks.json missing");
        return Load(s);
    });

    /// <summary>The loadouts compiled into this assembly.</summary>
    public static StartingLoadouts Embedded => s_embedded.Value;

    public static StartingLoadouts Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<FileShape>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new JsonException("empty starting-decks file");
        return new StartingLoadouts(file.Characters);
    }

    public IReadOnlyCollection<StartingLoadout> All => _byCharacter.Values;

    /// <summary>Accepts <c>CHARACTER.DEFECT</c> or <c>DEFECT</c>.</summary>
    public StartingLoadout? For(string character) =>
        _byCharacter.GetValueOrDefault(character.StartsWith("CHARACTER.", StringComparison.Ordinal) ? character : "CHARACTER." + character);

    private sealed record FileShape(List<StartingLoadout> Characters);
}
