using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace NetScope.Metadata;

/// <summary>Names of generic parameters in scope; null entries fall back to !N / !!N.</summary>
public sealed class GenericContext
{
    public static readonly GenericContext Empty = new(null, null);

    public GenericContext(string[]? typeParameters, string[]? methodParameters)
    {
        TypeParameters = typeParameters;
        MethodParameters = methodParameters;
    }

    public string[]? TypeParameters { get; }
    public string[]? MethodParameters { get; }

    public GenericContext WithMethod(string[]? methodParameters) => new(TypeParameters, methodParameters);
}

public enum NameStyle
{
    /// <summary>C#-like with short type names: List&lt;int&gt;.</summary>
    Brief,
    /// <summary>C#-like with namespaces: System.Collections.Generic.List&lt;int&gt;.</summary>
    Full,
    /// <summary>Canonical form for matching methods across assemblies: metadata names, numbered generics.</summary>
    Key,
}

/// <summary>C#-like rendering of signature types.</summary>
public sealed class CSharpTypeProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly CSharpTypeProvider Brief = new(NameStyle.Brief);
    public static readonly CSharpTypeProvider Full = new(NameStyle.Full);
    public static readonly CSharpTypeProvider Key = new(NameStyle.Key);

    private readonly NameStyle _style;

    private CSharpTypeProvider(NameStyle style) => _style = style;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Format(Names.TypeDefFullName(reader, handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        Format(Names.TypeRefFullName(reader, handle));

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    private string Format(string fullName) => _style switch
    {
        NameStyle.Key => fullName,
        NameStyle.Full => Names.StripArity(fullName).Replace('+', '.'),
        _ => Names.StripArity(Names.ShortName(fullName)),
    };

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(", ", typeArguments) + ">";

    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "delegate*<" + string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType)) + ">";

    public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
        _style != NameStyle.Key && genericContext.MethodParameters is { } p && index < p.Length && p[index] != null ? p[index] : "!!" + index;

    public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
        _style != NameStyle.Key && genericContext.TypeParameters is { } p && index < p.Length && p[index] != null ? p[index] : "!" + index;
}

/// <summary>ildasm-like rendering of signature types: class [System.Runtime]System.Object, int32, !!0.</summary>
public sealed class ILTypeProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly ILTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "int8",
        PrimitiveTypeCode.Byte => "uint8",
        PrimitiveTypeCode.Int16 => "int16",
        PrimitiveTypeCode.UInt16 => "uint16",
        PrimitiveTypeCode.Int32 => "int32",
        PrimitiveTypeCode.UInt32 => "uint32",
        PrimitiveTypeCode.Int64 => "int64",
        PrimitiveTypeCode.UInt64 => "uint64",
        PrimitiveTypeCode.Single => "float32",
        PrimitiveTypeCode.Double => "float64",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "native int",
        PrimitiveTypeCode.UIntPtr => "native uint",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => typeCode.ToString(),
    };

    private static string Kind(byte rawTypeKind) => rawTypeKind == (byte)SignatureTypeKind.ValueType ? "valuetype " : "class ";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Kind(rawTypeKind) + Names.TypeDefFullName(reader, handle, '/');

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        Kind(rawTypeKind) + TypeRefName(reader, handle);

    /// <summary>A type reference without class/valuetype prefix: [Assembly]Namespace.Type.</summary>
    public static string TypeRefName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var asm = Names.TypeRefAssembly(reader, handle);
        var name = Names.TypeRefFullName(reader, handle, '/');
        return asm == null ? name : "[" + asm + "]" + name;
    }

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(", ", typeArguments) + ">";

    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + string.Join(",", Enumerable.Repeat("0...", Math.Max(1, shape.Rank))) + "]";

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType + " pinned";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        unmodifiedType + (isRequired ? " modreq(" : " modopt(") + StripKind(modifier) + ")";

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "method " + signature.ReturnType + " *(" + string.Join(", ", signature.ParameterTypes) + ")";

    public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
        genericContext.MethodParameters is { } p && index < p.Length && p[index] != null ? "!!" + p[index] : "!!" + index;

    public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
        genericContext.TypeParameters is { } p && index < p.Length && p[index] != null ? "!" + p[index] : "!" + index;

    /// <summary>Removes the leading "class " / "valuetype " keyword (used for declaring types and operands).</summary>
    public static string StripKind(string type)
    {
        if (type.StartsWith("class ", StringComparison.Ordinal)) return type.Substring(6);
        if (type.StartsWith("valuetype ", StringComparison.Ordinal)) return type.Substring(10);
        return type;
    }
}
