using System.Collections.Immutable;
using System.Reflection;
using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class TrimmingTests
{
    [Test]
    public async Task InternalsVisibleToAreResolvedCorrectly()
    {
        var path = Assembly.GetExecutingAssembly().Location.Replace(".Tests.dll", ".dll");
        var bytes = await File.ReadAllBytesAsync(path);
        var t = new RefTrimmer();
        var res = await t.GenerateRefData(bytes.ToImmutableArray());

        Assert.That(res.InternalsVisibleTo, Is.EquivalentTo(new[] { "MSBuild.CompilerCache.Tests", "MSBuild.CompilerCache.Benchmarks" }));
        Assert.That(res.PublicRefHash, Is.Not.EqualTo(res.PublicAndInternalRefHash));
    }
}
