using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Relics;
using Sts2Overlay.Vision.Rest;
using VisionKind = Sts2Overlay.Vision.ScreenKind;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// Adapts <see cref="CardRecognizer"/> (Sts2Overlay.Vision) to the App's <see cref="ICardRecognizer"/> seam.
/// The art database is built from locally extracted card portraits (data/local/pck/card_art), so it is loaded lazily on
/// the capture worker thread: the first call pays ~0.5 s (or ~5 ms from the cache), the UI never waits.
/// </summary>
public sealed class VisionRecognizer : ICardRecognizer
{
    private readonly RecognitionTracker _tracker = new();
    private readonly Lock _loadLock = new();
    private volatile CardRecognizer? _recognizer;
    private bool _unavailable;
    private readonly string? _localDir;  // null = AppPaths.LocalDataDir
    private readonly string? _cachePath; // null = <local>/vision/card-art.bin

    public VisionRecognizer()
    {
    }

    /// <summary>
    /// Reads another local data folder and keeps the art cache at <paramref name="cachePath"/> (tools/MemProbe: measures the
    /// app's recogniser without ever writing the shared cache).
    /// </summary>
    internal VisionRecognizer(string localDir, string cachePath)
    {
        _localDir = localDir;
        _cachePath = cachePath;
    }

    /// <summary>
    /// Builds the art database now (call on a background thread right after the resources were prepared), so the first
    /// reward screen does not pay for it.
    /// </summary>
    public bool Preload() => (_recognizer ?? Load()) is not null;

    /// <summary>The loaded Vision recogniser, or null before <see cref="Preload"/> (tools/MemProbe dumps its raw output).</summary>
    internal CardRecognizer? Loaded => _recognizer;

    public RecognitionResult? Recognize(CapturedFrame frame, string? characterHint)
    {
        var rec = _recognizer ?? Load();
        if (rec is null) return null; // no local art: behave like "can't tell"

        var vf = new ScreenFrame(frame.Bgra, frame.Width, frame.Height, frame.Stride);
        var raw = rec.Recognize(vf, new RecognitionHints(characterHint));
        // The tracker merges a few frames: a tooltip or the merchant's hand covering one card for a moment
        // should not make its badge blink.
        var shown = _tracker.Update(raw, DateTime.UtcNow);
        // Right-click inspect view over any of these screens: no badges, no rest marks over the big card and its tips.
        if (raw.Gate.InspectLikely) return RecognitionResult.Other;

        var kind = shown.Kind switch
        {
            VisionKind.RewardCardSelect => ScreenKind.Reward,
            VisionKind.Shop => ScreenKind.Shop,
            VisionKind.CardChoice => ScreenKind.Choice,
            VisionKind.CardGrid => ScreenKind.Grid,
            _ => ScreenKind.Other,
        };
        // Rest site: option buttons / Smith grid. A fixed-layout colour check, ~0.2 ms at 4K, so it runs on every frame.
        var rest = RestSiteDetector.Detect(vf);
        var restSeen = rest.ChoiceScreen || rest.CardGridOpen ? rest : null;
        if (kind == ScreenKind.Other) return restSeen is null ? RecognitionResult.Other : RecognitionResult.Other with { RestSite = restSeen };

        var cards = shown.Cards
            .Where(c => c.Accepted && c.CardId is not null)
            .Select(c => new RecognizedCard(
                new CardRef(c.CardId!, c.Upgraded ? 1 : 0),
                // Vision works in frame pixels; the badge layer wants screen pixels, so add the capture origin.
                new PixelRect(c.CardBounds.X, c.CardBounds.Y, c.CardBounds.Width, c.CardBounds.Height)
                    .Offset(frame.ScreenRect.X, frame.ScreenRect.Y),
                // A card found by the partial match keeps a low whole-art score: its confidence is the partial NCC.
                c.Partial?.Score ?? c.Score,
                c.Price,
                c.Unaffordable,
                c.PriceReading?.OnSale == true,
                c.PriceBounds is { } p ? new PixelRect(p.X, p.Y, p.Width, p.Height).Offset(frame.ScreenRect.X, frame.ScreenRect.Y) : null))
            .ToArray();
        // Shop: is the card-removal service still for sale? A fixed-place colour probe (CardRemovalProbe), ~0.1 ms.
        var removal = kind == ScreenKind.Shop ? CardRemovalProbe.Read(vf) : null;
        var relics = kind == ScreenKind.Shop
            ? shown.Relics.Where(x => x.Accepted && x.RelicId is not null)
                .Select(x => new RecognizedShopRelic(x.RelicId!, x.Rarity,
                    new PixelRect(x.IconBounds.X, x.IconBounds.Y, x.IconBounds.Width, x.IconBounds.Height).Offset(frame.ScreenRect.X, frame.ScreenRect.Y),
                    x.Score, x.Price, x.Unaffordable))
                .ToArray()
            : [];
        return new RecognitionResult(kind, cards)
        {
            Relics = relics,
            RemovalService = removal is { Available: true, Bounds: var b }
                ? new PixelRect(b.X, b.Y, b.Width, b.Height).Offset(frame.ScreenRect.X, frame.ScreenRect.Y)
                : null,
            RestSite = restSeen,
            Slots = shown.SlotCount,
            ScreenId = shown.Generation,
            Unsettled = shown.Unsettled,
        };
    }

