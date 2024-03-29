using System.IO.Compression;
using Microsoft.Build.Framework;
using Moq;
using MSBuild.CompilerCache;
using Newtonsoft.Json;
using NUnit.Framework;
using IRefCache =
    MSBuild.CompilerCache.ICacheBase<MSBuild.CompilerCache.CacheKey, MSBuild.CompilerCache.RefDataWithOriginalExtract>;

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
            .Setup(x => x.GetRegisteredTaskObject(It.IsAny<object>(), It.IsAny<RegisteredTaskObjectLifetime>()))
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

    public record All(LocateInputs LocateInputs, LocalInputs LocalInputs, CacheKey CacheKey,
        string LocalInputsHash, FullExtract FullExtract);

    public static All AllFromInputs(LocateInputs inputs, IRefCache refCache)
    {
        var decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps);
        var localInputs = LocatorAndPopulator.CalculateLocalInputs(decomposed, refCache, assemblyName: "",
            trimmingConfig: new RefTrimmingConfig(), fileHashCache: new DictionaryBasedCache<FileHashCacheKey, string>(),
            hasher: Utils.DefaultHasher);
        var extract = localInputs.ToFullExtract();
        var hashString = Utils.ObjectToHash(extract, Utils.DefaultHasher);
        var cacheKey = LocatorAndPopulator.GenerateKey(inputs, hashString);
        var localInputsHash = Utils.ObjectToHash(localInputs, Utils.DefaultHasher);

        return new All(
            LocateInputs: inputs,
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
        var zip = LocatorAndPopulator.BuildOutputsZip(tmpDir.Dir, outputItems,
            new AllCompilationMetadata(null, all.LocalInputs), Utils.DefaultHasher);

        foreach (var outputItem in outputItems)
        {
            File.Move(outputItem.LocalPath, outputItem.LocalPath + ".copy");
        }

        _compilationResultsCache.Set(all.CacheKey, all.FullExtract, zip);

        locate.SetInputs(inputs);
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

        var allKeys = _compilationResultsCache.GetAllExistingKeys();
        Assert.That(allKeys, Is.EquivalentTo(new[] { all.CacheKey }));

        foreach (var outputItem in outputItems)
        {
            Assert.That(File.Exists(outputItem.LocalPath));
            Assert.That(File.ReadAllText(outputItem.LocalPath),
                Is.EqualTo(File.ReadAllText(outputItem.LocalPath + ".copy")));
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
            Assert.That(locateResult.LocalInputsHash, Is.EqualTo(all.LocalInputsHash));
            Assert.That(locateResult.CacheSupported, Is.True);
            Assert.That(locateResult.RunCompilation, Is.True);
        });

        use.Guid = locate.Guid;
        Assert.That(use.Execute(), Is.True);

        var allKeys = _compilationResultsCache.GetAllExistingKeys();
        Assert.That(allKeys, Is.EquivalentTo(new[] { all.CacheKey }));

        var zip = _compilationResultsCache.Get(all.CacheKey);
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