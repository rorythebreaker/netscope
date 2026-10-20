using System.IO.MemoryMappedFiles;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NetScope.Loading;

public interface IAssemblyLoader
{
    /// <summary>Opens a file if it is a managed PE image. Returns null and a reason otherwise.</summary>
    LoadedAssembly? TryLoad(int index, string path, out string? reason);
}

public sealed class AssemblyLoader : IAssemblyLoader
{
    private const uint ReadyToRunSignature = 0x00525452; // 'RTR'

    public unsafe LoadedAssembly? TryLoad(int index, string path, out string? reason)
    {
        reason = null;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        }
        catch (FileNotFoundException) { reason = "file not found"; return null; }
        catch (DirectoryNotFoundException) { reason = "file not found"; return null; }
        catch (Exception ex) { reason = "cannot open file: " + ex.Message; return null; }

        long length = stream.Length;
        if (length < 128)
        {
            stream.Dispose();
            reason = length == 0 ? "empty file" : "not a PE file (too small)";
            return null;
        }
        if (length > int.MaxValue)
        {
            stream.Dispose();
            reason = "file is larger than 2 GB";
            return null;
        }
        // Quick 'MZ' check before mapping.
        Span<byte> mz = stackalloc byte[2];
        stream.ReadExactly(mz);
        stream.Position = 0;
        if (mz[0] != (byte)'M' || mz[1] != (byte)'Z')
        {
            stream.Dispose();
            reason = "not a PE file (no MZ header)";
            return null;
        }

        MemoryMappedFile map;
        MemoryMappedViewAccessor view;
        try
        {
            map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch (Exception ex)
        {
            stream.Dispose();
            reason = "cannot map file: " + ex.Message;
            return null;
        }

        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        ptr += view.PointerOffset;
        var asm = new LoadedAssembly(index, path, map, view, ptr, (int)length);
        try
        {
            if (!Inspect(asm, out reason))
            {
                asm.Dispose();
                return null;
            }
            return asm;
        }
        catch (BadImageFormatException ex)
        {
            asm.Dispose();
            reason = "corrupted PE image: " + ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            asm.Dispose();
            reason = "cannot read image: " + ex.Message;
            return null;
        }
    }

    private static bool Inspect(LoadedAssembly asm, out string? reason)
    {
        reason = null;
        var headers = asm.PE.PEHeaders; // throws BadImageFormatException on a broken PE header
        asm.Machine = headers.CoffHeader.Machine;
        var cor = headers.CorHeader;
        if (cor == null || !asm.PE.HasMetadata)
        {
            reason = LooksLikeNativeAot(headers)
                ? "NativeAOT image: no metadata or IL to analyze (not supported in this version)"
                : "not a managed assembly (no CLR header)";
            return false;
        }

        var md = asm.PE.GetMetadataReader();
        asm.Metadata = md;
        asm.MetadataVersion = md.MetadataVersion;
        if (md.IsAssembly)
        {
            var def = md.GetAssemblyDefinition();
            asm.Name = md.GetString(def.Name);
            asm.Version = def.Version;
            asm.TargetFramework = ReadTargetFramework(md, def);
        }
        else
        {
            asm.Name = md.GetString(md.GetModuleDefinition().Name);
            asm.Notes.Add("module without assembly manifest");
        }

        var nativeHeader = cor.ManagedNativeHeaderDirectory;
        if (nativeHeader.Size >= 16 && nativeHeader.RelativeVirtualAddress != 0)
        {
            var span = asm.GetRvaSpan(nativeHeader.RelativeVirtualAddress);
            if (span.Length >= 4 && BitConverter.ToUInt32(span) == ReadyToRunSignature)
            {
                asm.IsReadyToRun = true;
                asm.Notes.Add("ReadyToRun image: precompiled native code is present");
            }
        }

        // ReadyToRun images clear the ILOnly flag too; only C++/CLI images are really mixed.
        if ((cor.Flags & CorFlags.ILOnly) == 0 && !asm.IsReadyToRun)
        {
            asm.IsMixedMode = true;
            asm.Notes.Add("mixed-mode image (C++/CLI): native methods are outside the managed part");
        }

        // Machine field of R2R images may be XOR-ed with an OS id; normalize for display.
        asm.Machine = Native.R2RReader.NormalizeMachine(headers.CoffHeader.Machine);
        asm.IsAnyCpu = asm.Machine == Machine.I386 && (cor.Flags & CorFlags.ILOnly) != 0 &&
                       (cor.Flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit)) == 0 && !asm.IsReadyToRun;
        return true;
    }

    private static bool LooksLikeNativeAot(PEHeaders headers)
    {
        foreach (var s in headers.SectionHeaders)
            if (s.Name is ".managed" or "__managedcode" or ".managedcode" or "hydrated" or ".hydrated")
                return true;
        return false;
    }

    private static string? ReadTargetFramework(MetadataReader md, AssemblyDefinition def)
    {
        foreach (var h in def.GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(h);
            if (Metadata.Names.GetAttributeTypeName(md, attr) != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
            try
            {
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 1) return null;
                return blob.ReadSerializedString();
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }
        return null;
    }
}
