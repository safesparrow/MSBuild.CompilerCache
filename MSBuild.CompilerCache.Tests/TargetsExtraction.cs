using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using NUnit.Framework;

namespace Tests;

public class DisposableDir : IDisposable
{
    public DisposableDir()
    {
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);
        Dir = Directory.CreateDirectory(tempFile);
    }

    public DirectoryInfo Dir { get; set; }

    public void Dispose()
    {
        Dir.Delete(recursive: true);
    }
}

public enum SupportedLanguage
{
    CSharp,
    FSharp
}

public record SDKVersion(string Version)
{
    public override string ToString() => Version;
}

[TestFixture]
public class TargetsExtraction
{
    private static readonly string ProjFile =
        """
<Project Sdk="Microsoft.NET.Sdk">
</Project>
""";

    public static string LanguageProjExtension(SupportedLanguage lang) =>
        lang == SupportedLanguage.CSharp
            ? "csproj"
            : "fsproj";

    private static string GlobalJson(SDKVersion sdk) =>
        "{\"sdk\": {\"version\": \"" + sdk + "\", \"rollForward\": \"disable\"}}";

    public void GenerateAllTargets(SDKVersion sdk, SupportedLanguage language, string outputPath)
    {
        using var dir = new DisposableDir();
        File.WriteAllText(Path.Combine(dir.Dir.FullName, "global.json"), GlobalJson(sdk));
        File.WriteAllText(Path.Combine(dir.Dir.FullName, $"project.{LanguageProjExtension(language)}"), ProjFile);
        Utils.RunProcess("dotnet", "msbuild /pp:targets.xml", dir.Dir);
        File.Copy(Path.Combine(dir.Dir.FullName, "targets.xml"), outputPath, overwrite: true);
    }
    
    [Test]
    public void Extract()
    {
        var sdks = new[]
        {
            new SDKVersion("7.0.202")
        };
        foreach (var sdk in sdks)
        {
            foreach (var lang in new[] { SupportedLanguage.CSharp, SupportedLanguage.FSharp })
            {
                var allTargetsPath = $"Targets.{sdk}.{lang}.xml";
                //GenerateAllTargets(sdk, lang, allTargetsPath);
                var coreCompilePath = $"CoreCompile.{sdk}.{lang}.targets";
                var allTargets = XDocument.Load(allTargetsPath);
                var xName = XName.Get("Target", "http://schemas.microsoft.com/developer/msbuild/2003");
                var allTargetsList = allTargets.Root.Descendants(xName);
                var coreCompileTargetNode = allTargetsList
                    .Single(n => n.Attribute("Name")?.Value == "CoreCompile");
                var root = allTargets.Root;
                var nodes = root.Nodes().ToImmutableArray();
                foreach (var e in nodes)
                {
                    if (e != coreCompileTargetNode)
                    {
                        e.Remove();
                        Console.WriteLine($"Remove node");
                    }
                }

                {
                    using var writer = XmlWriter.Create(coreCompilePath, new XmlWriterSettings { Indent = true, NewLineOnAttributes = true});
                    allTargets.Save(writer);
                }
                var xml = File.ReadAllText(coreCompilePath);
                xml = xml.Replace("&#xD;&#xA;", Environment.NewLine);
                File.WriteAllText(coreCompilePath, xml);
            }
        }

        // Prepare a project that uses a specific SDK
        // Use MSBuild's /p switch to dump all targets
        // Extract the CoreCompile target using XML queries
        // Save that target in a file
        // Decompose the target:
        // - inputs, outputs, other attributes - want to keep those intact
        // - Keep Csc/Fsc task intact, except adding a "cache miss" condition
        // - Keep the rest before/after Csc/Fsc intact
        // - Collect all inputs
        // Construct cached target
    }
}