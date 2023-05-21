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
    <PackageReference Include="MSBuild.CompilerCache" Version="0.7.4" PrivateAssets="all" />
</ItemGroup>
```
and create a config file like the one below:

```json
{
  "BaseCacheDir": "c:/compilation_cache"
}
```

The above code does two things:
1. Adds the `MSBuild.CompilerCache` package, which provides custom Task definitions and ships .targets files.
2. Sets the `CompilationCacheConfigPath` variable, which is used to read the config file.

## Local cache vs remote cache
The location set in `BaseCacheDir` can be either a local path or a network share - the caching logic behaves exactly the same.

This means that you could share the cache between multiple people.
If you do, note that in many scenarios this is not secure enough, as it means that users can inject malicious dll files into a shared place, to be used by other users.

## Outstanding issues
- Limited debugging ability - Dlls copied from the cache contain symbols with absolute paths to the source files from original compilation. This means that debugging the cached dlls will not work with source files in a different directory. This is a current limitation and should be fixed in upcoming versions.
- Currently only selected .NET SDKs are supported. 
- Some of the less common compilation inputs are currently ignored. This is easy to fix, but can currently lead to incorrectly reusing cached results when one of those inputs changes.
- The cache mechanism is currently not safe for running multiple builds in parallel. If the same project is being built in two workspaces at the same time, there might be some undefined behaviour.

## Supported SDKs
Below is the list of all supported .NET SDKs:
- 6.0.300
- 7.0.202

If your project is not using one of those versions, caching will be disabled.
Note that the SDK you use must match one of the supported ones precisely.
This is because the `CoreCompile` targets are created by first copy-pasting the original targets from the SDK and then adding caching logic - so a new SDK requires a new copy.

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
