using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;
using NetScope.Diagnostics;

namespace NetScope.Loading;

/// <summary>
/// Resolves assembly references for a group of input files:
/// first among the input files, then next to the referencing file, then in --ref-path directories,
/// finally in the installed framework. Referenced files are opened read-only and cached for the whole run.
/// </summary>
public sealed class ReferenceResolver : IDisposable
{
    private readonly Dictionary<string, LoadedAssembly> _group = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<string> _refPaths;
    private readonly ConcurrentDictionary<string, Lazy<MetadataFile?>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UniversalAssemblyResolver> _framework = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _reportedMissing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _frameworkLock = new();

    public ReferenceResolver(IEnumerable<LoadedAssembly> group, IReadOnlyList<string> refPaths)
    {
        foreach (var asm in group) _group.TryAdd(asm.Name, asm);
        _refPaths = refPaths;
    }

    public bool IsInGroup(string assemblyName) => _group.ContainsKey(assemblyName);

    public IAssemblyResolver For(LoadedAssembly asm) => new Scoped(this, asm);

    /// <summary>Checks every assembly reference of a file and warns once per missing dependency.</summary>
    public void CheckReferences(LoadedAssembly asm)
    {
        var md = asm.Metadata;
        var missing = new List<string>();
        foreach (var h in md.AssemblyReferences)
        {
            var reference = new AssemblyReferenceAdapter(md, md.GetAssemblyReference(h));
            if (_group.ContainsKey(reference.Name) || FindFile(asm, reference) != null) continue;
            if (_reportedMissing.TryAdd(reference.Name, 0)) missing.Add(reference.Name);
        }
        if (missing.Count > 0)
            Log.Warning(asm.FileName, $"dependencies not found: {string.Join(", ", missing)}; their types are shown by metadata names only (use --ref-path)");
    }

    private void ReportMissing(LoadedAssembly asm, string name)
    {
        if (_reportedMissing.TryAdd(name, 0))
            Log.Warning(asm.FileName, $"dependency '{name}' not found; its types are shown by metadata names only (use --ref-path)");
    }

    private string? FindFile(LoadedAssembly asm, IAssemblyReference reference)
    {
        var dir = Path.GetDirectoryName(asm.FilePath);
        var candidates = new List<string>();
        if (dir != null) candidates.Add(dir);
        candidates.AddRange(_refPaths);
        foreach (var d in candidates)
        {
            foreach (var ext in new[] { ".dll", ".exe", ".winmd" })
            {
                var p = Path.Combine(d, reference.Name + ext);
                if (File.Exists(p)) return p;
            }
        }
        try
        {
            lock (_frameworkLock) return FrameworkResolver(asm).FindAssemblyFile(reference);
        }
        catch
        {
            return null;
        }
    }

    private UniversalAssemblyResolver FrameworkResolver(LoadedAssembly asm)
    {
        var key = asm.TargetFramework ?? asm.MetadataVersion;
        return _framework.GetOrAdd(key, _ =>
            new UniversalAssemblyResolver(asm.FilePath, false, asm.TargetFramework, null, PEStreamOptions.PrefetchMetadata, MetadataReaderOptions.ApplyWindowsRuntimeProjections));
    }

    private MetadataFile? Resolve(LoadedAssembly asm, IAssemblyReference reference)
    {
        if (_group.TryGetValue(reference.Name, out var member))
            return member.DecompilerFile;
        var path = FindFile(asm, reference);
        if (path == null)
        {
            ReportMissing(asm, reference.Name);
            return null;
        }
        return OpenCached(path);
    }

    private MetadataFile? OpenCached(string path) =>
        _files.GetOrAdd(path, p => new Lazy<MetadataFile?>(() =>
        {
            try
            {
                return new PEFile(p, PEStreamOptions.PrefetchMetadata, MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            }
            catch (Exception ex)
            {
                Log.Warning(p, "cannot open dependency: " + ex.Message);
                return null;
            }
        })).Value;

    public void Dispose()
    {
        foreach (var f in _files.Values)
            if (f.IsValueCreated) f.Value?.Dispose();
    }

    private sealed class Scoped : IAssemblyResolver
    {
        private readonly ReferenceResolver _owner;
        private readonly LoadedAssembly _asm;

        public Scoped(ReferenceResolver owner, LoadedAssembly asm)
        {
            _owner = owner;
            _asm = asm;
        }

        public MetadataFile? Resolve(IAssemblyReference reference) => _owner.Resolve(_asm, reference);

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
        {
            var dir = Path.GetDirectoryName(mainModule.FileName);
            if (dir == null) return null;
            var path = Path.Combine(dir, moduleName);
            return File.Exists(path) ? _owner.OpenCached(path) : null;
        }

        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

        public IDisposable BeginSnapshot() => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class AssemblyReferenceAdapter : IAssemblyReference
    {
        public AssemblyReferenceAdapter(MetadataReader md, System.Reflection.Metadata.AssemblyReference r)
        {
            Name = md.GetString(r.Name);
            Version = r.Version;
            Culture = r.Culture.IsNil ? "" : md.GetString(r.Culture);
            var token = r.PublicKeyOrToken.IsNil ? null : md.GetBlobBytes(r.PublicKeyOrToken);
            PublicKeyToken = token;
            IsRetargetable = (r.Flags & System.Reflection.AssemblyFlags.Retargetable) != 0;
            IsWindowsRuntime = (r.Flags & System.Reflection.AssemblyFlags.WindowsRuntime) != 0;
            FullName = $"{Name}, Version={Version}, Culture={(Culture.Length == 0 ? "neutral" : Culture)}";
        }

        public string Name { get; }
        public string FullName { get; }
        public Version? Version { get; }
        public string Culture { get; }
        public byte[]? PublicKeyToken { get; }
        public bool IsWindowsRuntime { get; }
        public bool IsRetargetable { get; }
    }
}
