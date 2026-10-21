using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ref.NetFx35
{
    /// <summary>C# 3 code as written for .NET Framework 3.5: LINQ, extension methods, ArrayList.</summary>
    public static class Extensions
    {
        public static bool IsBlank(this string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        public static IEnumerable<T> EveryOther<T>(this IEnumerable<T> source)
        {
            bool take = true;
            foreach (T item in source)
            {
                if (take) yield return item;
                take = !take;
            }
        }

        public static string Join(this IEnumerable<string> parts, string separator)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string p in parts)
            {
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(p);
            }
            return sb.ToString();
        }
    }

    public class Customer
    {
        private string _name;

        public string Name
        {
            get { return _name; }
            set
            {
                if (value == null) throw new ArgumentNullException("value");
                _name = value;
            }
        }

        public string City { get; set; }
        public decimal Balance { get; set; }
    }

    public class Ledger
    {
        private readonly ArrayList _entries = new ArrayList();
        private readonly List<Customer> _customers = new List<Customer>();

        public void Add(Customer customer)
        {
            _customers.Add(customer);
            _entries.Add("added " + customer.Name);
        }

        public IEnumerable<string> CitiesWithDebt()
        {
            return (from c in _customers
                    where c.Balance < 0
                    group c by c.City into g
                    orderby g.Key
                    select g.Key + ": " + g.Count()).ToList();
        }

        public decimal Total()
        {
            return _customers.Sum(c => c.Balance);
        }

        public string[] Log()
        {
            string[] result = new string[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
                result[i] = (string)_entries[i];
            return result;
        }

        public Customer Find(Predicate<Customer> match)
        {
            return _customers.Find(match);
        }

        public Customer FindByName(string name)
        {
            return Find(delegate(Customer c) { return string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase); });
        }
    }
}
