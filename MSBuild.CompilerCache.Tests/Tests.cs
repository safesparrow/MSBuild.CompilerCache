using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Abstractions;
using MSBuild.CompilerCache.Trimming;

namespace Tests;

using NUnit.Framework;

public sealed class BuildEnvironment : IDisposable
{
    public DirectoryInfo Dir { get; set; }

    public BuildEnvironment()
    {
        Dir = CreateTempDir();
    }

    public static DirectoryInfo CreateTempDir()
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
    [Test]
    public void DummyTest()
    {
        using var env = new BuildEnvironment();
        var cache = new DirectoryInfo(Path.Combine(env.Dir.FullName, ".cache"));
        var projDir1 = new DirectoryInfo(Path.Combine(env.Dir.FullName, "1"));
        var projDir2 = new DirectoryInfo(Path.Combine(env.Dir.FullName, "2"));
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
        WriteProject(projDir2, proj);
        
        
        
        BuildProject(projDir1, proj);
        BuildProject(projDir2, proj);

        FileInfo DllFile(DirectoryInfo projDir, ProjectFileBuilder proj) =>
            new FileInfo(Path.Combine(projDir.FullName, "obj", "debug", "net6.0", $"{Path.GetFileNameWithoutExtension(proj.Name)}.dll"));

        var dll1 = DllFile(projDir1, proj);
        var dll2 = DllFile(projDir2, proj);
        
        Assert.That(dll1.LastWriteTime, Is.EqualTo(dll2.LastWriteTime));
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

    private static bool RunProcess(string name, string args, DirectoryInfo workingDir)
    {
        var pi = new ProcessStartInfo(name, args)
        {
            WorkingDirectory = workingDir.FullName,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        Console.WriteLine($"{name} {args}    in    {workingDir.FullName}");
        var p = Process.Start(pi);
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static void BuildProject(DirectoryInfo dir, ProjectFileBuilder project)
    {
        if (!RunProcess("dotnet.exe", "build", dir))
        {
            throw new Exception($"Failed to build project in {dir.FullName}");
        }
    }

    [Test]
    public void TrimTest()
    {
        var original =
            @"C:\projekty\fsharp\fsharp_main\artifacts\bin\FSharp.Compiler.Service\Debug\netstandard2.0\FSharp.Compiler.Service.dll";
        var remapped =
            @"C:\projekty\fsharp\fsharp_main\artifacts\bin\FSharp.Compiler.Service\Debug\netstandard2.0\FSharp.Compiler.Service.remapped.dll";
        var originalPath = @"C:\projekty\fsharp\fsharp_main\";
        var mappedPath = @"C:\projekty\fsharp\fsharp_main2\";
        
        // var remapped = @"C:\projekty\MSBuild.CompilerCache\MSBuild.CompilerCache\bin\Debug\net6.0\MSBuild.CompilerCache.remapped.dll";
        // var original = @"C:\projekty\MSBuild.CompilerCache\MSBuild.CompilerCache\bin\Debug\net6.0\MSBuild.CompilerCache.dll";
        // var originalPath = @"C:\projekty\MSBuild.CompilerCache\MSBuild.CompilerCache\";
        // var mappedPath = @"C:\projekty\MSBuild.CompilerCache\MSBuild.CompilerCache2\";
        var trimming = new Trimming();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1; i++)
        {
            trimming.RemapDllSymbolPaths(original,
                remapped,
                originalPath, mappedPath);
            Console.WriteLine($"[{i}] {sw.ElapsedMilliseconds}ms");
        }
        trimming.RemapDllSymbolPaths(remapped, remapped.Replace(".remapped.", ".reback."), mappedPath, originalPath);
    }
}