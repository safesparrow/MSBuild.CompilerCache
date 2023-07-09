using System.Collections.Immutable;
using System.Reflection;
using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class TestOutputBuildAndCache
{
    [Test]
    public void Test()
    {
        using var dir = new DisposableDir();
        var outputsDir = dir.Dir.CreateSubdirectory("outputs");
        var foo = outputsDir.CombineAsFile("foo.txt");
        File.WriteAllText(foo.FullName, "foo");
        var items = new[]
        {
            new OutputItem("foo", foo.FullName)
        };
        var metadata = new AllCompilationMetadata(
            Metadata: new CompilationMetadata(
                Hostname: "A",
                Username: "B",
                StopTimeUtc: DateTime.Today,
                WorkingDirectory: "e:/foo"),
            LocalInputs:
            new LocalInputs(
                Files: Array.Empty<LocalFileExtract>(),
                Props: new[]{("a", "b")},
                OutputFiles: items
            )
        );
        var zipPath = LocatorAndPopulator.BuildOutputsZip(dir, items, metadata);

        var cache = new Cache(dir.Dir.CombineAsDir(".cache").FullName);

        var key = new CacheKey("a");
        cache.Set(key, metadata.LocalInputs.ToFullExtract(), zipPath);

        var count = cache.OutputVersionsCount(key);

        Assert.That(count, Is.EqualTo(1));

        var cachedZip = cache.Get(key);
        Assert.That(cachedZip, Is.Not.Null);

        var mainOutputsDir = new DirectoryInfo(outputsDir.FullName);
        outputsDir.MoveTo(dir.Dir.CombineAsDir("old_output").FullName);
        mainOutputsDir.Create();
        LocatorAndPopulator.UseCachedOutputs(cachedZip!, items, DateTime.Now);
        AssertDirsSame(outputsDir, mainOutputsDir);
    }

    public static void AssertDirsSame(DirectoryInfo a, DirectoryInfo b)
    {
        (string Name, string Hash)[] GetInfo(DirectoryInfo dir) =>
            dir
                .EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Select(x => (x.Name, Hash: MSBuild.CompilerCache.Utils.FileBytesToHashHex(x.FullName)))
                .Order()
                .ToArray();

        var aInfo = GetInfo(a);
        var bInfo = GetInfo(b);

        Assert.That(bInfo, Is.EqualTo(aInfo));
    }
}