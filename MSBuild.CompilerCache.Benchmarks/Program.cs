﻿using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using MSBuild.CompilerCache;

namespace Tests;

[SimpleJob(RunStrategy.ColdStart, launchCount: 5, warmupCount: 0, iterationCount: 1, invocationCount: 1)]
public class Benchmarks
{
    private const string References =
        @"C:\projekty\TestSolutions\global-packages\castle.core\4.4.1\lib\netstandard1.5\Castle.Core.dll;C:\projekty\TestSolutions\global-packages\commandlineparser\2.8.0\lib\netstandard2.0\CommandLine.dll;C:\projekty\TestSolutions\global-packages\log4net\2.0.8\lib\netstandard1.3\log4net.dll;C:\projekty\TestSolutions\global-packages\microsoft.aspnetcore.http.abstractions\2.2.0\lib\netstandard2.0\Microsoft.AspNetCore.Http.Abstractions.dll;C:\projekty\TestSolutions\global-packages\microsoft.aspnetcore.http\2.2.2\lib\netstandard2.0\Microsoft.AspNetCore.Http.dll;C:\projekty\TestSolutions\global-packages\microsoft.aspnetcore.http.features\2.2.0\lib\netstandard2.0\Microsoft.AspNetCore.Http.Features.dll;C:\projekty\TestSolutions\global-packages\microsoft.aspnetcore.webutilities\2.2.0\lib\netstandard2.0\Microsoft.AspNetCore.WebUtilities.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\Microsoft.CSharp.dll;C:\projekty\TestSolutions\global-packages\microsoft.data.sqlite.core\3.1.8\lib\netstandard2.0\Microsoft.Data.Sqlite.dll;C:\projekty\TestSolutions\global-packages\microsoft.dotnet.internalabstractions\1.0.0\lib\netstandard1.3\Microsoft.DotNet.InternalAbstractions.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.configuration.abstractions\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Configuration.Abstractions.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.configuration.binder\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Configuration.Binder.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.configuration\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Configuration.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.dependencyinjection.abstractions\3.1.8\lib\netstandard2.0\Microsoft.Extensions.DependencyInjection.Abstractions.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.dependencyinjection\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.DependencyInjection.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.http\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Http.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.logging.abstractions\3.1.8\lib\netstandard2.0\Microsoft.Extensions.Logging.Abstractions.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.logging\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Logging.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.objectpool\2.2.0\lib\netstandard2.0\Microsoft.Extensions.ObjectPool.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.options\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Options.dll;C:\projekty\TestSolutions\global-packages\microsoft.extensions.primitives\3.1.8\lib\netcoreapp3.1\Microsoft.Extensions.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.net.http.headers\2.2.0\lib\netstandard2.0\Microsoft.Net.Http.Headers.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.testhost\16.7.0\lib\netcoreapp2.1\Microsoft.TestPlatform.CommunicationUtilities.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.objectmodel\16.7.0\lib\netstandard2.0\Microsoft.TestPlatform.CoreUtilities.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.testhost\16.7.0\lib\netcoreapp2.1\Microsoft.TestPlatform.CrossPlatEngine.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.objectmodel\16.7.0\lib\netstandard2.0\Microsoft.TestPlatform.PlatformAbstractions.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.testhost\16.7.0\lib\netcoreapp2.1\Microsoft.TestPlatform.Utilities.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\Microsoft.VisualBasic.Core.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\Microsoft.VisualBasic.dll;C:\projekty\TestSolutions\global-packages\microsoft.codecoverage\16.7.0\lib\netcoreapp1.0\Microsoft.VisualStudio.CodeCoverage.Shim.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.testhost\16.7.0\lib\netcoreapp2.1\Microsoft.VisualStudio.TestPlatform.Common.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.objectmodel\16.7.0\lib\netstandard2.0\Microsoft.VisualStudio.TestPlatform.ObjectModel.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\Microsoft.Win32.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\mscorlib.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\netstandard.dll;C:\projekty\TestSolutions\global-packages\newtonsoft.json\12.0.3\lib\netstandard2.0\Newtonsoft.Json.dll;C:\projekty\TestSolutions\global-packages\nuget.frameworks\5.0.0\lib\netstandard2.0\NuGet.Frameworks.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project122\bin\Debug\netcoreapp3.1\Project122.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project123\bin\Debug\netcoreapp3.1\Project123.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project124\bin\Debug\netcoreapp3.1\Project124.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project125\bin\Debug\netcoreapp3.1\Project125.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project126\bin\Debug\netcoreapp3.1\Project126.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project127\bin\Debug\netcoreapp3.1\Project127.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project130\bin\Debug\netcoreapp3.1\Project130.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project131\bin\Debug\netcoreapp3.1\Project131.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project134\bin\Debug\netcoreapp3.1\Project134.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project138\bin\Debug\netcoreapp3.1\Project138.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project140\bin\Debug\netcoreapp3.1\Project140.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project142\bin\Debug\netcoreapp3.1\Project142.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project144\bin\Debug\netcoreapp3.1\Project144.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project146\bin\Debug\netcoreapp3.1\Project146.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project147\bin\Debug\netcoreapp3.1\Project147.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project149\bin\Debug\netcoreapp3.1\Project149.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project151\bin\Debug\netcoreapp3.1\Project151.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project153\bin\Debug\netcoreapp3.1\Project153.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project157\bin\Debug\netcoreapp3.1\Project157.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project161\bin\Debug\netcoreapp3.1\Project161.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project162\bin\Debug\netcoreapp3.1\Project162.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project164\bin\Debug\netcoreapp3.1\Project164.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project165\bin\Debug\netcoreapp3.1\Project165.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project166\bin\Debug\netcoreapp3.1\Project166.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project168\bin\Debug\netcoreapp3.1\Project168.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project172\bin\Debug\netcoreapp3.1\Project172.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project173\bin\Debug\netcoreapp3.1\Project173.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project175\bin\Debug\netcoreapp3.1\Project175.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project177\bin\Debug\netcoreapp3.1\Project177.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project179\bin\Debug\netcoreapp3.1\Project179.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project184\bin\Debug\netcoreapp3.1\Project184.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project187\bin\Debug\netcoreapp3.1\Project187.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project189\bin\Debug\netcoreapp3.1\Project189.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project190\bin\Debug\netcoreapp3.1\Project190.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project192\bin\Debug\netcoreapp3.1\Project192.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project193\bin\Debug\netcoreapp3.1\Project193.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project195\bin\Debug\netcoreapp3.1\Project195.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project199\bin\Debug\netcoreapp3.1\Project199.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project200\bin\Debug\netcoreapp3.1\Project200.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project204\bin\Debug\netcoreapp3.1\Project204.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project206\bin\Debug\netcoreapp3.1\Project206.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project207\bin\Debug\netcoreapp3.1\Project207.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project209\bin\Debug\netcoreapp3.1\Project209.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project210\bin\Debug\netcoreapp3.1\Project210.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project213\bin\Debug\netcoreapp3.1\Project213.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project214\bin\Debug\netcoreapp3.1\Project214.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project216\bin\Debug\netcoreapp3.1\Project216.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project217\bin\Debug\netcoreapp3.1\Project217.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project221\bin\Debug\netcoreapp3.1\Project221.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project222\bin\Debug\netcoreapp3.1\Project222.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project224\bin\Debug\netcoreapp3.1\Project224.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project226\bin\Debug\netcoreapp3.1\Project226.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project232\bin\Debug\netcoreapp3.1\Project232.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project236\bin\Debug\netcoreapp3.1\Project236.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project239\bin\Debug\netcoreapp3.1\Project239.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project240\bin\Debug\netcoreapp3.1\Project240.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project243\bin\Debug\netcoreapp3.1\Project243.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project250\bin\Debug\netcoreapp3.1\Project250.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project252\bin\Debug\netcoreapp3.1\Project252.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project253\bin\Debug\netcoreapp3.1\Project253.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project255\bin\Debug\netcoreapp3.1\Project255.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project256\bin\Debug\netcoreapp3.1\Project256.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project257\bin\Debug\netcoreapp3.1\Project257.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project258\bin\Debug\netcoreapp3.1\Project258.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project260\bin\Debug\netcoreapp3.1\Project260.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project261\bin\Debug\netcoreapp3.1\Project261.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project264\bin\Debug\netcoreapp3.1\Project264.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project265\bin\Debug\netcoreapp3.1\Project265.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project268\bin\Debug\netcoreapp3.1\Project268.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project272\bin\Debug\netcoreapp3.1\Project272.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project273\bin\Debug\netcoreapp3.1\Project273.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project276\bin\Debug\netcoreapp3.1\Project276.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project278\bin\Debug\netcoreapp3.1\Project278.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project281\bin\Debug\netcoreapp3.1\Project281.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project283\bin\Debug\netcoreapp3.1\Project283.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project291\bin\Debug\netcoreapp3.1\Project291.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project292\bin\Debug\netcoreapp3.1\Project292.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project293\bin\Debug\netcoreapp3.1\Project293.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project294\bin\Debug\netcoreapp3.1\Project294.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project295\bin\Debug\netcoreapp3.1\Project295.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project297\bin\Debug\netcoreapp3.1\Project297.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project298\bin\Debug\netcoreapp3.1\Project298.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project3\bin\Debug\netcoreapp3.1\Project3.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project300\bin\Debug\netcoreapp3.1\Project300.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project301\bin\Debug\netcoreapp3.1\Project301.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project302\bin\Debug\netcoreapp3.1\Project302.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project305\bin\Debug\netcoreapp3.1\Project305.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project307\bin\Debug\netcoreapp3.1\Project307.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project310\bin\Debug\netcoreapp3.1\Project310.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project311\bin\Debug\netcoreapp3.1\Project311.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project314\bin\Debug\netcoreapp3.1\Project314.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project315\bin\Debug\netcoreapp3.1\Project315.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project317\bin\Debug\netcoreapp3.1\Project317.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project318\bin\Debug\netcoreapp3.1\Project318.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project319\bin\Debug\netcoreapp3.1\Project319.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project320\bin\Debug\netcoreapp3.1\Project320.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project321\bin\Debug\netcoreapp3.1\Project321.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project323\bin\Debug\netcoreapp3.1\Project323.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project324\bin\Debug\netcoreapp3.1\Project324.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project326\bin\Debug\netcoreapp3.1\Project326.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project327\bin\Debug\netcoreapp3.1\Project327.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project329\bin\Debug\netcoreapp3.1\Project329.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project331\bin\Debug\netcoreapp3.1\Project331.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project334\bin\Debug\netcoreapp3.1\Project334.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project336\bin\Debug\netcoreapp3.1\Project336.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project338\bin\Debug\netcoreapp3.1\Project338.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project339\bin\Debug\netcoreapp3.1\Project339.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project340\bin\Debug\netcoreapp3.1\Project340.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project341\bin\Debug\netcoreapp3.1\Project341.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project342\bin\Debug\netcoreapp3.1\Project342.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project344\bin\Debug\netcoreapp3.1\Project344.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project346\bin\Debug\netcoreapp3.1\Project346.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project5\bin\Debug\netcoreapp3.1\Project5.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project84\bin\Debug\netcoreapp3.1\Project84.dll;C:\projekty\TestSolutions\GRLargeAppCentralised\Project87\bin\Debug\netcoreapp3.1\Project87.dll;C:\projekty\TestSolutions\global-packages\protobuf-net.core\3.0.29\lib\netcoreapp3.1\protobuf-net.Core.dll;C:\projekty\TestSolutions\global-packages\protobuf-net\3.0.29\lib\netcoreapp3.1\protobuf-net.dll;C:\projekty\TestSolutions\global-packages\serilog\2.10.0\lib\netstandard2.1\Serilog.dll;C:\projekty\TestSolutions\global-packages\sqlitepclraw.bundle_e_sqlite3\2.0.2\lib\netcoreapp3.0\SQLitePCLRaw.batteries_v2.dll;C:\projekty\TestSolutions\global-packages\sqlitepclraw.core\2.0.2\lib\netstandard2.0\SQLitePCLRaw.core.dll;C:\projekty\TestSolutions\global-packages\sqlitepclraw.bundle_e_sqlite3\2.0.2\lib\netcoreapp3.0\SQLitePCLRaw.nativelibrary.dll;C:\projekty\TestSolutions\global-packages\sqlitepclraw.provider.dynamic_cdecl\2.0.2\lib\netstandard2.0\SQLitePCLRaw.provider.dynamic_cdecl.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.AppContext.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Buffers.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Collections.Concurrent.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Collections.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Collections.Immutable.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Collections.NonGeneric.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Collections.Specialized.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ComponentModel.Annotations.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ComponentModel.DataAnnotations.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ComponentModel.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ComponentModel.EventBasedAsync.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ComponentModel.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ComponentModel.TypeConverter.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Configuration.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Console.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Core.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Data.Common.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Data.DataSetExtensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Data.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.Contracts.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.Debug.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.DiagnosticSource.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.FileVersionInfo.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.Process.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.StackTrace.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.TextWriterTraceListener.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.Tools.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.TraceSource.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Diagnostics.Tracing.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Drawing.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Drawing.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Dynamic.Runtime.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Globalization.Calendars.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Globalization.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Globalization.Extensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.Compression.Brotli.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.Compression.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.Compression.FileSystem.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.Compression.ZipFile.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.FileSystem.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.FileSystem.DriveInfo.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.FileSystem.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.FileSystem.Watcher.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.IsolatedStorage.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.MemoryMappedFiles.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.Pipes.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.IO.UnmanagedMemoryStream.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Linq.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Linq.Expressions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Linq.Parallel.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Linq.Queryable.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Memory.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Http.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.HttpListener.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Mail.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.NameResolution.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.NetworkInformation.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Ping.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Requests.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Security.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.ServicePoint.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.Sockets.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.WebClient.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.WebHeaderCollection.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.WebProxy.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.WebSockets.Client.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Net.WebSockets.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Numerics.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Numerics.Vectors.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ObjectModel.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.DispatchProxy.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.Emit.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.Emit.ILGeneration.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.Emit.Lightweight.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.Extensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.Metadata.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Reflection.TypeExtensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Resources.Reader.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Resources.ResourceManager.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Resources.Writer.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.CompilerServices.Unsafe.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.CompilerServices.VisualC.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Extensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Handles.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.InteropServices.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.InteropServices.RuntimeInformation.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.InteropServices.WindowsRuntime.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Intrinsics.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Loader.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Numerics.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Serialization.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Serialization.Formatters.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Serialization.Json.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Serialization.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Runtime.Serialization.Xml.dll;C:\Program Files\dotnet\sdk\NuGetFallbackFolder\system.security.accesscontrol\4.5.0\ref\netstandard2.0\System.Security.AccessControl.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Claims.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Cryptography.Algorithms.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Cryptography.Csp.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Cryptography.Encoding.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Cryptography.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Cryptography.X509Certificates.dll;C:\Program Files\dotnet\sdk\NuGetFallbackFolder\system.security.cryptography.xml\4.5.0\ref\netstandard2.0\System.Security.Cryptography.Xml.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.dll;C:\Program Files\dotnet\sdk\NuGetFallbackFolder\system.security.permissions\4.5.0\ref\netstandard2.0\System.Security.Permissions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.Principal.dll;C:\Program Files\dotnet\sdk\NuGetFallbackFolder\system.security.principal.windows\4.5.0\ref\netstandard2.0\System.Security.Principal.Windows.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Security.SecureString.dll;C:\projekty\TestSolutions\global-packages\system.servicemodel.primitives\4.7.0\ref\netcoreapp2.1\System.ServiceModel.dll;C:\projekty\TestSolutions\global-packages\system.servicemodel.primitives\4.7.0\ref\netcoreapp2.1\System.ServiceModel.Primitives.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ServiceModel.Web.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ServiceProcess.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Text.Encoding.CodePages.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Text.Encoding.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Text.Encoding.Extensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Text.Encodings.Web.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Text.Json.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Text.RegularExpressions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Channels.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Overlapped.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Tasks.Dataflow.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Tasks.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Tasks.Extensions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Tasks.Parallel.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Thread.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.ThreadPool.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Threading.Timer.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Transactions.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Transactions.Local.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.ValueTuple.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Web.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Web.HttpUtility.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Windows.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.Linq.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.ReaderWriter.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.Serialization.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.XDocument.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.XmlDocument.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.XmlSerializer.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.XPath.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\System.Xml.XPath.XDocument.dll;C:\projekty\TestSolutions\global-packages\system.xml.xpath.xmldocument\4.3.0\ref\netstandard1.3\System.Xml.XPath.XmlDocument.dll;C:\projekty\TestSolutions\global-packages\microsoft.testplatform.testhost\16.7.0\lib\netcoreapp2.1\testhost.dll;C:\projekty\TestSolutions\global-packages\microsoft.netcore.app.ref\3.1.0\ref\netcoreapp3.1\WindowsBase.dll";

