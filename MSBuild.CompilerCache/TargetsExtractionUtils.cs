using System.Collections.Immutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace MSBuild.CompilerCache;

public static class TargetsExtractionUtils
{
    public static readonly Attr[] Attrs =
    {
        Unsup("AdditionalLibPaths"),
        Unsup("AddModules"),
        Unsup("AdditionalFiles"),
        Prop("AllowUnsafeBlocks"),
        Ignore("AnalyzerConfigFiles"), // TODO
        Ignore("Analyzers"), // TODO
        InputFiles("ApplicationConfiguration"),
        Prop("BaseAddress"),
        Prop("CheckForOverflowUnderflow"),
        Prop("ChecksumAlgorithm"),
        Unsup("CodeAnalysisRuleSet"), // We don't track analyzers used, and attempt to disable caching when any analysis might be performed.WIP
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
        Unsup("PathMap"),
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
        Unsup("ReferencePath"), // Not populated in default builds, but is used to populate the --lib compiler argument.  
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

    public enum AttrType
    {
        SimpleProperty,
        Unsupported,
        Ignore,
        InputFiles,
        OutputFile,
        References,
        Sources
    }

    public record Attr(string Name, AttrType Type);

    public static Attr Unsup(string Name) => new Attr(Name, AttrType.Unsupported);
    public static Attr Ignore(string Name) => new Attr(Name, AttrType.Ignore);
    public static Attr Prop(string Name) => new Attr(Name, AttrType.SimpleProperty);
    public static Attr InputFiles(string Name) => new Attr(Name, AttrType.InputFiles);
    public static Attr OutputFile(string Name) => new Attr(Name, AttrType.OutputFile);
    public static string[] SplitItemList(string value) =>
        string.IsNullOrEmpty(value)
            ? Array.Empty<string>()
            : value.Split(";").Where(x => !string.IsNullOrEmpty(x)).ToArray();

    public static DecomposedCompilerProps DecomposeCompilerProps(IDictionary<string, string> props, TaskLoggingHelper? log = null)
    {
        var relevant =
            props
                .Select(kvp => (Name: kvp.Key, kvp.Value,
                    KnownAttr: Attrs.FirstOrDefault(x => x.Name == kvp.Key)))
                .ToImmutableArray();

        var unknownAttributes =
            relevant
                .Where(x => x.KnownAttr == null)
                .ToImmutableArray();
        if (unknownAttributes.Length > 0)
        {
            throw new Exception($"{unknownAttributes.Length} unknown attributes found: " +
                                $"{String.Join((string?)",", (IEnumerable<string?>)unknownAttributes.Select(a => a.Name))}");
        }

        relevant = relevant
            .Where(x => x.KnownAttr != null)
            .ToImmutableArray();

        var mustBeEmptyAttrs =
            relevant
                .Where(a => a.KnownAttr!.Type == AttrType.Unsupported)
                .Select(a => (a.Name, a.Value))
                .ToImmutableArray();
        var unsupportedPropsSet = mustBeEmptyAttrs.Where(x => !string.IsNullOrEmpty(x.Value)).ToArray();

        var dict = relevant.ToDictionary(x => x.Name, x => x.Value);
        
        bool PropSatisfies(string name, Func<string, bool> predicate) => predicate(dict[name]);
        bool CaseInsensitiveEquals(string a, string b) => a.Equals(b, StringComparison.InvariantCultureIgnoreCase);

        bool PropEmpty(string name) => !dict.ContainsKey(name) || PropSatisfies(name, string.IsNullOrEmpty);
        bool PropNotTrue(string name) => !dict.ContainsKey(name) || PropSatisfies(name, x => !CaseInsensitiveEquals(x, "true"));
        
        bool ExtraSupportCheck()
        {
            return
                dict.ContainsKey("TargetType") && PropSatisfies("TargetType", x => CaseInsensitiveEquals(x, "Library") || CaseInsensitiveEquals(x, "Exe")) &&
                PropEmpty("AdditionalLibPaths") &&
                PropNotTrue("EmitCompilerGeneratedFiles") &&
                PropNotTrue("ProvideCommandLineArgs") &&
                PropNotTrue("ReportAnalyzer") &&
                PropNotTrue("SkipCompilerExecution") &&
                PropEmpty("SourceLink") &&
                PropNotTrue("PublicSign")
            ;
        }

        var extraConditionsMet = ExtraSupportCheck();
        if (!extraConditionsMet)
        {
            unsupportedPropsSet = unsupportedPropsSet.Append(("ExtraConditions", "Extra")).ToArray();
        }

        var regularProps =
            relevant
                .Where(x => new[] { AttrType.SimpleProperty, AttrType.OutputFile }.Contains(x.KnownAttr!.Type))
                .ToDictionary(x => x.Name);

        var refsItems = relevant.Where(x => x.KnownAttr!.Type == AttrType.References).ToArray();
        var refs =
            refsItems.Length == 1
                ? SplitItemList(refsItems[0].Value)
                : Array.Empty<string>();

        var inputMed = relevant
            .Where(x => new[] { AttrType.Sources, AttrType.InputFiles }.Contains(x.KnownAttr!.Type))
            .ToArray();
        log?.LogMessage(MessageImportance.High, "Before");
        foreach (var med in inputMed)
        {
            log?.LogMessage(MessageImportance.High, $"{med.Name}={med.Value}");
        }
        log?.LogMessage(MessageImportance.High, "After");
        var inputFiles =
            inputMed
                .SelectMany(x => SplitItemList(x.Value))
                .ToArray();
        
        var outputItems =
            relevant
                .Where(x => x.KnownAttr!.Type == AttrType.OutputFile)
                .Where(x => !string.IsNullOrEmpty(x.Value))
                .Select(x => new OutputItem(x.Name, x.Value))
                .ToArray();

        return new DecomposedCompilerProps(
            FileInputs: inputFiles,
            PropertyInputs: regularProps.ToDictionary(x => x.Key, x => x.Value.Value),
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
    (string Name, string Value)[] UnsupportedPropsSet
);