namespace NetScope.Diff;

public enum MethodStatus { Added, Removed, Changed, Unchanged }

/// <summary>One method present in either version, matched by full name and signature (never by token).</summary>
public sealed class MethodDiff
{
    /// <summary>Identity: type, name, generic arity, parameter and return types.</summary>
    public required string Key { get; init; }
    public required string FullName { get; init; }
    public required string Signature { get; init; }
    public required MethodStatus Status { get; init; }
    /// <summary>Unified diff of the IL listing (changed methods).</summary>
    public string? ILDiff { get; init; }
    /// <summary>Unified diff of the pseudocode (--pseudo, changed methods).</summary>
    public string? PseudoDiff { get; init; }
    public IReadOnlyList<string> CallsAdded { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CallsRemoved { get; init; } = Array.Empty<string>();
    public int? OldComplexity { get; init; }
    public int? NewComplexity { get; init; }
    public int? OldBlocks { get; init; }
    public int? NewBlocks { get; init; }

    public string StatusText => Status.ToString().ToLowerInvariant();
}

/// <summary>A change outside method bodies: type, field, attribute, assembly reference or P/Invoke.</summary>
public sealed record MetadataChange(string Kind, string Change, string Item, string? Detail = null);

public sealed class AssemblyDiff
{
    public required string Name { get; init; }
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public List<MethodDiff> Methods { get; } = new();
    public List<MetadataChange> Metadata { get; } = new();
    /// <summary>--audit: findings of the new version that the old one did not have.</summary>
    public List<Audit.Finding> NewFindings { get; } = new();
    public List<string> Errors { get; } = new();

    public int Count(MethodStatus status) => Methods.Count(m => m.Status == status);

    public bool HasDifferences => Methods.Any(m => m.Status != MethodStatus.Unchanged) || Metadata.Count > 0;
}

public sealed class DiffResult
{
    public List<AssemblyDiff> Assemblies { get; } = new();
    /// <summary>Directory mode: assemblies present on one side only (paths).</summary>
    public List<string> OnlyOld { get; } = new();
    public List<string> OnlyNew { get; } = new();

    public bool HasDifferences => OnlyOld.Count > 0 || OnlyNew.Count > 0 || Assemblies.Any(a => a.HasDifferences);
}
