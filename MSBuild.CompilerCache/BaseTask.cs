using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace MSBuild.CompilerCache;

public record BaseTaskInputs(
    string ProjectFullPath,
    string BaseCacheDir,
    IDictionary<string, string> AllProps
)
{
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public abstract class BaseTask : Task
{
    [Required] public string ProjectFullPath { get; set; } = null!;
    [Required] public string BaseCacheDir { get; set; } = null!;
    [Required] public ITaskItem AllCompilerProperties { get; set; }

    public void SetInputs(BaseTaskInputs inputs)
    {
        ProjectFullPath = inputs.ProjectFullPath;
        BaseCacheDir = inputs.BaseCacheDir;
        AllCompilerProperties = new TaskItem("__nonexistent__", (IDictionary)inputs.AllProps);
        GetTypedAllCompilerProps(AllCompilerProperties);
    }

    protected BaseTaskInputs GatherInputs()
    {
        var typedAllCompilerProps = GetTypedAllCompilerProps(AllCompilerProperties);
        return new(
            ProjectFullPath: ProjectFullPath ??
                             throw new ArgumentException($"{nameof(ProjectFullPath)} cannot be null"),
            BaseCacheDir: BaseCacheDir ?? throw new ArgumentException($"{nameof(BaseCacheDir)} cannot be null"),
            AllProps: typedAllCompilerProps
        );
    }

    public DecomposedCompilerProps GetDecomposedProps()
    {
        var props = GetTypedAllCompilerProps(AllCompilerProperties);
        var decomposed = TargetsExtractionUtils.DecomposeCompilerProps(props);
        return decomposed;
    }
    
    public static IDictionary<string, string> GetTypedAllCompilerProps(ITaskItem allTaskItem)
    {
        var _copy = allTaskItem.CloneCustomMetadata();
        return _copy as IDictionary<string, string> ?? throw new Exception(
            $"Expected the 'All' item's metadata to be IDictionary<string, string>, but was {_copy.GetType().FullName}");
    }
}