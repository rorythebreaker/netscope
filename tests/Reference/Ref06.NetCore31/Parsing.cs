using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Ref.NetCore31
{
    public enum Shape { Circle, Square, Triangle }

    public sealed class Measurement
    {
        public Measurement(string name, double value, string? unit = null)
        {
            Name = name;
            Value = value;
            Unit = unit;
        }

        public string Name { get; }
        public double Value { get; }
        public string? Unit { get; }

        public string Describe() => Unit is null ? $"{Name}={Value}" : $"{Name}={Value} {Unit}";
    }

    public static class Geometry
    {
        public static double Area(Shape shape, double size) => shape switch
        {
            Shape.Circle => Math.PI * size * size,
            Shape.Square => size * size,
            Shape.Triangle => Math.Sqrt(3) / 4 * size * size,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        public static string Classify(object? value) => value switch
        {
            null => "null",
            int i when i < 0 => "negative int",
            int _ => "int",
            string { Length: 0 } => "empty string",
            string s => "string of " + s.Length,
            (double x, double y) => $"point {x},{y}",
            _ => value.GetType().Name,
        };
    }

    public static class Binary
    {
        public static int ReadHeader(ReadOnlySpan<byte> data, out ushort version)
        {
            if (data.Length < 6) throw new ArgumentException("header is 6 bytes", nameof(data));
            int magic = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
            version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]);
            return magic;
        }

        public static string Hex(ReadOnlySpan<byte> data)
        {
            Span<char> buffer = data.Length <= 64 ? stackalloc char[data.Length * 2] : new char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                buffer[2 * i] = "0123456789abcdef"[data[i] >> 4];
                buffer[2 * i + 1] = "0123456789abcdef"[data[i] & 0xF];
            }
            return new string(buffer);
        }
    }

    public static class JsonExport
    {
        public static string Write(IEnumerable<Measurement> items)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var m in items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", m.Name);
                    writer.WriteNumber("value", m.Value);
                    if (m.Unit != null) writer.WriteString("unit", m.Unit);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static List<Measurement> Read(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<Measurement>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string? unit = e.TryGetProperty("unit", out var u) ? u.GetString() : null;
                list.Add(new Measurement(e.GetProperty("name").GetString() ?? "", e.GetProperty("value").GetDouble(), unit));
            }
            return list;
        }
    }

    public interface ILogger
    {
        void Log(string message);
        void LogError(Exception ex) => Log("error: " + ex.Message);
    }
}
