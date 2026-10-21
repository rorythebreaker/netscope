using System.CommandLine;
using NetScope.Diagnostics;
using NetScope.Input;
using NetScope.Loading;
using NetScope.Pipeline;

namespace NetScope.Ghidra;

/// <summary>
/// `netscope export-ghidra &lt;file&gt; --out annotate.py [--lang python|java]`: a pure static-analysis bridge
/// (TZ-NetScope-2 §5). NetScope computes native addresses and .NET names; the emitted script lets Ghidra name
/// its unnamed functions. The assembly is never executed and never modified; the only output is a text script.
/// </summary>
public static class ExportGhidraCommand
{
    public const string Name = "export-ghidra";

    public static int Run(string[] args, TextWriter stdout)
    {
        var root = new RootCommand("Export a Ghidra annotation script for the native code of an assembly (ReadyToRun, C++/CLI). Pure static analysis: the assembly is not executed, only a text script is written.");
        var fileArg = new Argument<string>("file") { Description = "Assembly to analyze (its PE/CLR header decides the type)" };
        var outOpt = new Option<string>("--out") { Description = "Script file to write (required)", Required = true };
        var langOpt = new Option<string>("--lang") { Description = "Script language: python (Ghidrathon/Jython) or java (GhidraScript)", DefaultValueFactory = _ => "python" };
        langOpt.AcceptOnlyFromAmong("python", "java");
        var auditOpt = new Option<bool>("--audit") { Description = "Add bookmarks at points of interest found by the security rules (P/Invoke, unsafe calls)" };
        var refPath = new Option<string[]>("--ref-path") { Description = "Directory with dependencies (repeatable)" };
        var quiet = new Option<bool>("--quiet", "-q") { Description = "Only errors in stderr" };
        root.Arguments.Add(fileArg);
        foreach (var o in new Option[] { outOpt, langOpt, auditOpt, refPath, quiet }) root.Options.Add(o);

        var parse = root.Parse(new[] { root.Name }.Concat(args).ToArray());
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors) Console.Error.WriteLine("netscope export-ghidra: error: " + e.Message);
            Console.Error.WriteLine("Try 'netscope export-ghidra --help' for more information.");
            return Analyzer.ExitBadArguments;
        }

        int code = Analyzer.ExitOk;
        root.SetAction(p =>
        {
            if (p.GetValue(quiet)) Log.Level = Verbosity.Quiet;
            var file = p.GetValue(fileArg)!;
            var outPath = p.GetValue(outOpt)!;
            var lang = p.GetValue(langOpt) ?? "python";
            bool audit = p.GetValue(auditOpt);

            // GhidraScript compiles a .java file only if its public class has the same name as the file.
            var javaClass = Path.GetFileNameWithoutExtension(outPath);
            if (lang == "java" && !GhidraScriptWriter.IsValidJavaClassName(javaClass))
            {
                Console.Error.WriteLine($"netscope export-ghidra: error: '{Path.GetFileName(outPath)}' cannot hold a GhidraScript: " +
                                        "the file name must be a valid Java class name (e.g. NetScopeAnnotate.java)");
                code = Analyzer.ExitBadArguments;
                return;
            }

            var paths = new InputResolver().Resolve(new[] { new InputItem(InputKind.Path, file) }, recursive: false);
            if (paths.Count == 0)
            {
                Log.Error(null, "no input file found: " + file);
                code = Analyzer.ExitNothingOpened;
                return;
            }

            GhidraAnnotations ann;
            using (var asm = new AssemblyLoader().TryLoad(0, paths[0], out var reason))
            {
                if (asm == null)
                {
                    // No CLR header: a NativeAOT image still names its methods in its stack trace metadata
                    // (TZ-3 §4.3); any other native PE has nothing NetScope can add to what Ghidra reads itself.
                    var native = NativeImageAnnotations.TryBuild(paths[0], out var nativeError);
                    if (native == null)
                    {
                        Log.Error(file, "skipped: " + (nativeError ?? reason));
                        code = Analyzer.ExitNothingOpened;
                        return;
                    }
                    if (audit) Log.Warning(file, "--audit needs IL; a NativeAOT image has none, so no bookmarks are set");
                    ann = native;
                }
                else
                {
                    try
                    {
                        ann = GhidraAnnotationBuilder.Build(asm, audit);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(file, "cannot build annotations: " + ex.Message);
                        code = Analyzer.ExitPartial;
                        return;
                    }
                }
            }

            foreach (var w in ann.Warnings) Log.Warning(file, w);
            if (!ann.HasNativeCode)
            {
                Log.Warning(file, "nothing to annotate: no native code in this assembly (pure IL). Ghidra needs native code (ReadyToRun or C++/CLI).");
                // Still write the script (it may carry the mscoree import label) so a pipeline step does not fail.
            }

            var script = GhidraScriptWriter.Write(ann, lang, javaClass);
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, script, OutputSink.Utf8);
            }
            catch (Exception ex)
            {
                Log.Error(file, "cannot write " + outPath + ": " + ex.Message);
                code = Analyzer.ExitPartial;
                return;
            }

            Log.Info($"{file}: wrote {ann.Functions.Count} function name(s), {ann.Funclets.Count} funclet label(s), {ann.Imports.Count} import(s), {ann.Bookmarks.Count} bookmark(s) to {outPath}");
        });

        parse.Invoke();
        return code;
    }
}
