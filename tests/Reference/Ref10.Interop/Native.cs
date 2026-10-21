using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ref.Interop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct Union
    {
        [FieldOffset(0)] public int Int;
        [FieldOffset(0)] public float Float;
    }

    public static class Kernel32
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void GetSystemTime(out SystemTime time);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetEnvironmentVariableW(string name, StringBuilder buffer, uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetTickCount64")]
        public static extern ulong Ticks();
    }

    public static partial class LibC
    {
        [LibraryImport("libc", EntryPoint = "getpid")]
        public static partial int GetPid();

        [LibraryImport("libc", EntryPoint = "strlen", StringMarshalling = StringMarshalling.Utf8)]
        public static partial nuint StrLen(string s);

        [LibraryImport("libc", EntryPoint = "qsort")]
        public static unsafe partial void QSort(void* data, nuint count, nuint size, delegate* unmanaged[Cdecl]<void*, void*, int> compare);
    }

    public static unsafe class Callbacks
    {
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int CompareInts(void* a, void* b) => (*(int*)a).CompareTo(*(int*)b);

        public static void Sort(int[] values)
        {
            fixed (int* p = values)
                LibC.QSort(p, (nuint)values.Length, sizeof(int), &CompareInts);
        }

        public static int Apply(delegate*<int, int, int> op, int a, int b) => op(a, b);

        private static int Add(int a, int b) => a + b;

        public static int AddViaPointer(int a, int b) => Apply(&Add, a, b);

        public delegate int BinaryOp(int a, int b);

        public static int CallNative(IntPtr fn, int a, int b)
        {
            var op = Marshal.GetDelegateForFunctionPointer<BinaryOp>(fn);
            return op(a, b);
        }

        public static IntPtr Export(BinaryOp op) => Marshal.GetFunctionPointerForDelegate(op);
    }

    public static class Platform
    {
        public static string Describe()
        {
            if (OperatingSystem.IsWindows())
            {
                Kernel32.GetSystemTime(out var t);
                var sb = new StringBuilder(260);
                uint n = Kernel32.GetEnvironmentVariableW("PATH", sb, (uint)sb.Capacity);
                return $"{t.Year}-{t.Month:00}-{t.Day:00}, PATH has {n} chars, up {Kernel32.Ticks()} ms";
            }
            return "pid " + LibC.GetPid() + ", " + LibC.StrLen("hello") + " bytes";
        }

        public static float Reinterpret(int bits) => new Union { Int = bits }.Float;

        public static string PtrToString(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? string.Empty;
    }
}
