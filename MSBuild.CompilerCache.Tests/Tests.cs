﻿using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using MSBuild.CompilerCache;
using Newtonsoft.Json;
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
        <add key="LocalTestSource" value="{sourcePath}" /> 
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

</Project>
""";

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
            ["TargetFramework"] = TargetFramework,
            ["NoWarn"] = "CS1591"
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
    public static readonly SDKVersion[] SDKs = {
        new SDKVersion("6.0.300"),
        new SDKVersion("7.0.202")
    };
    
    private const string Configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static readonly string NugetSourcePath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        Path.Combine("..", "..", "..", "..", "MSBuild.CompilerCache", "bin", Configuration)
    );

    [TestCaseSource(nameof(SDKs))]
    [Test]
    public void CompileTwoIdenticalProjectsAssertDllReused(SDKVersion sdk)
    {
        using var env = new BuildEnvironment(NugetSourcePath, sdk);
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
        var config = new Config
        {
            BaseCacheDir = cache.FullName
        };
        var configFile = env.Dir.CombineAsFile("config.json");
        File.WriteAllText(configFile.FullName, JsonConvert.SerializeObject(config));
        var proj =
            new ProjectFileBuilder("C.csproj")
                {
                    CompilationCacheBaseDir = cache.FullName
                }
                .WithSource(source);

        WriteProject(projDir1, proj);
        WriteProject(projDir2, proj);
        var projModified = proj with { Sources = new[] { source with { Path = "Library2.cs" } } };
        WriteProject(projDir3, projModified);

        var output1 = BuildProject(projDir1, proj);
        var output2 = BuildProject(projDir2, proj);
        var output3 = BuildProject(projDir3, projModified);
        
        Assert.That(output1.Where(x => x.Contains("CacheMiss - copying 3 files from output to cache")), Is.Not.Empty);
        Assert.That(output2.Where(x => x.Contains("CacheHit - copying 3 files from cache")), Is.Not.Empty);
        Assert.That(output3.Where(x => x.Contains("CacheMiss - copying 3 files from output to cache")), Is.Not.Empty);
        
        FileInfo DllFile(DirectoryInfo projDir, ProjectFileBuilder proj) =>
            new FileInfo(Path.Combine(projDir.FullName, "obj", "Debug", "net6.0",
                $"{Path.GetFileNameWithoutExtension(proj.Name)}.dll"));

        var dll1 = DllFile(projDir1, proj);
        var dll2 = DllFile(projDir2, proj);
        var dll3 = DllFile(projDir3, proj);

        var hash1 = MSBuild.CompilerCache.Utils.FileToSHA256String(dll1);
        var hash2 = MSBuild.CompilerCache.Utils.FileToSHA256String(dll2);
        var hash3 = MSBuild.CompilerCache.Utils.FileToSHA256String(dll3);
        
        Assert.That(hash2, Is.EqualTo(hash1));
        Assert.That(hash3, Is.Not.EqualTo(hash2));

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

    private static string[] BuildProject(DirectoryInfo dir, ProjectFileBuilder project)
    {
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", null);
        Environment.SetEnvironmentVariable("MSBuildExtensionsPath", null);
        Utils.RunProcess("dotnet", $"add package MSBuild.CompilerCache --prerelease", dir);
        return Utils.RunProcess("dotnet", $"build", dir);
    }
}