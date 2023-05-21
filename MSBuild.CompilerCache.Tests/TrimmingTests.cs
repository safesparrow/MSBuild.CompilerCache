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

public class RefCache : IRefCache
{
    public bool Exists(CacheKey key)
    {
        throw new NotImplementedException();
    }

    public RefDataWithOriginalExtract? Get(CacheKey key)
    {
        throw new NotImplementedException();
    }

    public void Set(CacheKey key, RefDataWithOriginalExtract data)
    {
        throw new NotImplementedException();
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