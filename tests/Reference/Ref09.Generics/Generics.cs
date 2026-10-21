using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ref.Generics
{
    public interface IProducer<out T>
    {
        T Produce();
    }

    public interface IConsumer<in T>
    {
        void Consume(T item);
    }

    public interface IConverter<in TIn, out TOut>
    {
        TOut Convert(TIn value);
    }

    public sealed class Constant<T> : IProducer<T>
    {
        private readonly T _value;
        public Constant(T value) => _value = value;
        public T Produce() => _value;
    }

    public sealed class Collector<T> : IConsumer<T>
    {
        public List<T> Items { get; } = new List<T>();
        public void Consume(T item) => Items.Add(item);
    }

    public abstract class Entity<TKey> where TKey : struct, IEquatable<TKey>
    {
        public TKey Id { get; protected set; }
        public bool SameAs(Entity<TKey> other) => other != null && Id.Equals(other.Id);
    }

    public sealed class User : Entity<int>
    {
        public User(int id, string name) { Id = id; Name = name; }
        public string Name { get; }
    }

    public class Pool<T> where T : class, new()
    {
        private readonly Stack<T> _free = new Stack<T>();

        public T Rent() => _free.Count > 0 ? _free.Pop() : new T();
        public void Return(T item) => _free.Push(item ?? throw new ArgumentNullException(nameof(item)));
        public int Free => _free.Count;
    }

    public struct Pair<TFirst, TSecond>
    {
        public Pair(TFirst first, TSecond second) { First = first; Second = second; }
        public TFirst First;
        public TSecond Second;
        public Pair<TSecond, TFirst> Swap() => new Pair<TSecond, TFirst>(Second, First);
        public override string ToString() => $"({First}, {Second})";
    }

    public class Node<T>
    {
        public T Value;
        public Node<T> Next;

        public class Enumerator
        {
            private Node<T> _current;
            public Enumerator(Node<T> head) => _current = head;
            public bool Next(out T value)
            {
                if (_current == null) { value = default; return false; }
                value = _current.Value;
                _current = _current.Next;
                return true;
            }
        }
    }

    public static class Algorithms
    {
        public static void Swap<T>(ref T a, ref T b)
        {
            T t = a;
            a = b;
            b = t;
        }

        public static TOut[] Map<TIn, TOut>(TIn[] source, Func<TIn, TOut> f)
        {
            var result = new TOut[source.Length];
            for (int i = 0; i < source.Length; i++) result[i] = f(source[i]);
            return result;
        }

        public static T Sum<T>(IEnumerable<T> values) where T : INumber<T>
        {
            T sum = T.Zero;
            foreach (var v in values) sum += v;
            return sum;
        }

        public static TResult Apply<TIn, TResult>(IConverter<TIn, TResult> converter, TIn value) => converter.Convert(value);

        public static IProducer<object> Widen(IProducer<string> producer) => producer;

        public static IConsumer<string> Narrow(IConsumer<object> consumer) => consumer;

        public static Dictionary<TKey, List<TValue>> GroupBy<TKey, TValue>(IEnumerable<TValue> values, Func<TValue, TKey> key) where TKey : notnull
        {
            var groups = new Dictionary<TKey, List<TValue>>();
            foreach (var v in values)
            {
                var k = key(v);
                if (!groups.TryGetValue(k, out var list)) groups[k] = list = new List<TValue>();
                list.Add(v);
            }
            return groups;
        }

        public static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;

        public static int UseMany()
        {
            int x = 1, y = 2;
            Swap(ref x, ref y);
            var lengths = Map(new[] { "a", "bb" }, s => s.Length);
            var total = Sum(new List<double> { 1.5, 2.5 });
            var pair = new Pair<int, string>(1, "one").Swap();
            var pool = new Pool<List<int>>();
            pool.Return(pool.Rent());
            return x + lengths.Length + (int)total + pair.Second + pool.Free + Max(3, 4) + Max("a", "b").Length;
        }
    }
}
