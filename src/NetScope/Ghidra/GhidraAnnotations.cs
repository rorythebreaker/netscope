using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using NetScope.Asm;
using NetScope.Audit;
using NetScope.Catalog;
using NetScope.Loading;
using NetScope.Metadata;

namespace NetScope.Ghidra;

/// <summary>A native function to name in Ghidra. <see cref="Rva"/> is an offset from the image base.</summary>
public sealed record GhidraFunction(int Rva, string Name, string? Signature);

/// <summary>A funclet (exception-handler code range) with a label linking it to its parent method.</summary>
public sealed record GhidraFunclet(int Rva, string ParentName, string Label);

/// <summary>An imported function reachable through an IAT thunk (C++/CLI IJW), labeled at the thunk slot.</summary>
public sealed record GhidraImport(int Rva, string Name);

/// <summary>A point of interest (from --audit) marked as a Ghidra bookmark.</summary>
public sealed record GhidraBookmark(int Rva, string Category, string Note);

/// <summary>
/// Everything NetScope knows statically about the native layout of an assembly, ready to be written as a
/// Ghidra annotation script (TZ-NetScope-2 §5). All addresses are image-base-relative RVAs; the script turns
/// them into Ghidra's own base. Building this never runs the assembly.
/// </summary>
public sealed class GhidraAnnotations
{
    public required string SourceFile { get; init; }
    public required string AssemblyName { get; init; }
    public string? Architecture { get; init; }
    /// <summary>What the native code is, when it is not the usual ReadyToRun / C++/CLI case (e.g. "NativeAOT ...").</summary>
    public string? Kind { get; init; }
    /// <summary>OS of a ReadyToRun image built for another OS than Windows (Linux, macOS, ...); null otherwise.</summary>
    public string? TargetOs { get; init; }
    /// <summary>analyzeHeadless options that import the image when Ghidra's PE loader does not recognize it on
    /// its own: an R2R image for Linux or macOS has an OS-encoded Machine field (TZ-3 §4.2). Null when not needed.</summary>
    public string? GhidraImportOptions { get; init; }
    public List<GhidraFunction> Functions { get; } = new();
    public List<GhidraFunclet> Funclets { get; } = new();
    public List<GhidraImport> Imports { get; } = new();
    public List<GhidraBookmark> Bookmarks { get; } = new();
    public List<string> Warnings { get; } = new();

    public bool HasAnything => Functions.Count > 0 || Funclets.Count > 0 || Imports.Count > 0 || Bookmarks.Count > 0;

    /// <summary>Native code worth analyzing in Ghidra (R2R / C++/CLI). The legacy mscoree import thunk alone
    /// does not count: a pure-IL assembly still carries it but has nothing native to annotate (§5.2).</summary>
    public bool HasNativeCode => Functions.Count > 0 || Funclets.Count > 0;
}

/// <summary>Collects <see cref="GhidraAnnotations"/> from a loaded assembly by static analysis only.</summary>
public static class GhidraAnnotationBuilder
{
    public static GhidraAnnotations Build(LoadedAssembly asm, bool audit)
    {
        var resolver = new MemberResolver(asm.Metadata, asm.Name);
        var methods = new MethodCatalog().Build(asm, resolver, MethodFilter.All);
        // allowRun: false — native code is read from the image (R2R / C++/CLI) only, the assembly is never run.
        var provider = new AsmProvider(resolver, allowRun: false, Array.Empty<string>(), TimeSpan.Zero);
        var natives = provider.GetNative(asm, methods);

        var ann = new GhidraAnnotations { SourceFile = asm.FilePath, AssemblyName = asm.Name };
        string? arch = null;

        foreach (var m in methods)
        {
            if (!natives.TryGetValue(m, out var code) || !code.Available) continue;
            arch ??= code.Architecture;
            int? mainRva = null;
            foreach (var part in code.Parts)
            {
                int rva = (int)part.Address;
                if (part.Name.Contains("funclet", StringComparison.OrdinalIgnoreCase))
                {
                    // "funclet 1" (single body) or "<instance label> / funclet 1" (generic instantiation).
                    int slash = part.Name.LastIndexOf(" / ", StringComparison.Ordinal);
                    string parent = slash >= 0 ? part.Name[..slash] : m.FullName;
                    string label = slash >= 0 ? part.Name[(slash + 3)..] : part.Name;
                    ann.Funclets.Add(new GhidraFunclet(rva, parent, label));
                }
                else
                {
                    // "main" for an ordinary body; otherwise the instantiation label (already name-qualified).
                    string name = part.Name == "main" ? m.FullName : part.Name;
                    ann.Functions.Add(new GhidraFunction(rva, name, m.Signature));
                    mainRva ??= rva;
                }
            }

            if (audit && mainRva is int at)
            {
                foreach (var f in RunRules(m, resolver, methods))
                    ann.Bookmarks.Add(new GhidraBookmark(at, f.RuleId, f.Message));
            }
        }

        ann.Warnings.AddRange(ReadImports(asm, ann.Imports));

        var rawMachine = asm.PE.PEHeaders.CoffHeader.Machine;
        string os = Native.R2RReader.TargetOs(rawMachine);
        string? importOptions = null;
        if (os != "Windows")
        {
            importOptions = GhidraImportOptions(Native.R2RReader.NormalizeMachine(rawMachine));
            ann.Warnings.Add($"{os} ReadyToRun image: its PE Machine field is OS-encoded (0x{(ushort)rawMachine:X4}), so Ghidra's PE loader does not " +
                             (importOptions != null
                                 ? $"recognize it on its own. Import it with: analyzeHeadless <proj> <name> -import <file> {importOptions} (the file is not modified)"
                                 : "recognize it, and NetScope knows no Ghidra language for this architecture"));
        }

        var result = new GhidraAnnotations
        {
            SourceFile = asm.FilePath,
            AssemblyName = asm.Name,
            Architecture = arch ?? asm.Architecture,
            TargetOs = os == "Windows" ? null : os,
            GhidraImportOptions = importOptions,
        };
        result.Functions.AddRange(ann.Functions.OrderBy(f => f.Rva));
        result.Funclets.AddRange(ann.Funclets.OrderBy(f => f.Rva));
        result.Imports.AddRange(ann.Imports.OrderBy(i => i.Rva));
        result.Bookmarks.AddRange(ann.Bookmarks.OrderBy(b => b.Rva).ThenBy(b => b.Category, StringComparer.Ordinal));
        result.Warnings.AddRange(ann.Warnings);
        return result;
    }

