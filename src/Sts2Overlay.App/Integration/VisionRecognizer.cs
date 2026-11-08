using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Vision;
using VisionKind = Sts2Overlay.Vision.ScreenKind;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// Adapts <see cref="CardRecognizer"/> (Sts2Overlay.Vision) to the App's <see cref="ICardRecognizer"/> seam.
/// The art database is built from locally extracted card portraits (data/local/pck/card_art), so it is loaded lazily on
/// the capture worker thread: the first call pays ~0.5 s (or ~5 ms from the cache), the UI never waits.
/// </summary>
public sealed class VisionRecognizer : ICardRecognizer
{
    private readonly string? _repoRoot = AppPaths.FindRepoRoot();
    private readonly RecognitionTracker _tracker = new();
    private readonly Lock _loadLock = new();
    private volatile CardRecognizer? _recognizer;
    private bool _unavailable;

    /// <summary>
    /// Builds the art database now (call on a background thread right after the resources were prepared), so the first
    /// reward screen does not pay for it.
    /// </summary>
    public bool Preload() => (_recognizer ?? Load()) is not null;

    public RecognitionResult? Recognize(CapturedFrame frame, string? characterHint)
    {
        var rec = _recognizer ?? Load();
        if (rec is null) return null; // no local art: behave like "can't tell"

        var vf = new ScreenFrame(frame.Bgra, frame.Width, frame.Height, frame.Stride);
        var raw = rec.Recognize(vf, new RecognitionHints(characterHint));
        // The tracker merges a few frames: a tooltip or the merchant's hand covering one card for a moment
        // should not make its badge blink.
        var shown = _tracker.Update(raw, DateTime.UtcNow);

        var kind = shown.Kind switch
        {
            VisionKind.RewardCardSelect => ScreenKind.Reward,
            VisionKind.Shop => ScreenKind.Shop,
            VisionKind.CardChoice => ScreenKind.Choice,
            VisionKind.CardGrid => ScreenKind.Grid,
            _ => ScreenKind.Other,
        };
        if (kind == ScreenKind.Other) return RecognitionResult.Other;

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
        return new RecognitionResult(kind, cards)
        {
            RemovalService = removal is { Available: true, Bounds: var b }
                ? new PixelRect(b.X, b.Y, b.Width, b.Height).Offset(frame.ScreenRect.X, frame.ScreenRect.Y)
                : null,
        };
    }

    private CardRecognizer? Load()
    {
        // Preload (background) and Recognize (capture worker) may race on the very first call: build only once.
        lock (_loadLock)
        {
            return _recognizer ?? LoadLocked();
        }
    }

    private CardRecognizer? LoadLocked()
    {
        if (_unavailable) return null;
        var root = _repoRoot ?? AppContext.BaseDirectory;
        var artRoot = Path.Combine(root, "data", "local", "pck", "card_art");
        // The catalog generated from the INSTALLED game (data/local, see LocalResourceService) knows cards added by a game
        // update; the committed names-free catalog is the fallback.
        var catalog = new Sts2Overlay.Resources.LocalLayout(Path.Combine(root, "data", "local")).ManifestCatalog()
            ?? Path.Combine(root, "data", "catalog", "cards.v0.107.1.json");
        var cache = Path.Combine(root, "data", "local", "vision", "card-art.bin");
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
            _recognizer = new CardRecognizer(db, new RecognizerOptions { Catalog = File.Exists(catalog) ? VisionCatalog.Load(catalog) : null });
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
