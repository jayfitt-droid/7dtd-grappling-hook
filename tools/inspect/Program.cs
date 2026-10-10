using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.WriteLine("usage: inspect <assembly.dll> <typeNameSubstring>");
    return 1;
}

string asmPath = args[0];
string filter = args[1];

var sig = new Sig();

using var fs = File.OpenRead(asmPath);
using var pe = new PEReader(fs);
var rd = pe.GetMetadataReader();

foreach (var tdh in rd.TypeDefinitions)
{
    var td = rd.GetTypeDefinition(tdh);
    string ns = rd.GetString(td.Namespace);
    string nm = rd.GetString(td.Name);
    if (nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

    string full = ns.Length > 0 ? ns + "." + nm : nm;
    Console.WriteLine("==== TYPE: " + full + " ====");

    foreach (var fh in td.GetFields())
    {
        var f = rd.GetFieldDefinition(fh);
        string ft = f.DecodeSignature(sig, null);
        Console.WriteLine($"  field : {ft} {rd.GetString(f.Name)}");
    }
    foreach (var ph in td.GetProperties())
    {
        var p = rd.GetPropertyDefinition(ph);
        string pt = p.DecodeSignature(sig, null).ReturnType;
        Console.WriteLine($"  prop  : {pt} {rd.GetString(p.Name)}");
    }
    foreach (var mh in td.GetMethods())
    {
        var m = rd.GetMethodDefinition(mh);
        var msig = m.DecodeSignature(sig, null);
        var ps = m.GetParameters();
        var parts = new List<string>();
        foreach (var pph in ps)
        {
            var p = rd.GetParameter(pph);
            if (p.SequenceNumber == 0) continue;
            parts.Add(msig.ParameterTypes[p.SequenceNumber - 1] + " " + rd.GetString(p.Name));
        }
        Console.WriteLine($"  method: {msig.ReturnType} {rd.GetString(m.Name)}({string.Join(", ", parts)})");
    }
}
return 0;

sealed class Sig : ISignatureTypeProvider<string, object>
{
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
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
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString()
    };
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        string ns = reader.GetString(td.Namespace);
        string nm = reader.GetString(td.Name);
        return (ns.Length > 0 ? ns + "." : "") + nm;
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        string ns = reader.GetString(tr.Namespace);
        string nm = reader.GetString(tr.Name);
        return (ns.Length > 0 ? ns + "." : "") + nm;
    }
    public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        var ts = reader.GetTypeSpecification(handle);
        return ts.DecodeSignature(this, genericContext);
    }
}