    /// <summary>
    /// Ghidra's PE loader has no opinion for an OS-encoded Machine value, so it is chosen explicitly with the
    /// language of the decoded architecture. The calling convention is the Unix one (gcc) on x86/x64; ARM64 uses
    /// the same convention everywhere. Checked with Ghidra 11.1.2 on linux-x64, linux-arm64 and osx-arm64 images
    /// (tests/Ghidra/Check-Ghidra.ps1); the x86 and ARM32 lines follow the same rule but are not checked.
    /// </summary>
    public static string? GhidraImportOptions(Machine decoded) => decoded switch
    {
        Machine.Arm64 => "-loader PeLoader -processor AARCH64:LE:64:v8A -cspec default",
        Machine.Amd64 => "-loader PeLoader -processor x86:LE:64:default -cspec gcc",
        Machine.I386 => "-loader PeLoader -processor x86:LE:32:default -cspec gcc",
        Machine.Arm or Machine.ArmThumb2 or Machine.Thumb => "-loader PeLoader -processor ARM:LE:32:v8T -cspec default",
        _ => null,
    };

    private static IReadOnlyList<Finding> RunRules(MethodModel m, MemberResolver resolver, IReadOnlyList<MethodModel> methods)
    {
        try
        {
            var auditor = new Auditor(RuleSet.All, Severity.Note, new Suppressions());
            var defs = methods.ToDictionary(x => x.Handle);
            return auditor.Check(m, resolver, defs, out _);
        }
        catch (Exception)
        {
            return Array.Empty<Finding>();
        }
    }

    /// <summary>
    /// Reads the PE import directory and labels each IAT thunk slot with its DLL!function. For C++/CLI (IJW)
    /// these are the `jmp [IAT]` thunks of §5.3. Best-effort: a malformed table degrades to a warning.
    /// </summary>
    private static List<string> ReadImports(LoadedAssembly asm, List<GhidraImport> imports)
    {
        var warnings = new List<string>();
        try
        {
            var headers = asm.PE.PEHeaders;
            var dir = headers.PEHeader?.ImportTableDirectory ?? default;
            if (dir.Size == 0 || dir.RelativeVirtualAddress == 0) return warnings;
            bool pe32Plus = headers.PEHeader!.Magic == PEMagic.PE32Plus;
            int ptr = pe32Plus ? 8 : 4;

            // IMAGE_IMPORT_DESCRIPTOR array, 20 bytes each, terminated by an all-zero descriptor.
            for (int d = 0; d < 4096; d++)
            {
                var desc = asm.GetRvaSpan(dir.RelativeVirtualAddress + d * 20);
                if (desc.Length < 20) break;
                uint ilt = BinaryPrimitives.ReadUInt32LittleEndian(desc);
                uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(desc.Slice(12));
                uint iat = BinaryPrimitives.ReadUInt32LittleEndian(desc.Slice(16));
                if (ilt == 0 && nameRva == 0 && iat == 0) break;
                string dll = ReadAsciiz(asm, (int)nameRva);
                uint lookup = ilt != 0 ? ilt : iat; // ILT is preferred; fall back to the IAT itself.
                if (lookup == 0 || iat == 0) continue;

                for (int i = 0; i < 65536; i++)
                {
                    var thunk = asm.GetRvaSpan((int)lookup + i * ptr);
                    if (thunk.Length < ptr) break;
                    ulong entry = pe32Plus ? BinaryPrimitives.ReadUInt64LittleEndian(thunk) : BinaryPrimitives.ReadUInt32LittleEndian(thunk);
                    if (entry == 0) break;
                    bool byOrdinal = pe32Plus ? (entry & 0x8000000000000000UL) != 0 : (entry & 0x80000000UL) != 0;
                    int iatSlotRva = (int)iat + i * ptr;
                    if (byOrdinal)
                    {
                        imports.Add(new GhidraImport(iatSlotRva, $"{dll}!Ordinal_{entry & 0xFFFF}"));
                    }
                    else
                    {
                        var nameEntry = asm.GetRvaSpan((int)entry);
                        if (nameEntry.Length < 3) continue;
                        string fn = ReadAsciiz(asm, (int)entry + 2); // skip the 2-byte hint
                        if (fn.Length > 0) imports.Add(new GhidraImport(iatSlotRva, $"{dll}!{fn}"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add("import table not annotated: " + ex.Message);
        }
        return warnings;
    }

    private static string ReadAsciiz(LoadedAssembly asm, int rva)
    {
        if (rva <= 0) return "";
        var span = asm.GetRvaSpan(rva);
        int end = span.IndexOf((byte)0);
        if (end < 0) end = Math.Min(span.Length, 256);
        return System.Text.Encoding.ASCII.GetString(span.Slice(0, end));
    }
}
