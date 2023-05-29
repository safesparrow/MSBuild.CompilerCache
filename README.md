# MSBuild.CompilerCache

[![NuGet version](https://img.shields.io/nuget/v/MSBuild.CompilerCache.svg)](https://www.nuget.org/packages/MSBuild.CompilerCache/)

## What is `MSBuild.CompilerCache`?
`MSBuild.CompilerCache` is a NuGet package that provides machine-wide or distributed caching of C# and F# project compilation.

It extends the `CoreCompile` targets from the .NET SDK with caching steps and uses custom MSBuild tasks that perform the actual caching.

Caching works in commandline builds as well as in the IDE.

## How does it work?
1. Before `Csc` or `Fsc` task is invoked, we calculate a hash of all the relevant compilation inputs.
2. If a cache entry exists with that hash, we simply copy the files and skip compilation.
3. If the file does not exist, we run compilation and then populate the cache.

## How can I use it?
> :warning: The project is in an experimental phase. It is known to have issues, like limited debugging ability and potential incorrect cache hits. Please keep that in mind before using.

To use the cache, add the following to your project file (or `Directory.Build.props` in your directory structure):
```xml
<PropertyGroup>
    <CompilationCacheConfigPath>c:/accessible/filesystem/location/compilation_cache_config.json</CompilationCacheConfigPath>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="MSBuild.CompilerCache" PrivateAssets="all" />
</ItemGroup>
```
and create a config file like the one below:

```json
{
  "CacheDir": "c:/compilation_cache"
}
```

The above code does two things:
1. Adds the `MSBuild.CompilerCache` package, which provides custom Task definitions and ships .targets files.
2. Sets the `CompilationCacheConfigPath` variable, which is used to read the config file.

## Local cache vs remote cache
The location set in `CacheDir` can be either a local path or a network share - the caching logic behaves exactly the same.

This means that you could share the cache between multiple people.
If you do, note that in many scenarios poses a security risk, as it means that bad actors can inject malicious dll files into a shared place, to be used by other users.

## Using Reference assemblies for hash generation and ignoring `internal` members
When generating a hash of all compilation inputs,
we treat assembly references differently.

For each referenced dll we produce two reference assemblies and calculate their hashes:
- `PublicAndInternal` - contains public and internal symbols (but not their implementation)
- `Public` - same but with internal symbols removed.

Reference assemblies are generated using [JetBrains Refasmer](https://github.com/JetBrains/Refasmer/)

When compiling assembly A that references B, we check if B's `InternalsVisibleTo` lists `A` and choose either `PublicAndInternal` or `Public` hash accordingly.

Using reference assemblies' hash allows to reuse compilation results if the only changes happen in non-visible parts of the referenced assembly.

Note that compilation still uses the original referenced dlls, and the generated ref assemblies are only used for hash calculation.

### Disabling reference assembly generation
This mechanism can be disabled using the following config entry:
```json
{
  ...
  "RefTrimming":
  {
    "Enabled": false
  }
}
```
### Location of the reference assembly cache
When generating reference assemblies, we only store their calculated hash in a separate "refCache" location.

This location defaults to "%BaseCacheDir/.refcache" subdirectory and can be overriden using the "RefTrimming.RefCacheDir" config entry

## Outstanding issues
### Limited debugging ability
When debugging binaries produced when using the cache,
we use PathMap compilation option to map all source paths to a nonexistent directory.

We then depend on the IDE to figure out the correct source location.

This is known to work in JetBrains Rider.
Other IDEs might require manual action to point the debugger to the sources.

### Only Selected SDKs supported
Below is the list of all supported .NET SDKs:
```
7.0.302
7.0.203
7.0.202
7.0.105
6.0.408
6.0.300
```

If your project is not using one of those versions, caching will be disabled.

Note that the SDK you use must match one of the supported ones precisely.
This is because the `CoreCompile` targets are created by first copy-pasting the original targets from the SDK and then adding caching logic - so a new SDK requires a new copy.

### Some of the less common compilation inputs are currently ignored
This can currently lead to incorrectly reusing cached results when one of those inputs changes.

This currently includes the following properties:
```
AnalyzerConfigFiles
Analyzers
ToolExe
ToolPath
DotnetFscCompilerPath
```

See [TargetsExtractionUtils.cs](https://github.com/safesparrow/MSBuild.CompilerCache/blob/main/MSBuild.CompilerCache/TargetsExtractionUtils.cs#L9) for how every compilation input is handled.

## Reporting issues
The project is in a very early stage. You are free to try the tool, but it is expected to cause _some_ issues in some cases.
If you think you found an issue that isn't covered in the "Outstanding issues" list above, please raise it on GitHub.

## Contributing
You are most welcome to contribute to the project here on GitHub.

Please raise an issue if you would like to:
- share an idea
- report a bug
- ask a question
- make implementation changes
