using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Tests;

public sealed class BuildEnvironment : IDisposable
{
    public SDKVersion SdkVersion { get; }
    public DirectoryInfo Dir { get; set; }

    public BuildEnvironment(string nugetSource, SDKVersion sdkVersion)
    {
        SdkVersion = sdkVersion;
        Dir = CreateTempDir(nugetSource, sdkVersion);
    }

    private static string GlobalJson(SDKVersion sdk) =>
        "{\"sdk\": {\"version\": \"" + sdk + "\", \"rollForward\": \"disable\"}}";
    
    public static DirectoryInfo CreateTempDir(string nugetSource, SDKVersion sdk)
    {
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);
        var dir = Directory.CreateDirectory(tempFile);
        File.WriteAllText(Path.Combine(dir.FullName, "global.json"), GlobalJson(sdk));
        var props = Path.Combine(dir.FullName, "Directory.Build.props");
        File.WriteAllText(props, PropsFile);
        var nugetConfig = Path.Combine(dir.FullName, "nuget.config");
        File.WriteAllText(nugetConfig, NugetConfig(nugetSource));
        return dir;
    }

    private static string NugetConfig(string sourcePath) =>
        $"""
<?xml version="1.0" encoding="utf-8" ?> 
<configuration>
    <packageSources>
        <add key="MyLocalSharedSource" value="{sourcePath}" /> 
    </packageSources>
    <config>
        <add key="globalPackagesFolder" value="packages" />
    </config>
</configuration>
""";

    private static readonly string PropsFile =
        $"""
<Project>

    <PropertyGroup>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
        <DebugType>Embedded</DebugType>
        <Deterministic>true</Deterministic>
    </PropertyGroup>
    
    <PropertyGroup>
        <CompilationCacheBaseDir Condition="'$(CompilationCacheBaseDir)' == ''">$(MSBuildThisFileDirectory).cache/</CompilationCacheBaseDir>
    </PropertyGroup>
    
    <ItemGroup>
        <PackageReference Include="MSBuild.CompilerCache" Version="{NuGetVersion()}" />
    </ItemGroup>
    
</Project>
""";

    private static string NuGetVersion()
    {
        var v = ThisAssembly.AssemblyInformationalVersion;
        var r = Regex.Replace(v, "\\+([0-9a-zA-Z]+)$", "+g$1");
        return r;
    }

    public void Dispose()
    {
        Dir.Delete(recursive: true);
    }
}

public enum OutputType
{
    Exe,
    Library
}

public enum DebugType
{
    Embedded
}

public static class StringExtensions
{
    public static string StringsJoin(this IEnumerable<string> items, string separator) =>
        string.Join(separator, items);
}

public enum ItemType
{
    Compile,
    Embedded
}

public record Item(ItemType ItemType, string Include);

public record ProjectFileRaw
{
    public IReadOnlyDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty;
    public IReadOnlyCollection<Item> Items { get; init; } = ImmutableArray<Item>.Empty;

    private string ItemToXml(Item item) => $"<{item.ItemType.ToString()} Include=\"{item.Include}\" />";

    public string ToXml()
    {
        var properties =
            Properties
                .Select(pair => $"        <{pair.Key}>{pair.Value}</{pair.Key}>")
                .StringsJoin(Environment.NewLine);

        var items =
            Items
                .Select(item => $"        {ItemToXml(item)}")
                .StringsJoin(Environment.NewLine);

        return $"""
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        {properties}
    </PropertyGroup>
    <ItemGroup>
        {items}
    </ItemGroup>
</Project>
""";
    }
}

public record ProjectFileBuilder
{
    public OutputType OutputType { get; init; } = OutputType.Library;
    public DebugType DebugType { get; init; } = DebugType.Embedded;
    public bool GenerateDocumentationFile { get; init; } = true;
    public bool ProduceReferenceAssembly { get; init; } = true;
    public string? AssemblyName { get; init; } = null;
    public string? CompilationCacheBaseDir { get; init; } = null;
    public string TargetFramework { get; init; } = "net6.0";
    public string Name { get; init; } = null;

    public ProjectFileBuilder(string name) => Name = name;

    public IReadOnlyCollection<SourceFile> Sources { get; init; } = ImmutableArray<SourceFile>.Empty;
    public IReadOnlyCollection<SourceFile> Embeds { get; init; } = ImmutableArray<SourceFile>.Empty;

