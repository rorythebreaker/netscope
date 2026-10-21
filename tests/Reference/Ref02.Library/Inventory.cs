using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ref.Library
{
    public enum Unit
    {
        Piece,
        Kilogram,
        Litre,
    }

    public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
    {
        public Quantity(decimal amount, Unit unit)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            Amount = amount;
            Unit = unit;
        }

        public decimal Amount { get; }
        public Unit Unit { get; }

        public static Quantity operator +(Quantity a, Quantity b)
        {
            if (a.Unit != b.Unit) throw new InvalidOperationException("units differ");
            return new Quantity(a.Amount + b.Amount, a.Unit);
        }

        public static bool operator ==(Quantity a, Quantity b) => a.Equals(b);
        public static bool operator !=(Quantity a, Quantity b) => !a.Equals(b);

        public bool Equals(Quantity other) => Amount == other.Amount && Unit == other.Unit;
        public override bool Equals(object obj) => obj is Quantity q && Equals(q);
        public override int GetHashCode() => HashCode.Combine(Amount, Unit);
        public int CompareTo(Quantity other) => Unit != other.Unit ? Unit.CompareTo(other.Unit) : Amount.CompareTo(other.Amount);

        public override string ToString() => Amount.ToString(CultureInfo.InvariantCulture) + " " + Unit switch
        {
            Unit.Kilogram => "kg",
            Unit.Litre => "l",
            _ => "pcs",
        };
    }

    public class Item
    {
        public Item(string sku, string name, Quantity stock)
        {
            Sku = sku ?? throw new ArgumentNullException(nameof(sku));
            Name = name;
            Stock = stock;
        }

        public string Sku { get; }
        public string Name { get; set; }
        public Quantity Stock { get; internal set; }
        public virtual decimal Price => 0m;
    }

    public sealed class PricedItem : Item
    {
        private readonly decimal _price;

        public PricedItem(string sku, string name, Quantity stock, decimal price) : base(sku, name, stock) => _price = price;

        public override decimal Price => _price;
    }

    public interface IRepository<T> where T : class
    {
        T Find(string key);
        void Save(T item);
        IReadOnlyCollection<T> All { get; }
    }

    public class MemoryRepository<T> : IRepository<T> where T : class
    {
        private readonly Dictionary<string, T> _items = new Dictionary<string, T>(StringComparer.Ordinal);
        private readonly Func<T, string> _key;

        public MemoryRepository(Func<T, string> key) => _key = key;

        public event EventHandler<T> Saved;

        public T Find(string key) => _items.TryGetValue(key, out var item) ? item : null;

        public void Save(T item)
        {
            _items[_key(item)] = item;
            Saved?.Invoke(this, item);
        }

        public IReadOnlyCollection<T> All => _items.Values;
    }

    public sealed class Inventory
    {
        private readonly MemoryRepository<Item> _items = new MemoryRepository<Item>(i => i.Sku);
        private readonly List<int> _history = new List<int>();

        public int Changes => _history.Count;

        public void Receive(Item item)
        {
            var existing = _items.Find(item.Sku);
            if (existing == null) _items.Save(item);
            else existing.Stock += item.Stock;
            _history.Add(1);
        }

        public bool TryShip(string sku, Quantity amount, out string error)
        {
            error = null;
            var item = _items.Find(sku);
            if (item == null)
            {
                error = "unknown item " + sku;
                return false;
            }
            try
            {
                if (item.Stock.CompareTo(amount) < 0) throw new InvalidOperationException("not enough stock");
                item.Stock = new Quantity(item.Stock.Amount - amount.Amount, amount.Unit);
                _history.Add(-1);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                _history.Add(0);
            }
        }

        public decimal TotalValue()
        {
            decimal total = 0;
            foreach (var item in _items.All)
                total += item.Price * item.Stock.Amount;
            return total;
        }

        public string Export()
        {
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                foreach (var item in _items.All)
                    writer.WriteLine("{0};{1};{2}", item.Sku, item.Name, item.Stock);
            }
            return sb.ToString();
        }

        public static Inventory Import(TextReader reader)
        {
            var inventory = new Inventory();
            string line;
            int number = 0;
            while ((line = reader.ReadLine()) != null)
            {
                number++;
                var parts = line.Split(';');
                if (parts.Length != 3) throw new FormatException($"line {number}: expected 3 fields");
                try
                {
                    var amount = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
                    inventory.Receive(new Item(parts[0], parts[1], new Quantity(amount, Unit.Piece)));
                }
                catch (FormatException) when (parts[2].Length == 0)
                {
                    inventory.Receive(new Item(parts[0], parts[1], new Quantity(0, Unit.Piece)));
                }
            }
            return inventory;
        }
    }

    public static class Statistics
    {
        public static T Max<T>(IEnumerable<T> values) where T : IComparable<T>
        {
            using (var e = values.GetEnumerator())
            {
                if (!e.MoveNext()) throw new InvalidOperationException("empty");
                T max = e.Current;
                while (e.MoveNext())
                    if (e.Current.CompareTo(max) > 0) max = e.Current;
                return max;
            }
        }

        public static int MaxInt(List<int> values) => Max(values);
        public static Quantity MaxQuantity(Quantity[] values) => Max(values);
        public static string MaxString(List<string> values) => Max(values);

        public static double Mean(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return sum / values.Count;
        }
    }
}
