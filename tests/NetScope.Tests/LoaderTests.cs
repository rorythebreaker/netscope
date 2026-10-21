using System.Reflection.PortableExecutable;
using NetScope.Loading;
using NetScope.Pipeline;
using Xunit;

namespace NetScope.Tests;

/// <summary>
/// Locks down file detection by PE/CLR header (TZ-NetScope-2 §6): the type of a file is decided by
/// its contents, not its extension. One test per case of the §6.3 table, plus the §6.4 CLI criteria.
/// Fixtures are synthesized by byte-patching the real managed sample so the tests run identically on
/// every platform (no dependency on an OS-native DLL such as kernel32.dll).
/// </summary>
public sealed class LoaderTests : IDisposable
{
    private readonly string _dir = TestEnv.TempDir();
    private readonly byte[] _managed = File.ReadAllBytes(TestEnv.SamplesPath);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static LoadedAssembly? Load(string path, out string? reason) =>
        new AssemblyLoader().TryLoad(0, path, out reason);

    // --- §6.3: managed assembly is accepted whatever its extension (rule 6.2.3) ---

    [Theory]
    [InlineData("renamed.bin")]      // foreign extension
    [InlineData("renamed.module")]   // foreign extension
    [InlineData("noextension")]      // no extension at all
    [InlineData("library.winmd")]    // Windows metadata
    public void ManagedAssemblyAcceptedRegardlessOfExtension(string name)
    {
        var path = Write(name, _managed);
        using var asm = Load(path, out var reason);
        Assert.NotNull(asm);
        Assert.Null(reason);
        Assert.Equal("NetScope.Samples", asm!.Name);
    }

    // --- §6.3: native PE (no CLR header) is skipped as "not a managed assembly" ---

    [Fact]
    public void NativePeWithoutClrHeaderIsSkipped()
    {
        // Zero the COR20 (data directory index 14) entry: a structurally valid PE with no CLR header,
        // the same code path a real native DLL (e.g. kernel32.dll) takes.
        var bytes = (byte[])_managed.Clone();
        ClearCorDirectory(bytes);
        var path = Write("native.dll", bytes);

        using var asm = Load(path, out var reason);
        Assert.Null(asm);
        Assert.NotNull(reason);
        Assert.Contains("no CLR header", reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- §6.3: a .dll that is not a PE at all (text, renamed archive) ---

    [Fact]
    public void DllExtensionButNotPeIsSkippedNotCrashed()
    {
        var text = System.Text.Encoding.ASCII.GetBytes(new string('x', 256));
        var path = Write("text.dll", text);

        using var asm = Load(path, out var reason);
        Assert.Null(asm);
        Assert.NotNull(reason);
        Assert.Contains("MZ", reason!); // "not a PE file (no MZ header)"
    }

    [Fact]
    public void ZipArchiveRenamedToDllIsSkipped()
    {
        // ZIP local-file-header signature "PK\x03\x04" — not "MZ".
        var zip = new byte[256];
        zip[0] = (byte)'P'; zip[1] = (byte)'K'; zip[2] = 3; zip[3] = 4;
        var path = Write("archive.dll", zip);

        using var asm = Load(path, out var reason);
        Assert.Null(asm);
        Assert.Contains("not a PE file", reason!);
    }

    // --- §6.3: truncated / corrupted PE — skip indicating where parsing stopped ---

    [Fact]
    public void TruncatedPeIsSkippedPointingAtBrokenHeader()
    {
        // Keep the MZ signature (and enough bytes to pass the size gate) but cut the image short so
        // the PE header cannot be parsed.
        var bytes = _managed.Take(200).ToArray();
        Assert.True(bytes.Length >= 128);
        Assert.Equal((byte)'M', bytes[0]);
        var path = Write("truncated.dll", bytes);

        using var asm = Load(path, out var reason);
        Assert.Null(asm);
        Assert.NotNull(reason);
        Assert.Contains("PE", reason!); // "corrupted PE image: ..." names the header that failed
    }

    // --- §6.3: mixed-mode C++/CLI — accepted (has both a CLR header and native code) ---

    [Fact]
    public void MixedModeImageIsAccepted()
    {
        // Clear the COR ILOnly flag: the loader then treats the image as mixed-mode C++/CLI and still
        // accepts it (CLR header present).
        var bytes = (byte[])_managed.Clone();
        ClearIlOnlyFlag(bytes);
        var path = Write("mixed.dll", bytes);

        using var asm = Load(path, out var reason);
        Assert.NotNull(asm);
        Assert.Null(reason);
        Assert.True(asm!.IsMixedMode);
    }

    // --- §6.4 on real OS binaries (Windows): complements the synthesized fixtures above ---

    /// <summary>§6.4 names the real kernel32.dll: it must be skipped with a clear reason and exit code 3.</summary>
    [Fact]
    public void RealKernel32IsSkippedAsNativePe()
    {
        var kernel32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(kernel32)) return;

        using var asm = Load(kernel32, out var reason);
        Assert.Null(asm);
        Assert.Contains("no CLR header", reason, StringComparison.OrdinalIgnoreCase);

        var r = TestEnv.Run(kernel32, "--no-color");
        Assert.Equal(Analyzer.ExitNothingOpened, r.ExitCode);
        Assert.Contains("kernel32.dll", r.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no CLR header", r.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", r.Stdout);
    }

    /// <summary>A real C++/CLI image shipped with .NET Framework: CLR header and native code together.</summary>
    [Fact]
    public void RealMixedModeImageIsAccepted()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "System.EnterpriseServices.Wrapper.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;

        using var asm = Load(path, out var reason);
        Assert.NotNull(asm);
        Assert.Null(reason);
        Assert.True(asm!.IsMixedMode);
    }

