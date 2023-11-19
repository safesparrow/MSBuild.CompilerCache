using System.IO.Compression;
using Microsoft.Build.Framework;
using Moq;
using MSBuild.CompilerCache;
using Newtonsoft.Json;
using NUnit.Framework;
using IRefCache = MSBuild.CompilerCache.ICacheBase<MSBuild.CompilerCache.CacheKey, MSBuild.CompilerCache.RefDataWithOriginalExtract>;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Tests;

[TestFixture]
public class InMemoryTaskBasedTests
{
    private Mock<IBuildEngine9> _buildEngine = null!;
    private CompilerCacheLocate locate;
    private CompilerCachePopulateCache use;
    private DisposableDir tmpDir;
    private CompilationResultsCache _compilationResultsCache;
    private string baseCacheDir;

    [SetUp]
    public void SetUp()
    {
        _buildEngine = new Mock<IBuildEngine9>();
        _buildEngine.Setup(x => x.LogMessageEvent(It.IsAny<BuildMessageEventArgs>()))
            .Callback((BuildMessageEventArgs a) => Console.WriteLine($"Log - {a.Message}"));
        _buildEngine.Setup(x => x.LogWarningEvent(It.IsAny<BuildWarningEventArgs>()))
            .Callback((BuildWarningEventArgs a) => Console.WriteLine($"Warn - {a.Message}"));
        var dict = new Dictionary<(object, RegisteredTaskObjectLifetime), object>();
        _buildEngine
            .Setup(x => x.RegisterTaskObject(It.IsAny<object>(), It.IsAny<object>(),
                It.IsAny<RegisteredTaskObjectLifetime>(), It.IsAny<bool>()))
            .Callback((object key, object value, RegisteredTaskObjectLifetime life, bool early) =>
                dict[(key, life)] = value);

        _buildEngine
            .Setup(x => x.UnregisterTaskObject(It.IsAny<object>(), It.IsAny<RegisteredTaskObjectLifetime>()))
            .Returns((object key, RegisteredTaskObjectLifetime life) =>
            {
                var k = (key, life);
                if (dict.TryGetValue(k, out var expression)) return expression;
                return null!;
            });

        locate = new CompilerCacheLocate();
        locate.BuildEngine = _buildEngine.Object;

        use = new CompilerCachePopulateCache();
        use.BuildEngine = _buildEngine.Object;

        tmpDir = new DisposableDir();
        baseCacheDir = Path.Combine(tmpDir.FullName, ".cache");
        _compilationResultsCache = new CompilationResultsCache(baseCacheDir);
    }

    [TearDown]
    public void TearDown()
    {
        tmpDir.Dispose();
    }

    public record All(LocalInputs LocalInputs, CacheKey CacheKey, FullExtract FullExtract);

    public static All AllFromInputs(LocateInputs inputs, IRefCache refCache)
    {
        var decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps);
        var localInputs = LocatorAndPopulator.CalculateLocalInputs(decomposed, refCache, compilingAssemblyName: "",
            trimmingConfig: new RefTrimmingConfig(), fileHashCache: new DictionaryBasedCache<FileHashCacheKey, string>(), hasher: TestUtils.DefaultHasher, counters: null);
        var extract = localInputs.ToSlim().ToFullExtract();
        var jsonBytes = JsonSerializerExt.SerializeToUtf8Bytes(extract, null, FullExtractJsonContext.Default.FullExtract);
        var hashString = Utils.BytesToHash(jsonBytes, TestUtils.DefaultHasher);
        var cacheKey = LocatorAndPopulator.GenerateKey(inputs, hashString);

