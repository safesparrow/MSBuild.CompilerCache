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
        
        Assert.That(res.InternalsVisibleTo, Is.EquivalentTo(new[]{"MSBuild.CompilerCache.Tests"}));
        Assert.That(res.PublicRefHash, Is.Not.EqualTo(res.PublicAndInternalRefHash));
    }
}

[TestFixture]
public class CachedTrimmingTests
{
    public record AllRefData(
        string Path,
        string Name,
        string Hash,
        ImmutableArray<string> InternalsVisibleToAssemblies,
        string PublicRefHash,
        string PublicAndInternalsRefHash
    );
    
    [Test]
    public void METHOD()
    {
        IRefCache cache = null;

        var dlls = new[] { "a", "b" };
        var res = dlls.AsParallel().WithDegreeOfParallelism(4).Select(GetAllRefData).ToImmutableArray();

        AllRefData GetAllRefData(string dllPath)
        {
            
        }
    }
}