using System.Text;
using NetScope.Calls;
using NetScope.Diagnostics;
using NetScope.Loading;
using NetScope.Reporting;

namespace NetScope.Pipeline;

/// <summary>Writes the report to stdout, to one file, or to a directory with one report per assembly.</summary>
public abstract class OutputSink
{
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    protected OutputSink(IReporter reporter) => Reporter = reporter;

    /// <summary>Reporter used to render assemblies (rendering may run on worker threads).</summary>
    public IReporter Reporter { get; }

    public abstract bool FirstFlag(int index);
    public abstract void Begin();
    public abstract void WriteAssembly(LoadedAssembly asm, string text);
    public abstract void WriteCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies);
    public abstract void End();

    public static OutputSink Create(AnalysisOptions o, TextWriter stdout, Func<IReporter> reporterFactory)
    {
        if (o.Out == null) return new SingleSink(stdout, reporterFactory(), ownsWriter: false);
        bool directory = Directory.Exists(o.Out) || o.Out.EndsWith(Path.DirectorySeparatorChar) || o.Out.EndsWith(Path.AltDirectorySeparatorChar);
        if (directory) return new DirectorySink(o.Out, reporterFactory);
        var parent = Path.GetDirectoryName(Path.GetFullPath(o.Out));
        if (parent != null) Directory.CreateDirectory(parent);
        return new SingleSink(new StreamWriter(o.Out, false, Utf8), reporterFactory(), ownsWriter: true);
    }

    private sealed class SingleSink : OutputSink
    {
        private readonly TextWriter _writer;
        private readonly bool _owns;

        public SingleSink(TextWriter writer, IReporter reporter, bool ownsWriter) : base(reporter)
        {
            _writer = writer;
            _owns = ownsWriter;
        }

        public override bool FirstFlag(int index) => index == 0;
        public override void Begin() => _writer.Write(Reporter.BeginDocument());

        public override void WriteAssembly(LoadedAssembly asm, string text)
        {
            _writer.Write(text);
            _writer.Flush();
        }

        public override void WriteCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies) =>
            _writer.Write(Reporter.RenderCallGraph(edges, afterAssemblies));

        public override void End()
        {
            _writer.Write(Reporter.EndDocument());
            _writer.Flush();
            if (_owns) _writer.Dispose();
        }
    }

    private sealed class DirectorySink : OutputSink
    {
        private readonly string _dir;
        private readonly Func<IReporter> _factory;
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        public DirectorySink(string dir, Func<IReporter> factory) : base(factory())
        {
            _dir = dir;
            _factory = factory;
            Directory.CreateDirectory(dir);
        }

        public override bool FirstFlag(int index) => true;
        public override void Begin() { }
        public override void End() { }

        private string UniquePath(string baseName)
        {
            var name = baseName;
            for (int i = 2; !_used.Add(name); i++) name = $"{Path.GetFileNameWithoutExtension(baseName)}.{i}{Path.GetExtension(baseName)}";
            return Path.Combine(_dir, name);
        }

        public override void WriteAssembly(LoadedAssembly asm, string text)
        {
            var reporter = _factory();
            var path = UniquePath($"{asm.FileName}.{reporter.Extension}");
            File.WriteAllText(path, reporter.BeginDocument() + text + reporter.EndDocument(), Utf8);
            Log.Info("written " + path);
        }

        public override void WriteCallGraph(IReadOnlyList<CallGraphEdge> edges, bool afterAssemblies)
        {
            var reporter = _factory();
            var path = UniquePath($"callgraph.{reporter.Extension}");
            File.WriteAllText(path, reporter.BeginDocument() + reporter.RenderCallGraph(edges, false) + reporter.EndDocument(), Utf8);
            Log.Info("written " + path);
        }
    }
}
