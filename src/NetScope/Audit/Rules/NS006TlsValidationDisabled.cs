using System.Reflection.Emit;

namespace NetScope.Audit.Rules;

/// <summary>NS006: TLS certificate validation turned off with a callback that always returns true.</summary>
public sealed class NS006TlsValidationDisabled : IRule
{
    public string Id => "NS006";
    public string Name => "TlsValidationDisabled";
    public string Title => "TLS certificate validation disabled";
    public string Description => "A certificate validation callback (ServicePointManager.ServerCertificateValidationCallback, SslStream, HttpClientHandler.ServerCertificateCustomValidationCallback) that returns true accepts any certificate and defeats TLS.";
    public Severity Level => Severity.Error;

    public void Check(RuleContext context)
    {
        var list = context.Instructions;
        for (int i = 0; i < list.Count; i++)
        {
            var ins = list[i];
            if (ins.OpCode.OperandType != OperandType.InlineMethod) continue;
            var target = context.Resolver.ResolveMethod(ins.Handle);

            if (RuleContext.Is(target, "System.Net.Http.HttpClientHandler", "get_DangerousAcceptAnyServerCertificateValidator"))
            {
                context.Report(this, ins, "uses HttpClientHandler.DangerousAcceptAnyServerCertificateValidator");
            }
            // ldftn <callback> ; newobj <validation delegate>::.ctor — the lambda (or method) behind the delegate.
            else if (ins.OpCode.Value == OpCodes.Ldftn.Value && i + 1 < list.Count && list[i + 1].OpCode.Value == OpCodes.Newobj.Value &&
                     IsValidationDelegate(context.Resolver.ResolveMethod(list[i + 1].Handle)))
            {
                var body = context.LoadDefinition(target);
                if (body != null && ReturnsOnlyTrue(body.Instructions))
                    context.Report(this, ins, $"certificate validation callback {target.Name} always returns true");
            }
        }
    }

    private static bool IsValidationDelegate(Metadata.MethodRef ctor) =>
        ctor.DeclaringType.EndsWith("RemoteCertificateValidationCallback", StringComparison.Ordinal) ||
        // Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> for HttpClientHandler
        ctor.DeclaringType.StartsWith("System.Func<", StringComparison.Ordinal) && ctor.DeclaringType.Contains("SslPolicyErrors", StringComparison.Ordinal);

    /// <summary>The body is "return true" (ldc.i4.1; ret), allowing the nop/stloc/br/ldloc a debug build adds.</summary>
    internal static bool ReturnsOnlyTrue(IReadOnlyList<IL.ILInstruction> body)
    {
        bool pushedTrue = false, returns = false;
        foreach (var ins in body)
        {
            var v = ins.OpCode.Value;
            if (v == OpCodes.Nop.Value || v == OpCodes.Br.Value || v == OpCodes.Br_S.Value || v == OpCodes.Stloc_0.Value || v == OpCodes.Ldloc_0.Value) continue;
            if (RuleContext.IntConstant(ins) == 1) pushedTrue = true;
            else if (v == OpCodes.Ret.Value) returns = true;
            else return false; // any real logic: not an unconditional true
        }
        return returns && pushedTrue;
    }
}
