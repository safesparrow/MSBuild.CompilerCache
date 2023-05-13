using System.IO.Compression;
using Microsoft.Build.Framework;
using Moq;
using MSBuild.CompilerCache;
using Newtonsoft.Json;
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
                Props: new[]{("a", "b")},
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
        var localInputs = Locator.CalculateLocalInputs(TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps));
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

    public string SaveConfig(Config config)
    {
        var json = JsonConvert.SerializeObject(config);
        return CreateTmpFile(".config", json);
    }
    
    [Test]
    public void SimpleCacheHitTest()
    {
        var outputItems = new[]
        {
            new OutputItem("OutputAssembly", CreateTmpFile("Output.txt", "content_output")),
        };

        var allProps = new Dictionary<string, string>
        {
            ["OutputAssembly"] = outputItems[0].LocalPath,
            ["TargetType"] = "library"
        };
        var config = new Config
        {
            BaseCacheDir = baseCacheDir
        };
        
        var configPath = SaveConfig(config);
        var baseInputs = new BaseTaskInputs(
            ConfigPath: configPath,
            ProjectFullPath: "",
            AllProps: allProps
        );
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
            Assert.That(locateResult.CacheSupported, Is.True);
            Assert.That(locateResult.RunCompilation, Is.False);
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
    
    [Test]
    public void SimpleCacheMissTest()
    {
        var outputItems = new[]
        {
            new OutputItem("OutputAssembly", CreateTmpFile("Output.txt", "content_output")),
        };

        var allProps = new Dictionary<string, string>
        {
            ["TargetType"] = "library",
            ["References"] = "",
            ["OutputAssembly"] = outputItems[0].LocalPath
        };
        var config = new Config
        {
            BaseCacheDir = baseCacheDir
        };
        var configPath = SaveConfig(config);
        
        var baseInputs = new BaseTaskInputs(
            ConfigPath: configPath,
            ProjectFullPath: "",
            AllProps: allProps
        );
        var all = AllFromInputs(baseInputs);

        locate.SetInputs(baseInputs);
        var locateSuccess = locate.Execute();
        Assert.That(locateSuccess, Is.True);
        var locateResult = locate.LocateResult;

        Assert.Multiple(() =>
        {
            Assert.That(locateResult.CacheHit, Is.False);
            Assert.That(locateResult.CacheKey, Is.EqualTo(all.CacheKey));
            Assert.That(locateResult.LocalInputsHash, Is.EqualTo(all.LocalInputsHash));
            Assert.That(locateResult.CacheSupported, Is.True);
            Assert.That(locateResult.RunCompilation, Is.True);
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

        var zip = cache.Get(all.CacheKey);
        Assert.That(zip, Is.Not.Null);
        var fromCacheDir = tmpDir.Dir.CreateSubdirectory("from_cache");
        ZipFile.ExtractToDirectory(zip, fromCacheDir.FullName);
        
        foreach (var outputItem in outputItems)
        {
            var cachedFile = fromCacheDir.CombineAsFile(outputItem.CacheFileName);
            Assert.That(File.Exists(cachedFile.FullName));
            Assert.That(File.ReadAllText(cachedFile.FullName), Is.EqualTo(File.ReadAllText(outputItem.LocalPath)));
        }
    }

    private string CreateTmpFile(string name, string content)
    {
        var path = Path.Combine(tmpDir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }
}