using System.Collections.Immutable;
using Microsoft.Build.Utilities;

namespace MSBuild.CompilerCache;

public static class TargetsExtractionUtils
{
    internal static readonly Attr[] Attrs =
    {
        Unsup("AdditionalLibPaths"),
        Unsup("AddModules"),
        Unsup("AdditionalFiles"),
        Prop("AllowUnsafeBlocks"),
        // This includes auto-generated .editorconfig files that contain absolute paths
        // and breaks caching. Ignore it.
        Ignore("AnalyzerConfigFiles"),
        InputFiles("Analyzers"),
        InputFiles("ApplicationConfiguration"),
        Prop("BaseAddress"),
        Prop("CheckForOverflowUnderflow"),
        Prop("ChecksumAlgorithm"),
        Unsup("CodeAnalysisRuleSet"), // We don't track analyzers used, and attempt to disable caching when any analysis might be performed.WIP
        Prop("CodePage"),
        Prop("DebugType"), // TODO Affects whether separate PDB files are generated - but PDB path is not populated when no separate pdb file used.
        Prop("DefineConstants"),
        Prop("DelaySign"),
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
        Unsup("KeyContainer"),
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
        Ignore("ToolExe"),
        Ignore("ToolPath"),
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
        Unsup("PathMap"),
        // FSharp 7.0.202 - ones that were not in CSharp
        Prop("CompilerTools"),
        Prop("CompressMetadata"),
        Prop("DebugSymbols"),
        Ignore("DotnetFscCompilerPath"),
        InputFiles("Embed"),
        Unsup("GenerateInterfaceFile"),
        Prop("LCID"),
        Prop("NoFramework"),
        Prop("NoInterfaceData"),
        Prop("NoOptimizationData"),
        Unsup("ReferencePath"), // Not populated in default builds, but is used to populate the --lib compiler argument.  
        Prop("ReflectionFree"),
        Prop("OtherFlags"),
        //new Attr("ReferencePath", AttrType.References),
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

    private static readonly Dictionary<string, Attr> AttrsDictionary = Attrs.ToDictionary(a => a.Name);

    internal enum AttrType
    {
        SimpleProperty,
        Unsupported,
        Ignore,
        InputFiles,
        OutputFile,
        References,
        Sources,
        Unknown
    }

    internal record Attr(string Name, AttrType Type);

    private static Attr Unsup(string Name) => new Attr(Name, AttrType.Unsupported);
    private static Attr Ignore(string Name) => new Attr(Name, AttrType.Ignore);
    private static Attr Prop(string Name) => new Attr(Name, AttrType.SimpleProperty);
    private static Attr InputFiles(string Name) => new Attr(Name, AttrType.InputFiles);
    private static Attr OutputFile(string Name) => new Attr(Name, AttrType.OutputFile);
    private static string[] SplitItemList(string value) =>
        string.IsNullOrEmpty(value)
            ? Array.Empty<string>()
            : value.Split(";").Where(x => !string.IsNullOrEmpty(x)).ToArray();

    public static DecomposedCompilerProps DecomposeCompilerProps(IDictionary<string, string> props, TaskLoggingHelper? log = null)
    {
        var relevant =
            props
                .Select(kvp =>
                {
                    AttrsDictionary.TryGetValue(kvp.Key, out var attr);
                    return (Name: kvp.Key, kvp.Value, KnownAttr: attr);
                })
                .ToImmutableArray();

        var grouped = relevant.GroupBy(x => x.KnownAttr?.Type ?? AttrType.Unknown).ToDictionary(g => g.Key, g => g.ToImmutableArray());
        
        ImmutableArray<(string Name, string Value, Attr Attr)> GetGroup(AttrType attrType)
        {
            if (grouped.TryGetValue(attrType, out var items))
            {
                return items;
            }
            return ImmutableArray.Create<(string Name, string Value, Attr Attr)>();
        }

        var unknownAttributes = GetGroup(AttrType.Unknown);
        if (unknownAttributes.Length > 0)
        {
            throw new Exception($"{unknownAttributes.Length} unknown attributes found: " +
                                $"{String.Join((string?)",", (IEnumerable<string?>)unknownAttributes.Select(a => a.Name))}");
        }

        var unsupportedPropsSet =
            GetGroup(AttrType.Unsupported)
                .Where(a => !string.IsNullOrEmpty(a.Value))
                .Select(a => (a.Name, a.Value))
                .ToImmutableArray();

        var dict = relevant.ToDictionary(x => x.Name, x => x.Value);
        
        bool PropSatisfies(string name, Func<string, bool> predicate)
        {
            if (dict.TryGetValue(name, out var value))
            {
                return predicate(value);
            }
            return false;
        }

        bool CaseInsensitiveEquals(string a, string b) => a.Equals(b, StringComparison.InvariantCultureIgnoreCase);

        bool PropEmpty(string name) => !dict.ContainsKey(name) || PropSatisfies(name, string.IsNullOrEmpty);
        bool PropNotTrue(string name) => !dict.ContainsKey(name) || PropSatisfies(name, x => !CaseInsensitiveEquals(x, "true"));
        
        bool ExtraConditionsMet()
        {
            return
                PropSatisfies("TargetType", x => CaseInsensitiveEquals(x, "Library") || CaseInsensitiveEquals(x, "Exe")) &&
                PropEmpty("AdditionalLibPaths") &&
                PropNotTrue("EmitCompilerGeneratedFiles") &&
                PropNotTrue("ProvideCommandLineArgs") &&
                PropNotTrue("ReportAnalyzer") &&
                PropNotTrue("SkipCompilerExecution") &&
                PropEmpty("SourceLink")
            ;
        }

        if (!ExtraConditionsMet())
        {
            unsupportedPropsSet = unsupportedPropsSet.Append(("ExtraConditions", "Extra")).ToImmutableArray();
        }

        var regularProps =
            GetGroup(AttrType.SimpleProperty).Concat(GetGroup(AttrType.OutputFile))
                .ToDictionary(x => x.Name, x => x.Value);

        var refsItems = GetGroup(AttrType.References);
        var refs =
            refsItems.Length == 1
                ? SplitItemList(refsItems[0].Value)
                : Array.Empty<string>();

        var inputFiles =
            GetGroup(AttrType.Sources).Concat(GetGroup(AttrType.InputFiles))
                .SelectMany(x => SplitItemList(x.Value))
                .ToArray();
        
        var outputItems =
            GetGroup(AttrType.OutputFile)
                .Where(x => !string.IsNullOrEmpty(x.Value))
                .Select(x => new OutputItem(x.Name, x.Value))
                .ToArray();

        return new DecomposedCompilerProps(
            FileInputs: inputFiles,
            PropertyInputs: regularProps,
            References: refs,
            OutputsToCache: outputItems,
            UnsupportedPropsSet: unsupportedPropsSet
        );
    }
}

public record DecomposedCompilerProps(
    string[] FileInputs,
    IDictionary<string, string> PropertyInputs,
    string[] References,
    OutputItem[] OutputsToCache,
    ImmutableArray<(string Name, string Value)> UnsupportedPropsSet
);