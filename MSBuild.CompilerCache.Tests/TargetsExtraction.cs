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
        var targetsPath = Path.Combine(dir.Dir.FullName, "targets.xml");
        if (File.Exists(targetsPath) == false)
        {
            throw new Exception("FOO");
        }
        File.Copy(targetsPath, outputPath, overwrite: true);
    }

    private static readonly string[] UseCacheConditions =
    {
        "('$(OutputType)' == 'Library' OR '$(OutputType)' == 'Exe')",
        "'$(AdditionalLibPaths)' == ''",
        "'@(AddModules)' == ''",
        "'$(EmitCompilerGeneratedFiles)' != 'true'",
        "'$(ProvideCommandLineArgs)' != 'true'",
        "'$(ReportAnalyzer)' != 'true'",
        "'$(SkipCompilerExecution)' != 'true'",
        "'$(SourceLink)' == ''",
        "'$(PublicSign)' != 'true'"
    };

    public enum AttrType
    {
        SimpleProperty,
        Unsupported,
        InputFiles,
        OutputFile,
        References,
        Sources,
        Ignore,
        PathMap
    }

    public record Attr(string Name, AttrType Type);

    public static Attr Unsup(string Name) => new Attr(Name, AttrType.Unsupported);
    public static Attr Prop(string Name) => new Attr(Name, AttrType.SimpleProperty);
    public static Attr InputFiles(string Name) => new Attr(Name, AttrType.InputFiles);
    public static Attr OutputFile(string Name) => new Attr(Name, AttrType.OutputFile);
    public static Attr Ignore(string Name) => new Attr(Name, AttrType.Ignore);

    public static readonly Attr[] Attrs =
    {
        Unsup("AdditionalLibPaths"),
        Unsup("AddModules"),
        Unsup("AdditionalFiles"),
        Prop("AllowUnsafeBlocks"),
        Unsup("AnalyzerConfigFiles"),
        Unsup("Analyzers"),
        InputFiles("ApplicationConfiguration"),
        Prop("BaseAddress"),
        Prop("CheckForOverflowUnderflow"),
        Prop("ChecksumAlgorithm"),
        Prop("CodeAnalysisRuleSet"),
        Prop("CodePage"),
        Prop("DebugType"), // TODO Affects whether separate PDB files are generated - but PDB path is not populated when no separate pdb file used.
        Prop("DefineConstants"),
        Unsup("DelaySign"),
        Prop("Deterministic"),
        Prop("DisabledWarnings"),
        Prop("DisableSdkPath"),
        OutputFile("DocumentationFile"),
        Prop("EmbedAllSources"),
        InputFiles("EmbeddedFiles"),
        Prop("EmitDebugInformation"),
        Prop("EnvironmentVariables"),
        Prop("ErrorEndLocation"),
        Unsup("ErrorLog"),
        Prop("ErrorReport"),
        Prop("Features"),
        Prop("FileAlignment"),
        Unsup("GeneratedFilesOutputPath"),
        Prop("GenerateFullPaths"),
        Prop("HighEntropyVA"),
        Prop("Instrument"),
        Prop("KeyContainer"),
        InputFiles("KeyFile"),
        Prop("LangVersion"),
        Unsup("LinkResources"),
        Prop("MainEntryPoint"),
        Unsup("ModuleAssemblyName"),
        Prop("NoConfig"),
        Prop("NoLogo"),
        Prop("NoStandardLib"),
        Prop("NoWin32Manifest"),
        Prop("Nullable"),
        Prop("Optimize"),
        OutputFile("OutputAssembly"),
        OutputFile("OutputRefAssembly"),
        OutputFile("PdbFile"),
        Prop("Platform"),
        Prop("Prefer32Bit"),
        Prop("PreferredUILang"),
        Prop("ProvideCommandLineArgs"),
        Prop("PublicSign"),
        new Attr("References", AttrType.References),
        Prop("RefOnly"),
        Prop("ReportAnalyzer"),
        InputFiles("Resources"),
        Unsup("ResponseFiles"),
        Prop("RuntimeMetadataVersion"),
        Unsup("SharedCompilationId"),
        Prop("SkipAnalyzers"),
        Prop("SkipCompilerExecution"),
        Prop("SourceLink"),
        new Attr("Sources", AttrType.Sources),
        Prop("SubsystemVersion"),
        Prop("TargetType"),
        Unsup("ToolExe"),
        Unsup("ToolPath"),
        Prop("TreatWarningsAsErrors"),
        Prop("UseHostCompilerIfAvailable"),
        Prop("UseSharedCompilation"),
        Prop("Utf8Output"),
        Unsup("VsSessionGuid"),
        Prop("WarningLevel"),
        Prop("WarningsAsErrors"),
        Prop("WarningsNotAsErrors"),
        InputFiles("Win32Icon"),
        InputFiles("Win32Manifest"),
        InputFiles("Win32Resource"),
        new Attr("PathMap", AttrType.PathMap),
        // FSharp 7.0.202 - ones that were not in CSharp
        Prop("CompilerTools"),
        Prop("CompressMetadata"),
        Prop("DebugSymbols"),
        Unsup("DotnetFscCompilerPath"),
        InputFiles("Embed"),
        Unsup("GenerateInterfaceFile"),
        Prop("LCID"),
        Prop("NoFramework"),
        Prop("NoInterfaceData"),
        Prop("NoOptimizationData"),
        Prop("ReflectionFree"),
        Prop("OtherFlags"),
        new Attr("ReferencePath", AttrType.References),
        Prop("Tailcalls"),
        Prop("TargetProfile"),
        Prop("UseStandardResourceNames"),
        Unsup("VersionFile"), // ??
        Prop("VisualStudioStyleErrors"),
        Prop("WarnOn"),
        InputFiles("Win32IconFile"),
        InputFiles("Win32ManifestFile"),
        InputFiles("Win32ResourceFile")
    };

    [Test]
    public void Extract()
    {
        var sdks = new[]
        {
            new SDKVersion("7.0.202"),
            new SDKVersion("6.0.300")
        };
        foreach (var sdk in sdks)
        {
            foreach (var lang in new[] { SupportedLanguage.CSharp, SupportedLanguage.FSharp })
            {
                var allTargetsPath = $"Targets.{sdk}.{lang}.xml";
                GenerateAllTargets(sdk, lang, allTargetsPath);
                var coreCompilePath = $"CoreCompile.{sdk}.{lang}.targets";
                var cachedPath = $"Cached.CoreCompile.{sdk}.{lang}.targets";
                var allTargets = XDocument.Load(allTargetsPath);
                string nmsp = "http://schemas.microsoft.com/developer/msbuild/2003";
                XName Name(string localName) => XName.Get(localName, nmsp);
                var allTargetsList = allTargets.Root!.Descendants(Name("Target"));
                var coreCompileTargetNode = allTargetsList
                    .Single(n => n.Attribute("Name")?.Value == "CoreCompile");
                var root = allTargets.Root;
                var nodes = root.Nodes().ToImmutableArray();
                foreach (var e in nodes)
                {
                    if (e != coreCompileTargetNode)
                    {
                        e.Remove();
                    }
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

                condition.Value = $"'$(DoInvokeCompilation)' == 'true' AND {condition.Value}";

                var relevantAttributes =
                    compilationTask.Attributes()
                        .Where(a => a.Name.LocalName != "Condition" && !a.IsNamespaceDeclaration)
                        .Select(a => (a.Name.LocalName, a.Value,
                            KnownAttr: Attrs.FirstOrDefault(x => x.Name == a.Name.LocalName)))
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

                relevantAttributes = relevantAttributes
                    .Where(x => x.KnownAttr != null)
                    .ToImmutableArray();

                var startComment = new XComment("START OF CACHING EXTENSION CODE");
                compilationTask.AddBeforeSelf(startComment);

                var extraCanCacheConditions =
                    relevantAttributes
                        .Where(a => a.KnownAttr!.Type == AttrType.Unsupported)
                        .Select(a => $"'{a.Value}' == ''")
                        .ToImmutableArray();

                var fullCanCacheCondition = UseCacheConditions.Union(extraCanCacheConditions).StringsJoin($"{Environment.NewLine}AND{Environment.NewLine}");
                var propertygroup = Name("PropertyGroup");
                var firstPropsGroupElement = new XElement(propertygroup);
                var canCacheElement =
                    new XElement(Name("CanCache"), new XAttribute("Condition", fullCanCacheCondition), "true");
                firstPropsGroupElement.Add(canCacheElement);
                compilationTask.AddBeforeSelf(firstPropsGroupElement);

                var propsString =
                    relevantAttributes
                        .Where(x => new[] { AttrType.SimpleProperty, AttrType.OutputFile }.Contains(x.KnownAttr!.Type))
                        .Select(x => $"{x.LocalName}={x.Value}")
                        .StringsJoin(";");

                var canCacheCondition = new XAttribute("Condition", "'$(CanCache)' == 'true'");
                var propsGroupElement = new XElement(propertygroup, canCacheCondition);
                var propsElement = new XElement(Name("PropertyInputs"), propsString);
                propsGroupElement.Add(propsElement);
                compilationTask.AddBeforeSelf(propsGroupElement);

                var itemgroup = Name("ItemGroup");
                var itemGroupElement = new XElement(itemgroup, canCacheCondition);
                var inputFiles =
                    relevantAttributes
                        .Where(x => x.KnownAttr!.Type == AttrType.InputFiles)
                        .ToImmutableArray();
                // Add metadata with item names, similar to property names above, to avoid hash clashes.
                var inputFileItems =
                    inputFiles
                        .Select(i => new XElement(Name("FileInputs"), new XAttribute("Include", i.Value)))
                        .ToImmutableArray();
                itemGroupElement.Add(inputFileItems);
                compilationTask.AddBeforeSelf(itemGroupElement);

                var locateElement = new XElement(Name("LocateCompilationCacheEntry"),
                    canCacheCondition,
                    new XAttribute("FileInputs", "@(FileInputs)"),
                    new XAttribute("PropertyInputs", "@(PropertyInputs)"),
                    new XAttribute("BaseCacheDir", "$(CompilationCacheBaseDir)"),
                    new XElement(Name("Output"), new XAttribute("TaskParameter", "CacheDir"),
                        new XAttribute("PropertyName", "CacheDir")),
                    new XElement(Name("Output"), new XAttribute("TaskParameter", "CacheHit"),
                        new XAttribute("PropertyName", "CacheHit"))
                );
                compilationTask.AddBeforeSelf(locateElement);

                var gElement = new XElement(propertygroup,
                    new XElement(Name("DoInvokeCompilation"),
                        new XAttribute("Condition",
                            "'$(CacheHit)' != 'true' OR '$(CompileAndCheckAgainstCache)' == 'true'"), "true"));
                compilationTask.AddBeforeSelf(gElement);
                var endComment = new XComment("END OF CACHING EXTENSION CODE");
                compilationTask.AddBeforeSelf(endComment);

                var outputsGroup = new XElement(itemgroup, canCacheCondition);
                var outputItems =
                    relevantAttributes
                        .Where(x => x.KnownAttr!.Type == AttrType.OutputFile)
                        .Select(x => new XElement(Name("CompileOutputsToCache"), new XAttribute("Include", x.Value)))
                        .ToImmutableArray();
                outputsGroup.Add(outputItems);

                var useOrPopulateCacheElement =
                    new XElement(Name("UseOrPopulateCache"),
                        canCacheCondition,
                        new XAttribute("IntermediateOutputPath", "$(IntermediateOutputPath)"),
                        new XAttribute("OutputsToCache", "@(CompileOutputsToCache)"),
                        new XAttribute("CheckCompileOutputAgainstCache", "$(CompileAndCheckAgainstCache)"),
                        new XAttribute("CacheHit", "$(CacheHit)"),
                        new XAttribute("CacheDir", "$(CacheDir)")
                    );

                compilationTask.AddAfterSelf(startComment, outputsGroup, useOrPopulateCacheElement, endComment);

                {
                    using var writer = XmlWriter.Create(cachedPath,
                        new XmlWriterSettings { Indent = true, NewLineOnAttributes = true });
                    allTargets.Save(writer);
                }
                xml = File.ReadAllText(cachedPath);
                xml = xml.Replace("&#xD;&#xA;", Environment.NewLine);
                File.WriteAllText(cachedPath, xml);
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