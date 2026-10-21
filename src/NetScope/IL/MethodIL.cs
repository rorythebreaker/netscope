using System.Reflection.Metadata;
using NetScope.Catalog;
using NetScope.Metadata;

namespace NetScope.IL;

/// <summary>Decoded body of one method plus the names and types of its arguments and locals.</summary>
public sealed class MethodIL
{
    private MethodIL(MethodModel method, MethodBodyBlock body, List<ILInstruction> instructions, GenericContext context)
    {
        Method = method;
        Body = body;
        Instructions = instructions;
        Context = context;
    }

    public MethodModel Method { get; }
    public MethodBodyBlock Body { get; }
    public List<ILInstruction> Instructions { get; }
    public GenericContext Context { get; }
    /// <summary>Argument names by IL index (arg 0 is "this" for instance methods).</summary>
    public string[] ArgNames { get; private set; } = Array.Empty<string>();
    public string[] ArgTypes { get; private set; } = Array.Empty<string>();
    public string[] LocalTypes { get; private set; } = Array.Empty<string>();
    public string ReturnType { get; private set; } = "void";
    /// <summary>Local variable names by slot (null where unknown); filled from debug symbols when they are available.</summary>
    public string?[] LocalNames { get; internal set; } = Array.Empty<string?>();

    public string LocalName(int index) => index < LocalNames.Length && LocalNames[index] is { Length: > 0 } n ? n : "V_" + index;

    public string ArgName(int index) => index < ArgNames.Length ? ArgNames[index] : "arg" + index;

    /// <summary>
    /// Decodes the IL of a method. Returns null with an error for methods without a body,
    /// unreadable (encrypted) bodies or malformed IL.
    /// </summary>
    public static MethodIL? Load(MethodModel method, MemberResolver resolver, out string? error, out int? errorOffset)
    {
        errorOffset = null;
        var body = method.GetBody(out error);
        if (body == null) return null;

        List<ILInstruction> instructions;
        try
        {
            instructions = ILDecoder.Decode(body.GetILReader());
        }
        catch (ILDecodeException ex)
        {
            error = "malformed IL: " + ex.Message;
            errorOffset = ex.Offset;
            return null;
        }

        var md = resolver.Metadata;
        var ctx = resolver.ContextFor(method.Handle);
        var il = new MethodIL(method, body, instructions, ctx);
        var def = md.GetMethodDefinition(method.Handle);
        try
        {
            var sig = def.DecodeSignature(CSharpTypeProvider.Brief, ctx);
            il.ReturnType = sig.ReturnType;
            int shift = method.IsStatic ? 0 : 1;
            var names = new string[sig.ParameterTypes.Length + shift];
            var types = new string[names.Length];
            if (shift == 1)
            {
                names[0] = "this";
                types[0] = Names.StripArity(Names.ShortName(method.TypeName));
            }
            for (int i = 0; i < sig.ParameterTypes.Length; i++)
            {
                names[i + shift] = "arg" + (i + 1);
                types[i + shift] = sig.ParameterTypes[i];
            }
            foreach (var ph in def.GetParameters())
            {
                var p = md.GetParameter(ph);
                int i = p.SequenceNumber - 1 + shift;
                if (p.SequenceNumber > 0 && i < names.Length && !p.Name.IsNil)
                {
                    var n = md.GetString(p.Name);
                    if (n.Length > 0) names[i] = n;
                }
            }
            il.ArgNames = names;
            il.ArgTypes = types;
        }
        catch (BadImageFormatException) { }

        if (!body.LocalSignature.IsNil)
        {
            try
            {
                il.LocalTypes = md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(CSharpTypeProvider.Brief, ctx).ToArray();
            }
            catch (BadImageFormatException) { }
        }
        return il;
    }
}
