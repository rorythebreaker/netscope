using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

// Examples for the security rules NS001-NS010: every class has methods that must produce a finding of
// its rule (Positive*) and methods that must produce none at all (Negative*). Only analyzed, never run.
namespace MyApp.Audit
{
    public static class Ns001
    {
        public static void PositiveStartsShell() => Process.Start("cmd.exe", "/c dir");

        public static ProcessStartInfo PositiveBuildsStartInfo(string file) => new(file) { UseShellExecute = false };

        public static int NegativeReadsOwnProcess() => Environment.ProcessId + Process.GetCurrentProcess().Threads.Count;
    }

    public static class Ns002
    {
        public static Assembly PositiveLoadsFromPath(string path) => Assembly.LoadFrom(path);

        public static object? PositiveCreatesByName(string assembly, string type) => Activator.CreateInstance(assembly, type);

        public static object? NegativeCreatesByType() => Activator.CreateInstance(typeof(StringBuilder));
    }

    public static class Ns003
    {
        public static object? PositiveInvokesByName(string typeName) => Type.GetType(typeName)!.GetMethod("Run")!.Invoke(null, null);

        public static string NegativeLooksUpWithoutInvoke() => typeof(string).GetMethod("Trim", Type.EmptyTypes)!.Name;
    }

    public static class Ns004
    {
        public static object PositiveBinaryFormatter(Stream data) => new BinaryFormatter().Deserialize(data);

        public static Newtonsoft.Json.JsonSerializerSettings PositiveTypeNameHandling() => new() { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All };

        public static Newtonsoft.Json.JsonSerializerSettings NegativeTypeNameHandlingNone() => new() { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None };

        public static int[]? NegativeSystemTextJson(string json) => System.Text.Json.JsonSerializer.Deserialize<int[]>(json);
    }

    public static class Ns005
    {
        public static byte[] PositiveMd5(byte[] data) => MD5.HashData(data);

        public static void PositiveEcb(Aes aes) => aes.Mode = CipherMode.ECB;

        public static string PositiveRandomToken() => new Random().Next().ToString("x8");

        public static byte[] NegativeSha256(byte[] data) => SHA256.HashData(data);

        public static void NegativeCbc(Aes aes) => aes.Mode = CipherMode.CBC;

        public static int NegativeShuffleCards(int count) => new Random().Next(count);
    }

    public static class Ns006
    {
        [Obsolete("Sample of a disabled TLS check.")]
        public static void PositiveAcceptAnyCertificate() =>
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;

        [Obsolete("Sample of a TLS check.")]
        public static void NegativeChecksErrors() =>
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => errors == SslPolicyErrors.None;
    }

    public static class Ns007
    {
        // Constants are folded into one literal in IL; the source never holds a complete secret.
        private const string AwsPrefix = "AKIA";
        private const string PemHeader = "-----BEGIN " + "PRIVATE KEY-----";

        public static string PositiveConnectionString() => "Server=db;User Id=app;Password=" + "Example-1;";

        public static string PositiveAwsKey() => AwsPrefix + "EXAMPLEEXAMPLE12";

        public static string PositivePrivateKey() => PemHeader + "\nMIIB...\n";

        public static string NegativeIntegratedSecurity() => "Server=db;Integrated Security=true;";

        public static string NegativePrompt() => "Enter your password:";
    }

    public static class Ns008
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibrary(string name);

        public static Action PositiveFunctionPointer(IntPtr p) => Marshal.GetDelegateForFunctionPointer<Action>(p);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public static int NegativeSizeOf() => Marshal.SizeOf<long>();
    }

    public static class Ns009
    {
        public static System.Data.SqlClient.SqlCommand PositiveConcatenation(string name, object connection) =>
            new("SELECT * FROM Users WHERE Name = '" + name + "'", connection);

        public static void PositiveInterpolation(System.Data.SqlClient.SqlCommand command, int id) =>
            command.CommandText = $"DELETE FROM Orders WHERE Id = {id}";

        public static System.Data.SqlClient.SqlCommand NegativeParameters(object connection) =>
            new("SELECT * FROM Users WHERE Name = @name", connection);
    }

    [SupportedOSPlatform("windows")]
    public static class Ns010
    {
        public static void PositiveAutorun(string path) =>
            Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run").SetValue("app", path);

        public static object? NegativeReadsSetting() => Registry.CurrentUser.OpenSubKey(@"Software\MyApp")?.GetValue("Theme");
    }

    public static class Suppressed
    {
        /// <summary>Would be NS001; the attribute suppresses it (it is also compiled without CODE_ANALYSIS).</summary>
        [UnconditionalSuppressMessage("Security", "NS001:ProcessStart", Justification = "Sample of a suppressed finding.")]
        public static void StartsToolOnPurpose() => Process.Start("notepad.exe");
    }
}

// Stand-ins for APIs from packages the samples do not reference. Rules match by full type name.
namespace Newtonsoft.Json
{
    public enum TypeNameHandling { None = 0, Objects = 1, Arrays = 2, All = 3, Auto = 4 }

    public class JsonSerializerSettings
    {
        public TypeNameHandling TypeNameHandling { get; set; }
    }
}

namespace System.Data.SqlClient
{
    public class SqlCommand
    {
        public SqlCommand(string commandText, object connection)
        {
            CommandText = commandText;
            Connection = connection;
        }

        public string CommandText { get; set; }
        public object Connection { get; }
    }
}
