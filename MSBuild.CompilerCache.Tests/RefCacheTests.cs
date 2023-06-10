using System.Collections.Immutable;
using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

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
                Hash: null,
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