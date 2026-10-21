using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NetScope.Loading;

namespace NetScope.Symbols;

/// <summary>
/// Finds the Portable PDB of an assembly: embedded in the image, then next to it, then in the --pdb-path
/// directories. Nothing is downloaded. A PDB is used only when its ID matches the CodeView record.
/// </summary>
public static class SymbolLoader
{
    private static readonly byte[] PortableSignature = "BSJB"u8.ToArray();
    private static readonly byte[] WindowsSignature = "Microsoft C/C++ MSF 7.00"u8.ToArray();

    /// <summary>Returns the symbols, or null with warnings that explain why none were used.</summary>
    public static DebugSymbols? Load(LoadedAssembly assembly, IReadOnlyList<string> pdbPaths, List<string> warnings)
    {
        DebugDirectoryEntry? embedded = null;
        (DebugDirectoryEntry Entry, CodeViewDebugDirectoryData Data)? codeView = null;
        try
        {
            foreach (var e in assembly.PE.ReadDebugDirectory())
            {
                if (e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb) embedded ??= e;
                else if (e.Type == DebugDirectoryEntryType.CodeView && codeView == null) codeView = (e, assembly.PE.ReadCodeViewDebugDirectoryData(e));
            }
        }
        catch (BadImageFormatException ex)
        {
            warnings.Add("cannot read the debug directory: " + ex.Message);
            return null;
        }

        // 1. Embedded portable PDB.
        if (embedded is { } embeddedEntry)
        {
            try
            {
                var provider = assembly.PE.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntry);
                if (Matches(provider.GetMetadataReader(), codeView, out var why)) return new DebugSymbols(provider, "embedded");
                provider.Dispose();
                warnings.Add("embedded PDB ignored: " + why);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException)
            {
                warnings.Add("embedded PDB is unreadable: " + ex.Message);
            }
        }

        if (codeView == null) return null; // no reference to an external PDB

        // 2. Next to the assembly, 3. --pdb-path directories.
        var fileName = PdbFileName(codeView.Value.Data.Path, assembly.FilePath);
        var candidates = new List<string> { Path.Combine(Path.GetDirectoryName(assembly.FilePath) ?? ".", fileName) };
        candidates.AddRange(pdbPaths.Select(d => Path.Combine(d, fileName)));
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var symbols = TryOpen(path, codeView.Value, warnings);
            if (symbols != null) return symbols;
        }
        return null;
    }

    private static DebugSymbols? TryOpen(string path, (DebugDirectoryEntry Entry, CodeViewDebugDirectoryData Data) codeView, List<string> warnings)
    {
        FileStream stream;
        try
        {
            stream = File.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"cannot open {path}: {ex.Message}");
            return null;
        }

        var header = new byte[WindowsSignature.Length];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (read >= WindowsSignature.Length && header.AsSpan().SequenceEqual(WindowsSignature))
        {
            stream.Dispose();
            warnings.Add($"{path} is a Windows PDB; only Portable PDB is supported, symbols are not used");
            return null;
        }
        if (read < PortableSignature.Length || !header.AsSpan(0, PortableSignature.Length).SequenceEqual(PortableSignature))
        {
            stream.Dispose();
            warnings.Add($"{path} is not a Portable PDB, symbols are not used");
            return null;
        }

        try
        {
            var provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata);
            if (Matches(provider.GetMetadataReader(), codeView, out var why)) return new DebugSymbols(provider, path);
            provider.Dispose();
            warnings.Add($"{path} does not belong to this assembly ({why}), symbols are not used");
        }
        catch (BadImageFormatException ex)
        {
            stream.Dispose();
            warnings.Add($"{path} is unreadable: {ex.Message}");
        }
        return null;
    }

    /// <summary>The PDB ID is the CodeView GUID followed by the CodeView time stamp.</summary>
    private static bool Matches(MetadataReader pdb, (DebugDirectoryEntry Entry, CodeViewDebugDirectoryData Data)? codeView, out string? why)
    {
        why = null;
        if (pdb.DebugMetadataHeader == null)
        {
            why = "no #Pdb stream";
            return false;
        }
        if (codeView == null) return true; // embedded PDB without a CodeView record: nothing to compare
        var id = pdb.DebugMetadataHeader.Id;
        if (id.Length != 20)
        {
            why = "unexpected PDB ID length";
            return false;
        }
        var guid = new Guid(id.AsSpan(0, 16));
        uint stamp = BitConverter.ToUInt32(id.AsSpan(16, 4));
        var (entry, data) = codeView.Value;
        if (guid != data.Guid || stamp != entry.Stamp)
        {
            why = $"PDB ID {guid:N}{stamp:X8} differs from the assembly's {data.Guid:N}{entry.Stamp:X8}";
            return false;
        }
        return true;
    }

    /// <summary>File name from the CodeView path; falls back to "&lt;assembly&gt;.pdb".</summary>
    private static string PdbFileName(string codeViewPath, string assemblyPath)
    {
        var name = codeViewPath.Substring(codeViewPath.LastIndexOfAny(Separators) + 1);
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(assemblyPath) + ".pdb";
    }

    internal static readonly char[] Separators = { '/', '\\' };
}
