param(
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $repoRoot "src\HCSWhisper"
$pkgDir = Join-Path $repoRoot "packages"
$distPluginDir = Join-Path $repoRoot "dist\HCSWhisper"
$zipOutPath = Join-Path $repoRoot "dist\HCSWhisper-v$Version-win-x64.zip"

New-Item -ItemType Directory -Force -Path $pkgDir, $distPluginDir | Out-Null

# Locate VoiceAttack 2 (for Roslyn + NAudio compile references if .NET SDK isn't installed)
$va2Candidates = @(
    "C:\Program Files (x86)\Steam\steamapps\common\VoiceAttack 2",
    "C:\Program Files\VoiceAttack 2",
    "C:\Program Files (x86)\VoiceAttack 2",
    "C:\Program Files\VoiceAttack"
)
$va2Dir = $va2Candidates | Where-Object { Test-Path (Join-Path $_ "VoiceAttack.dll") } | Select-Object -First 1
if (-not $va2Dir) { throw "VoiceAttack 2 installation not found for reference assemblies." }

# Ensure NuGet dependencies are fetched
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

# Locate .NET 8 Runtime & WindowsDesktop shared frameworks
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
        string srcDir = args[3];
        string outDll = args[4];
        string[] extraDlls = args.Skip(5).ToArray();

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
                // Skip native (non-CLI metadata) DLLs cleanly
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
        object comp = createComp.Invoke(null, new object[] { "HCSWhisper", treesArr, refsList, opts });

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

$outDll = Join-Path $distPluginDir "HCSWhisper.dll"
$extraRefs = @(
    (Join-Path $pkgDir "whisper.net\lib\net8.0\Whisper.net.dll"),
    (Join-Path $pkgDir "microsoft.extensions.ai.abstractions\lib\net8.0\Microsoft.Extensions.AI.Abstractions.dll"),
    (Join-Path $va2Dir "NAudio.Core.dll"),
    (Join-Path $va2Dir "NAudio.Wasapi.dll")
)

Write-Host "Compiling HCSWhisper.dll (net8.0-windows)..."
& "C:\Program Files\dotnet\dotnet.exe" "$buildTmp\Driver.dll" "$va2Dir" "$net8CoreDir" "$net8DeskDir" "$srcDir" "$outDll" @extraRefs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed!" }
Remove-Item -Recurse -Force $buildTmp

# Stage runtime dependencies into dist\HCSWhisper
Copy-Item -Force "$pkgDir\whisper.net\lib\net8.0\Whisper.net.dll" "$distPluginDir\"
Copy-Item -Force "$pkgDir\microsoft.extensions.ai.abstractions\lib\net8.0\Microsoft.Extensions.AI.Abstractions.dll" "$distPluginDir\"

New-Item -ItemType Directory -Force -Path "$distPluginDir\runtimes\vulkan\win-x64", "$distPluginDir\runtimes\win-x64" | Out-Null
Copy-Item -Force "$pkgDir\whisper.net.runtime.vulkan\build\win-x64\*" "$distPluginDir\runtimes\vulkan\win-x64\"
Copy-Item -Force "$pkgDir\whisper.net.runtime\build\win-x64\*" "$distPluginDir\runtimes\win-x64\"

if (Test-Path $zipOutPath) { Remove-Item -Force $zipOutPath }
[System.IO.Compression.ZipFile]::CreateFromDirectory($distPluginDir, $zipOutPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipMb = [Math]::Round((Get-Item $zipOutPath).Length / 1MB, 2)
Write-Host "Built HCSWhisper successfully! Release zip: $zipOutPath ($zipMb MB)"
