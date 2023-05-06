using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace MSBuild.CompilerCache;

public record BaseTaskInputs(
    string ProjectFullPath,
    string PropertyInputs,
    string[] FileInputs,
    string[] References,
    ITaskItem[] RawOutputsToCache,
    string BaseCacheDir
)
{
    public OutputItem[] OutputsToCache => RawOutputsToCache.Select(ParseOutputToCache).ToArray();
    
    private static OutputItem ParseOutputToCache(ITaskItem arg) =>
        new(
            Name: arg.GetMetadata("Name") ?? throw new ArgumentException($"The '{arg.ItemSpec}' item has no 'Name' metadata."),
            LocalPath: arg.ItemSpec
        );
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public abstract class BaseTask : Task
{
    [Required] public string PropertyInputs { get; set; } = null!;
    [Required] public string[] FileInputs { get; set; } = null!;
    [Required] public string[] References { get; set; } = null!;
    [Required] public ITaskItem[] OutputsToCache { get; set; } = null!;
    [Required] public string ProjectFullPath { get; set; } = null!;
    [Required] public string BaseCacheDir { get; set; } = null!;

    public void SetInputs(BaseTaskInputs inputs)
    {
        PropertyInputs = inputs.PropertyInputs;
        FileInputs = inputs.FileInputs;
        References = inputs.References;
        OutputsToCache = inputs.RawOutputsToCache;
        ProjectFullPath = inputs.ProjectFullPath;
    }

    protected BaseTaskInputs GatherInputs() =>
        new(
            ProjectFullPath: ProjectFullPath ?? throw new ArgumentException($"{ProjectFullPath} cannot be null"),
            PropertyInputs: PropertyInputs ?? throw new ArgumentException($"{PropertyInputs} cannot be null"),
            FileInputs: (FileInputs ?? throw new ArgumentException($"{FileInputs} cannot be null")).OrderBy(x => x)
            .ToArray(),
            References: (References ?? throw new ArgumentException($"{References} cannot be null")).OrderBy(x => x)
            .ToArray(),
            RawOutputsToCache: OutputsToCache ?? throw new ArgumentException($"{OutputsToCache} cannot be null"),
            BaseCacheDir: BaseCacheDir ?? throw new ArgumentException($"{BaseCacheDir} cannot be null")
        );
}