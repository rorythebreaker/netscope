using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ref.Modern
{
    // Positional record class: compiler-generated Equals, GetHashCode, PrintMembers, <Clone>$, Deconstruct.
    public record Person(string First, string Last)
    {
        public string Full => $"{First} {Last}";
    }

    public sealed record Employee(string First, string Last, decimal Salary) : Person(First, Last)
    {
        public Employee Raise(decimal percent) => this with { Salary = Salary * (1 + percent / 100) };
    }

    public readonly record struct Point(double X, double Y)
    {
        public double Length => Math.Sqrt(X * X + Y * Y);
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    }

    public record struct Counter
    {
        public int Value { get; set; }
        public void Increment() => Value++;
    }

    // init-only setters and required members (modreq IsExternalInit, RequiredMemberAttribute).
    public sealed class Settings
    {
        public required string Name { get; init; }
        public int Retries { get; init; } = 3;
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    }

    // Default interface methods, including a static member and a private helper.
    public interface IGreeter
    {
        string Name { get; }
        string Greet() => Prefix() + Name;
        string GreetLoudly() => Greet().ToUpperInvariant() + "!";
        private string Prefix() => "Hello, ";
        static string Anonymous => "stranger";
    }

    public sealed class Greeter : IGreeter
    {
        public Greeter(string name) => Name = name;
        public string Name { get; }
    }

    public sealed class PoliteGreeter : IGreeter
    {
        public string Name => "sir";
        public string Greet() => "Good day, " + Name;
    }

    // Static abstract and static virtual interface members (generic math style).
    public interface IShape<TSelf> where TSelf : IShape<TSelf>
    {
        static abstract TSelf Unit { get; }
        static abstract TSelf operator *(TSelf shape, double factor);
        double Area { get; }
        static virtual string Kind => typeof(TSelf).Name;
    }

    public readonly struct Square : IShape<Square>
    {
        public Square(double side) => Side = side;
        public double Side { get; }
        public double Area => Side * Side;
        public static Square Unit => new(1);
        public static Square operator *(Square s, double factor) => new(s.Side * factor);
        static string IShape<Square>.Kind => "square";
    }

    public static class Shapes
    {
        public static T Scale<T>(double factor) where T : IShape<T> => T.Unit * factor;

        public static string KindOf<T>() where T : IShape<T> => T.Kind;

        public static T Add<T>(T a, T b) where T : IAdditionOperators<T, T, T> => a + b;
    }

    // Primary constructor of a class, collection expressions. (No file-local type: its metadata name contains a
    // hash of the source path, which would make the reference output depend on the checkout location and OS.)
    public sealed class Catalog(string name, IEnumerable<string> items)
    {
        private readonly List<string> _items = [.. items];

        public string Name => name;
        public int Count => _items.Count;
        public string[] Sorted()
        {
            string[] copy = [.. _items];
            Array.Sort(copy, StringComparer.Ordinal);
            return copy;
        }
        public bool Contains(string item) => Helper.Normalize(_items).Contains(Helper.Normalize(item));
    }

    internal static class Helper
    {
        public static string Normalize(string s) => s.Trim().ToLowerInvariant();
        public static HashSet<string> Normalize(IEnumerable<string> items)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in items) set.Add(Normalize(i));
            return set;
        }
    }
}
