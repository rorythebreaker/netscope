using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using NetScope.Catalog;
using NetScope.Cfg;
using NetScope.IL;
using NetScope.Loading;
using NetScope.Metadata;

namespace NetScope.Pseudo;

public sealed class PseudoResult
{
    public required string Text { get; init; }
    /// <summary>decompiler or fallback.</summary>
    public required string Source { get; init; }
    /// <summary>Why the decompiler was not used.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Counts decompilations abandoned after their time limit (their threads may keep running).
/// Shared by all generators of one run; once the limit is reached, the decompiler is no longer started.
/// </summary>
public sealed class AbandonedDecompilations
{
    private int _count;
    private int _alive;

    public AbandonedDecompilations(int limit) => Limit = limit;

    public int Limit { get; }
    public int Count => Volatile.Read(ref _count);
    /// <summary>Abandoned tasks that are still running.</summary>
    public int Alive => Volatile.Read(ref _alive);
    public bool Exhausted => Count >= Limit;

    internal void Add(Task task)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Increment(ref _alive);
        task.ContinueWith(_ => Interlocked.Decrement(ref _alive), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

public interface IPseudoGenerator
{
    PseudoResult Generate(MethodModel method, MethodIL? il, ControlFlowGraph? cfg);
}

/// <summary>
/// C# pseudocode via ICSharpCode.Decompiler (one decompiler per assembly, used by one thread),
/// with a per-method time limit and a three-address fallback.
/// </summary>
public sealed class PseudoGenerator : IPseudoGenerator
{
    private readonly LoadedAssembly _asm;
    private readonly IAssemblyResolver _resolver;
    private readonly MemberResolver _members;
    private readonly bool _raw;
    private readonly TimeSpan _timeout;
    private readonly AbandonedDecompilations _abandoned;
    private readonly Func<MethodModel, CancellationToken, string>? _decompile;
    private CSharpDecompiler? _decompiler;
    private string? _initError;

    public PseudoGenerator(LoadedAssembly asm, IAssemblyResolver resolver, MemberResolver members, bool raw, TimeSpan timeout, AbandonedDecompilations abandoned)
    {
        _asm = asm;
        _resolver = resolver;
        _members = members;
        _raw = raw;
        _timeout = timeout;
        _abandoned = abandoned;
    }

    /// <summary>For tests: a replacement decompiler and a shorter grace period.</summary>
    internal PseudoGenerator(MemberResolver members, TimeSpan timeout, AbandonedDecompilations abandoned, Func<MethodModel, CancellationToken, string> decompile, TimeSpan grace)
        : this(null!, null!, members, false, timeout, abandoned)
    {
        _decompile = decompile;
        Grace = grace;
    }

    /// <summary>Extra wait for decompiler code that does not check the cancellation token.</summary>
    internal TimeSpan Grace { get; } = TimeSpan.FromSeconds(2);

    public static DecompilerSettings CreateSettings(bool raw)
    {
        var s = new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false,
            UseDebugSymbols = false,
            LoadInMemory = false,
            // Keep LINQ as method calls: it mirrors the real call sites.
            QueryExpressions = false,
        };
        if (raw)
        {
            // Show compiler-generated shapes: closures, state machines and iterators as emitted.
            s.AnonymousMethods = false;
            s.AnonymousTypes = false;
            s.ExpressionTrees = false;
            s.YieldReturn = false;
            s.AsyncAwait = false;
            s.AsyncEnumerator = false;
            s.AwaitInCatchFinally = false;
            s.LocalFunctions = false;
            s.Discards = false;
        }
        return s;
    }

    private CSharpDecompiler? GetDecompiler()
    {
        if (_decompiler != null || _initError != null) return _decompiler;
        try
        {
            var settings = CreateSettings(_raw);
            // With a PDB the decompiler names locals as in the source.
            settings.UseDebugSymbols = _asm.Symbols != null;
            _decompiler = new CSharpDecompiler(_asm.DecompilerFile, _resolver, settings) { DebugInfoProvider = _asm.Symbols };
        }
        catch (Exception ex)
        {
            _initError = "decompiler cannot load the assembly: " + FirstLine(ex.Message);
        }
        return _decompiler;
    }

    public PseudoResult Generate(MethodModel method, MethodIL? il, ControlFlowGraph? cfg) =>
        Decompile(method, out var text) ? new PseudoResult { Text = text, Source = "decompiler" } : Fallback(method, il, cfg, text);

    /// <summary>Returns true with the text, or false with the reason the decompiler was not used.</summary>
    private bool Decompile(MethodModel method, out string result)
    {
        if (_abandoned.Exhausted)
        {
            result = $"decompiler disabled after {_abandoned.Count} decompilation(s) exceeded the time limit (--max-abandoned {_abandoned.Limit})";
            return false;
        }
        Func<CancellationToken, string> run;
        if (_decompile != null)
        {
            run = token => _decompile(method, token);
        }
        else
        {
            var decompiler = GetDecompiler();
            if (decompiler == null)
            {
                result = _initError!;
                return false;
            }
            run = token =>
            {
                decompiler.CancellationToken = token;
                return Clean(decompiler.DecompileAsString(method.Handle));
            };
        }

        using var cts = new CancellationTokenSource(_timeout);
        // A dedicated thread: an abandoned decompilation must not hold a thread pool thread.
        var task = Task.Factory.StartNew(() => run(cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var limitText = $"decompilation exceeded the time limit of {_timeout.TotalSeconds:0.###} s";
        try
        {
            // The decompiler checks the token cooperatively; the extra wait covers code that does not.
            if (task.Wait(_timeout + Grace))
            {
                result = task.Result;
                return true;
            }
            _decompiler = null; // still busy in the abandoned task: use a fresh instance next time
            _abandoned.Add(task);
            result = limitText;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            result = limitText;
        }
        catch (AggregateException ex)
        {
            var inner = ex.InnerException ?? ex;
            result = "decompiler failed: " + inner.GetType().Name + ": " + FirstLine(inner.Message);
        }
        return false;
    }

    private PseudoResult Fallback(MethodModel method, MethodIL? il, ControlFlowGraph? cfg, string reason)
    {
        if (il == null)
        {
            return new PseudoResult { Text = method.Signature + ";", Source = "fallback", Reason = reason };
        }
        try
        {
            cfg ??= new CfgBuilder().Build(il, _members);
            return new PseudoResult { Text = FallbackPseudo.Render(il, cfg, _members), Source = "fallback", Reason = reason };
        }
        catch (Exception ex)
        {
            return new PseudoResult { Text = "// pseudocode unavailable", Source = "fallback", Reason = reason + "; fallback failed: " + FirstLine(ex.Message) };
        }
    }

    /// <summary>Drops the leading using directives emitted for a single member and expands tabs.</summary>
    internal static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int start = 0;
        while (start < lines.Count && (lines[start].StartsWith("using ", StringComparison.Ordinal) || lines[start].Trim().Length == 0)) start++;
        return string.Join(Environment.NewLine, lines.Skip(start).Select(l => l.Replace("\t", "    ").TrimEnd())).Trim();
    }

    private static string FirstLine(string s)
    {
        int nl = s.AsSpan().IndexOfAny('\r', '\n');
        return nl < 0 ? s : s.Substring(0, nl);
    }
}
