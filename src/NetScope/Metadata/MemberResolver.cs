using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace NetScope.Metadata;

/// <summary>A method referenced from IL (call target, delegate target, ldtoken).</summary>
public sealed class MethodRef
{
    /// <summary>C#-like declaring type with namespace: MyApp.Db.Users, List&lt;int&gt;.</summary>
    public required string DeclaringType { get; init; }
    public required string Name { get; init; }
    /// <summary>C#-like parameter list: (string, int).</summary>
    public required string Parameters { get; init; }
    public required string ReturnType { get; init; }
    /// <summary>Canonical identity used to match a reference with a definition in another file.</summary>
    public required string Key { get; init; }
    /// <summary>Defining assembly, or null when the method is in the current assembly.</summary>
    public string? Assembly { get; init; }
    public required string IL { get; init; }
    public int ParameterCount { get; init; }
    public bool HasThis { get; init; }
    public bool ReturnsVoid { get; init; }
    public MethodDefinitionHandle Definition { get; init; }

    public bool IsInternal => Assembly == null;
    public string Display => DeclaringType + "::" + Name + Parameters;
    public string ShortDeclaringType => Names.ShortName(DeclaringType);
}

public sealed class FieldRef
{
    public required string DeclaringType { get; init; }
    public required string Name { get; init; }
    public required string FieldType { get; init; }
    public required string IL { get; init; }
    public string ShortDeclaringType => Names.ShortName(DeclaringType);
}

/// <summary>Resolves metadata tokens of one assembly into display names (C#-like, ildasm-like and canonical keys).</summary>
public sealed class MemberResolver
{
    private readonly MetadataReader _md;
    private readonly string _assemblyName;
    private readonly ConcurrentDictionary<int, MethodRef> _methods = new();
    private readonly ConcurrentDictionary<int, FieldRef> _fields = new();
    private readonly ConcurrentDictionary<int, (string Display, string IL)> _types = new();

    public MemberResolver(MetadataReader md, string assemblyName)
    {
        _md = md;
        _assemblyName = assemblyName;
    }

    public MetadataReader Metadata => _md;

    public static string MethodKey(string assembly, string declaringType, string name, int genericArity, IEnumerable<string> parameters, string returnType) =>
        $"{assembly}|{declaringType}::{name}`{genericArity}({string.Join(",", parameters)}):{returnType}";

    public GenericContext ContextFor(MethodDefinitionHandle handle)
    {
        var m = _md.GetMethodDefinition(handle);
        var t = _md.GetTypeDefinition(m.GetDeclaringType());
        return new GenericContext(Names.GenericParameterNames(_md, t.GetGenericParameters()), Names.GenericParameterNames(_md, m.GetGenericParameters()));
    }

    public string KeyOf(MethodDefinitionHandle handle) => ResolveMethod(handle).Key;

    public MethodRef ResolveMethod(EntityHandle handle)
    {
        int token = MetadataTokens.GetToken(handle);
        return _methods.GetOrAdd(token, _ => handle.Kind switch
        {
            HandleKind.MethodDefinition => FromDefinition((MethodDefinitionHandle)handle),
            HandleKind.MemberReference => FromReference((MemberReferenceHandle)handle, ImmutableArray<string>.Empty, null),
            HandleKind.MethodSpecification => FromSpecification((MethodSpecificationHandle)handle),
            _ => Unknown(handle),
        });
    }

    private static MethodRef Unknown(EntityHandle handle) => new()
    {
        DeclaringType = "?",
        Name = $"<token 0x{MetadataTokens.GetToken(handle):X8}>",
        Parameters = "()",
        ReturnType = "?",
        Key = $"?|{MetadataTokens.GetToken(handle):X8}",
        IL = $"0x{MetadataTokens.GetToken(handle):X8}",
    };

