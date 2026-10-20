namespace NetScope.Audit.Rules;

/// <summary>NS001: the method starts processes.</summary>
public sealed class NS001ProcessStart : IRule
{
    public string Id => "NS001";
    public string Name => "ProcessStart";
    public string Title => "Process start";
    public string Description => "The method starts an operating system process (Process.Start or a ProcessStartInfo). Check where the file name and arguments come from: untrusted input here means command injection.";
    public Severity Level => Severity.Warning;

    public void Check(RuleContext context)
    {
        foreach (var (_, ins, target) in context.Calls())
        {
            if (RuleContext.Is(target, "System.Diagnostics.Process") && RuleContext.NameOf(target).StartsWith("Start", StringComparison.Ordinal) && RuleContext.NameOf(target) != "StartInfo")
                context.Report(this, ins, $"starts a process: {target.Display}");
            else if (RuleContext.Is(target, "System.Diagnostics.ProcessStartInfo", ".ctor"))
                context.Report(this, ins, "creates a ProcessStartInfo");
        }
    }
}
