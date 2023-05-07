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
    public void TestOutputBuildAndCache()
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
public class InMemoryTaskBasedTests
{
    private Mock<IBuildEngine9> _buildEngine = null!;
    private LocateCompilationCacheEntry locate;
    private UseOrPopulateCache use;
    private DisposableDir tmpDir;
    private Cache cache;
    private string baseCacheDir;

    [SetUp]
    public void SetUp()
    {
        _buildEngine = new Mock<IBuildEngine9>();
        _buildEngine.Setup(x => x.LogMessageEvent(It.IsAny<BuildMessageEventArgs>()));
        
        locate = new LocateCompilationCacheEntry();
        locate.BuildEngine = _buildEngine.Object;
        
        use = new UseOrPopulateCache();
        use.BuildEngine = _buildEngine.Object;
        
        tmpDir = new DisposableDir();
        baseCacheDir = Path.Combine(tmpDir.FullName, ".cache");
        cache = new Cache(baseCacheDir);
    }

    [TearDown]
    public void TearDown()
    {
        tmpDir.Dispose();
    }

    public record All(BaseTaskInputs BaseTaskInputs, LocalInputs LocalInputs, CacheKey CacheKey,
        string LocalInputsHash, FullExtract FullExtract);

    public static All AllFromInputs(BaseTaskInputs inputs)
    {
        var localInputs = Locator.CalculateLocalInputs(inputs);
        var extract = localInputs.ToFullExtract();
        var hashString = MSBuild.CompilerCache.Utils.ObjectToSHA256Hex(extract);
        var cacheKey = UserOrPopulator.GenerateKey(inputs, hashString);
        var localInputsHash = MSBuild.CompilerCache.Utils.ObjectToSHA256Hex(localInputs);

        return new All(
            BaseTaskInputs: inputs,
            LocalInputs: localInputs,
            CacheKey: cacheKey,
            LocalInputsHash: localInputsHash,
            FullExtract: extract
        );
    }
    
    [Test]
    public void SimpleCacheHitTest()
    {
        var outputItems = new[]
        {
            new OutputItem("OutputAssembly", CreateTmpFile("Output", "content_output")),
            new OutputItem("OutputRefAssembly", CreateTmpFile("OutputRef", "content_output_ref")),
        };

        var baseInputs = EmptyBaseTaskInputs with { RawOutputsToCache = BuildRawOutputsToCache(outputItems) };
        var all = AllFromInputs(baseInputs);
        var zip = UserOrPopulator.BuildOutputsZip(tmpDir.Dir, outputItems,
            new AllCompilationMetadata(null, all.LocalInputs));
        
        foreach (var outputItem in outputItems)
        {
            File.Move(outputItem.LocalPath, outputItem.LocalPath + ".copy");
        }

        cache.Set(all.CacheKey, all.FullExtract, zip);

        locate.SetInputs(baseInputs);
        var locateSuccess = locate.Execute();
        Assert.That(locateSuccess, Is.True);
        var locateResult = locate.LocateResult;

        Assert.Multiple(() =>
        {
            Assert.That(locateResult.CacheHit, Is.True);
            Assert.That(locateResult.CacheKey, Is.EqualTo(all.CacheKey));
            Assert.That(locateResult.LocalInputsHash, Is.EqualTo(all.LocalInputsHash));
        });

        var useInputs = new UseOrPopulateInputs(
            Inputs: baseInputs,
            CheckCompileOutputAgainstCache: false,
            LocateResult: locateResult
        );
        use.SetAllInputs(useInputs);
        Assert.That(use.Execute(), Is.True);

        var allKeys = cache.GetAllExistingKeys();
        Assert.That(allKeys, Is.EquivalentTo(new[] { all.CacheKey }));

        foreach (var outputItem in outputItems)
        {
            Assert.That(File.Exists(outputItem.LocalPath));
            Assert.That(File.ReadAllText(outputItem.LocalPath), Is.EqualTo(File.ReadAllText(outputItem.LocalPath + ".copy")));
        }
    }
    
    private BaseTaskInputs EmptyBaseTaskInputs =>
        new BaseTaskInputs(
            ProjectFullPath: "",
            PropertyInputs: "",
            FileInputs: new string[] { },
            References: new string[] { },
            RawOutputsToCache: new ITaskItem[]{ },
            BaseCacheDir: baseCacheDir
        );

    private string CreateTmpFile(string name, string content)
    {
        var path = Path.Combine(tmpDir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ITaskItem[] BuildRawOutputsToCache(OutputItem[] outputItems) =>
        outputItems.Select(o =>
        {
            var meta = new Dictionary<string, string> { ["name"] = o.Name };
            return (ITaskItem) new TaskItem(o.LocalPath, meta);
        }).ToArray();
}