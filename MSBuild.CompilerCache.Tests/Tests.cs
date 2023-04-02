using System.IO.Abstractions.TestingHelpers;
using MSBuild.CompilerCache;

namespace Tests;

using NUnit.Framework;

[TestFixture]
public class Tests
{
    [Test]
    public void DummyTest()
    {
        var fs = new MockFileSystem();
        var task = new LocateCompilationCacheEntry();
        task.Execute(fs);
        Assert.Pass();
    }
}