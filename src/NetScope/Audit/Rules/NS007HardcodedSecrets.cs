using System.Text.RegularExpressions;

namespace NetScope.Audit.Rules;

/// <summary>NS007: credentials and keys in string literals. The secret itself is never printed.</summary>
public sealed partial class NS007HardcodedSecrets : IRule
{
    public string Id => "NS007";
    public string Name => "HardcodedSecret";
    public string Title => "Hard-coded secret";
    public string Description => "A string literal contains a credential: a connection string with a password, an AWS access key ID (AKIA...), or a PEM private key. Anyone with the binary can read it.";
    public Severity Level => Severity.Error;

    [GeneratedRegex(@"(?:^|;)\s*(?:password|pwd)\s*=\s*(?!\s*;|\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKey();

    public void Check(RuleContext context)
    {
        foreach (var (_, ins, value) in context.Strings())
        {
            string? what =
                ConnectionStringPassword().IsMatch(value) ? "a connection string with a password" :
                AwsAccessKey().IsMatch(value) ? "an AWS access key ID" :
                PemPrivateKey().IsMatch(value) ? "a PEM private key" :
                null;
            // Only the kind and the length: the report must not spread the secret further.
            if (what != null) context.Report(this, ins, $"string literal contains {what} ({value.Length} characters)");
        }
    }
}