    // --- §6.3: empty file / directory caught by a mask — skipped without an error ---

    [Fact]
    public void EmptyFileIsSkippedWithoutError()
    {
        var path = Write("empty.dll", Array.Empty<byte>());
        using var asm = Load(path, out var reason);
        Assert.Null(asm);
        Assert.Equal("empty file", reason);
    }

    [Fact]
    public void DirectoryInAMaskIsSkippedWithoutThrowing()
    {
        var sub = Path.Combine(_dir, "sub.dll");
        Directory.CreateDirectory(sub);
        using var asm = Load(sub, out var reason); // must not throw
        Assert.Null(asm);
        Assert.NotNull(reason);
    }

    // --- §6.4: CLI acceptance criteria ---

    [Fact]
    public void CliAnalyzesManagedAssemblyRenamedToBin()
    {
        var path = Write("renamed.bin", _managed);
        var r = TestEnv.Run(path, "--methods", "--quiet");
        Assert.Equal(Analyzer.ExitOk, r.ExitCode);
        Assert.Contains("NetScope.Samples", r.Stdout);
    }

    [Fact]
    public void CliReturnsNothingOpenedWhenNoFileIsManaged()
    {
        var path = Write("text.dll", System.Text.Encoding.ASCII.GetBytes(new string('x', 256)));
        var r = TestEnv.Run(path, "--quiet");
        Assert.Equal(Analyzer.ExitNothingOpened, r.ExitCode);
    }

    [Fact]
    public void CliSucceedsWhenAtLeastOneFileIsManaged()
    {
        var text = Write("text.dll", System.Text.Encoding.ASCII.GetBytes(new string('x', 256)));
        var managed = Write("ok.bin", _managed);
        var r = TestEnv.Run(text, managed, "--methods", "--quiet");
        // Exit 3 happens only if NO file is managed; here the skipped text file makes it partial (1),
        // and the managed assembly is still analyzed.
        Assert.NotEqual(Analyzer.ExitNothingOpened, r.ExitCode);
        Assert.Equal(Analyzer.ExitPartial, r.ExitCode);
        Assert.Contains("NetScope.Samples", r.Stdout);
    }

    // --- byte-patching helpers ---

    /// <summary>Zeroes the 8-byte COR20 data directory entry (index 14) in the PE optional header.</summary>
    private static void ClearCorDirectory(byte[] image)
    {
        using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(image));
        var h = pe.PEHeaders;
        Assert.NotNull(h.PEHeader);
        int dirBase = h.PEHeaderStartOffset + (h.PEHeader!.Magic == PEMagic.PE32Plus ? 112 : 96);
        int corEntry = dirBase + 14 * 8;
        for (int i = 0; i < 8; i++) image[corEntry + i] = 0;
    }

    /// <summary>Clears the ILOnly bit (0x1) in the COR header Flags field (offset 16 from its start).</summary>
    private static void ClearIlOnlyFlag(byte[] image)
    {
        using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(image));
        int flagsOffset = pe.PEHeaders.CorHeaderStartOffset + 16;
        Assert.True(flagsOffset > 0);
        image[flagsOffset] &= 0xFE; // drop bit 0
    }
}
