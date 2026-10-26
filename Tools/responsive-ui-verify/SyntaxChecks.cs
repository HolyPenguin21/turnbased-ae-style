using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

static class SyntaxChecks
{
    static int Main(string[] args)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: new[] {
            "UNITY_EDITOR", "UNITY_INCLUDE_TESTS", "UNITY_6000_0_OR_NEWER", "UNITY_6000_3_OR_NEWER" });
        var files = Directory.GetFiles(args[0], "*.cs", SearchOption.AllDirectories);
        int errors = 0;
        foreach (string file in files)
            foreach (var error in CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file)
                .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            { Console.WriteLine(error); errors++; }
        Console.WriteLine($"C# syntax: {files.Length} files, {errors} errors (no Unity API/type binding)");
        return errors == 0 ? 0 : 1;
    }
}
