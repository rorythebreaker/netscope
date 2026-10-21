using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ref.Unsafe
{
    public unsafe struct FixedHeader
    {
        public fixed byte Magic[4];
        public int Length;

        public bool HasMagic(byte a, byte b, byte c, byte d) => Magic[0] == a && Magic[1] == b && Magic[2] == c && Magic[3] == d;
    }

    public unsafe struct Vector3
    {
        public float X, Y, Z;

        public static float Dot(Vector3* a, Vector3* b) => a->X * b->X + a->Y * b->Y + a->Z * b->Z;
    }

    public static unsafe class Memory
    {
        public static int Sum(int[] values)
        {
            int sum = 0;
            fixed (int* start = values)
            {
                int* end = start + values.Length;
                for (int* p = start; p < end; p++) sum += *p;
            }
            return sum;
        }

        public static void Fill(byte* destination, int length, byte value)
        {
            for (int i = 0; i < length; i++) destination[i] = value;
        }

        public static string Reverse(string s)
        {
            int length = s.Length;
            char* buffer = stackalloc char[length];
            fixed (char* source = s)
            {
                for (int i = 0; i < length; i++) buffer[i] = source[length - 1 - i];
            }
            return new string(buffer, 0, length);
        }

        public static long Checksum(ReadOnlySpan<byte> data)
        {
            long sum = 0;
            fixed (byte* p = data)
            {
                long* q = (long*)p;
                int words = data.Length / 8;
                for (int i = 0; i < words; i++) sum ^= q[i];
                for (int i = words * 8; i < data.Length; i++) sum ^= p[i];
            }
            return sum;
        }

        public static void Copy(void* from, void* to, int bytes) => Buffer.MemoryCopy(from, to, bytes, bytes);

        public static int ReadInt(byte[] data, int offset)
        {
            fixed (byte* p = &data[offset]) return *(int*)p;
        }

        public static void Write(ref int target, int value) => target = value;

        public static ref int Element(int[] array, int index) => ref array[index];

        public static void Increment(int* p) => (*p)++;

        public static IntPtr Allocate(int bytes)
        {
            var p = NativeMemory.AllocZeroed((nuint)bytes);
            if (p == null) throw new OutOfMemoryException();
            return (IntPtr)p;
        }

        public static void Release(IntPtr p) => NativeMemory.Free((void*)p);

        public static int SizeOfHeader() => sizeof(FixedHeader);

        public static T ReadUnaligned<T>(byte* p) where T : unmanaged => System.Runtime.CompilerServices.Unsafe.ReadUnaligned<T>(p);

        public static int Pinned(string text)
        {
            var handle = GCHandle.Alloc(text, GCHandleType.Pinned);
            try
            {
                char* c = (char*)handle.AddrOfPinnedObject();
                return c[0];
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
