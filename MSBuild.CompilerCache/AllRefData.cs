using System.Collections.Immutable;

namespace MSBuild.CompilerCache;

/// <summary>
/// Information about a dll, and its trimmed versions (reference assemblies).
/// </summary>
/// <param name="Original">Metadata about the original trimmed file.</param>
/// <param name="InternalsVisibleToAssemblies"></param>
/// <param name="PublicRefHash">Hash of the dll resulting from trimming the original to public symbols only</param>
/// <param name="PublicAndInternalsRefHash">
/// Hash of the dll resulting from trimming the original to public and internal symbols only.
/// If <see cref="HasAnyInternalsVisibleToAssemblies"/> is false, this will be null.
/// </param>
public record AllRefData(
    LocalFileExtract Original,
    RefData RefData
)
{
    public AllRefData(
        LocalFileExtract Original,
        ImmutableArray<string> InternalsVisibleToAssemblies,
        string PublicRefHash,
        string? PublicAndInternalsRefHash
    ) : this(Original, new RefData(PublicRefHash, PublicAndInternalsRefHash, InternalsVisibleToAssemblies))
    {  }
    
    public ImmutableArray<string> InternalsVisibleToAssemblies => RefData.InternalsVisibleTo;

    public string PublicRefHash => RefData.PublicRefHash;
    public string? PublicAndInternalsRefHash => RefData.PublicAndInternalRefHash;
    
    public string DllName() => Path.GetFileNameWithoutExtension(Original.Path);
    public bool HasAnyInternalsVisibleToAssemblies => InternalsVisibleToAssemblies.Length > 0;
}