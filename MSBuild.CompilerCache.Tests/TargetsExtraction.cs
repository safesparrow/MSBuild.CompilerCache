using System.Collections.Immutable;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using MSBuild.CompilerCache;
using NUnit.Framework;

namespace Tests;

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
[Parallelizable(ParallelScope.Children)]
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
        TestUtils.RunProcess("dotnet", "msbuild /pp:targets.xml", dir.Dir);
        var targetsPath = Path.Combine(dir.Dir.FullName, "targets.xml");
        if (File.Exists(targetsPath) == false)
        {
            throw new Exception("Targets file does not exist after generation.");
        }

        var text = File.ReadAllText(targetsPath);
        var text2 = text.Replace(Path.GetTempPath(), "%TempPath%" + Path.DirectorySeparatorChar);
        text2 = text2.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "%UserProfile%" + Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, text2);
    }

    private static readonly string[] DoNotUseCacheConditions =
    {
        "'$(EmitCompilerGeneratedFiles)' == 'true'",
    };

    internal static readonly ImmutableArray<SDKVersion> SupportedSdks = new[]
        {
            "7.0.302",
            "7.0.203",
            "7.0.202",
            "7.0.105",
            "6.0.408",
            "6.0.301",
            "6.0.300",
        }
        .Select(sdk => new SDKVersion(sdk))
        .ToImmutableArray();

    internal static readonly ImmutableArray<SupportedLanguage> SupportedLanguages = new[]
    {
        SupportedLanguage.CSharp, SupportedLanguage.FSharp
    }.ToImmutableArray();

    public static readonly ImmutableArray<(SDKVersion sdk, SupportedLanguage lang)> SdkLanguages =
        SupportedSdks.SelectMany(sdk => SupportedLanguages.Select(lang => (sdk, lang))).ToImmutableArray();

    [Explicit, Test]
    [TestCaseSource(nameof(SdkLanguages))]
    public void Extract((SDKVersion sdk, SupportedLanguage language) test)
    {
        var (sdk, lang) = test;
        var baseDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "..", "..", "..",
            "..",
            "MSBuild.CompilerCache", "Targets");
        var referenceTargetsDir = Path.Combine(baseDir, "ReferenceTargets");
        Directory.CreateDirectory(referenceTargetsDir);

        Console.WriteLine(
            $"Generating targets files for {lang}, SDK {sdk}, in base directory {baseDir} and reference directory {referenceTargetsDir}.");

        var allTargetsPath = Path.Combine(referenceTargetsDir, $"Targets.{sdk}.{lang}.xml");
        GenerateAllTargets(sdk, lang, allTargetsPath);
        var coreCompilePath = Path.Combine(referenceTargetsDir, $"CoreCompile.{sdk}.{lang}.targets");
        var cachedPath = Path.Combine(baseDir, $"Cached.CoreCompile.{sdk}.{lang}.targets");
        var allTargets = XDocument.Load(allTargetsPath);
        string nmsp = "http://schemas.microsoft.com/developer/msbuild/2003";
        XName Name(string localName) => XName.Get(localName, nmsp);
        foreach (var commentNode in allTargets.Nodes().Where(n => n.NodeType == XmlNodeType.Comment))
        {
            commentNode.Remove();
        }

        var root = allTargets.Root!;
        root.Name = Name("Project");
        var allTargetsList = allTargets.Root!.Descendants(Name("Target"));
        var coreCompileTargetNode = allTargetsList
            .Single(n => n.Attribute("Name")?.Value == "CoreCompile");

        var nodes = root.Nodes().ToImmutableArray();
        foreach (var e in nodes.Where(e => e != coreCompileTargetNode))
        {
            e.Remove();
        }

        {
            using var writer = XmlWriter.Create(coreCompilePath,
                new XmlWriterSettings { Indent = true, NewLineOnAttributes = true });
            allTargets.Save(writer);
        }
        var xml = File.ReadAllText(coreCompilePath);
        xml = xml.Replace("&#xD;&#xA;", Environment.NewLine);
        File.WriteAllText(coreCompilePath, xml);

        var compilationTaskName =
            lang == SupportedLanguage.CSharp
                ? "Csc"
                : "Fsc";
        var compilationTask = allTargets.Root.Descendants(Name(compilationTaskName)).Single();
        var condition = compilationTask.Attribute("Condition");
        if (condition == null)
        {
            throw new NotSupportedException("Expected compilation task Condition attribute to be set.");
        }

        var originalCompilationConditionValue = condition.Value;

        condition.Value = $"'$(CompilerCacheRunCompilation)' == 'true' AND {condition.Value}";

        var regularAttributes = compilationTask.Attributes()
            .Where(a => a.Name.LocalName != "Condition" && !a.IsNamespaceDeclaration)
            .ToArray();

        var itemgroup2 = Name("ItemGroup");
        var allItemGroup = new XElement(itemgroup2);
        var all = new XElement(Name("CompilerCacheAllCompilerProperties"),
            new XAttribute("Include", "___nonexistent___"));
        all.Add(regularAttributes);
        allItemGroup.Add(all);

        var relevantAttributes =
            regularAttributes
                .Select(a => (a.Name.LocalName, a.Value,
                    KnownAttr: TargetsExtractionUtils.Attrs.FirstOrDefault(x => x.Name == a.Name.LocalName)))
                .ToImmutableArray();

        var unknownAttributes =
            relevantAttributes
                .Where(x => x.KnownAttr == null)
                .ToImmutableArray();
        if (unknownAttributes.Length > 0)
        {
            throw new Exception($"{unknownAttributes.Length} unknown attributes found:" +
                                $"{string.Join(",", unknownAttributes.Select(a => a.LocalName))}");
        }

        var startComment = new XComment("START OF CACHING EXTENSION CODE");

        var doNotUseCacheCondition = DoNotUseCacheConditions
            .StringsJoin($"{Environment.NewLine}OR{Environment.NewLine}");
        var propertygroup = Name("PropertyGroup");
        var firstPropsGroupElement = new XElement(propertygroup);
        var canCacheElement =
            new XElement(Name("CanCache"), new XAttribute("Condition", doNotUseCacheCondition), "false");
        var compilerCacheCompilationWouldRunElement = new XElement(Name("CompilerCacheCompilationWouldRun"),
            new XAttribute("Condition", originalCompilationConditionValue), "true");
        firstPropsGroupElement.Add(canCacheElement, compilerCacheCompilationWouldRunElement);

        XElement Elem(string taskParameter, string? propertyName = null) =>
            new XElement(Name("Output"), new XAttribute("TaskParameter", taskParameter),
                new XAttribute("PropertyName", propertyName ?? taskParameter));

        var locateElement = new XElement(Name("CompilerCacheLocate"),
            new XAttribute("Condition", "'$(CanCache)' == 'true' AND '$(CompilerCacheCompilationWouldRun)' == 'true'"),
            new XAttribute("ConfigPath", "$(CompilerCacheConfigPath)"),
            new XAttribute("AllCompilerProperties", "@(CompilerCacheAllCompilerProperties)"),
            new XAttribute("ProjectFullPath", "$(MSBuildProjectFullPath)"),
            new XAttribute("AssemblyName", "$(AssemblyName)"),
            Elem("RunCompilation", "CompilerCacheRunCompilation"),
            Elem("PopulateCache", "CompilerCachePopulateCache"),
            Elem("Guid", "CompilerCacheGuid")
        );

        var gElement = new XElement(propertygroup,
            new XElement(Name("CompilerCacheRunCompilation"), new XAttribute("Condition", "'$(CanCache)' != 'true'"),
                "true"));
        var endComment = new XComment("END OF CACHING EXTENSION CODE");
        compilationTask.AddBeforeSelf(startComment, allItemGroup, firstPropsGroupElement, locateElement,
            gElement, endComment);

        var populateCacheElement =
            new XElement(Name("CompilerCachePopulateCache"),
                new XAttribute("Condition", "'$(CompilerCachePopulateCache)' == 'true'"),
                new XAttribute("Guid", "$(CompilerCacheGuid)"),
                new XAttribute("CompilationSucceeded", "$(MSBuildLastTaskResult)")
            );

        compilationTask.AddAfterSelf(startComment, populateCacheElement, endComment);

        var p = compilationTask.Attribute("PathMap");
        p.Value = "$(MSBuildProjectDirectory)=/__nonexistent__directory__";

        {
            using var writer = XmlWriter.Create(cachedPath,
                new XmlWriterSettings { Indent = true, NewLineOnAttributes = true });
            allTargets.Save(writer);
        }

        xml = File.ReadAllText(cachedPath);
        xml = xml.Replace("&#xD;&#xA;", Environment.NewLine);
        xml = xml.Replace("xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"", "");
        File.WriteAllText(cachedPath, xml);
    }
}