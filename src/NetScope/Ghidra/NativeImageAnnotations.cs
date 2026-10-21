using System.Reflection.PortableExecutable;
using NetScope.Native;

namespace NetScope.Ghidra;

/// <summary>
/// export-ghidra for PE files without a CLR header (TZ-NetScope-3 §4.3). A NativeAOT image keeps the names of its
/// compiled methods in its stack trace metadata (<see cref="NativeAotReader"/>); they become function names.
/// A native PE that is not NativeAOT has no .NET data: Ghidra already reads its exports, imports and .pdata,
/// so NetScope has nothing to add and says so.
/// </summary>
public static class NativeImageAnnotations
{
    public static GhidraAnnotations? TryBuild(string path, out string? error)
    {
        byte[] file;
        PEHeaders headers;
        try
        {
            file = File.ReadAllBytes(path);
            using var pe = new PEReader(new MemoryStream(file, writable: false));
            headers = pe.PEHeaders;
            if (headers.PEHeader == null) { error = null; return null; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            error = null; // the loader's reason (not a PE file, cannot open) stands
            return null;
        }

        var reader = NativeAotReader.TryRead(file, headers, out var reason);
        if (reader == null)
        {
            error = reason == NativeAotReader.NoModuleHeader
                ? "native PE without .NET metadata: Ghidra reads its exports, imports and exception data itself, and NetScope has nothing to add (export-ghidra needs ReadyToRun, C++/CLI or NativeAOT code)"
                : "NativeAOT image: " + reason;
            return null;
        }

        var ann = new GhidraAnnotations
        {
            SourceFile = path,
            AssemblyName = Path.GetFileNameWithoutExtension(path),
            Architecture = R2RReader.ArchitectureName(headers.CoffHeader.Machine),
            Kind = $"NativeAOT (module header {reader.MajorVersion}.{reader.MinorVersion})",
        };
        // Identical method bodies are folded into one: keep the first name at an address and count the rest.
        int folded = 0;
        int previous = -1;
        foreach (var m in reader.Methods)
        {
            if (m.Rva == previous) { folded++; continue; }
            previous = m.Rva;
            ann.Functions.Add(new GhidraFunction(m.Rva, m.Name, null));
        }
        ann.Warnings.Add($"NativeAOT image: {ann.Functions.Count} method name(s) taken from its stack trace metadata (no PDB needed)" +
                         (folded > 0 ? $"; {folded} more name(s) share an address with another method (identical code folded) and are not used" : ""));
        error = null;
        return ann;
    }
}
