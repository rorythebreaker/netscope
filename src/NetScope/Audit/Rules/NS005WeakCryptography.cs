using System.Text.RegularExpressions;

namespace NetScope.Audit.Rules;

/// <summary>NS005: broken hash and cipher algorithms, ECB mode, System.Random for secrets.</summary>
public sealed partial class NS005WeakCryptography : IRule
{
    public string Id => "NS005";
    public string Name => "WeakCryptography";
    public string Title => "Weak cryptography";
    public string Description => "MD5, SHA-1, DES, 3DES and RC2 are broken or too weak, ECB mode leaks patterns of the plaintext, and System.Random is predictable, so it must not generate tokens, passwords or keys.";
    public Severity Level => Severity.Warning;

    private const string Crypto = "System.Security.Cryptography.";

    private static readonly Dictionary<string, string> Weak = new(StringComparer.Ordinal)
    {
        [Crypto + "MD5"] = "MD5", [Crypto + "MD5CryptoServiceProvider"] = "MD5",
        [Crypto + "SHA1"] = "SHA-1", [Crypto + "SHA1Managed"] = "SHA-1", [Crypto + "SHA1CryptoServiceProvider"] = "SHA-1", [Crypto + "SHA1Cng"] = "SHA-1",
        [Crypto + "HMACMD5"] = "MD5", [Crypto + "HMACSHA1"] = "SHA-1",
        [Crypto + "DES"] = "DES", [Crypto + "DESCryptoServiceProvider"] = "DES",
        [Crypto + "TripleDES"] = "3DES", [Crypto + "TripleDESCryptoServiceProvider"] = "3DES", [Crypto + "TripleDESCng"] = "3DES",
        [Crypto + "RC2"] = "RC2", [Crypto + "RC2CryptoServiceProvider"] = "RC2",
    };

    /// <summary>Names of methods that probably produce secrets.</summary>
    [GeneratedRegex("token|passw|pwd|secret|salt|nonce|otp|apikey|api_key|privatekey|generatekey|keygen", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    private const int CipherModeEcb = 2;

    public void Check(RuleContext context)
    {
        foreach (var (index, ins, target) in context.Calls())
        {
            var type = RuleContext.TypeOf(target);
            if (Weak.TryGetValue(type, out var algorithm))
            {
                if (RuleContext.NameOf(target) is ".ctor" or "Create" or "HashData" or "TryHashData" or "HashDataAsync")
                    context.Report(this, ins, $"uses {algorithm}: {target.Display}");
            }
            else if (type.StartsWith(Crypto, StringComparison.Ordinal) && RuleContext.NameOf(target) == "set_Mode" && RuleContext.IntConstant(context.Producer(index, 0)) == CipherModeEcb)
            {
                context.Report(this, ins, "sets CipherMode.ECB");
            }
            else if (type.StartsWith(Crypto, StringComparison.Ordinal) && RuleContext.NameOf(target) is "EncryptEcb" or "DecryptEcb")
            {
                context.Report(this, ins, $"uses ECB mode: {RuleContext.NameOf(target)}");
            }
            else if (RuleContext.Is(target, "System.Random", ".ctor") && SecretName().IsMatch(context.Method.FullName))
            {
                context.Report(this, ins, "System.Random in a method that seems to create secrets; use RandomNumberGenerator");
            }
        }
    }
}
