using System.Diagnostics;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Resources.Catalog;
using Sts2Overlay.Resources.Pck;

namespace Sts2Overlay.Resources;

/// <summary>Is a rebuild needed?</summary>
public enum PrepareNeed
{
    /// <summary>Manifest matches the installed game and every output exists.</summary>
    UpToDate,
    /// <summary>No (readable) manifest, or an output is missing.</summary>
    Missing,
    /// <summary>Manifest exists but was built from other game files or an older preparer.</summary>
    Stale,
}

/// <summary>Result of <see cref="ResourcePreparer.Check"/>. <paramref name="Reason"/> is a short English log text.</summary>
public sealed record PrepareCheck(PrepareNeed Need, string Reason, ResourceManifest? Manifest);

/// <summary>One progress report: overall 0..1 plus a short status line for the panel (current overlay language).</summary>
public sealed record PrepareProgress(double Fraction, string Text);

/// <summary>
/// Generates the local resources in-process (what the README's three manual commands did):
/// <list type="number">
/// <item>localization (kor, eng) from the pck → <c>pck/localization/&lt;lang&gt;/*.json</c></item>
/// <item>card portraits from the pck → <c>pck/card_art/&lt;pool&gt;/[beta/]&lt;id&gt;.webp</c></item>
/// <item>card catalog from sts2.dll (+ names from step 1) → <c>catalog.&lt;version&gt;.json</c></item>
/// <item><c>manifest.json</c> with the install fingerprint (last)</item>
/// </list>
/// Everything is written to a staging folder first and swapped in at the end, so a failure or cancel keeps the old
/// resources usable. A lock file stops two processes (overlay + <c>--prepare</c>) from preparing at once.
/// </summary>
public sealed class ResourcePreparer(LocalLayout layout, Func<CatalogOptions, CatalogFile>? buildCatalog = null)
{
    /// <summary>Bump when the generated layout/content changes, so existing installs rebuild once.</summary>
    /// <remarks>2: relic icons (pck/relic_icons) added for the shop relic reader.</remarks>
    public const int FormatVersion = 2;

    public const string CardArtGlob = "images/packed/card_portraits/**/*.png";
    public static readonly string[] LocalizationLanguages = ["kor", "eng"];

    private readonly Func<CatalogOptions, CatalogFile> _buildCatalog = buildCatalog ?? (o => CatalogGenerator.Build(o).Catalog);

    public LocalLayout Layout { get; } = layout;

    /// <summary>Cheap check (no extraction): compares the manifest with the game's fingerprint and looks for the outputs.</summary>
    public PrepareCheck Check(InstalledGame game)
    {
        var manifest = ResourceManifest.TryLoad(Layout.ManifestPath);
        if (manifest is null) return new PrepareCheck(PrepareNeed.Missing, "no manifest.json", null);
        if (manifest.PrepareFormat != FormatVersion)
            return new PrepareCheck(PrepareNeed.Stale, $"prepare format {manifest.PrepareFormat} -> {FormatVersion}", manifest);
        if (manifest.Fingerprint != game.Fingerprint)
            return new PrepareCheck(PrepareNeed.Stale, $"game files changed: {manifest.Fingerprint} -> {game.Fingerprint}", manifest);
        if (manifest.CatalogFile is null || !File.Exists(Path.Combine(Layout.LocalDir, manifest.CatalogFile)))
            return new PrepareCheck(PrepareNeed.Missing, "catalog file missing", manifest);
        if (!Directory.Exists(Layout.CardArtDir) || !Directory.EnumerateFileSystemEntries(Layout.CardArtDir).Any())
            return new PrepareCheck(PrepareNeed.Missing, "card art missing", manifest);
        if (!Directory.Exists(Layout.LocalizationDir))
            return new PrepareCheck(PrepareNeed.Missing, "localization missing", manifest);
        if (manifest.RelicIconFiles > 0 && !Directory.Exists(Layout.RelicIconDir))
            return new PrepareCheck(PrepareNeed.Missing, "relic icons missing", manifest);
        return new PrepareCheck(PrepareNeed.UpToDate, "up to date", manifest);
    }

