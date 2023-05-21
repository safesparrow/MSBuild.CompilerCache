using System.Collections.Immutable;
using System.Reflection;
using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class TrimmingTests
{
    [Test]
    public void InternalsVisibleToAreResolvedCorrectly()
    {
        var path = Assembly.GetExecutingAssembly().Location.Replace(".Tests.dll", ".dll");
        var bytes = File.ReadAllBytes(path);
        var t = new RefTrimmer();
        var res = t.GenerateRefData(bytes.ToImmutableArray());

        Assert.That(res.InternalsVisibleTo, Is.EquivalentTo(new[] { "MSBuild.CompilerCache.Tests" }));
        Assert.That(res.PublicRefHash, Is.Not.EqualTo(res.PublicAndInternalRefHash));
    }
}

[TestFixture]
public class CachedTrimmingTests
{
    [Test]
    public void METHOD()
    {
        IRefCache cache = null;

        var dlls = new[] { Assembly.GetExecutingAssembly().Location.Replace(".Tests.dll", ".dll") };
        var res = dlls
            .AsParallel()
            .WithDegreeOfParallelism(4)
            .Select(filepath => Locator.GetAllRefData(filepath, cache))
            .ToImmutableArray();
    }
}

[TestFixture]
public class RefCacheTests
{
    [Test]
    public void EmptyCacheMisses()
    {
        using var dir = new DisposableDir();
        var cache = new RefCache(dir.FullName);
        var key = new CacheKey("a");
        Assert.Multiple(() =>
        {
            Assert.That(cache.Exists(key), Is.False);
            Assert.That(cache.Get(key), Is.Null);
        });
    }

    [Test]
    public void AfterSetCacheHits()
    {
        using var dir = new DisposableDir();
        var cache = new RefCache(dir.FullName);
        var key = new CacheKey("a");
        var data = new RefDataWithOriginalExtract(
            Ref: new RefData(
                PublicRefHash: "public",
                PublicAndInternalRefHash: "publicandinternal",
                InternalsVisibleTo: ImmutableArray.Create("asm1")
            ),
            Original: new LocalFileExtract(
                Path: "a",
                Hash: "original_hash",
                Length: 1212,
                LastWriteTimeUtc: DateTime.MaxValue
            )
        );
        cache.Set(key, data);
        
        Assert.Multiple(() =>
        {
            Assert.That(cache.Exists(key), Is.True);
            var cached = cache.Get(key);
            Assert.That(cached, Is.Not.Null);
            Assert.That(cached.ToJson(), Is.EqualTo(data.ToJson()));
            Assert.That(cache.Exists(new CacheKey("b")), Is.False);
        }); 
    }
}

public static class Extensions
{
    public static string ToJson(this object x) => Newtonsoft.Json.JsonConvert.SerializeObject(x);
}