    private MethodRef FromDefinition(MethodDefinitionHandle handle, ImmutableArray<string> methodArgsShort = default, ImmutableArray<string> methodArgsIL = default)
    {
        var m = _md.GetMethodDefinition(handle);
        var typeHandle = m.GetDeclaringType();
        var ctx = ContextFor(handle);
        var name = _md.GetString(m.Name);
        var typeFull = Names.TypeDefFullName(_md, typeHandle);

        var display = m.DecodeSignature(CSharpTypeProvider.Brief, Instantiate(ctx, methodArgsShort));
        var key = m.DecodeSignature(CSharpTypeProvider.Key, GenericContext.Empty);
        // ildasm keeps !!N in the signature and shows the instantiation after the name.
        var il = m.DecodeSignature(ILTypeProvider.Instance, ctx);
        var genericSuffixShort = methodArgsShort.IsDefaultOrEmpty ? "" : "<" + string.Join(", ", methodArgsShort) + ">";
        var genericSuffixIL = methodArgsIL.IsDefaultOrEmpty ? "" : "<" + string.Join(", ", methodArgsIL) + ">";

        return new MethodRef
        {
            DeclaringType = Names.StripArity(typeFull).Replace('+', '.'),
            Name = name + genericSuffixShort,
            Parameters = "(" + string.Join(", ", display.ParameterTypes) + ")",
            ReturnType = display.ReturnType,
            Key = MethodKey(_assemblyName, typeFull, name, key.GenericParameterCount, key.ParameterTypes, key.ReturnType),
            Assembly = null,
            IL = FormatILMethod(il, Names.TypeDefFullName(_md, typeHandle, '/'), name + genericSuffixIL),
            ParameterCount = display.ParameterTypes.Length,
            HasThis = display.Header.IsInstance,
            ReturnsVoid = display.ReturnType == "void",
            Definition = handle,
        };
    }

    private static GenericContext Instantiate(GenericContext ctx, ImmutableArray<string> methodArgs) =>
        methodArgs.IsDefaultOrEmpty ? ctx : ctx.WithMethod(methodArgs.ToArray());

    private MethodRef FromSpecification(MethodSpecificationHandle handle)
    {
        var spec = _md.GetMethodSpecification(handle);
        var argsShort = spec.DecodeSignature(CSharpTypeProvider.Brief, GenericContext.Empty);
        var argsIL = spec.DecodeSignature(ILTypeProvider.Instance, GenericContext.Empty);
        return spec.Method.Kind switch
        {
            HandleKind.MethodDefinition => FromDefinition((MethodDefinitionHandle)spec.Method, argsShort, argsIL),
            HandleKind.MemberReference => FromReference((MemberReferenceHandle)spec.Method, argsShort, argsIL),
            _ => Unknown(handle),
        };
    }

    private readonly record struct Parent(string Display, string KeyName, string IL, string? Assembly, string[]? TypeArgs, MethodDefinitionHandle? VarargDef);

