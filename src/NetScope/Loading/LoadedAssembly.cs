using System.IO.MemoryMappedFiles;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace NetScope.Loading;

/// <summary>
/// A managed PE file opened read-only through a memory-mapped view.
/// The file is never modified and never executed by this type.
/// </summary>
public sealed unsafe class LoadedAssembly : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Lazy<PEFile> _decompilerFile;
    private bool _disposed;

    internal LoadedAssembly(int index, string path, MemoryMappedFile map, MemoryMappedViewAccessor view, byte* pointer, int length)
    {
        Index = index;
        FilePath = path;
        FileName = System.IO.Path.GetFileName(path);
        _map = map;
        _view = view;
        ImageStart = pointer;
        Length = length;
        PE = new PEReader(pointer, length);
        _decompilerFile = new Lazy<PEFile>(() => new PEFile(FilePath, PE, MetadataReaderOptions.ApplyWindowsRuntimeProjections), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Position of the file in the input list; output follows this order.</summary>
    public int Index { get; }
    public string FilePath { get; }
    public string FileName { get; }
    public byte* ImageStart { get; }
    public int Length { get; }
    public PEReader PE { get; }
    public MetadataReader Metadata { get; internal set; } = null!;

    /// <summary>Simple assembly name (or module name for a netmodule).</summary>
    public string Name { get; internal set; } = "";
    public Version? Version { get; internal set; }
    public string? TargetFramework { get; internal set; }
    public string MetadataVersion { get; internal set; } = "";
    public bool IsMixedMode { get; internal set; }
    public bool IsReadyToRun { get; internal set; }
    public Machine Machine { get; internal set; }
    /// <summary>IL-only image without a 32-bit requirement: runs on any architecture.</summary>
    public bool IsAnyCpu { get; internal set; }
    public string Architecture => IsAnyCpu ? "anycpu" : Native.R2RReader.ArchitectureName(Machine);
    public List<string> Notes { get; } = new();
    /// <summary>Portable PDB, when one was found and matches the assembly (and --no-pdb is not set).</summary>
    public Symbols.DebugSymbols? Symbols { get; set; }

    /// <summary>ICSharpCode.Decompiler view over the same mapped image (created on demand).</summary>
    public PEFile DecompilerFile => _decompilerFile.Value;

    /// <summary>Raw bytes of the PE image starting at an RVA, up to the end of its section.</summary>
    public ReadOnlySpan<byte> GetRvaSpan(int rva)
    {
        var block = PE.GetSectionData(rva);
        return block.Length == 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(block.Pointer, block.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Symbols?.Dispose();
        PE.Dispose();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }
}