        return new All(LocalInputs: localInputs,
            CacheKey: cacheKey,
            FullExtract: extract
        );
    }

    public string SaveConfig(Config config)
    {
        var json = JsonConvert.SerializeObject(config);
        return CreateTmpFile(".config", json);
    }

    [Test]
    public async Task SimpleCacheHitTest()
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
            CacheDir = baseCacheDir
        };

        var configPath = SaveConfig(config);
        var inputs = new LocateInputs(
            ConfigPath: configPath,
            ProjectFullPath: "",
            AssemblyName: "bar",
            AllProps: allProps
        );
        var refCache = new RefCache(tmpDir.Dir.CombineAsDir(".refcache").FullName);
        var all = AllFromInputs(inputs, refCache);
        var hasher = TestUtils.DefaultHasher;
        var outputData = await Task.WhenAll(outputItems.Select(i => LocatorAndPopulator.GatherSingleOutputData(i, hasher, null)).ToArray());
        var zip = await LocatorAndPopulator.BuildOutputsZip(tmpDir.Dir, outputData,
            new AllCompilationMetadata(null, all.LocalInputs.ToSlim()), hasher);

        foreach (var outputItem in outputItems)
        {
            File.Move(outputItem.LocalPath, outputItem.LocalPath + ".copy");
        }

        await _compilationResultsCache.SetAsync(all.CacheKey, all.FullExtract, zip);

        locate.SetInputs(inputs);
        var locateSuccess = locate.Execute();
        Assert.That(locateSuccess, Is.True);
        var locateResult = locate.LocateResult;

        Assert.Multiple(() =>
        {
            Assert.That(locateResult.CacheHit, Is.True);
            Assert.That(locateResult.CacheKey, Is.EqualTo(all.CacheKey));
            Assert.That(locateResult.CacheSupported, Is.True);
            Assert.That(locateResult.RunCompilation, Is.False);
        });

        var allKeys = _compilationResultsCache.GetAllExistingKeys();
        Assert.That(allKeys, Is.EquivalentTo(new[] { all.CacheKey }));

        foreach (var outputItem in outputItems)
        {
            Assert.That(File.Exists(outputItem.LocalPath));
            Assert.That(await File.ReadAllTextAsync(outputItem.LocalPath),
                Is.EqualTo(await File.ReadAllTextAsync(outputItem.LocalPath + ".copy")));
        }
    }

    [Test]
    public async Task SimpleCacheMissTest()
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
            CacheDir = baseCacheDir
        };
        var configPath = SaveConfig(config);

        var baseInputs = new LocateInputs(
            ConfigPath: configPath,
            ProjectFullPath: "",
            AssemblyName: "Bar",
            AllProps: allProps
        );
        var refCache = new RefCache(tmpDir.Dir.CombineAsDir(".refcache").FullName);
        var all = AllFromInputs(baseInputs, refCache);

        locate.SetInputs(baseInputs);
        var locateSuccess = locate.Execute();
        Assert.That(locateSuccess, Is.True);
        var locateResult = locate.LocateResult;

        Assert.Multiple(() =>
        {
            Assert.That(locateResult.CacheHit, Is.False);
            Assert.That(locateResult.CacheKey, Is.EqualTo(all.CacheKey));
            Assert.That(locateResult.CacheSupported, Is.True);
            Assert.That(locateResult.RunCompilation, Is.True);
        });

        use.Guid = locate.Guid;
        use.CompilationSucceeded = true;
        Assert.That(use.Execute(), Is.True);

        var allKeys = _compilationResultsCache.GetAllExistingKeys();
        Assert.That(allKeys, Is.EquivalentTo(new[] { all.CacheKey }));

        var zip = await _compilationResultsCache.GetAsync(all.CacheKey);
        Assert.That(zip, Is.Not.Null);
        var fromCacheDir = tmpDir.Dir.CreateSubdirectory("from_cache");
        ZipFile.ExtractToDirectory(zip, fromCacheDir.FullName);

        foreach (var outputItem in outputItems)
        {
            var cachedFile = fromCacheDir.CombineAsFile(outputItem.CacheFileName);
            Assert.That(File.Exists(cachedFile.FullName));
            Assert.That(await File.ReadAllTextAsync(cachedFile.FullName), Is.EqualTo(await File.ReadAllTextAsync(outputItem.LocalPath)));
        }
    }

    private string CreateTmpFile(string name, string content)
    {
        var path = Path.Combine(tmpDir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }
}