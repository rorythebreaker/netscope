using System;
using System.Collections.Generic;
using System.Text;

namespace ObfuscationSample
{
    // Ordinary code with branches, loops, strings and calls, so that every ConfuserEx protection has work.
    public sealed class Item
    {
        public Item(string name, int quantity, decimal price)
        {
            Name = name;
            Quantity = quantity;
            Price = price;
        }

        public string Name { get; }
        public int Quantity { get; private set; }
        public decimal Price { get; }

        public void Take(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "count must be positive");
            if (count > Quantity) throw new InvalidOperationException("not enough " + Name + " in stock");
            Quantity -= count;
        }
    }

    public sealed class Inventory
    {
        private readonly Dictionary<string, Item> _items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _log = new List<string>();

        public void Add(Item item)
        {
            if (_items.ContainsKey(item.Name)) throw new ArgumentException("duplicate item: " + item.Name);
            _items.Add(item.Name, item);
            _log.Add("added " + item.Name);
        }

        public bool TryTake(string name, int count)
        {
            Item item;
            if (!_items.TryGetValue(name, out item)) return false;
            try
            {
                item.Take(count);
                _log.Add("took " + count + " of " + name);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                _log.Add("failed: " + ex.Message);
                return false;
            }
        }

        public decimal Total()
        {
            decimal sum = 0;
            foreach (var item in _items.Values)
            {
                if (item.Quantity == 0) continue;
                sum += item.Quantity * item.Price;
            }
            return sum;
        }

        public string Describe(int code)
        {
            switch (code)
            {
                case 0: return "empty";
                case 1: return "low";
                case 2: return "normal";
                case 3: return "high";
                case 4: return "full";
                default: return "unknown";
            }
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Inventory report");
            foreach (var pair in _items)
            {
                var level = pair.Value.Quantity < 5 ? "low" : pair.Value.Quantity < 50 ? "ok" : "plenty";
                sb.Append(pair.Key).Append(": ").Append(pair.Value.Quantity).Append(" (").Append(level).AppendLine(")");
            }
            for (int i = 0; i < _log.Count; i++)
            {
                if (i % 10 == 0) sb.AppendLine("--- log page " + (i / 10 + 1) + " ---");
                sb.AppendLine(_log[i]);
            }
            return sb.ToString();
        }

        public static int Checksum(string text)
        {
            int hash = 17;
            foreach (char c in text)
            {
                hash = unchecked(hash * 31 + c);
                if ((hash & 1) == 0) hash ^= 0x5bd1e995;
            }
            return hash;
        }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            var inventory = new Inventory();
            inventory.Add(new Item("apple", 10, 0.5m));
            inventory.Add(new Item("pear", 3, 0.7m));
            inventory.Add(new Item("plum", 120, 0.2m));
            inventory.TryTake("apple", 4);
            inventory.TryTake("pear", 9);
            Console.WriteLine(inventory.Report());
            Console.WriteLine("total: " + inventory.Total());
            Console.WriteLine(inventory.Describe(args.Length));
            return Inventory.Checksum(inventory.Report()) & 0xFF;
        }
    }
}
