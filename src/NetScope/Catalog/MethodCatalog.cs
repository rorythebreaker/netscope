using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using NetScope.Loading;
using NetScope.Metadata;

namespace NetScope.Catalog;

public interface IMethodCatalog
{
    /// <summary>All methods of the assembly in metadata order; <see cref="MethodModel.Selected"/> reflects the filters.</summary>
    IReadOnlyList<MethodModel> Build(LoadedAssembly assembly, MemberResolver resolver, MethodFilter filter);
}

public sealed class MethodCatalog : IMethodCatalog
{
    public IReadOnlyList<MethodModel> Build(LoadedAssembly assembly, MemberResolver resolver, MethodFilter filter)
    {
        var md = assembly.Metadata;
        var list = new List<MethodModel>(md.MethodDefinitions.Count);
        int entryPointToken = assembly.PE.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress;

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var typeName = Names.TypeDefFullName(md, typeHandle);
            var ns = Names.TypeDefNamespace(md, typeHandle);
            var accessors = CollectAccessors(md, type);
            bool typeGenerated = IsCompilerGeneratedType(md, typeHandle);

            foreach (var methodHandle in type.GetMethods())
            {
                var model = Describe(assembly, resolver, typeHandle, typeName, ns, methodHandle, accessors, typeGenerated, entryPointToken);
                model.Selected = filter.Matches(model);
                list.Add(model);
            }
        }
        return list;
    }

    private static Dictionary<MethodDefinitionHandle, string> CollectAccessors(MetadataReader md, TypeDefinition type)
    {
        var map = new Dictionary<MethodDefinitionHandle, string>();
        foreach (var ph in type.GetProperties())
        {
            var acc = md.GetPropertyDefinition(ph).GetAccessors();
            if (!acc.Getter.IsNil) map[acc.Getter] = "property-get";
            if (!acc.Setter.IsNil) map[acc.Setter] = "property-set";
        }
        foreach (var eh in type.GetEvents())
        {
            var acc = md.GetEventDefinition(eh).GetAccessors();
            if (!acc.Adder.IsNil) map[acc.Adder] = "event-add";
            if (!acc.Remover.IsNil) map[acc.Remover] = "event-remove";
            if (!acc.Raiser.IsNil) map[acc.Raiser] = "event-raise";
        }
        return map;
    }

    private static bool IsCompilerGeneratedType(MetadataReader md, TypeDefinitionHandle handle)
    {
        for (var h = handle; !h.IsNil; h = md.GetTypeDefinition(h).GetDeclaringType())
        {
            var t = md.GetTypeDefinition(h);
            if (Names.HasAttribute(md, t.GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) return true;
            if (md.GetString(t.Name).StartsWith('<')) return true;
        }
        return false;
    }

    private static MethodModel Describe(
        LoadedAssembly asm, MemberResolver resolver, TypeDefinitionHandle typeHandle, string typeName, string ns,
        MethodDefinitionHandle handle, Dictionary<MethodDefinitionHandle, string> accessors, bool typeGenerated, int entryPointToken)
    {
        var md = asm.Metadata;
        var m = md.GetMethodDefinition(handle);
        var name = md.GetString(m.Name);
        var attrs = m.Attributes;
        var impl = m.ImplAttributes;
        var token = MetadataTokens.GetToken(handle);
        var kinds = new List<string>();
        var customAttrs = m.GetCustomAttributes();
        bool specialName = (attrs & MethodAttributes.SpecialName) != 0;
        int paramCount = SafeParamCount(md, m);

        // Special members.
        if (name == ".ctor") kinds.Add("ctor");
        else if (name == ".cctor") kinds.Add("cctor");
        else if (name == "Finalize" && (attrs & MethodAttributes.Virtual) != 0 && paramCount == 0) kinds.Add("finalizer");

        if (accessors.TryGetValue(handle, out var accessorKind)) kinds.Add(accessorKind);
        else if (specialName && name.StartsWith("get_", StringComparison.Ordinal)) kinds.Add("property-get");
        else if (specialName && name.StartsWith("set_", StringComparison.Ordinal)) kinds.Add("property-set");
        else if (specialName && name.StartsWith("add_", StringComparison.Ordinal)) kinds.Add("event-add");
        else if (specialName && name.StartsWith("remove_", StringComparison.Ordinal)) kinds.Add("event-remove");

        if (specialName && name.StartsWith("op_", StringComparison.Ordinal))
            kinds.Add(name is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit" ? "conversion" : "operator");

        kinds.Add((attrs & MethodAttributes.Static) != 0 ? "static" : "instance");

        // Virtuality.
        if ((attrs & MethodAttributes.Abstract) != 0) kinds.Add("abstract");
        else if ((attrs & MethodAttributes.Virtual) != 0)
            kinds.Add((attrs & MethodAttributes.NewSlot) != 0 ? "virtual" : "override");
        if ((attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) != 0) kinds.Add("sealed");

        // Implementation.
        PInvokeInfo? pinvoke = null;
        if ((attrs & MethodAttributes.PinvokeImpl) != 0)
        {
            kinds.Add("pinvoke");
            var import = m.GetImport();
            if (!import.Module.IsNil)
            {
                var entry = import.Name.IsNil ? name : md.GetString(import.Name);
                pinvoke = new PInvokeInfo { Dll = md.GetString(md.GetModuleReference(import.Module).Name), EntryPoint = entry.Length == 0 ? name : entry };
            }
        }
        if ((impl & MethodImplAttributes.InternalCall) != 0) kinds.Add("internalcall");
        if ((impl & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime) kinds.Add("runtime");
        if ((impl & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Native && m.RelativeVirtualAddress != 0) kinds.Add("native");

        // State machines.
        bool asyncIterator = Names.HasAttribute(md, customAttrs, "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute");
        if (asyncIterator || Names.HasAttribute(md, customAttrs, "System.Runtime.CompilerServices.AsyncStateMachineAttribute")) kinds.Add("async");
        if (asyncIterator || Names.HasAttribute(md, customAttrs, "System.Runtime.CompilerServices.IteratorStateMachineAttribute")) kinds.Add("iterator");

        // Compiler-generated code: lambdas, local functions, anonymous methods, state machine bodies.
        bool generated = typeGenerated || Names.HasAttribute(md, customAttrs, "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        var typeShort = md.GetString(md.GetTypeDefinition(typeHandle).Name);
        if (name.Contains(">b__", StringComparison.Ordinal) || name.StartsWith("_Lambda$__", StringComparison.Ordinal)) kinds.Add("lambda");
        else if (name.Contains(">g__", StringComparison.Ordinal)) kinds.Add("local-function");
        else if (name == "MoveNext" && typeShort.StartsWith('<') && typeShort.Contains(">d__", StringComparison.Ordinal)) kinds.Add("state-machine");
        if (generated || name.StartsWith('<') || kinds.Contains("lambda") || kinds.Contains("local-function")) kinds.Add("compiler-generated");

        if (m.GetGenericParameters().Count > 0) kinds.Add("generic");
        if (Names.HasAttribute(md, customAttrs, "System.Runtime.CompilerServices.ExtensionAttribute")) kinds.Add("extension");
        if (token == entryPointToken) kinds.Add("entry-point");
        if (m.RelativeVirtualAddress == 0) kinds.Add("no-body");

        return new MethodModel
        {
            Assembly = asm,
            Handle = handle,
            DeclaringTypeHandle = typeHandle,
            Token = token,
            Namespace = ns,
            TypeName = typeName,
            Name = name,
            Signature = FormatSignature(md, resolver, handle, m, name),
            Access = Access(attrs),
            Kinds = kinds.Distinct().ToList(),
            Rva = m.RelativeVirtualAddress,
            Key = resolver.KeyOf(handle),
            Attributes = attrs,
            ImplAttributes = impl,
            PInvoke = pinvoke,
        };
    }

    private static int SafeParamCount(MetadataReader md, MethodDefinition m)
    {
        try
        {
            var blob = md.GetBlobReader(m.Signature);
            var header = blob.ReadSignatureHeader();
            if (header.IsGeneric) blob.ReadCompressedInteger();
            return blob.ReadCompressedInteger();
        }
        catch (BadImageFormatException)
        {
            return -1;
        }
    }

    public static string Access(MethodAttributes attrs) => (attrs & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Private => "private",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.FamANDAssem => "private protected",
        _ => "privatescope",
    };

    private static string FormatSignature(MetadataReader md, MemberResolver resolver, MethodDefinitionHandle handle, MethodDefinition m, string name)
    {
        MethodSignature<string> sig;
        var ctx = resolver.ContextFor(handle);
        try
        {
            sig = m.DecodeSignature(CSharpTypeProvider.Brief, ctx);
        }
        catch (BadImageFormatException)
        {
            return name + "(?)";
        }

        var names = new string?[sig.ParameterTypes.Length];
        var modifiers = new string?[sig.ParameterTypes.Length];
        foreach (var ph in m.GetParameters())
        {
            var p = md.GetParameter(ph);
            int i = p.SequenceNumber - 1;
            if (i < 0 || i >= names.Length) continue;
            names[i] = md.GetString(p.Name);
            if ((p.Attributes & ParameterAttributes.Out) != 0 && (p.Attributes & ParameterAttributes.In) == 0 && sig.ParameterTypes[i].StartsWith("ref ", StringComparison.Ordinal))
                modifiers[i] = "out";
            else if (Names.HasAttribute(md, p.GetCustomAttributes(), "System.ParamArrayAttribute"))
                modifiers[i] = "params";
        }

        var ps = new List<string>(sig.ParameterTypes.Length);
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            var type = sig.ParameterTypes[i];
            if (modifiers[i] == "out") type = string.Concat("out ", type.AsSpan(4));
            else if (modifiers[i] == "params") type = "params " + type;
            var pname = string.IsNullOrEmpty(names[i]) ? "arg" + (i + 1) : names[i];
            ps.Add(type + " " + pname);
        }
        var generic = ctx.MethodParameters is { Length: > 0 } g ? "<" + string.Join(", ", g) + ">" : "";
        if (sig.Header.CallingConvention == SignatureCallingConvention.VarArgs) ps.Add("__arglist");
        return $"{sig.ReturnType} {name}{generic}({string.Join(", ", ps)})";
    }
}
