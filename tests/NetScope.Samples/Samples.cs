using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MyApp
{
    internal static class Marker
    {
        /// <summary>Proves whether code of this assembly was executed: tests set the variable and check the file.</summary>
        [ModuleInitializer]
        internal static void OnLoad()
        {
            var path = Environment.GetEnvironmentVariable("NETSCOPE_SAMPLE_MARKER");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, "executed");
        }
    }
}

namespace MyApp.Db
{
    public class User
    {
        public string Name { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    public class Users
    {
        private readonly Dictionary<string, User> _byName = new();

        public virtual User? Find(string name) => _byName.TryGetValue(name, out var u) ? u : null;

        public void Add(User user) => _byName[user.Name] = user;
    }
}

namespace MyApp.Auth
{
    using MyApp.Db;

    public interface ILoginService
    {
        bool Login(string user, string pass);
    }

    public class LoginService : ILoginService
    {
        private readonly Users _users = new();

        public virtual bool Login(string user, string pass)
        {
            if (user == null) return false;
            var u = _users.Find(user);
            return Hash(pass) == u!.Hash;
        }

        public static string Hash(string s) => s.Length.ToString();
    }

    public sealed class AdminLoginService : LoginService
    {
        public override bool Login(string user, string pass) => user == "admin" && base.Login(user, pass);
    }
}

namespace MyApp.Flow
{
    public static class Branches
    {
        public static int Loop(int n)
        {
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (i % 3 == 0) continue;
                sum += i;
            }
            return sum;
        }

        public static string Switch(int code)
        {
            switch (code)
            {
                case 0: return "zero";
                case 1: return "one";
                case 2: return "two";
                case 3: return "three";
                default: return "many";
            }
        }

        public static int NestedTry(string s)
        {
            int result = 0;
            try
            {
                try
                {
                    result = int.Parse(s);
                }
                catch (FormatException)
                {
                    result = -1;
                }
                finally
                {
                    result++;
                }
            }
            catch (Exception ex) when (ex.Message.Length > 0)
            {
                result = -2;
            }
            return result;
        }

        public static void Throws(object? o)
        {
            if (o is null) throw new ArgumentNullException(nameof(o));
            while (true)
            {
                if (o.GetHashCode() > 10) break;
            }
        }
    }

    public class Generic<T> where T : class
    {
        public T? Value { get; private set; }

        public TOut Map<TOut>(Func<T?, TOut> map) => map(Value);

        public static Generic<T> Create(T value) => new() { Value = value };
    }

    public static class GenericUse
    {
        /// <summary>Instantiates Generic&lt;T&gt; so that ReadyToRun compiles shared (__Canon) code for it.</summary>
        public static int Length(string s) => Generic<string>.Create(s).Map(v => v!.Length);
    }

    public class Features : IDisposable
    {
        private int _count;

        static Features() => Instances = 0;

        public Features() => Instances++;

        ~Features() => _count = -1;

        public static int Instances { get; private set; }

        public event EventHandler? Changed;

        public static Features operator +(Features a, Features b) => new() { _count = a._count + b._count };

        public static implicit operator int(Features f) => f._count;

        public async Task<int> ComputeAsync(int x)
        {
            await Task.Delay(1);
            return x * 2;
        }

        public IEnumerable<int> Range(int n)
        {
            for (int i = 0; i < n; i++) yield return i;
        }

        public int Lambdas(int[] values)
        {
            int offset = _count;
            var shifted = values.Select(v => v + offset).Where(v => v > 0);
            return Local(shifted.Sum());

            int Local(int v) => v + 1;
        }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose() => GC.SuppressFinalize(this);

        [DllImport("kernel32.dll", EntryPoint = "GetTickCount")]
        public static extern uint Ticks();
    }

    /// <summary>Overloads share "Type:Method" in JIT listings; each must still get its own native code.</summary>
    public static class Overloaded
    {
        public static int Foo(int x) => x + 1;

        public static int Foo(string s)
        {
            int h = 0;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }

        public static int Foo<T>(T value)
        {
            var text = value?.ToString() ?? "";
            return text.Length > 10 ? text.GetHashCode() ^ text.Length : string.Concat(text, text).Length * 3;
        }
    }

    public abstract class Shape
    {
        public abstract double Area();

        public virtual string Describe() => GetType().Name + " " + Area();
    }

    public class Circle : Shape
    {
        public double R { get; init; }

        public override double Area() => Math.PI * R * R;
    }
}
