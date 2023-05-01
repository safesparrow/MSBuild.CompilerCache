using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class UnitTests
{
    [Test]
    public void Test()
    {
        using var dir = new DisposableDir();
        var foo = dir.Dir.CombineAsFile("foo.txt");
        File.WriteAllText(foo.FullName, "foo");
        var items = new[]
        {
            new OutputItem("foo", foo.FullName)
        };
        var metadata = new AllCompilationMetadata(
            Metadata: new PostCompilationMetadata(
                Hostname: "A",
                Username: "B",
                StartTimeUtc: DateTime.Today,
                StopTimeUtc: DateTime.UtcNow),
            LocalInputs:
            new LocalInputs(
                Files: new LocalFileExtract[] { },
                Props: new[] { "a=b" },
                OutputFiles: items
            )
        );
        var zipPath = UserOrPopulator.BuildOutputsZip(dir, items, metadata);
        
    }
}