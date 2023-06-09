using System.Collections.Immutable;

namespace MSBuild.CompilerCache;

public record AllRefData(
    LocalFileExtract Original,
    ImmutableArray<string> InternalsVisibleToAssemblies,
    string PublicRefHash,
    string PublicAndInternalsRefHash
)
{
    public string Name() => Path.GetFileNameWithoutExtension(Original.Path);
}