# What is this?
`MSBuild.CompilerCache` is a package that provides machine-wide or distributed caching of C# and F# project compilation.

It extends the `CoreCompile` targets from the .NET SDK with caching steps and uses custom MSBuild tasks that perform the actual caching.

# How does it work?
1. Before `Csc` or `Fsc` task is invoked, we calculate a hash of all the relevant compilation inputs.
2. If a cache entry exists with that hash, we simply copy the files and skip compilation.
3. If the file does not exist, we run compilation and then populate the cache.

# How can I use it?
> :warning: The project is in an experimental phase. It is known to have issues, like limited debugging ability and potential incorrect cache hits. Please keep that in mind before using.

To use the cache, add the following to your project file (or `Directory.Build.props` in your directory structure):
```xml
<PropertyGroup>
    <CompilationCacheConfigPath>c:/accessible/filesystem/location/compilation_cache_config.json</CompilationCacheConfigPath>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="MSBuild.CompilerCache" Version="0.10.6" PrivateAssets="all" />
</ItemGroup>
```
and create a config file like the one below:

```json
{
  "CacheDir": "c:/compilation_cache"
}
```
