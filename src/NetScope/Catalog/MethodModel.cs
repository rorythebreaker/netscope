using System.Reflection;
using System.Reflection.Metadata;
using NetScope.Loading;

namespace NetScope.Catalog;

public sealed class PInvokeInfo
{
    /// <summary>Empty for C++/CLI (IJW) imports that are bound by the native part of the image.</summary>
    public required string Dll { get; init; }
    public required string EntryPoint { get; init; }

    public override string ToString() => Dll.Length == 0 ? EntryPoint + " (C++/CLI native import, no DLL in metadata)" : Dll + "!" + EntryPoint;
}

/// <summary>A method definition with its classification (group 4.1 and 4.2 of the report).</summary>
public sealed class MethodModel
{
    private MethodBodyBlock? _body;
    private string? _bodyError;
    private bool _bodyRead;

    public required LoadedAssembly Assembly { get; init; }
    public required MethodDefinitionHandle Handle { get; init; }
    public required TypeDefinitionHandle DeclaringTypeHandle { get; init; }
    public int Token { get; init; }
    public required string Namespace { get; init; }
    /// <summary>Full type name with namespace, nested types separated by '+'.</summary>
    public required string TypeName { get; init; }
    public required string Name { get; init; }
    /// <summary>C#-like signature with parameter names: bool Login(string user, string pass).</summary>
    public required string Signature { get; init; }
    public required string Access { get; init; }
    /// <summary>Classification without the access modifier, e.g. instance, virtual, async.</summary>
    public required List<string> Kinds { get; init; }
    public int Rva { get; init; }
    public required string Key { get; init; }
    public MethodAttributes Attributes { get; init; }
    public MethodImplAttributes ImplAttributes { get; init; }
    public PInvokeInfo? PInvoke { get; init; }
    /// <summary>Whether the method passed --filter and --type.</summary>
    public bool Selected { get; set; } = true;

    public string FullName => TypeName + "::" + Name;
    public bool HasBody => Rva != 0 && (ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL;
    public bool IsNativeCode => Rva != 0 && (ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Native;
    public bool IsStatic => (Attributes & MethodAttributes.Static) != 0;

    /// <summary>Kinds as shown in text output: static/instance, then access, then the rest.</summary>
    public IEnumerable<string> DisplayKinds()
    {
        bool accessAdded = false;
        foreach (var k in Kinds)
        {
            yield return k;
            if (!accessAdded && (k == "static" || k == "instance"))
            {
                accessAdded = true;
                yield return Access;
            }
        }
        if (!accessAdded) yield return Access;
    }

    /// <summary>Reads the method body once; encrypted or broken bodies are reported as unreadable.</summary>
    public MethodBodyBlock? GetBody(out string? error)
    {
        if (!_bodyRead)
        {
            _bodyRead = true;
            if (HasBody)
            {
                try
                {
                    _body = Assembly.PE.GetMethodBody(Rva);
                }
                catch (BadImageFormatException ex)
                {
                    _bodyError = "method body is unreadable (encrypted or corrupted): " + ex.Message;
                }
            }
            else
            {
                _bodyError = IsNativeCode ? "native code outside the managed part" : "method has no IL body";
            }
        }
        error = _bodyError;
        return _body;
    }

    /// <summary>Releases the cached body so that large groups do not keep all of them alive.</summary>
    public void ReleaseBody()
    {
        _body = null;
        _bodyRead = false;
    }
}
