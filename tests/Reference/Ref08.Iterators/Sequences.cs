using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ref.Iterators
{
    public static class Sequences
    {
        public static IEnumerable<int> Range(int start, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            return RangeIterator(start, count);
        }

        private static IEnumerable<int> RangeIterator(int start, int count)
        {
            for (int i = 0; i < count; i++) yield return start + i;
        }

        public static IEnumerable<long> Fibonacci()
        {
            long a = 0, b = 1;
            while (true)
            {
                yield return a;
                (a, b) = (b, a + b);
            }
        }

        public static IEnumerable<T[]> Chunk<T>(IEnumerable<T> source, int size)
        {
            var chunk = new List<T>(size);
            foreach (var item in source)
            {
                chunk.Add(item);
                if (chunk.Count == size)
                {
                    yield return chunk.ToArray();
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0) yield return chunk.ToArray();
        }

        public static IEnumerable<string> Lines(TextReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) yield break;
                    yield return line;
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        public static IEnumerator<char> Letters(string s)
        {
            foreach (char c in s)
                if (char.IsLetter(c)) yield return c;
        }

        public static async IAsyncEnumerable<int> TicksAsync(int count, TimeSpan interval, [EnumeratorCancellation] CancellationToken token = default)
        {
            for (int i = 0; i < count; i++)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                yield return i;
            }
        }

        public static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken token = default)
        {
            using var reader = new StreamReader(stream);
            string line;
            while ((line = await reader.ReadLineAsync(token)) != null)
                yield return line;
        }

        public static async Task<int> SumAsync(IAsyncEnumerable<int> source, CancellationToken token = default)
        {
            int sum = 0;
            await foreach (var x in source.WithCancellation(token).ConfigureAwait(false))
                sum += x;
            return sum;
        }
    }

    /// <summary>A hand-written enumerable next to the compiler-generated ones.</summary>
    public sealed class Countdown : IEnumerable<int>
    {
        private readonly int _from;

        public Countdown(int from) => _from = from;

        public IEnumerator<int> GetEnumerator()
        {
            for (int i = _from; i >= 0; i--) yield return i;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class Tree<T> : IEnumerable<T>
    {
        public Tree(T value, params Tree<T>[] children)
        {
            Value = value;
            Children = children;
        }

        public T Value { get; }
        public Tree<T>[] Children { get; }

        public IEnumerable<T> DepthFirst()
        {
            var stack = new Stack<Tree<T>>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node.Value;
                for (int i = node.Children.Length - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
        }

        public IEnumerator<T> GetEnumerator() => DepthFirst().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