    /// <summary>Runs all steps and returns the new manifest. Throws on failure (old resources stay in place).</summary>
    /// <exception cref="PrepareBusyException">Another process is preparing right now.</exception>
    public ResourceManifest Prepare(InstalledGame game, IProgress<PrepareProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(Layout.LocalDir);
        using var lockFile = AcquireLock();

        string staging = Layout.StagingDir;
        DeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        try
        {
            string stLoc = Path.Combine(staging, "localization");
            string stArt = Path.Combine(staging, "card_art");
            string versionLabel = game.VersionLabel;
            string catalogName = Path.GetFileName(Layout.CatalogPath(versionLabel));
            string stCatalog = Path.Combine(staging, catalogName);

            // Step weights for the overall bar: textures dominate (≈900 files), the catalog is one ~1-2 s step.
            const double LocEnd = 0.05, ArtEnd = 0.75, CatalogEnd = 0.97;

            string stRelics = Path.Combine(staging, "relic_icons");
            int locFiles = 0, artFiles, relicFiles = 0;
            progress?.Report(new PrepareProgress(0, Loc.T("리소스 준비: 게임 파일 여는 중", "Preparing resources: opening game files")));
            using (var pck = new PckReader(game.Install.PckPath))
            {
                foreach (var lang in LocalizationLanguages)
                {
                    locFiles += PckExtractor.ExtractFiles(pck, $"localization/{lang}/*.json", Path.Combine(stLoc, lang), ct: ct);
                }
                progress?.Report(new PrepareProgress(LocEnd, Loc.T($"리소스 준비: 현지화 {locFiles}개", $"Preparing resources: localization files {locFiles}")));

                var artProgress = new Throttled(progress, (done, total) =>
                    new PrepareProgress(LocEnd + (ArtEnd - LocEnd) * done / Math.Max(1, total), Loc.T($"리소스 준비: 카드 그림 {done}/{total}", $"Preparing resources: card art {done}/{total}")));
                var art = PckExtractor.ExtractImages(pck, CardArtGlob, stArt, artProgress, ct);
                artFiles = art.Written;
                if (artFiles == 0) throw new InvalidDataException($"no card portraits found in {game.Install.PckPath} ({CardArtGlob})");

                // Relic icons for the shop relic reader: one BC7 atlas (~0.3 s to decode). Optional: a pack without the
                // atlas (older game, test pack) still prepares; the shop then simply reads no relics.
                if (pck.Entries.Any(e => e.Path == RelicIconExtractor.AtlasImport))
                    relicFiles = RelicIconExtractor.Extract(pck, stRelics, ct);
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new PrepareProgress(ArtEnd, Loc.T("리소스 준비: 카드 목록 분석 중 (sts2.dll)", "Preparing resources: reading the card list (sts2.dll)")));
            var catalog = _buildCatalog(new CatalogOptions(game.Install.DllPath, stLoc, versionLabel));
            CatalogGenerator.Write(catalog, stCatalog);
            progress?.Report(new PrepareProgress(CatalogEnd, Loc.T($"리소스 준비: 카드 {catalog.Cards.Count}장", $"Preparing resources: cards {catalog.Cards.Count}")));

            // ---- swap in (each move is a quick rename on the same drive) ----
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Layout.PckDir);
            ReplaceDirectory(stLoc, Layout.LocalizationDir);
            ReplaceDirectory(stArt, Layout.CardArtDir);
            if (relicFiles > 0) ReplaceDirectory(stRelics, Layout.RelicIconDir);
            File.Move(stCatalog, Path.Combine(Layout.LocalDir, catalogName), overwrite: true);

            var manifest = new ResourceManifest
            {
                PrepareFormat = FormatVersion,
                GameVersion = game.Version?.Text,
                GameCommit = game.Release?.Commit,
                GameDir = game.Install.Directory,
                Fingerprint = game.Fingerprint,
                PreparedAtUtc = DateTime.UtcNow,
                DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
                CatalogFile = catalogName,
                CatalogCards = catalog.Cards.Count,
                CardArtFiles = artFiles,
                LocalizationFiles = locFiles,
                RelicIconFiles = relicFiles,
            };
            manifest.Save(Layout.ManifestPath);
            progress?.Report(new PrepareProgress(1, Loc.T($"리소스 준비 완료 ({manifest.DurationSeconds:0.0}초)", $"Resources ready ({manifest.DurationSeconds:0.0}s)")));
            return manifest;
        }
        finally
        {
            DeleteDirectory(staging);
        }
    }

    private FileStream AcquireLock()
    {
        try
        {
            // FileShare.None: a second process gets an IOException instead of preparing into the same folders.
            return new FileStream(Layout.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new PrepareBusyException(Loc.T("다른 프로세스가 리소스를 준비하는 중입니다", "Another process is preparing the resources"), ex);
        }
    }

    /// <summary>Moves <paramref name="source"/> to <paramref name="target"/>, replacing it (old copy renamed away first, then deleted).</summary>
    private static void ReplaceDirectory(string source, string target)
    {
        string? old = null;
        if (Directory.Exists(target))
        {
            old = target + ".old-" + DateTime.UtcNow.Ticks;
            Directory.Move(target, old);
        }
        Directory.Move(source, target);
        if (old is not null) DeleteDirectory(old);
    }

    /// <summary>Best-effort recursive delete of a folder WE generated (retries: a reader may hold a file for a moment).</summary>
    private static void DeleteDirectory(string dir)
    {
        for (int attempt = 0; attempt < 3 && Directory.Exists(dir); attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    /// <summary>Forwards at most one report per 1 % step, so 900 textures don't flood the UI thread with 900 updates.</summary>
    private sealed class Throttled(IProgress<PrepareProgress>? target, Func<int, int, PrepareProgress> map) : IProgress<(int Done, int Total)>
    {
        private int _lastPercent = -1;

        public void Report((int Done, int Total) value)
        {
            if (target is null) return;
            int percent = value.Total == 0 ? 100 : value.Done * 100 / value.Total;
            if (percent == _lastPercent && value.Done != value.Total) return;
            _lastPercent = percent;
            target.Report(map(value.Done, value.Total));
        }
    }
}

/// <summary>Thrown when another process holds the prepare lock.</summary>
public sealed class PrepareBusyException(string message, Exception inner) : Exception(message, inner);
