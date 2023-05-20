using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Decoding.Tests;
using System.Reflection.PortableExecutable;
using JetBrains.Refasmer;
using JetBrains.Refasmer.Filters;

namespace MSBuild.CompilerCache;

/// <summary>
/// Information about an assembly used for hash calculations in dependant projects' compilation.
/// </summary>
/// <param name="PublicRefHash">Hash of a reference assembly for this assembly, that excluded internal symbols. </param>
/// <param name="PublicAndInternalRefHash">Hash of a reference assembly for this assembly, that included internal symbols. </param>
/// <param name="InternalsVisibleTo">A list of assembly names that can access internal symbols from this assembly, via the InternalsVisibleTo attribute.</param>
public record RefData(string PublicRefHash, string PublicAndInternalRefHash, ImmutableArray<string> InternalsVisibleTo);

/// <summary>
/// A cache for storing hashes of reference assemblies (generated using Refasmer).
/// Does not store the actual assemblies, just their hashes - used for having a more robust inputs cache that does not change when private parts of references change.
/// Thread-safe.
/// </summary>
public interface IRefCache
{
    bool Exists(CacheKey key);
    RefData? Get(CacheKey key);
    void Set(CacheKey key, RefData data);
}

public class RefTrimmer
{
    public static void MakeRefasm(string inputPath, string outputPath, LoggerBase logger, IImportFilter filter)
    {
        logger.Debug?.Invoke($"Reading assembly {inputPath}");
        var peReader = new PEReader(new FileStream(inputPath, FileMode.Open, FileAccess.Read));
        var metaReader = peReader.GetMetadataReader();

        if (!metaReader.IsAssembly)
            throw new Exception("File format is not supported");

        var result = MetadataImporter.MakeRefasm(metaReader, peReader, logger, filter, makeMock: false);

        logger.Debug?.Invoke($"Writing result to {outputPath}");
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        File.WriteAllBytes(outputPath, result);
    }

    public enum RefType
    {
        Public,
        PublicAndInternal
    }


    public static bool HasAnyInternalsVisibleToAttribute(MetadataReader reader) =>
        // ReSharper disable once ReplaceWithSingleCallToAny
        reader.IsAssembly && reader.GetAssemblyDefinition().GetCustomAttributes()
            .Select(reader.GetCustomAttribute)
            .Where(a => reader.GetFullname(reader.GetCustomAttrClass(a)) == FullNames.InternalsVisibleTo)
            .Any();


    private class CustomAttributeTypeProvider : DisassemblingTypeProvider, ICustomAttributeTypeProvider<string>
    {
        public string GetSystemType()
        {
            return "[System.Runtime]System.Type";
        }

        public bool IsSystemType(string type)
        {
            return type == "[System.Runtime]System.Type" // encountered as typeref
                   || Type.GetType(type) == typeof(Type); // encountered as serialized to reflection notation
        }

        public string GetTypeFromSerializedName(string name)
        {
            return name;
        }

        public PrimitiveTypeCode GetUnderlyingEnumType(string type)
        {
            throw new ArgumentOutOfRangeException();
        }
    }

    public static ImmutableArray<string> GetInternalsVisibleToAssemblies(MetadataReader reader)
    {
        var provider = new CustomAttributeTypeProvider();
        if (!reader.IsAssembly) return ImmutableArray.Create<string>();

        return
            reader
                .GetAssemblyDefinition()
                .GetCustomAttributes()
                .Select(reader.GetCustomAttribute)
                .Where(a => reader.GetFullname(reader.GetCustomAttrClass(a)) == FullNames.InternalsVisibleTo)
                .Select(x => x.DecodeValue(provider))
                .Select(x =>
                {
                    if (x.FixedArguments.Length != 1)
                    {
                        throw new Exception(
                            $"Excpected InternalsVisibleTo attribute to have exactly 1 fixed argument but got {x.FixedArguments.Length}.");
                    }

                    var arg = x.FixedArguments[0];
                    return arg.Value switch
                    {
                        string assemblyName => assemblyName,
                        _ => throw new Exception(
                            $"Expected InternalsVisible attribute to have a single 'string' argument but got argument type '{arg.Type}'")
                    };
                })
                .ToImmutableArray();
    }

    public static (byte[] res, ImmutableArray<string> internalsVisibleToAssemblies) MakeRefasm(
        ImmutableArray<byte> content, LoggerBase logger, RefType refType)
    {
        var peReader = new PEReader(content);
        var metaReader = peReader.GetMetadataReader();

        var internalsVisibleToAssemblies = GetInternalsVisibleToAssemblies(metaReader);

        if (!metaReader.IsAssembly)
            throw new Exception("File format is not supported");

        IImportFilter filter = refType == RefType.PublicAndInternal
            ? new AllowPublicAndInternals()
            : new AllowPublic();

        byte[] res = null;
        for (int i = 0; i < 5; i++)
        {
            res = MetadataImporter.MakeRefasm(metaReader, peReader, logger, filter, makeMock: false)!;
        }

        return (res, internalsVisibleToAssemblies);
    }

    public static (string hash, ImmutableArray<string> internalsVisibleToAssemblies) MakeRefasmAndGetHash(
        ImmutableArray<byte> content, LoggerBase logger, RefType refType)
    {
        var (bytes, internalsVisibleToAssemblies) = MakeRefasm(content, logger, refType);
        var hash = Utils.ObjectToSHA256Hex(bytes);
        return (hash, internalsVisibleToAssemblies);
    }

    public RefData GenerateRefData(ImmutableArray<byte> content)
    {
        var logger = new VerySimpleLogger(Console.Out, LogLevel.Warning);
        var loggerBase = new LoggerBase(logger);

        var (publicRefHash, internalsVisibleToAssemblies) = MakeRefasmAndGetHash(content, loggerBase, RefType.Public);
        var (publicAndInternalRefHash, _) = MakeRefasmAndGetHash(content, loggerBase, RefType.PublicAndInternal);

        return new RefData(
            PublicRefHash: publicRefHash,
            PublicAndInternalRefHash: publicAndInternalRefHash,
            InternalsVisibleTo: internalsVisibleToAssemblies
        );
    }
}