using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Vision;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Card portraits for the panel: a small thumbnail next to each card name and a large version shown on hover.
/// The portraits are the ones extracted from the installed game into <c>data/local/pck/card_art</c> (gitignored, see
/// LocalResourceService); they are WebP, which WPF cannot decode on every Windows install, so SkiaSharp decodes them
/// (the same library the recogniser uses) and the pixels are copied into a frozen WPF bitmap.
/// Missing art (resources not prepared yet, or a card without a portrait) simply gives no image.
/// </summary>
public sealed class CardArtProvider
{
    /// <summary>Thumbnail size in pixels (portraits are 4:3, 1000x760 in the game files).</summary>
    public const int ThumbWidth = 96, ThumbHeight = 73;

    /// <summary>Hover size in pixels; the tooltip shows it at 380x289 DIPs.</summary>
    public const int LargeWidth = 600, LargeHeight = 456;

    private readonly string _artRoot;
    private readonly string? _catalogPath;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _files;
    private readonly ConcurrentDictionary<(string Id, bool Large), ImageSource?> _cache = new();

    public CardArtProvider(string artRoot, string? catalogPath)
    {
        _artRoot = artRoot;
        _catalogPath = catalogPath;
        _files = new Lazy<IReadOnlyDictionary<string, string>>(Index);
    }

    /// <summary>Uses the current data/local layout (the catalog the manifest names, else the committed one).</summary>
    public static CardArtProvider ForLocalData()
    {
        var root = AppPaths.FindRepoRoot() ?? AppContext.BaseDirectory;
        var local = Path.Combine(root, "data", "local");
        var catalog = new Sts2Overlay.Resources.LocalLayout(local).ManifestCatalog()
            ?? Path.Combine(root, "data", "catalog", "cards.v0.107.1.json");
        return new CardArtProvider(Path.Combine(local, "pck", "card_art"), File.Exists(catalog) ? catalog : null);
    }

    /// <summary>Lazy pair for one card; the large image is decoded only when the tooltip asks for it.</summary>
    public CardArtVm? For(string cardId) => PathOf(cardId) is null ? null : new CardArtVm(this, cardId);

    public ImageSource? Thumb(string cardId) => Get(cardId, large: false);

    public ImageSource? Large(string cardId) => Get(cardId, large: true);

    private string? PathOf(string cardId) => _files.Value.GetValueOrDefault(cardId);

    private ImageSource? Get(string cardId, bool large)
        => _cache.GetOrAdd((cardId, large), key =>
        {
            var file = PathOf(key.Id);
            if (file is null) return null;
            try
            {
                return Decode(file, key.Large ? LargeWidth : ThumbWidth, key.Large ? LargeHeight : ThumbHeight);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn($"card art: could not read {file} ({ex.Message})");
                return null;
            }
        });

    private IReadOnlyDictionary<string, string> Index()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_artRoot)) return map;
        try
        {
            var catalog = _catalogPath is null ? null : VisionCatalog.Load(_catalogPath);
            // First entry per card = the art the game shows (main, else beta); see CardArtSource.
            foreach (var art in CardArtSource.Enumerate(_artRoot, catalog))
                map.TryAdd(art.CardId, Path.Combine(_artRoot, art.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Warn($"card art: index failed ({ex.Message})");
        }
        AppLog.Info($"card art: {map.Count} portraits");
        return map;
    }

    /// <summary>WebP → resized BGRA → frozen <see cref="BitmapSource"/> (frozen = usable from any thread).</summary>
    internal static BitmapSource Decode(string file, int width, int height)
    {
        using var src = SKBitmap.Decode(file) ?? throw new InvalidOperationException("not an image");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var resized = src.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("resize failed");
        // Skia's Bgra8888/Premul has the same byte layout as WPF's Pbgra32, so the row bytes copy over unchanged.
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null,
            resized.GetPixels(), resized.RowBytes * height, resized.RowBytes);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>
/// One card's portrait for a panel row. <see cref="Thumb"/> is decoded when the row is drawn, <see cref="Large"/>
/// only when the hover tooltip opens (WPF builds tooltip content lazily), so a 30-card deck costs 30 small decodes.
/// </summary>
public sealed class CardArtVm(CardArtProvider provider, string cardId)
{
    public ImageSource? Thumb => provider.Thumb(cardId);
    public ImageSource? Large => provider.Large(cardId);
}
