param(
    [string]$Version = "1.0.3"
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$coreSrcDir = Join-Path $repoRoot "src\HCSWhisper"
$loaderSrcDir = Join-Path $repoRoot "src\HCSWhisper.Loader"
$pkgDir = Join-Path $repoRoot "packages"
$distPluginDir = Join-Path $repoRoot "dist\HCSWhisper"
$engineStageDir = Join-Path $repoRoot "dist\engine_stage"
$zipOutPath = Join-Path $repoRoot "dist\HCSWhisper-win-x64.zip"

if (Test-Path $distPluginDir) { Remove-Item -Recurse -Force $distPluginDir }
if (Test-Path $engineStageDir) { Remove-Item -Recurse -Force $engineStageDir }
New-Item -ItemType Directory -Force -Path $pkgDir, $distPluginDir, $engineStageDir | Out-Null

$va2Candidates = @(
    "C:\Program Files (x86)\Steam\steamapps\common\VoiceAttack 2",
    "C:\Program Files\VoiceAttack 2",
    "C:\Program Files (x86)\VoiceAttack 2",
    "C:\Program Files\VoiceAttack"
)
$va2Dir = $va2Candidates | Where-Object { Test-Path (Join-Path $_ "VoiceAttack.dll") } | Select-Object -First 1
if (-not $va2Dir) { throw "VoiceAttack 2 installation not found for reference assemblies." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$nugetSpecs = @(
    @{ Id = "whisper.net"; Ver = "1.9.1" },
    @{ Id = "whisper.net.runtime.vulkan"; Ver = "1.9.1" },
    @{ Id = "whisper.net.runtime"; Ver = "1.9.1" },
    @{ Id = "microsoft.extensions.ai.abstractions"; Ver = "10.2.0" }
)
foreach ($spec in $nugetSpecs) {
    $targetFolder = Join-Path $pkgDir $spec.Id
    if (-not (Test-Path $targetFolder)) {
        $nupkg = Join-Path $pkgDir "$($spec.Id).$($spec.Ver).nupkg"
        Write-Host "Fetching NuGet package $($spec.Id) $($spec.Ver)..."
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$($spec.Id)/$($spec.Ver)/$($spec.Id).$($spec.Ver).nupkg" -OutFile $nupkg
        [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $targetFolder)
    }
}

$net8CoreDir = Get-ChildItem "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.*" | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$net8DeskDir = Get-ChildItem "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*" | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $net8CoreDir -or -not $net8DeskDir) { throw ".NET 8 Desktop Runtime not found." }

$buildTmp = Join-Path $repoRoot "obj_roslyn"
New-Item -ItemType Directory -Force -Path $buildTmp | Out-Null

$roslynDriverCs = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static object Call(MethodInfo m, object target, params object[] provided)
    {
        var ps = m.GetParameters();
        var args = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i < provided.Length) args[i] = provided[i];
            else if (ps[i].HasDefaultValue && ps[i].DefaultValue != DBNull.Value) args[i] = ps[i].DefaultValue;
            else if (ps[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(ps[i].ParameterType);
            else args[i] = null;
        }
        return m.Invoke(target, args);
    }

    static int Main(string[] args)
    {
        string va2Dir = args[0];
        string netCoreDir = args[1];
        string netDeskDir = args[2];
        string asmName = args[3];
        string srcDir = args[4];
        string outDll = args[5];
        string[] extraDlls = args.Skip(6).ToArray();

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in new[] { va2Dir, netCoreDir, netDeskDir })
            {
                string f = Path.Combine(d, n);
                if (File.Exists(f)) return Assembly.LoadFrom(f);
            }
            return null;
        };

        Assembly ca = Assembly.LoadFrom(Path.Combine(va2Dir, "Microsoft.CodeAnalysis.dll"));
        Assembly cs = Assembly.LoadFrom(Path.Combine(va2Dir, "Microsoft.CodeAnalysis.CSharp.dll"));

        Type syntaxTreeBase = ca.GetType("Microsoft.CodeAnalysis.SyntaxTree");
        Type csTreeType = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
        MethodInfo parseText = csTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "ParseText" && m.GetParameters()[0].ParameterType == typeof(string));

        var treesList = new List<object>();
        foreach (string csFile in Directory.GetFiles(srcDir, "*.cs"))
        {
            treesList.Add(Call(parseText, null, File.ReadAllText(csFile)));
        }
        string tfmAttr = "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v8.0\", FrameworkDisplayName = \".NET 8.0\")]";
        treesList.Add(Call(parseText, null, tfmAttr));

        Array treesArr = Array.CreateInstance(syntaxTreeBase, treesList.Count);
        for (int i = 0; i < treesList.Count; i++) treesArr.SetValue(treesList[i], i);

        Type metaRefType = ca.GetType("Microsoft.CodeAnalysis.MetadataReference");
        MethodInfo createFromFile = metaRefType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateFromFile" && m.GetParameters()[0].ParameterType == typeof(string));

        var refsList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(metaRefType));
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> candidateRefs = extraDlls
            .Concat(Directory.GetFiles(netCoreDir, "*.dll"))
            .Concat(Directory.GetFiles(netDeskDir, "*.dll"));

        foreach (string dll in candidateRefs)
        {
            if (!File.Exists(dll)) continue;
            string fn = Path.GetFileName(dll);
            if (!seenFileNames.Add(fn)) continue;
            try
            {
                AssemblyName.GetAssemblyName(dll);
                refsList.Add(Call(createFromFile, null, dll));
            }
            catch { }
        }

        Type outKindType = ca.GetType("Microsoft.CodeAnalysis.OutputKind");
        object dllKind = Enum.Parse(outKindType, "DynamicallyLinkedLibrary");
        Type compOptsType = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
        var ctor = compOptsType.GetConstructors().OrderBy(c => c.GetParameters().Length).First(c => c.GetParameters()[0].ParameterType == outKindType);
        var cps = ctor.GetParameters();
        var cArgs = new object[cps.Length];
        cArgs[0] = dllKind;
        for (int i = 1; i < cps.Length; i++)
            cArgs[i] = (cps[i].HasDefaultValue && cps[i].DefaultValue != DBNull.Value) ? cps[i].DefaultValue : (cps[i].ParameterType.IsValueType ? Activator.CreateInstance(cps[i].ParameterType) : null);
        object opts = ctor.Invoke(cArgs);

        MethodInfo createComp = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string));
        object comp = createComp.Invoke(null, new object[] { asmName, treesArr, refsList, opts });

        MethodInfo emitMethod = comp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .First(m => m.Name == "Emit" && m.GetParameters()[0].ParameterType == typeof(Stream));

        object res;
        using (var fs = File.Create(outDll))
        {
            res = Call(emitMethod, comp, fs);
        }

        bool ok = (bool)res.GetType().GetProperty("Success").GetValue(res);
        foreach (var d in (System.Collections.IEnumerable)res.GetType().GetProperty("Diagnostics").GetValue(res))
        {
            string ds = d.ToString();
            if (ds.IndexOf("error ", StringComparison.OrdinalIgnoreCase) >= 0 || !ok)
                Console.WriteLine(ds);
        }
        return ok ? 0 : 1;
    }
}
"@
Set-Content "$buildTmp\Driver.cs" $roslynDriverCs -Encoding UTF8
Set-Content "$buildTmp\Driver.runtimeconfig.json" '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.WindowsDesktop.App","version":"8.0.0"}}}' -Encoding UTF8

& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:"$buildTmp\Driver.dll" /target:exe "$buildTmp\Driver.cs"

# 1. Compile HCSWhisper.Core.dll (contains Whisper.net, NAudio, UI, HCS matcher -> goes inside engine.pak!)
$coreDll = Join-Path $engineStageDir "HCSWhisper.Core.dll"
$extraRefs = @(
    (Join-Path $pkgDir "whisper.net\lib\net8.0\Whisper.net.dll"),
    (Join-Path $pkgDir "microsoft.extensions.ai.abstractions\lib\net8.0\Microsoft.Extensions.AI.Abstractions.dll"),
    (Join-Path $va2Dir "NAudio.Core.dll"),
    (Join-Path $va2Dir "NAudio.Wasapi.dll")
)
Write-Host "Compiling HCSWhisper.Core.dll..."
& "C:\Program Files\dotnet\dotnet.exe" "$buildTmp\Driver.dll" "$va2Dir" "$net8CoreDir" "$net8DeskDir" "HCSWhisper.Core" "$coreSrcDir" "$coreDll" @extraRefs
if ($LASTEXITCODE -ne 0) { throw "HCSWhisper.Core compilation failed!" }

# 2. Compile zero-dependency HCSWhisper.dll shim (goes into VoiceAttack 2\Apps\HCSWhisper\HCSWhisper.dll)
$shimDll = Join-Path $distPluginDir "HCSWhisper.dll"
Write-Host "Compiling zero-dependency HCSWhisper.dll VoiceAttack shim..."
& "C:\Program Files\dotnet\dotnet.exe" "$buildTmp\Driver.dll" "$va2Dir" "$net8CoreDir" "$net8DeskDir" "HCSWhisper" "$loaderSrcDir" "$shimDll"
if ($LASTEXITCODE -ne 0) { throw "HCSWhisper shim compilation failed!" }

Remove-Item -Recurse -Force $buildTmp

# 3. Pack HCSWhisper.Core.dll + Whisper.net + Vulkan/CPU runtimes into engine.pak
Copy-Item -Force "$pkgDir\whisper.net\lib\net8.0\Whisper.net.dll" "$engineStageDir\"
Copy-Item -Force "$pkgDir\microsoft.extensions.ai.abstractions\lib\net8.0\Microsoft.Extensions.AI.Abstractions.dll" "$engineStageDir\"

New-Item -ItemType Directory -Force -Path "$engineStageDir\runtimes\vulkan\win-x64", "$engineStageDir\runtimes\win-x64" | Out-Null
Copy-Item -Force "$pkgDir\whisper.net.runtime.vulkan\build\win-x64\*" "$engineStageDir\runtimes\vulkan\win-x64\"
Copy-Item -Force "$pkgDir\whisper.net.runtime\build\win-x64\*" "$engineStageDir\runtimes\win-x64\"

$pakOut = Join-Path $distPluginDir "engine.pak"
[System.IO.Compression.ZipFile]::CreateFromDirectory($engineStageDir, $pakOut, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -Recurse -Force $engineStageDir

if (Test-Path $zipOutPath) { Remove-Item -Force $zipOutPath }
[System.IO.Compression.ZipFile]::CreateFromDirectory($distPluginDir, $zipOutPath, [System.IO.Compression.CompressionLevel]::NoCompression, $true)
$zipMb = [Math]::Round((Get-Item $zipOutPath).Length / 1MB, 2)
Write-Host "Built HCSWhisper v$Version successfully! Release zip: $zipOutPath ($zipMb MB)"
