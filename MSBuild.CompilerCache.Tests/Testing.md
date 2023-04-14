# Testing approach
This document discusses how the project could be tested.
We first outline the specification and try to infer the right testing approaches based on that.

## High-level specification
The compilation cache modifies the `CoreCompile` target for C# and F# projects.
Any observable effect of using the cache should be limited to the effects that target has.

However, it is worth keeping in mind that users do not typically interact with that target directly, and instead operates on a higher level invoking various build-related commands (eg. `dotnet build`).

With regards to the `CoreCompile` target, the goal of the cache is as follows:
1. The visible effects of using a cached `CoreCompile` target should be identical to when using the non-cached version.
2. When there is a cache hit, `CoreCompile` should be faster.
3. When there is a cache miss, `CoreCompile` should not be significantly slower.

### What is considered "identical" effects for the `CoreCompile` target?
The following aspects are expected to be identical to when not using the cache:
- All compilation output files have binary-identical contents.
- The file ordering based on file modification timestamps is the same. This guarantees that subsequent MSBuild targets based on comparing input and output file timestamps behave the same.
- Specifically, if a file timestamp is not updated when not using the cache, it is not updated when using it.

Using the cache is expected to differ in the following ways:
- When there is a cache hit, the compilation task (`Csc` or `Fsc`) is not executed.
- When there is a cache hit, extra copy operations are performed to provide the cached results.
- When there is a cache miss, extra copy operations are performed to populate the cache.

## Testing the `CoreCompile` target vs testing top-level build operations.
As the cache only modifies a single MSBuild target, we could write tests that operate solely on that target.
While some tests might work that way, we might also perform tests on a higher level, because:
1. The decision about whether the custom cached `CoreCompile` target can be introduced in the first place needs to be tested as well.
2. The `CoreCompile` target can in some ways be treated as an implementation detail of the build process. This is how most users view it. There are exceptions, like users with non-standard build processes or IDEs invoking the `CoreCompile` target directly.
3. It might be easier and more intuitive to perform those higher-level tests by invoking `dotnet` commands as opposed to invoking the `CoreCompile` target directly.

We will probably end up with some `dotnet build`-based tests and some tests that work with the `CoreCompile` target directly.

## Capabilities required for high-level testing
To perform relevant high-level tests, we will need the following capabilities:
- Test different SDKs.
- Run various `dotnet` commands (by spawning the `dotnet` process).
- Generate MSBuild binary log and inspect it - to inspect values of certain properties, like `CanCache`.
- Have a set of C# and F# sample projects.
- Ability to copy a project to another directory, then build it in both directories. 
- Compare results of build operations in two directories.
- Update timestamps of all input files.
- Provide input dlls that are different but have the same public API 

## Case: private changes to a project, incremental build, cache hit.
1. A C# project was built previously.
2. A change was made to implementation of a method.

Without the cache, the following will happen:
1. Project will be compiled again.
2. The main dll will be overwritten. The reference dll will NOT be overwritten, as its contents will not have changed. Its timestamp will not change.
3. Subsequent targets that use the reference assembly as input will not have to rerun as the timestamp hasn't changed.
4. Subsequent targets that use the full assembly as input (eg. copy) will have to rerun.
5. Targets in dependent projects that use the reference assembly (eg. compilation) will not have to rerun.
6. Targets in dependent projects that use the full assembly (eg. copy) will have to rerun.

We need to make sure that when the cache is used, the same happens. 