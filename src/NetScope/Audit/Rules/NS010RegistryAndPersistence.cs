namespace NetScope.Audit.Rules;

/// <summary>NS010: registry writes, autorun keys and service installation.</summary>
public sealed class NS010RegistryAndPersistence : IRule
{
    public string Id => "NS010";
    public string Name => "RegistryAndPersistence";
    public string Title => "Registry write, autorun or service";
    public string Description => "The method writes to the registry (RegistryKey.SetValue, Registry.SetValue), refers to an autorun key (...\\CurrentVersion\\Run) or installs a Windows service. These change the machine beyond the program itself.";
    public Severity Level => Severity.Note;

    public void Check(RuleContext context)
    {
        foreach (var (_, ins, target) in context.Calls())
        {
            var type = RuleContext.TypeOf(target);
            if (type is "Microsoft.Win32.RegistryKey" or "Microsoft.Win32.Registry" && RuleContext.NameOf(target) is "SetValue" or "CreateSubKey" or "DeleteValue" or "DeleteSubKey" or "DeleteSubKeyTree")
                context.Report(this, ins, $"writes to the registry: {target.ShortDeclaringType}.{RuleContext.NameOf(target)}");
            else if (type is "System.ServiceProcess.ServiceInstaller" or "System.ServiceProcess.ServiceProcessInstaller" && RuleContext.NameOf(target) == ".ctor")
                context.Report(this, ins, $"installs a Windows service ({target.ShortDeclaringType})");
        }
        foreach (var (_, ins, value) in context.Strings())
        {
            if (value.Contains(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase))
                context.Report(this, ins, "refers to an autorun registry key");
            else if (value.Contains(@"\CurrentControlSet\Services", StringComparison.OrdinalIgnoreCase))
                context.Report(this, ins, "refers to the services registry key");
        }
    }
}
