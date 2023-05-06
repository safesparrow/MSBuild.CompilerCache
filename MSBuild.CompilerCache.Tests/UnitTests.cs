using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Moq;
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
        cache.Set(key, metadata.LocalInputs.ToFullExtract(), zipPath);

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
public class InMemoryIntegrationTests
{
    private Mock<IBuildEngine9> _buildEngine;

    [SetUp]
    public void SetUp()
    {
        _buildEngine = new Mock<IBuildEngine9>();
        _buildEngine.Setup(x => x.LogMessageEvent(It.IsAny<BuildMessageEventArgs>()));
    }
    
    [Test]
    public void SimpleCacheHitTest()
    {
        /*
         * 1. Create input files on disk, setup the cache with/without entries.
         * 2. Call Locate task, grab and assert on its outputs.
         * 3. Either do nothing, or generate fake compilation outputs.
         * 4. Call UseOrPopulate task
         * 5. Inspect the cache.
         */
        using var tmpDir = new DisposableDir();
        var baseCacheDir = Path.Combine(tmpDir.FullName, ".cache");
        var cache = new Cache(baseCacheDir);

        string CreateTmpOutputFile(string name, string content)
        {
            var path = Path.Combine(tmpDir.FullName, name);
            File.WriteAllText(path, content);
            return path;
        }
        var outputItems = new[]
        {
            new OutputItem("OutputAssembly", CreateTmpOutputFile("Output", "content_output")),
            new OutputItem("OutputRefAssembly", CreateTmpOutputFile("OutputRef", "content_output_ref")),
        };

        var baseInputs = new BaseTaskInputs(
            ProjectFullPath: "",
            PropertyInputs: "",
            FileInputs: new string[] { },
            References: new string[] { },
            RawOutputsToCache: BuildRawOutputsToCache(outputItems),
            BaseCacheDir: baseCacheDir
        );

        var localInputs = Locator.CalculateLocalInputs(baseInputs);
        var extract = localInputs.ToFullExtract();
        var hashString = MSBuild.CompilerCache.Utils.ObjectToSHA256Hex(extract);
        var cacheKey = UserOrPopulator.GenerateKey(baseInputs, hashString);
        var localInputsHash = MSBuild.CompilerCache.Utils.ObjectToSHA256Hex(localInputs);
        var zip = UserOrPopulator.BuildOutputsZip(tmpDir.Dir, outputItems,
            new AllCompilationMetadata(null, localInputs));

        cache.Set(cacheKey, extract, zip);

        var locate = new LocateCompilationCacheEntry();
        locate.BuildEngine = _buildEngine.Object;
        locate.SetInputs(baseInputs);
        var locateSuccess = locate.Execute();
        Assert.That(locateSuccess, Is.True);
        var locateResult = locate.LocateResult;

        Assert.Multiple(() =>
        {
            Assert.That(locateResult.CacheHit, Is.True);
            Assert.That(locateResult.CacheKey, Is.EqualTo(cacheKey));
            Assert.That(locateResult.LocalInputsHash, Is.EqualTo(localInputsHash));
        });

        var use = new UseOrPopulateCache();
        use.BuildEngine = _buildEngine.Object;
        var useInputs = new UseOrPopulateInputs(
            Inputs: baseInputs,
            CacheHit: locateResult.CacheHit,
            CacheKey: locateResult.CacheKey,
            LocatorLocalInputsHash: locateResult.LocalInputsHash,
            CheckCompileOutputAgainstCache: true
        );
        use.SetAllInputs(useInputs);
        Assert.That(use.Execute(), Is.True);

        var allKeys = cache.GetAllExistingKeys();
        Assert.That(allKeys, Is.EquivalentTo(new[]{cacheKey}));
    }

    private static ITaskItem[] BuildRawOutputsToCache(OutputItem[] outputItems) =>
        outputItems.Select(o =>
        {
            var meta = new Dictionary<string, string> { ["name"] = o.Name };
            return (ITaskItem) new TaskItem(o.LocalPath, meta);
        }).ToArray();
}