    [ParamsSource(nameof(Parallelisms))]
    public int Parallelism { get; set; }

    public static IEnumerable<int> Parallelisms => new[] { 16 };
    
    [Benchmark]
    public void HashCalculationPerfTest()
    {
        var inputs = new LocateInputs(
            AssemblyName: "assembly",
            ConfigPath: "",
            ProjectFullPath: "sfsd.csproj",
            AllProps: new Dictionary<string, string>
            {
                ["References"] = References,
                ["TargetType"] = "Library"
            }
        );
        var decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps);
        var memCache = new DictionaryBasedCache<CacheKey, RefDataWithOriginalExtract>();
        var refCacheFileBased = new RefCache("c:/projekty/.refcache");
        var refCache = new CacheCombiner<CacheKey, RefDataWithOriginalExtract>(memCache, refCacheFileBased);
        var refTrimmingConfig = new RefTrimmingConfig();

        void Act()
        {
            var inputs = LocatorAndPopulator.CalculateLocalInputs(decomposed, refCache, "assembly", refTrimmingConfig, new DictionaryBasedCache<FileCacheKey, string>(), Utils.DefaultHasher);
            if (inputs.Files.Length == 0) throw new Exception();
        }

        var sw = Stopwatch.StartNew();
        Enumerable.Range(0, 4).AsParallel().WithDegreeOfParallelism(Parallelism).ForAll(i =>
        {
            Act();
            // Console.WriteLine($"{i}: {sw.ElapsedMilliseconds}ms");
        });
    }
}

public static class Program
{
    public static void Main()
    {
        BenchmarkRunner.Run<Benchmarks>();
    }
}