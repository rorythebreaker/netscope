namespace NetScope.Audit.Rules;

/// <summary>NS003: members looked up by string name and then invoked.</summary>
public sealed class NS003StringReflection : IRule
{
    public string Id => "NS003";
    public string Name => "ReflectionByName";
    public string Title => "Reflection by string name with Invoke";
    public string Description => "The method looks up a type or member by a string name (Type.GetType(string), GetMethod(string), ...) and then invokes it. Which code runs depends on the string, which hides the call from static analysis.";
    public Severity Level => Severity.Note;

    private static readonly HashSet<string> Lookups = new(StringComparer.Ordinal)
    {
        "GetType", "GetMethod", "GetProperty", "GetField", "GetMember", "GetConstructor", "InvokeMember",
    };

    public void Check(RuleContext context)
    {
        string? lookup = null;
        foreach (var (_, ins, target) in context.Calls())
        {
            var type = RuleContext.TypeOf(target);
            bool reflectionType = type is "System.Type" or "System.Reflection.Assembly" or "System.Reflection.Module" or "System.Reflection.TypeInfo";
            if (reflectionType && Lookups.Contains(RuleContext.NameOf(target)) && target.Parameters.StartsWith("(string", StringComparison.Ordinal))
            {
                lookup ??= target.Display;
                if (RuleContext.NameOf(target) == "InvokeMember") context.Report(this, ins, $"invokes a member by name: {target.Display}");
            }
            else if (lookup != null && RuleContext.NameOf(target) == "Invoke" &&
                     type is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo" or "System.Reflection.PropertyInfo")
            {
                context.Report(this, ins, $"invokes a member found by name ({lookup})");
            }
        }
    }
}
