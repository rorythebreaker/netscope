using System.Reflection.Metadata;

namespace NetScope.Metadata;

/// <summary>Helpers to build type names from metadata handles.</summary>
public static class Names
{
    /// <summary>Full name with namespace, nested types joined by <paramref name="nestedSeparator"/>.</summary>
    public static string TypeDefFullName(MetadataReader md, TypeDefinitionHandle handle, char nestedSeparator = '+')
    {
        var td = md.GetTypeDefinition(handle);
        var name = md.GetString(td.Name);
        var declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return TypeDefFullName(md, declaring, nestedSeparator) + nestedSeparator + name;
        var ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Namespace of the outermost declaring type.</summary>
    public static string TypeDefNamespace(MetadataReader md, TypeDefinitionHandle handle)
    {
        var td = md.GetTypeDefinition(handle);
        while (!td.GetDeclaringType().IsNil) td = md.GetTypeDefinition(td.GetDeclaringType());
        return md.GetString(td.Namespace);
    }

    public static string TypeRefFullName(MetadataReader md, TypeReferenceHandle handle, char nestedSeparator = '+')
    {
        var tr = md.GetTypeReference(handle);
        var name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefFullName(md, (TypeReferenceHandle)tr.ResolutionScope, nestedSeparator) + nestedSeparator + name;
        var ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Assembly that defines a type reference, or null when it is defined in this module.</summary>
    public static string? TypeRefAssembly(MetadataReader md, TypeReferenceHandle handle)
    {
        var tr = md.GetTypeReference(handle);
        while (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            tr = md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope);
        var scope = tr.ResolutionScope;
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
            HandleKind.ModuleReference => null,
            HandleKind.ModuleDefinition => null,
            _ => null, // nil scope: exported type lookup in this assembly
        };
    }

    public static string TypeFullName(MetadataReader md, EntityHandle handle, char nestedSeparator = '+') => handle.Kind switch
    {
        HandleKind.TypeDefinition => TypeDefFullName(md, (TypeDefinitionHandle)handle, nestedSeparator),
        HandleKind.TypeReference => TypeRefFullName(md, (TypeReferenceHandle)handle, nestedSeparator),
        _ => "?",
    };

    /// <summary>Full name of the attribute type of a custom attribute.</summary>
    public static string? GetAttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        var ctor = attr.Constructor;
        EntityHandle type;
        switch (ctor.Kind)
        {
            case HandleKind.MethodDefinition:
                type = md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType();
                break;
            case HandleKind.MemberReference:
                var parent = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                if (parent.Kind != HandleKind.TypeReference && parent.Kind != HandleKind.TypeDefinition) return null;
                type = (EntityHandle)parent;
                break;
            default:
                return null;
        }
        return TypeFullName(md, type);
    }

    public static bool HasAttribute(MetadataReader md, CustomAttributeHandleCollection attributes, string fullName)
    {
        foreach (var h in attributes)
            if (GetAttributeTypeName(md, md.GetCustomAttribute(h)) == fullName) return true;
        return false;
    }

    /// <summary>Removes the generic arity suffix: "List`1" -> "List".</summary>
    public static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        if (tick < 0) return name;
        // Nested names may contain several arity markers: Outer`1+Inner`1.
        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '`')
            {
                while (i + 1 < name.Length && char.IsDigit(name[i + 1])) i++;
                continue;
            }
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>Last segment of a full type name (after the namespace and outer types).</summary>
    public static string ShortName(string fullName)
    {
        int lt = fullName.IndexOf('<');
        var head = lt < 0 ? fullName : fullName.Substring(0, lt);
        int cut = Math.Max(head.LastIndexOf('.'), head.LastIndexOf('+'));
        return cut < 0 ? fullName : fullName.Substring(cut + 1);
    }

    public static string[] GenericParameterNames(MetadataReader md, GenericParameterHandleCollection parameters)
    {
        var names = new string[parameters.Count];
        int i = 0;
        foreach (var h in parameters) names[i++] = md.GetString(md.GetGenericParameter(h).Name);
        return names;
    }

    public static string EscapeString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (char.IsControl(c) || char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
