namespace NetScope.Audit.Rules;

/// <summary>NS002: code loaded at run time.</summary>
public sealed class NS002DynamicCodeLoading : IRule
{
    public string Id => "NS002";
    public string Name => "DynamicCodeLoading";
    public string Title => "Dynamic code loading";
    public string Description => "The method loads code at run time: Assembly.Load*, AppDomain.Load, AssemblyLoadContext.LoadFrom*, or Activator.CreateInstance by assembly and type name. The loaded code is not visible to static analysis.";
    public Severity Level => Severity.Warning;

    public void Check(RuleContext context)
    {
        foreach (var (_, ins, target) in context.Calls())
        {
            var type = RuleContext.TypeOf(target);
            bool load =
                type == "System.Reflection.Assembly" && RuleContext.NameOf(target) is "Load" or "LoadFrom" or "LoadFile" or "UnsafeLoadFrom" or "LoadWithPartialName" ||
                type == "System.AppDomain" && RuleContext.NameOf(target) is "Load" or "ExecuteAssembly" or "ExecuteAssemblyByName" or "CreateInstanceAndUnwrap" or "CreateInstanceFromAndUnwrap" ||
                type == "System.Runtime.Loader.AssemblyLoadContext" && RuleContext.NameOf(target) is "LoadFromAssemblyPath" or "LoadFromStream" or "LoadFromNativeImagePath" ||
                // CreateInstance(string assemblyName, string typeName, ...) and CreateInstanceFrom(...); not CreateInstance(Type).
                type == "System.Activator" && (RuleContext.NameOf(target) == "CreateInstanceFrom" || RuleContext.NameOf(target) == "CreateInstance" && target.Parameters.StartsWith("(string", StringComparison.Ordinal));
            if (load) context.Report(this, ins, $"loads code at run time: {target.Display}");
        }
    }
}
