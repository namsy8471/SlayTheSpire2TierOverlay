using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Data;

/// <summary>The committed names-free catalog (<c>data/catalog/cards.v0.107.1.json</c>), loaded once.</summary>
internal static class RealCatalog
{
    private static readonly Lazy<CardCatalog> Lazy = new(() => DataPackLoader.LoadCatalogFile(Path));

    public static string Path => System.IO.Path.Combine(TestPaths.RepoRoot, DataPackLoader.DefaultCatalogRelativePath);

    /// <summary>Shared instance: do not mutate.</summary>
    public static CardCatalog Instance => Lazy.Value;

    public static string TestPackDir => System.IO.Path.Combine(TestPaths.RepoRoot, "tests", "Sts2Overlay.Core.Tests", "TestData", "pack-defect");

    public static string SamplePackDir => System.IO.Path.Combine(TestPaths.RepoRoot, "data", "packs", "sample");
}
