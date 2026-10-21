using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Ref.Lambdas
{
    public sealed record Order(int Id, string Customer, decimal Amount, DateTime Date);

    public sealed class Reports
    {
        private readonly List<Order> _orders;
        private decimal _threshold = 100m;

        public Reports(IEnumerable<Order> orders) => _orders = orders.ToList();

        public event Action<string> Logged;

        // Static lambdas: cached in <>c.
        public IEnumerable<string> Customers() => _orders.Select(o => o.Customer).Distinct().OrderBy(c => c, StringComparer.Ordinal);

        // A lambda capturing `this`: an instance method on the class itself.
        public IEnumerable<Order> Large() => _orders.Where(o => o.Amount >= _threshold);

        // A lambda capturing a local and a parameter: a display class.
        public IEnumerable<Order> Between(DateTime from, int days)
        {
            var to = from.AddDays(days);
            return _orders.Where(o => o.Date >= from && o.Date < to);
        }

        // Query syntax with join, group and let.
        public IEnumerable<(string Customer, decimal Total, int Count)> Totals(IEnumerable<string> vip) =>
            from o in _orders
            join v in vip on o.Customer equals v
            group o by o.Customer into g
            let total = g.Sum(x => x.Amount)
            orderby total descending
            select (g.Key, total, g.Count());

        // Nested closures: the inner lambda captures from two scopes.
        public Func<int, Func<decimal, bool>> Rules()
        {
            int calls = 0;
            return minCount =>
            {
                calls++;
                return amount => amount > _threshold && _orders.Count(o => o.Amount == amount) >= minCount && calls > 0;
            };
        }

        // Local functions: static, capturing (by ref struct closure) and recursive.
        public decimal Median()
        {
            var sorted = _orders.Select(o => o.Amount).OrderBy(a => a).ToArray();
            return sorted.Length == 0 ? 0 : Middle();

            decimal Middle()
            {
                int mid = sorted.Length / 2;
                return sorted.Length % 2 == 1 ? sorted[mid] : Average(sorted[mid - 1], sorted[mid]);
            }

            static decimal Average(decimal a, decimal b) => (a + b) / 2;
        }

        public static long Fib(int n)
        {
            return Go(n);

            static long Go(int k) => k < 2 ? k : Go(k - 1) + Go(k - 2);
        }

        // A local function turned into a delegate (ldftn on a closure method).
        public void Log(IEnumerable<string> lines)
        {
            int n = 0;
            void Write(string line) => Logged?.Invoke($"{++n}: {line}");
            foreach (var line in lines) Write(line);
            Array.ForEach(new[] { "done" }, Write);
        }

        // Closures in a loop: one display class instance per iteration.
        public List<Func<int>> Counters(int count)
        {
            var result = new List<Func<int>>();
            for (int i = 0; i < count; i++)
            {
                int captured = i;
                result.Add(() => captured * captured);
            }
            return result;
        }

        // Expression trees are built, not compiled to IL methods.
        public static Expression<Func<Order, bool>> Filter(string customer) => o => o.Customer == customer && o.Amount > 0;

        public void SetThreshold(decimal value) => _threshold = value;

        public static Func<T, TResult> Memoize<T, TResult>(Func<T, TResult> f) where T : notnull
        {
            var cache = new Dictionary<T, TResult>();
            return x => cache.TryGetValue(x, out var r) ? r : cache[x] = f(x);
        }

        public static Action Compose(params Action[] actions) => () =>
        {
            foreach (var a in actions) a();
        };
    }
}
