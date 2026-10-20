using System.Reflection.Emit;

namespace NetScope.Audit.Rules;

/// <summary>NS009: SQL text built by string concatenation and passed to a command.</summary>
public sealed class NS009SqlConcatenation : IRule
{
    public string Id => "NS009";
    public string Name => "SqlConcatenation";
    public string Title => "SQL built by concatenation";
    public string Description => "The SQL text of a SqlCommand, OleDbCommand or NpgsqlCommand comes from string concatenation, string.Format or interpolation. If any part comes from input, this is SQL injection; use parameters.";
    public Severity Level => Severity.Warning;

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "System.Data.SqlClient.SqlCommand", "Microsoft.Data.SqlClient.SqlCommand",
        "System.Data.OleDb.OleDbCommand", "Npgsql.NpgsqlCommand",
    };

    public void Check(RuleContext context)
    {
        foreach (var (index, ins, target) in context.Calls())
        {
            if (!Commands.Contains(RuleContext.TypeOf(target))) continue;
            int? fromTop = RuleContext.NameOf(target) switch
            {
                // new SqlCommand(string sql [, connection [, transaction]]): sql is the first argument.
                ".ctor" when target.ParameterCount > 0 && target.Parameters.StartsWith("(string", StringComparison.Ordinal) => target.ParameterCount - 1,
                "set_CommandText" => 0,
                _ => null,
            };
            if (fromTop is not int k) continue;
            var producer = context.Producer(index, k);
            if (producer != null && BuildsString(context, producer))
                context.Report(this, ins, $"SQL text of {target.ShortDeclaringType} is built by concatenation or formatting");
        }
    }

    private static bool BuildsString(RuleContext context, IL.ILInstruction producer)
    {
        if (producer.OpCode.Value != OpCodes.Call.Value && producer.OpCode.Value != OpCodes.Callvirt.Value) return false;
        var m = context.Resolver.ResolveMethod(producer.Handle);
        var type = RuleContext.TypeOf(m);
        return type == "System.String" && m.Name is "Concat" or "Format" or "Join" ||
               type == "System.Text.StringBuilder" && m.Name == "ToString" ||
               type.EndsWith("DefaultInterpolatedStringHandler", StringComparison.Ordinal) && m.Name == "ToStringAndClear" ||
               type == "System.FormattableString" && m.Name == "ToString";
    }
}
