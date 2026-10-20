namespace NetScope.Audit.Rules;

/// <summary>NS004: deserializers that can instantiate arbitrary types.</summary>
public sealed class NS004UnsafeDeserialization : IRule
{
    public string Id => "NS004";
    public string Name => "UnsafeDeserialization";
    public string Title => "Unsafe deserialization";
    public string Description => "BinaryFormatter, NetDataContractSerializer, LosFormatter and SoapFormatter, and Json.NET with TypeNameHandling other than None, create the types named in the data. Deserializing untrusted data with them can execute code.";
    public Severity Level => Severity.Error;

    private static readonly HashSet<string> Formatters = new(StringComparer.Ordinal)
    {
        "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter",
        "System.Runtime.Serialization.NetDataContractSerializer",
        "System.Web.UI.LosFormatter",
        "System.Runtime.Serialization.Formatters.Soap.SoapFormatter",
    };

    private static readonly HashSet<string> JsonSettings = new(StringComparer.Ordinal)
    {
        "Newtonsoft.Json.JsonSerializerSettings", "Newtonsoft.Json.JsonSerializer",
    };

    public void Check(RuleContext context)
    {
        // Deserialization calls are reported; a formatter that is only created here is reported at its constructor.
        var created = new List<(IL.ILInstruction Ins, string Type)>();
        bool deserializes = false;
        foreach (var (index, ins, target) in context.Calls())
        {
            var type = RuleContext.TypeOf(target);
            if (Formatters.Contains(type))
            {
                var name = RuleContext.NameOf(target);
                if (name is "Deserialize" or "UnsafeDeserialize" or "UnsafeDeserializeMethodResponse" or "ReadObject")
                {
                    deserializes = true;
                    context.Report(this, ins, $"deserializes with {Short(type)}.{name}");
                }
                else if (name == ".ctor") created.Add((ins, type));
            }
            else if (JsonSettings.Contains(type) && RuleContext.NameOf(target) == "set_TypeNameHandling")
            {
                // TypeNameHandling.None == 0; a value that is not a constant may be anything.
                var value = RuleContext.IntConstant(context.Producer(index, 0));
                if (value != 0)
                    context.Report(this, ins, value is int v ? $"sets TypeNameHandling to {TypeNameHandling(v)}" : "sets TypeNameHandling to a non-constant value");
            }
        }
        if (!deserializes)
            foreach (var (ins, type) in created) context.Report(this, ins, $"creates a {Short(type)}");
    }

    private static string Short(string type) => type.Substring(type.LastIndexOf('.') + 1);

    private static string TypeNameHandling(int v) => v switch
    {
        1 => "Objects",
        2 => "Arrays",
        3 => "All",
        4 => "Auto",
        _ => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
