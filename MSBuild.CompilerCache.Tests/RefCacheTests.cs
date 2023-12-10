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
        Assert.Multiple(async () =>
        {
            Assert.That(cache.Exists(key), Is.False);
            Assert.That(await cache.GetAsync(key), Is.Null);
        });
    }

    [Test]
    public async Task AfterSetCacheHits()
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
            Original: new LocalFileExtract(Info: new FileHashCacheKey(
                    FullName: "a",
                    Length: 1212,
                    LastWriteTimeUtc: new DateTime(2023, 7, 1, 0, 0, 0, kind: DateTimeKind.Utc)
                ),
                Hash: "hash"
            )
        );
        
        await cache.SetAsync(key, data);

        Assert.Multiple(async () =>
        {
            Assert.That(cache.Exists(key), Is.True);
            var cached = await cache.GetAsync(key);
            Assert.That(cached, Is.Not.Null);
            Assert.That(cached!.ToJson(), Is.EqualTo(data.ToJson()));
            Assert.That(cache.Exists(new CacheKey("b")), Is.False);
        }); 
    }
}