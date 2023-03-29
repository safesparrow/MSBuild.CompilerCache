# Summary
This directory contains sample projects that use the compiler cache.
The reference to the MSBuild.CompilerCache package and cache base directory are added in Directory.Build.props.

# How to test it
You can run the following script in this directory, that demonstrates cache is used when compiling the same project in the same directory:
```powershell
cd CSharp1
dotnet build # Populates the cache - should output "CacheMiss - copying 2 files from output to cache"
dotnet clean
dotnet build # Uses the cache - should output "CacheHit - copying 2 files from cache"
```

You can also run the following script which demonstrates that the cache is used when compiling an identical project in a different directory:
```powershell
cd CSharp1
dotnet clean ; dotnet build # Populates the cache 
cd ../CSharp2
dotnet clean ; dotnet build # Uses the cache 
```

Same test can be done with the `FSharp.1` and `FSharp.2` projects.