    private Parent ResolveParent(EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
            {
                var h = (TypeReferenceHandle)parent;
                var full = Names.TypeRefFullName(_md, h);
                return new Parent(Names.StripArity(full).Replace('+', '.'), full, ILTypeProvider.TypeRefName(_md, h), Names.TypeRefAssembly(_md, h), null, null);
            }
            case HandleKind.TypeDefinition:
            {
                var h = (TypeDefinitionHandle)parent;
                var full = Names.TypeDefFullName(_md, h);
                return new Parent(Names.StripArity(full).Replace('+', '.'), full, Names.TypeDefFullName(_md, h, '/'), null, null, null);
            }
            case HandleKind.TypeSpecification:
                return ResolveTypeSpecParent((TypeSpecificationHandle)parent);
            case HandleKind.MethodDefinition:
                return new Parent("", "", "", null, null, (MethodDefinitionHandle)parent);
            case HandleKind.ModuleReference:
                return new Parent("<Module>", "<Module>", "<Module>", null, null, null);
            default:
                return new Parent("?", "?", "?", null, null, null);
        }
    }

    private Parent ResolveTypeSpecParent(TypeSpecificationHandle handle)
    {
        var spec = _md.GetTypeSpecification(handle);
        var display = spec.DecodeSignature(CSharpTypeProvider.Full, GenericContext.Empty);
        var il = spec.DecodeSignature(ILTypeProvider.Instance, GenericContext.Empty);
        var blob = _md.GetBlobReader(spec.Signature);
        if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
        {
            blob.ReadCompressedInteger(); // class / valuetype
            var genericType = blob.ReadTypeHandle();
            int count = blob.ReadCompressedInteger();
            var decoder = new SignatureDecoder<string, GenericContext>(CSharpTypeProvider.Brief, _md, GenericContext.Empty);
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = decoder.DecodeType(ref blob);
            string keyName = Names.TypeFullName(_md, genericType);
            string? asm = genericType.Kind == HandleKind.TypeReference ? Names.TypeRefAssembly(_md, (TypeReferenceHandle)genericType) : null;
            return new Parent(display, keyName, il, asm, args, null);
        }
        // Arrays and other constructed types: methods are provided by the runtime.
        return new Parent(display, display, il, "<runtime>", null, null);
    }

    private MethodRef FromReference(MemberReferenceHandle handle, ImmutableArray<string> methodArgsShort, ImmutableArray<string>? methodArgsIL)
    {
        var mr = _md.GetMemberReference(handle);
        var parent = ResolveParent(mr.Parent);
        if (parent.VarargDef is { } def) return FromDefinition(def, methodArgsShort, methodArgsIL ?? default);

        var name = _md.GetString(mr.Name);
        var ctxShort = new GenericContext(parent.TypeArgs, methodArgsShort.IsDefaultOrEmpty ? null : methodArgsShort.ToArray());
        MethodSignature<string> display, key, il;
        try
        {
            display = mr.DecodeMethodSignature(CSharpTypeProvider.Brief, ctxShort);
            key = mr.DecodeMethodSignature(CSharpTypeProvider.Key, GenericContext.Empty);
            il = mr.DecodeMethodSignature(ILTypeProvider.Instance, GenericContext.Empty);
        }
        catch (BadImageFormatException)
        {
            return Unknown(handle);
        }
        var genericSuffixShort = methodArgsShort.IsDefaultOrEmpty ? "" : "<" + string.Join(", ", methodArgsShort) + ">";
        var genericSuffixIL = methodArgsIL is { IsDefaultOrEmpty: false } a ? "<" + string.Join(", ", a) + ">" : "";
        var assembly = parent.Assembly;
        return new MethodRef
        {
            DeclaringType = parent.Display,
            Name = name + genericSuffixShort,
            Parameters = "(" + string.Join(", ", display.ParameterTypes) + ")",
            ReturnType = display.ReturnType,
            Key = MethodKey(assembly ?? _assemblyName, parent.KeyName, name, key.GenericParameterCount, key.ParameterTypes, key.ReturnType),
            Assembly = assembly,
            IL = FormatILMethod(il, parent.IL, name + genericSuffixIL),
            ParameterCount = display.ParameterTypes.Length,
            HasThis = display.Header.IsInstance,
            ReturnsVoid = display.ReturnType == "void",
        };
    }

    private static string FormatILMethod(MethodSignature<string> sig, string declaringType, string name)
    {
        var prefix = sig.Header.IsInstance ? "instance " : "";
        if (sig.Header.CallingConvention == SignatureCallingConvention.VarArgs) prefix = "vararg " + prefix;
        var ps = sig.ParameterTypes;
        if (sig.RequiredParameterCount < ps.Length)
            ps = ps.Take(sig.RequiredParameterCount).Append("...").Concat(ps.Skip(sig.RequiredParameterCount)).ToImmutableArray();
        return $"{prefix}{sig.ReturnType} {declaringType}::{QuoteIL(name)}({string.Join(", ", ps)})";
    }

    /// <summary>ildasm quotes names that are not plain identifiers.</summary>
    private static string QuoteIL(string name)
    {
        int lt = name.IndexOf('<');
        var core = lt > 0 && name.EndsWith('>') ? name.Substring(0, lt) : name;
        if (core is ".ctor" or ".cctor") return name;
        foreach (var c in core)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '$' || c == '@' || c == '`' || c == '?'))
                return string.Concat("'", core, "'", name.AsSpan(core.Length));
        return name;
    }

    public FieldRef ResolveField(EntityHandle handle)
    {
        int token = MetadataTokens.GetToken(handle);
        return _fields.GetOrAdd(token, _ =>
        {
            try
            {
                if (handle.Kind == HandleKind.FieldDefinition)
                {
                    var f = _md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    var typeHandle = f.GetDeclaringType();
                    var t = _md.GetTypeDefinition(typeHandle);
                    var ctx = new GenericContext(Names.GenericParameterNames(_md, t.GetGenericParameters()), null);
                    var name = _md.GetString(f.Name);
                    var ilType = f.DecodeSignature(ILTypeProvider.Instance, ctx);
                    var ilDecl = Names.TypeDefFullName(_md, typeHandle, '/');
                    return new FieldRef
                    {
                        DeclaringType = Names.StripArity(Names.TypeDefFullName(_md, typeHandle)).Replace('+', '.'),
                        Name = name,
                        FieldType = f.DecodeSignature(CSharpTypeProvider.Brief, ctx),
                        IL = $"{ilType} {ilDecl}::{QuoteIL(name)}",
                    };
                }
                if (handle.Kind == HandleKind.MemberReference)
                {
                    var mr = _md.GetMemberReference((MemberReferenceHandle)handle);
                    var parent = ResolveParent(mr.Parent);
                    var name = _md.GetString(mr.Name);
                    var ctx = new GenericContext(parent.TypeArgs, null);
                    return new FieldRef
                    {
                        DeclaringType = parent.Display,
                        Name = name,
                        FieldType = mr.DecodeFieldSignature(CSharpTypeProvider.Brief, ctx),
                        IL = $"{mr.DecodeFieldSignature(ILTypeProvider.Instance, GenericContext.Empty)} {parent.IL}::{QuoteIL(name)}",
                    };
                }
            }
            catch (BadImageFormatException) { }
            return new FieldRef { DeclaringType = "?", Name = $"<token 0x{token:X8}>", FieldType = "?", IL = $"0x{token:X8}" };
        });
    }

    /// <summary>Type operand: C#-like display and ildasm text.</summary>
    public (string Display, string IL) ResolveType(EntityHandle handle, GenericContext context)
    {
        int token = MetadataTokens.GetToken(handle);
        if (handle.Kind == HandleKind.TypeSpecification)
        {
            // Type specs may use the method's generic parameters, so they are not cached.
            try
            {
                var spec = _md.GetTypeSpecification((TypeSpecificationHandle)handle);
                return (spec.DecodeSignature(CSharpTypeProvider.Brief, context), spec.DecodeSignature(ILTypeProvider.Instance, context));
            }
            catch (BadImageFormatException)
            {
                return ("?", $"0x{token:X8}");
            }
        }
        return _types.GetOrAdd(token, _ => handle.Kind switch
        {
            HandleKind.TypeDefinition => (Names.StripArity(Names.ShortName(Names.TypeDefFullName(_md, (TypeDefinitionHandle)handle))), Names.TypeDefFullName(_md, (TypeDefinitionHandle)handle, '/')),
            HandleKind.TypeReference => (Names.StripArity(Names.ShortName(Names.TypeRefFullName(_md, (TypeReferenceHandle)handle))), ILTypeProvider.TypeRefName(_md, (TypeReferenceHandle)handle)),
            _ => ("?", $"0x{token:X8}"),
        });
    }

    public string ResolveStandaloneSignature(StandaloneSignatureHandle handle, out MethodSignature<string> display)
    {
        var sig = _md.GetStandaloneSignature(handle);
        display = sig.DecodeMethodSignature(CSharpTypeProvider.Brief, GenericContext.Empty);
        var il = sig.DecodeMethodSignature(ILTypeProvider.Instance, GenericContext.Empty);
        var conv = il.Header.CallingConvention switch
        {
            SignatureCallingConvention.CDecl => "unmanaged cdecl ",
            SignatureCallingConvention.StdCall => "unmanaged stdcall ",
            SignatureCallingConvention.ThisCall => "unmanaged thiscall ",
            SignatureCallingConvention.FastCall => "unmanaged fastcall ",
            SignatureCallingConvention.Unmanaged => "unmanaged ",
            SignatureCallingConvention.VarArgs => "vararg ",
            _ => "",
        };
        return $"{conv}{(il.Header.IsInstance ? "instance " : "")}{il.ReturnType}({string.Join(", ", il.ParameterTypes)})";
    }
}
