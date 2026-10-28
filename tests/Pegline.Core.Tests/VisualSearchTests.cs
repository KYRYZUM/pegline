using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Search;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

public sealed class VisualSearchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>The prepared model folder (assets/CLIP); tests that need it pass trivially on machines without it.</summary>
    private static string? Models()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "CLIP");
            if (File.Exists(Path.Combine(candidate, SearchModelConfig.Vocab))) return candidate;
        }
        return null;
    }

    [Fact]
    public void TheTokenizerMatchesClip()
    {
        if (Models() is not { } models) return;
        var tokenizer = ClipTokenizer.FromFiles(Path.Combine(models, SearchModelConfig.Vocab), Path.Combine(models, SearchModelConfig.Merges));
        // The well-known CLIP encoding of this sentence.
        Assert.Equal([49406, 320, 1125, 539, 320, 2368, 49407], tokenizer.Encode("A  photo of a CAT"));
        var tensor = tokenizer.Tensor(string.Join(' ', Enumerable.Repeat("dog", 200)));
        Assert.Equal(77, tensor.Length);
        Assert.Equal(ClipTokenizer.Start, tensor[0]);
        Assert.Equal(ClipTokenizer.End, tensor[76]);
        Assert.Equal([0L, 0L], tokenizer.Tensor("")[2..4]);
        Assert.Equal([49406L, 49407L], tokenizer.Tensor("")[..2]);
        // Turkish letters are byte-encoded, never dropped.
        Assert.True(tokenizer.Encode("kırmızı çiçek").Count > 4);
    }

    [Fact]
    public void WideImagesAreSeenWholeAndAsTilesSliversNotAtAll()
    {
        Assert.Equal([null], VisualRanking.Regions(400, 400));
        var wide = VisualRanking.Regions(1000, 400);
        Assert.Equal(4, wide.Count);
        Assert.Null(wide[0]);
        Assert.Equal(new ImageRegion(0, 0, 400, 400), wide[1]);
        Assert.Equal(new ImageRegion(600, 0, 400, 400), wide[3]);
        Assert.Equal(new ImageRegion(0, 300, 300, 300), VisualRanking.Regions(300, 600)[2]);
        Assert.Empty(VisualRanking.Regions(1000, 100));
        Assert.Empty(VisualRanking.Regions(1000, 240));
        Assert.Throws<ArgumentOutOfRangeException>(() => VisualRanking.Regions(2000, 400));
    }

    [Fact]
    public void TensorsFitThePictureOnNeutralGray()
    {
        // 200x400 opaque red: letterboxed left and right; a transparent picture blends into the gray.
        var red = new byte[200 * 400 * 4];
        for (var i = 0; i < red.Length; i += 4) (red[i], red[i + 1], red[i + 2], red[i + 3]) = (0, 0, 255, 255);
        var views = VisualRanking.Tensors(red, 200, 400);
        Assert.Equal(3, views.Count); // whole + two tiles
        const int plane = 256 * 256;
        var whole = views[0];
        Assert.Equal(3 * plane, whole.Length);
        Assert.Equal(128f / 255, whole[128 * 256 + 2], 4);            // left margin: gray
        Assert.Equal(1f, whole[128 * 256 + 128], 4);                  // centre red channel
        Assert.Equal(0f, whole[plane + 128 * 256 + 128], 4);          // centre green channel
        Assert.Equal(1f, views[1][0], 4);                             // tiles fill the square

        var clear = new byte[200 * 200 * 4];
        Assert.All(VisualRanking.Tensors(clear, 200, 200)[0], value => Assert.Equal(128f / 255, value, 4));
    }

    [Fact]
    public void MatchesKeepOnlyClearlyBetterImages()
    {
        var ids = Enumerable.Range(0, 10).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        // One strong match in a set that otherwise resembles nothing.
        var scores = ids.Select((id, i) => (id, i == 3 ? 0.31 : 0.12 + i * 0.001)).ToList();
        Assert.Equal([ids[3]], VisualRanking.Matches(scores));
        // Small sets only need the floor and closeness to the best.
        Assert.Equal([ids[1], ids[0]], VisualRanking.Matches([(ids[0], 0.24), (ids[1], 0.26), (ids[2], 0.20)]));
        Assert.Empty(VisualRanking.Matches([(ids[0], 0.15)]));
        Assert.Empty(VisualRanking.Matches([]));
    }

    [Fact]
    public void ScoresAreTheBestViewCosine()
    {
        var a = Unit(0);
        var b = Unit(1);
        Assert.Equal(1, VisualRanking.Score(a, [b, a])!.Value, 5);
        Assert.Equal(0, VisualRanking.Score(a, [b])!.Value, 5);
        Assert.Null(VisualRanking.Score(a, []));
        Assert.Null(VisualRanking.Normalized(new float[VisualRanking.Dimensions]));
        Assert.Null(VisualRanking.Normalized(new float[3]));
    }

    [Fact]
    public void TheIndexIsStampedWithItsModelAndPruned()
    {
        var key = new string('a', 64);
        var other = new string('b', 64);
        var model = Enumerable.Repeat((byte)1, 32).ToArray();
        var cache = new VectorCache(_directory, model);
        Assert.Null(cache.Load(key));
        cache.Save(key, [Unit(5), Unit(6)]);
        cache.Save(other, []);

        var reopened = new VectorCache(_directory, model);
        Assert.Equal(2, reopened.Load(key)!.Count);
        Assert.Equal(1f, reopened.Load(key)![1][6]);
        Assert.Empty(reopened.Load(other)!); // seen, nothing usable

        // Another model's vectors are not comparable.
        Assert.Null(new VectorCache(_directory, Enumerable.Repeat((byte)2, 32).ToArray()).Load(key));

        Assert.Equal(1, reopened.Prune(new HashSet<string> { key }));
        Assert.Null(new VectorCache(_directory, model).Load(other));
        Assert.Equal(key, VectorCache.KeyOf(key + ".png"));
        Assert.Null(VectorCache.KeyOf("../" + key + ".png"));
    }

    [Fact]
    public void AlteredModelFilesAreRefused()
    {
        var folder = Path.Combine(_directory, "models");
        Directory.CreateDirectory(folder);
        var files = new Dictionary<string, SearchModelConfig.PinnedFile>();
        foreach (var name in new[] { SearchModelConfig.ImageModel, SearchModelConfig.TextModel, SearchModelConfig.Vocab, SearchModelConfig.Merges, SearchModelConfig.Source })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            files[name] = new SearchModelConfig.PinnedFile { Bytes = bytes.Length, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) };
        }
        var json = System.Text.Json.JsonSerializer.Serialize(new SearchModelConfig { SchemaVersion = 1, Version = "test", Files = files });
        var config = SearchModelConfig.Parse(json);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SearchModelConfig.Source)), config.Verify(folder));

        File.WriteAllText(Path.Combine(folder, SearchModelConfig.Vocab), SearchModelConfig.Vocab.ToUpperInvariant());
        Assert.Null(config.Verify(folder));
        Assert.Null(config.Verify(Path.Combine(_directory, "missing")));
        Assert.Throws<InvalidDataException>(() => SearchModelConfig.Parse("{\"schemaVersion\":1,\"version\":\"x\",\"files\":{}}"));
    }

    [Fact]
    public void TheShippedModelConfigIsValid()
    {
        var config = SearchModelConfig.Pinned();
        Assert.Equal("mobileclip-s2-onnx-fp32-v1", config.Version);
        Assert.Equal(8, config.Files.Count);
        if (Models() is { } models) Assert.NotNull(config.Verify(models));
    }

    [Fact]
    public void VisualMatchesJoinTextMatchesWithinTheScope()
    {
        using var store = new LibraryStore(_directory);
        var cat = store.InsertImage(TestImages.Png(6, 4));
        var invoice = store.InsertImage(TestImages.Png(5, 4, shade: 9));
        var unsaved = store.InsertImage(TestImages.Png(4, 4, shade: 3));
        var note = store.InsertText("kedi maması listesi");
        store.Retain([cat.Id, invoice.Id, note.Id]);
        var visual = new FakeVisual(cat.Id, unsaved.Id);
        var model = new LibraryModel(store) { Visual = visual };
        model.Open();

        model.SetSearch("kedi");
        model.FocusContent();
        Assert.Equal("kedi", visual.LastText);
        Assert.DoesNotContain(unsaved.Id, visual.LastEligible); // not saved: outside My library
        Assert.DoesNotContain(note.Id, visual.LastEligible);    // only images are ranked
        // Best match: the literal text match first, then the visual suggestion.
        Assert.Equal([note.Id, cat.Id], model.Ids);

        model.SetSearchMode(LibrarySearchMode.Text);
        Assert.Equal([note.Id], model.Ids);
        model.SetSearchMode(LibrarySearchMode.Visual);
        Assert.Equal([cat.Id], model.Ids);
        model.SetKind(ItemKind.Text);
        Assert.Empty(model.Ids);

        var calls = visual.Calls;
        model.SetKind(null);
        model.SetSort(LibrarySort.Newest);
        Assert.Equal(calls + 1, visual.Calls); // order changes do not search again
        model.SetSearch("");
        model.FocusContent();
        Assert.Empty(model.Query.VisualIds);
    }

    private static float[] Unit(int axis)
    {
        var vector = new float[VisualRanking.Dimensions];
        vector[axis] = 1;
        return vector;
    }

    private sealed class FakeVisual(params Guid[] results) : IVisualSearch
    {
        public string LastText { get; private set; } = "";
        public IReadOnlyList<Guid> LastEligible { get; private set; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Guid>> SearchAsync(string text, IReadOnlyList<Guid> eligible, CancellationToken cancel)
        {
            Calls++;
            LastText = text;
            LastEligible = eligible;
            return Task.FromResult<IReadOnlyList<Guid>>([.. results.Where(eligible.Contains)]);
        }
    }
}