    /// <summary>The log says the screen closed: forget the merged frames (capture worker thread, like Recognize).</summary>
    public void Reset() => _tracker.Reset();

    private CardRecognizer? Load()
    {
        // Preload (background) and Recognize (capture worker) may race on the very first call: build only once.
        lock (_loadLock)
        {
            return _recognizer ?? LoadLocked();
        }
    }

    /// <summary>
    /// The merchant relic reader: icons cut from the game's relic atlas (data/local/pck/relic_icons, prepared on start)
    /// and the committed relic catalog (ids and rarities only). Optional: without icons the shop shows cards only.
    /// </summary>
    private static RelicRecognizer? LoadRelics(string local)
    {
        var iconDir = new Sts2Overlay.Resources.LocalLayout(local).RelicIconDir;
        var catalog = Path.Combine(Path.GetDirectoryName(AppPaths.CommittedCatalogFile)!, RelicCatalogFile);
        if (!Directory.Exists(iconDir) || !File.Exists(catalog))
        {
            AppLog.Info($"[vision] shop relics off (icons {(Directory.Exists(iconDir) ? "ok" : "missing")}, catalog {(File.Exists(catalog) ? "ok" : "missing")})");
            return null;
        }
        try
        {
            var db = RelicIconDatabase.Build(iconDir, RelicCatalog.Load(catalog));
            AppLog.Info($"[vision] shop relic reader ready ({db.Count} relics)");
            return new RelicRecognizer(db);
        }
        catch (Exception ex)
        {
            AppLog.Error("relic icons failed to load; shop relics off", ex);
            return null;
        }
    }

    /// <summary>The committed relic catalog next to the card catalog (data/catalog).</summary>
    private const string RelicCatalogFile = "relics.v0.107.1.json";

    private CardRecognizer? LoadLocked()
    {
        if (_unavailable) return null;
        var local = _localDir ?? AppPaths.LocalDataDir;
        var artRoot = Path.Combine(local, "pck", "card_art");
        // The catalog generated from the INSTALLED game (data/local, see LocalResourceService) knows cards added by a game
        // update; the committed names-free catalog is the fallback.
        var catalog = new Sts2Overlay.Resources.LocalLayout(local).ManifestCatalog()
            ?? AppPaths.CommittedCatalogFile;
        var cache = _cachePath ?? Path.Combine(local, "vision", "card-art.bin");
        if (!Directory.Exists(artRoot))
        {
            _unavailable = true;
            AppLog.Error($"card art not found at {artRoot}; it is generated on start (or run with --prepare). Recognition disabled until then.");
            return null;
        }
        try
        {
            var db = CardArtDatabase.LoadOrBuild(artRoot, File.Exists(catalog) ? catalog : null, cache, msg => AppLog.Info("[vision] " + msg));
            // The catalog gives the price reader each card's rarity, which tightens its plausibility check.
            _recognizer = new CardRecognizer(db, new RecognizerOptions { Catalog = File.Exists(catalog) ? VisionCatalog.Load(catalog) : null })
            {
                Relics = LoadRelics(local),
            };
            AppLog.Info($"[vision] recognizer ready (catalog {Path.GetFileName(catalog)})");
            return _recognizer;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            AppLog.Error("card art database failed to load; recognition disabled", ex);
            return null;
        }
    }
}
