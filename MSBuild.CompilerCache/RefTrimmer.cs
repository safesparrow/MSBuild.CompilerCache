using System.Collections.Immutable;
using System.Reflection.Metadata;
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

public record RefDataWithOriginalExtract(RefData Ref, LocalFileExtract Original);

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

    public enum RefAsmType
    {
        Public,
        PublicAndInternal
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

    public static (ImmutableArray<byte> bytes, ImmutableArray<string> internalsVisibleToAssemblies) MakeRefasm(
        ImmutableArray<byte> content, LoggerBase logger, RefAsmType refAsmType)
    {
        var peReader = new PEReader(content);
        var metaReader = peReader.GetMetadataReader();

        var internalsVisibleToAssemblies = GetInternalsVisibleToAssemblies(metaReader);

        if (!metaReader.IsAssembly)
            throw new Exception("File format is not supported");

        IImportFilter filter = refAsmType == RefAsmType.PublicAndInternal
            ? new AllowPublicAndInternals()
            : new AllowPublic();

        var refBytes = MetadataImporter.MakeRefasm(metaReader, peReader, logger, filter, makeMock: false)!;

        return (ImmutableArray.Create(refBytes), internalsVisibleToAssemblies);
    }

    public static (string hash, ImmutableArray<string> internalsVisibleToAssemblies) MakeRefasmAndGetHash(
        ImmutableArray<byte> content, LoggerBase logger, RefAsmType refAsmType)
    {
        var (bytes, internalsVisibleToAssemblies) = MakeRefasm(content, logger, refAsmType);
        var hash = Utils.BytesToSHA256Hex(bytes);
        return (hash, internalsVisibleToAssemblies);
    }

    public RefData GenerateRefData(ImmutableArray<byte> content)
    {
        var logger = new VerySimpleLogger(Console.Out, LogLevel.Warning);
        var loggerBase = new LoggerBase(logger);

        var (publicRefHash, internalsVisibleToAssemblies) =
            MakeRefasmAndGetHash(content, loggerBase, RefAsmType.Public);
        // If no assemblies can see the internals, there is no need to generate public+internal ref assembly 
        var internalsNeverAccessible = internalsVisibleToAssemblies.IsEmpty;
        var publicAndInternalRefHash =
            internalsNeverAccessible
                ? publicRefHash
                : MakeRefasmAndGetHash(content, loggerBase, RefAsmType.PublicAndInternal).hash;

        return new RefData(
            PublicRefHash: publicRefHash,
            PublicAndInternalRefHash: publicAndInternalRefHash,
            InternalsVisibleTo: internalsVisibleToAssemblies
        );
    }
}