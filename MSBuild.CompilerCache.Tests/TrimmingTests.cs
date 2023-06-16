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

        Assert.That(res.InternalsVisibleTo, Is.EquivalentTo(new[] { "MSBuild.CompilerCache.Tests", "MSBuild.CompilerCache.Benchmarks" }));
        Assert.That(res.PublicRefHash, Is.Not.EqualTo(res.PublicAndInternalRefHash));
    }
}