    public ProjectFileBuilder WithSource(SourceFile source) => this with { Sources = Sources.Append(source).ToList() };

    public ProjectFileRaw ToRaw() =>
        new()
        {
            Properties = GetProperties(),
            Items =
                Sources.Select(s => new Item(ItemType.Compile, s.Path))
                    .Union(Embeds.Select(e => new Item(ItemType.Embedded, e.Path)))
                    .ToImmutableArray()
        };

    private Dictionary<string, string> GetProperties()
    {
        var properties = new Dictionary<string, string>
        {
            ["EnableDefaultCompileItems"] = "false",
            ["OutputType"] = OutputType.ToString(),
            ["DebugType"] = DebugType.ToString(),
            ["GenerateDocumentationFile"] = GenerateDocumentationFile.ToString(),
            ["ProduceReferenceAssembly"] = ProduceReferenceAssembly.ToString(),
            ["TargetFramework"] = TargetFramework
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

        return properties;
    }

    public string ToXml() => ToRaw().ToXml();
}

public record SourceFile(string Path, string Text);

[TestFixture]
public class EndToEndTests
{
    public static readonly SDKVersion[] SDKs = new[]
    {
        new SDKVersion("6.0.300"),
        // new SDKVersion("7.0.202")
    };
        
    // [TestCaseSource(nameof(SDKs))]
    [Test]
    public void CompileTwoIdenticalProjectsAssertDllReused()
    {
        var sdk = new SDKVersion("6.0.300");
        var nugetSourcePath =
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetCallingAssembly().Location)!,
                @"..\..\..\..\MSBuild.CompilerCache\bin\Debug"
            );
        using var env = new BuildEnvironment(nugetSourcePath, sdk);
        var cache = new DirectoryInfo(Path.Combine(env.Dir.FullName, ".cache"));
        var projDir1 = new DirectoryInfo(Path.Combine(env.Dir.FullName, "1"));
        var projDir2 = new DirectoryInfo(Path.Combine(env.Dir.FullName, "2"));
        var projDir3 = new DirectoryInfo(Path.Combine(env.Dir.FullName, "3"));
        cache.Create();
        projDir1.Create();
        var source = new SourceFile("Library.cs", """
namespace CSharp;
public class Class { }
""");
        var proj =
            new ProjectFileBuilder("C.csproj")
                {
                    CompilationCacheBaseDir = cache.FullName
                }
                .WithSource(source);

        WriteProject(projDir1, proj);
        // WriteProject(projDir2, proj);

        BuildProject(projDir1, proj);
        // BuildProject(projDir2, proj);

        FileInfo DllFile(DirectoryInfo projDir, ProjectFileBuilder proj) =>
            new FileInfo(Path.Combine(projDir.FullName, "obj", "Debug", "net6.0",
                $"{Path.GetFileNameWithoutExtension(proj.Name)}.dll"));

        var dll1 = DllFile(projDir1, proj);
        // var dll2 = DllFile(projDir2, proj);

        // Assert.That(dll1.LastWriteTime, Is.EqualTo(dll2.LastWriteTime));

        var projModified = proj with { Sources = new[] { source with { Path = "Library2.cs" } } };
        // WriteProject(projDir3, projModified);
        // BuildProject(projDir3, projModified);
        void Print(FileInfo file) => Console.WriteLine($"{file.FullName} - Exists={file.Exists} - LastWriteTime={file.LastWriteTime}");
        var dll3 = DllFile(projDir3, proj);
        Print(dll1);
        // Print(dll2);
        // Print(dll3);
        // Assert.That(dll3.LastWriteTime, Is.GreaterThan(dll2.LastWriteTime));
    }

    private static void WriteProject(DirectoryInfo dir, ProjectFileBuilder project)
    {
        dir.Create();
        FileInfo GetPath(string relPath) => new FileInfo(Path.Combine(dir.FullName, relPath));
        var entries =
            project.Sources.Select(s => (GetPath(s.Path), s.Text))
                .Append((GetPath(project.Name), project.ToXml()))
                .ToArray();

        foreach (var entry in entries)
        {
            File.WriteAllText(entry.Item1.FullName, entry.Item2);
        }
    }

    private static void BuildProject(DirectoryInfo dir, ProjectFileBuilder project)
    {
        Utils.RunProcess("dotnet", "--list-sdks", dir);
        Utils.RunProcess("dotnet", "--version", dir);
        Utils.RunProcess("dotnet", "build", dir);
    }
}