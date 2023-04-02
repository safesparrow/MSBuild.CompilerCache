using Microsoft.VisualStudio.TestPlatform.Common.Utilities;

namespace Tests;

using System.IO.Abstractions.TestingHelpers;
using MSBuild.CompilerCache;
using NUnit.Framework;

public class BuildEnvironment : IDisposable
{
    public DirectoryInfo Dir { get; set; }

    public BuildEnvironment()
    {
        Dir = CreateTempDir();
    }

    private static DirectoryInfo CreateTempDir()
    {
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);
        var dir = Directory.CreateDirectory(tempFile);
        return dir;
    }

    public void Dispose()
    {
        Dir.Delete(recursive: true);
    }
}

public enum OutputType { Exe, Library }
public enum DebugType { Embedded }

public static class StringExtensions
{
    public static string StringsJoin(this IEnumerable<string> items, string separator) =>
        String.Join(separator, items);
}

public record ProjectFileRaw
{
    public IReadOnlyDictionary<string, string> Properties { get; init; }
    public IReadOnlyCollection<string> Compile { get; init; }

    public string ToXml()
    {
        var properties =
            Properties
                .Select(pair => $"        <{pair.Key}>{pair.Value}</{pair.Key}>")
                .StringsJoin(Environment.NewLine);

        var compiles =
            Compile
                .Select(item => $"        <Compile Include=\"{item}\" />")
                .StringsJoin(Environment.NewLine);
        
        return $"""
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        {properties}
    </PropertyGroup>
    <ItemGroup>
        {compiles}
    </ItemGroup>
</Project>
""";
    }
}

public record ProjectFileBuilder
{
    public OutputType OutputType { get; set; } = OutputType.Library;
    public DebugType DebugType { get; set; } = DebugType.Embedded;
    public bool GenerateDocumentationFile { get; set; } = true;
    public bool ProduceReferenceAssembly { get; set; } = true;
    public string? AssemblyName { get; set; } = null;
    public string? CompilationCacheBaseDir { get; set; } = null;
    public List<string> Compile { get; } = new List<string>();
    

    public ProjectFileRaw ToRaw()
    {
        var properties = new Dictionary<string, string>
        {
            ["OutputType"] = OutputType.ToString(),
            ["DebugType"] = DebugType.ToString(),
            ["GenerateDocumentationFile"] = GenerateDocumentationFile.ToString(),
            ["ProduceReferenceAssembly"] = ProduceReferenceAssembly.ToString(),
        };

        void AddIfNotNull(string name, string? value)
        {
            if (value != null)
            {
                properties[name] = value;
            }
        }
        
        AddIfNotNull("AssemblyName", AssemblyName);
        AddIfNotNull("CompilationCacheBaseDir", CompilationCacheBaseDir);

        return new ProjectFileRaw
        {
            Properties = properties,
            Compile = Compile
        };
    }
}


[TestFixture]
public class EndToEndTests
{
    [Test]
    public void DummyTest()
    {
        var env = new BuildEnvironment();
    }
}