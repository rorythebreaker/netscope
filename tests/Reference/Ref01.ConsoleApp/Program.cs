using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ref.ConsoleApp
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }
            if (options.Error != null)
            {
                Console.Error.WriteLine(options.Error);
                return 2;
            }

            var counter = new WordCounter(options.IgnoreCase);
            foreach (var path in options.Files)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine("not found: " + path);
                    return 3;
                }
                counter.Add(File.ReadAllText(path));
            }
            foreach (var (word, count) in counter.Top(options.Top))
                Console.WriteLine($"{count,6} {word}");
            return 0;
        }
    }

    internal sealed class Options
    {
        public const string Usage = "usage: wc [--top N] [--ignore-case] <file>...";

        public bool ShowHelp { get; private set; }
        public bool IgnoreCase { get; private set; }
        public int Top { get; private set; } = 10;
        public string Error { get; private set; }
        public List<string> Files { get; } = new List<string>();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h":
                    case "--help":
                        o.ShowHelp = true;
                        break;
                    case "-i":
                    case "--ignore-case":
                        o.IgnoreCase = true;
                        break;
                    case "--top":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out int n) || n <= 0)
                        {
                            o.Error = "--top expects a positive number";
                            return o;
                        }
                        o.Top = n;
                        break;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                        {
                            o.Error = "unknown option " + args[i];
                            return o;
                        }
                        o.Files.Add(args[i]);
                        break;
                }
            }
            if (o.Files.Count == 0 && !o.ShowHelp) o.Error = "no input files";
            return o;
        }
    }

    internal sealed class WordCounter
    {
        private readonly Dictionary<string, int> _counts;

        public WordCounter(bool ignoreCase)
        {
            _counts = new Dictionary<string, int>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        public int Distinct => _counts.Count;

        public void Add(string text)
        {
            int start = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool letter = i < text.Length && char.IsLetter(text[i]);
                if (letter && start < 0) start = i;
                else if (!letter && start >= 0)
                {
                    var word = text.Substring(start, i - start);
                    _counts[word] = _counts.TryGetValue(word, out int c) ? c + 1 : 1;
                    start = -1;
                }
            }
        }

        public IEnumerable<(string Word, int Count)> Top(int n) =>
            _counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(n).Select(p => (p.Key, p.Value));
    }
}
