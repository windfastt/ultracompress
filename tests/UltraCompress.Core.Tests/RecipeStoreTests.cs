using UltraCompress.Core.Recipes;

namespace UltraCompress.Core.Tests;

public sealed class RecipeStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRecipeStore _store;

    public RecipeStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"uc-recipes-{Guid.NewGuid():N}.db");
        _store = new SqliteRecipeStore(_dbPath);
        _store.EnsureInitialized();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

  private static FileFingerprint CreateFingerprint(
        string path,
        string? hash = null,
        long size = 128,
        DateTimeOffset? mtime = null) =>
        new()
        {
            Path = path,
            SizeBytes = size,
            LastModifiedUtc = mtime ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            ContentHashSha256 = hash,
        };

    private static PipelineRecipe CreateRecipe(string id) =>
        new()
        {
            Id = id,
            Profile = CompressionProfile.Tiny,
            Steps = [new CompressStep { Codec = "zstd", Level = 19 }],
        };

    [Fact]
    public void PinRecipe_ExtensionBinding_ResolvesBeforeDefaults()
    {
        var recipe = CreateRecipe("pdf-insane");
        _store.PinRecipe(RecipeBindingKind.Extension, ".pdf", recipe);

        var fp = CreateFingerprint("/work/doc.PDF");
        Assert.True(_store.TryResolveRecipe(fp, out var resolved));
        Assert.Equal("pdf-insane", resolved!.Id);
    }

    [Fact]
    public void PathBinding_WinsOverExtension()
    {
        var appLog = Path.Combine(Path.GetTempPath(), "uc-path-binding", "app.log");
        _store.PinRecipe(RecipeBindingKind.Extension, ".log", CreateRecipe("ext-log"));
        _store.PinRecipe(RecipeBindingKind.Path, appLog, CreateRecipe("path-specific"));

        var fp = CreateFingerprint(appLog);
        Assert.True(_store.TryResolveRecipe(fp, out var resolved));
        Assert.Equal("path-specific", resolved!.Id);
    }

    [Fact]
    public void ContentHashPinned_WinsOverPath()
    {
        const string hash = "abc123";
        var binPath = Path.Combine(Path.GetTempPath(), "uc-hash-vs-path", "file.bin");
        _store.PinRecipe(RecipeBindingKind.Path, binPath, CreateRecipe("by-path"));
        _store.PinRecipe(RecipeBindingKind.ContentHash, hash, CreateRecipe("by-hash"));

        var fp = CreateFingerprint(Path.Combine(Path.GetTempPath(), "other.bin"), hash: hash);
        Assert.True(_store.TryResolveRecipe(fp, out var resolved));
        Assert.Equal("by-hash", resolved!.Id);
    }

    [Fact]
    public void WinningCache_InvalidatesWhenMtimeChanges()
    {
        const string hash = "deadbeef";
        var mtime = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var fp = CreateFingerprint("/tmp/x.dat", hash: hash, mtime: mtime);
        _store.SaveWinningCache(fp, CreateRecipe("cached"));

        fp = fp with { LastModifiedUtc = mtime.AddMinutes(5) };
        Assert.False(_store.TryResolveRecipe(fp, out _));
    }

    [Fact]
    public void WinningCache_InvalidatesWhenSizeChanges()
    {
        const string hash = "cafebabe";
        var fp = CreateFingerprint("/tmp/x.dat", hash: hash, size: 100);
        _store.SaveWinningCache(fp, CreateRecipe("cached"));

        fp = fp with { SizeBytes = 101 };
        Assert.False(_store.TryResolveRecipe(fp, out _));
    }

    [Fact]
    public void PathGlob_LongestPatternWins()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uc-globs"));
        var logsGlob = Path.Combine(root, "logs", "*");
        var criticalGlob = Path.Combine(root, "logs", "critical", "*");
        var targetFile = Path.Combine(root, "logs", "critical", "2026", "evt.log");

        _store.PinRecipe(RecipeBindingKind.PathGlob, logsGlob, CreateRecipe("logs-generic"));
        _store.PinRecipe(RecipeBindingKind.PathGlob, criticalGlob, CreateRecipe("logs-critical"));

        var fp = CreateFingerprint(targetFile);
        Assert.True(_store.TryResolveRecipe(fp, out var resolved));
        Assert.Equal("logs-critical", resolved!.Id);
    }

    [Fact]
    public void RemoveBinding_StopsResolution()
    {
        _store.PinRecipe(RecipeBindingKind.Extension, ".json", CreateRecipe("json-v1"));
        Assert.True(_store.RemoveBinding(RecipeBindingKind.Extension, ".json"));
        var fp = CreateFingerprint("/a/b.json");
        Assert.False(_store.TryResolveRecipe(fp, out _));
    }

    [Fact]
    public void RoundTrip_RetainsCompressSteps()
    {
        var recipe = CreateRecipe("roundtrip");
        recipe.Steps.Insert(0, new PreprocessStep { Name = "noop" });
        _store.PinRecipe(RecipeBindingKind.Extension, ".uc", recipe);

        var fp = CreateFingerprint("/tmp/sample.uc");
        Assert.True(_store.TryResolveRecipe(fp, out var resolved));
        Assert.Equal(2, resolved!.Steps.Count);
        Assert.IsType<PreprocessStep>(resolved.Steps[0]);
        Assert.IsType<CompressStep>(resolved.Steps[1]);
    }

    [Fact]
    public void ListBindings_FiltersByKind()
    {
        _store.PinRecipe(RecipeBindingKind.Extension, ".txt", CreateRecipe("t1"));
        _store.PinRecipe(RecipeBindingKind.Extension, ".bin", CreateRecipe("t2"));
        _store.PinRecipe(RecipeBindingKind.Path, "/x", CreateRecipe("p1"));

        var extensions = _store.ListBindings(RecipeBindingKind.Extension);
        Assert.Equal(2, extensions.Count);
        Assert.All(extensions, b => Assert.Equal(RecipeBindingKind.Extension, b.Kind));
    }
}