from pathlib import Path
import subprocess
import tempfile

program = r'''using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var files = Directory.GetFiles("Assets/Scripts/Ai/V2", "*.cs", SearchOption.AllDirectories)
    .Concat(Directory.GetFiles("Assets/Editor", "*.cs", SearchOption.AllDirectories))
    .ToArray();
int failures = 0;
foreach (string file in files)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file),
        new CSharpParseOptions(LanguageVersion.Preview), path: file);
    foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
    {
        Console.WriteLine(diagnostic);
        failures++;
    }
}
Console.WriteLine($"Roslyn syntax: {files.Length} C# files, {failures} errors");
if (failures != 0) Environment.Exit(1);
'''
project = r'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.CodeAnalysis">
      <HintPath>$(MSBuildSDKsPath)/../Roslyn/bincore/Microsoft.CodeAnalysis.dll</HintPath>
    </Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp">
      <HintPath>$(MSBuildSDKsPath)/../Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
'''
with tempfile.TemporaryDirectory(prefix='raid-roslyn-') as directory:
    root = Path(directory)
    (root / 'Program.cs').write_text(program)
    (root / 'parse.csproj').write_text(project)
    subprocess.run(['dotnet', 'run', '--project', str(root / 'parse.csproj'), '--', 'Assets/Scripts/Ai/V2', 'Assets/Editor'], check=True)
