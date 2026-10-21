using System;
using System.Collections.Generic;

namespace Ref.Switches
{
    public abstract record Shape;
    public sealed record Circle(double Radius) : Shape;
    public sealed record Rect(double Width, double Height) : Shape;
    public sealed record Polygon(IReadOnlyList<(double X, double Y)> Points) : Shape;

    public static class Dispatch
    {
        // Dense integer cases: a jump table.
        public static string HttpStatus(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 203: return "Non-Authoritative Information";
                case 204: return "No Content";
                case 205: return "Reset Content";
                case 206: return "Partial Content";
                case 207: return "Multi-Status";
                case 208: return "Already Reported";
                case 300: return "Multiple Choices";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 303: return "See Other";
                case 304: return "Not Modified";
                case 307: return "Temporary Redirect";
                case 308: return "Permanent Redirect";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 402: return "Payment Required";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 406: return "Not Acceptable";
                case 408: return "Request Timeout";
                case 409: return "Conflict";
                case 410: return "Gone";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Unknown";
            }
        }

        // Enough string cases for the compiler to switch on a computed hash.
        public static int Keyword(string word)
        {
            switch (word)
            {
                case "abstract": return 1;
                case "as": return 2;
                case "base": return 3;
                case "bool": return 4;
                case "break": return 5;
                case "byte": return 6;
                case "case": return 7;
                case "catch": return 8;
                case "char": return 9;
                case "checked": return 10;
                case "class": return 11;
                case "const": return 12;
                case "continue": return 13;
                case "decimal": return 14;
                case "default": return 15;
                case "delegate": return 16;
                case "do": return 17;
                case "double": return 18;
                case "else": return 19;
                case "enum": return 20;
                case null: return -2;
                default: return -1;
            }
        }

        // A short string switch: compiled to equality checks.
        public static ConsoleColor Color(string name) => name switch
        {
            "red" => ConsoleColor.Red,
            "green" => ConsoleColor.Green,
            "blue" => ConsoleColor.Blue,
            _ => ConsoleColor.Gray,
        };

        public static double Area(Shape shape) => shape switch
        {
            Circle { Radius: <= 0 } => 0,
            Circle c => Math.PI * c.Radius * c.Radius,
            Rect { Width: var w, Height: var h } when w == h => w * w,
            Rect r => r.Width * r.Height,
            Polygon { Points.Count: < 3 } => 0,
            Polygon p => Shoelace(p.Points),
            null => throw new ArgumentNullException(nameof(shape)),
            _ => throw new NotSupportedException(shape.GetType().Name),
        };

        private static double Shoelace(IReadOnlyList<(double X, double Y)> points)
        {
            double sum = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var (x1, y1) = points[i];
                var (x2, y2) = points[(i + 1) % points.Count];
                sum += x1 * y2 - x2 * y1;
            }
            return Math.Abs(sum) / 2;
        }

        public static string Quadrant(int x, int y) => (x, y) switch
        {
            (0, 0) => "origin",
            ( > 0, > 0) => "I",
            ( < 0, > 0) => "II",
            ( < 0, < 0) => "III",
            ( > 0, < 0) => "IV",
            _ => "axis",
        };

        public static string Describe(object o) => o switch
        {
            int i and >= 0 and < 10 => "digit " + i,
            int i => "int " + i,
            long or ulong => "64-bit",
            string { Length: > 10 } s => "long string " + s[..10],
            string s => "string " + s,
            int[] { Length: 0 } => "empty array",
            int[] and [var first, .., var last] => $"array {first}..{last}",
            not null => o.ToString(),
            _ => "null",
        };

        public static char Grade(int score)
        {
            if (score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(score));
            return score switch
            {
                >= 90 => 'A',
                >= 80 => 'B',
                >= 70 => 'C',
                >= 60 => 'D',
                _ => 'F',
            };
        }

        public static int CountTokens(string text)
        {
            int count = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                switch (c)
                {
                    case ' ':
                    case '\t':
                    case '\n':
                        inWord = false;
                        continue;
                    case '(':
                    case ')':
                    case ';':
                        count++;
                        inWord = false;
                        break;
                    default:
                        if (!inWord) count++;
                        inWord = true;
                        break;
                }
            }
            return count;
        }
    }
}
