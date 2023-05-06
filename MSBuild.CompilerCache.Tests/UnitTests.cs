using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class UnitTests
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
            Metadata: new PreCompilationMetadata(
                Hostname: "A",
                Username: "B",
                StartTimeUtc: DateTime.Today,
                WorkingDirectory: "e:/foo"),
            LocalInputs:
            new LocalInputs(
                Files: new LocalFileExtract[] { },
                Props: "a=b",
                OutputFiles: items
            )
        );
        var zipPath = UserOrPopulator.BuildOutputsZip(dir, items, metadata);

        var cache = new Cache(dir.Dir.CombineAsDir(".cache").FullName);

        var key = new CacheKey("a");
        cache.Set(key,  metadata.LocalInputs.ToFullExtract(), zipPath);

        var count = cache.OutputVersionsCount(key);
        
        Assert.That(count, Is.EqualTo(1));

        var cachedZip = cache.Get(key);
        Assert.That(cachedZip, Is.Not.Null);

        var mainOutputsDir = new DirectoryInfo(outputsDir.FullName); 
        outputsDir.MoveTo(dir.Dir.CombineAsDir("old_output").FullName);
        mainOutputsDir.Create();
        UserOrPopulator.UseCachedOutputs(cachedZip!, items, DateTime.Now);
        AssertDirsSame(outputsDir, mainOutputsDir);
    }

    public static void AssertDirsSame(DirectoryInfo a, DirectoryInfo b)
    {
        (string Name, string Hash)[] GetInfo(DirectoryInfo dir) =>
            dir
                .EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Select(x => (x.Name, Hash: MSBuild.CompilerCache.Utils.FileToSHA256String(new FileInfo(x.FullName))))
                .Order()
                .ToArray();

        var aInfo = GetInfo(a);
        var bInfo = GetInfo(b);
        
        Assert.That(bInfo, Is.EqualTo(aInfo));
    }
}

[TestFixture]
public class Bigger
{
    [Test]
    public void Test()
    {
        /*
         * 1. Create input files on disk, setup the cache with/without entries.
         * 2. Call Locate task, grab and assert on its outputs.
         * 3. Either do nothing, or generate fake compilation outputs.
         * 4. Call UseOrPopulate task
         * 5. Inspect the cache.
         */
    }
}