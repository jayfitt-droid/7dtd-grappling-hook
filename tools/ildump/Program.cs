using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 3)
{
    Console.WriteLine("usage: ildump <assembly.dll> <typeSubstring> <methodSubstring>");
    return 1;
}

string asmPath = args[0];
string typeFilter = args[1];
string methodFilter = args[2];

var opcodes = BuildOpcodeTable();

using var fs = File.OpenRead(asmPath);
using var pe = new PEReader(fs);
var rd = pe.GetMetadataReader();

foreach (var tdh in rd.TypeDefinitions)
{
    var td = rd.GetTypeDefinition(tdh);
    string ns = rd.GetString(td.Namespace);
    string nm = rd.GetString(td.Name);
    if (nm.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

    foreach (var mh in td.GetMethods())
    {
        var m = rd.GetMethodDefinition(mh);
        string mn = rd.GetString(m.Name);
        if (mn.IndexOf(methodFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

        string full = (ns.Length > 0 ? ns + "." : "") + nm;
        Console.WriteLine("===== " + full + "::" + mn + " =====");
        DumpIL(pe, rd, m, opcodes);
        Console.WriteLine();
    }
}
return 0;

static void DumpIL(PEReader pe, MetadataReader rd, MethodDefinition m, Dictionary<int, (string, OpType)> opcodes)
{
    int rva = m.RelativeVirtualAddress;
    if (rva == 0) { Console.WriteLine("  (no body)"); return; }
    var body = pe.GetMethodBody(rva);
    byte[] il = body.GetILBytes();
    int offset = 0;
    try
    {
    while (offset < il.Length)
    {
        int start = offset;
        int b = il[offset++];
        int op;
        if (b == 0xFE) { op = 0xFE00 | il[offset++]; }
        else { op = b; }

        if (!opcodes.TryGetValue(op, out var info))
        {
            Console.WriteLine($"  IL_{start:X4}: UNKNOWN 0x{op:X}");
            break;
        }
        var (name, ot) = info;

        string operand = "";
        switch (ot)
        {
            case OpType.None: break;
            case OpType.ShortI:
                operand = ((sbyte)il[offset++]).ToString();
                break;
            case OpType.I:
                operand = BitConverter.ToInt32(il, offset).ToString();
                offset += 4;
                break;
            case OpType.I8:
                operand = BitConverter.ToInt64(il, offset).ToString();
                offset += 8;
                break;
            case OpType.ShortR:
                operand = BitConverter.ToSingle(il, offset).ToString();
                offset += 4;
                break;
            case OpType.R:
                operand = BitConverter.ToDouble(il, offset).ToString();
                offset += 8;
                break;
            case OpType.ShortVar:
                operand = "loc" + il[offset++];
                break;
            case OpType.Var:
                operand = "loc" + BitConverter.ToUInt16(il, offset);
                offset += 2;
                break;
            case OpType.BrTarget:
                {
                    int delta = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    operand = "IL_" + (start + 5 + delta).ToString("X4");
                    break;
                }
            case OpType.ShortBrTarget:
                {
                    int delta = (sbyte)il[offset++];
                    operand = "IL_" + (start + 2 + delta).ToString("X4");
                    break;
                }
            case OpType.Switch:
                {
                    int n = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    var targets = new List<string>();
                    for (int i = 0; i < n; i++) { int d = BitConverter.ToInt32(il, offset); offset += 4; targets.Add("IL_" + (start + 4 + n * 4 + d).ToString("X4")); }
                    operand = "[" + string.Join(", ", targets) + "]";
                    break;
                }
            case OpType.String:
                {
                    int tok = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    operand = "\"" + rd.GetUserString(MetadataTokens.UserStringHandle(tok)) + "\"";
                    break;
                }
            case OpType.Field:
            case OpType.Method:
            case OpType.Tok:
                {
                    int tok = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    operand = ResolveMember(rd, tok);
                    break;
                }
            case OpType.Type:
                {
                    int tok = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    operand = ResolveType(rd, tok);
                    break;
                }
            case OpType.Sig:
                operand = "sig(0x" + BitConverter.ToInt32(il, offset).ToString("X") + ")";
                offset += 4;
                break;
            default:
                Console.WriteLine($"  IL_{start:X4}: {name} [unhandled {ot}]");
                continue;
        }
        Console.WriteLine($"  IL_{start:X4}: {name} {operand}");
    }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  [decode stopped at IL_" + offset.ToString("X4") + ": " + ex.GetType().Name + "]");
    }
}

static string ResolveMember(MetadataReader rd, int tok)
{
    try
    {
        var handle = MetadataTokens.EntityHandle(tok);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    var m = rd.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return rd.GetString(rd.GetTypeDefinition(m.GetDeclaringType()).Name) + "::" + rd.GetString(m.Name);
                }
            case HandleKind.FieldDefinition:
                {
                    var f = rd.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return rd.GetString(rd.GetTypeDefinition(f.GetDeclaringType()).Name) + "::" + rd.GetString(f.Name);
                }
            case HandleKind.MemberReference:
                {
                    var mr = rd.GetMemberReference((MemberReferenceHandle)handle);
                    return "ref::" + rd.GetString(mr.Name);
                }
            case HandleKind.MethodSpecification:
                {
                    var ms = rd.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return "spec(" + ResolveMember(rd, MetadataTokens.GetToken(ms.Method)) + ")";
                }
        }
        return handle.Kind.ToString();
    }
    catch (Exception ex) { return "ERR(" + ex.GetType().Name + ")"; }
}

static string ResolveType(MetadataReader rd, int tok)
{
    uint kind = (uint)(tok & 0xFF000000);
    try
    {
        if (kind == 0x02000000)
            return rd.GetString(rd.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(tok)).Name);
        if (kind == 0x01000000)
            return rd.GetString(rd.GetTypeReference(MetadataTokens.TypeReferenceHandle(tok)).Name);
    }
    catch { }
    return "tok(0x" + tok.ToString("X") + ")";
}

static Dictionary<int, (string, OpType)> BuildOpcodeTable()
{
    var d = new Dictionary<int, (string, OpType)>
    {
        [0x00] = ("nop", OpType.None),
        [0x01] = ("break", OpType.None),
        [0x02] = ("ldarg.0", OpType.None),
        [0x03] = ("ldarg.1", OpType.None),
        [0x04] = ("ldarg.2", OpType.None),
        [0x05] = ("ldarg.3", OpType.None),
        [0x06] = ("ldloc.0", OpType.None),
        [0x07] = ("ldloc.1", OpType.None),
        [0x08] = ("ldloc.2", OpType.None),
        [0x09] = ("ldloc.3", OpType.None),
        [0x0A] = ("stloc.0", OpType.None),
        [0x0B] = ("stloc.1", OpType.None),
        [0x0C] = ("stloc.2", OpType.None),
        [0x0D] = ("stloc.3", OpType.None),
        [0x0E] = ("ldarg.s", OpType.ShortVar),
        [0x0F] = ("ldarga.s", OpType.ShortVar),
        [0x10] = ("starg.s", OpType.ShortVar),
        [0x11] = ("ldloc.s", OpType.ShortVar),
        [0x12] = ("ldloca.s", OpType.ShortVar),
        [0x13] = ("stloc.s", OpType.ShortVar),
        [0x14] = ("ldnull", OpType.None),
        [0x15] = ("ldc.i4.m1", OpType.None),
        [0x16] = ("ldc.i4.0", OpType.None),
        [0x17] = ("ldc.i4.1", OpType.None),
        [0x18] = ("ldc.i4.2", OpType.None),
        [0x19] = ("ldc.i4.3", OpType.None),
        [0x1A] = ("ldc.i4.4", OpType.None),
        [0x1B] = ("ldc.i4.5", OpType.None),
        [0x1C] = ("ldc.i4.6", OpType.None),
        [0x1D] = ("ldc.i4.7", OpType.None),
        [0x1E] = ("ldc.i4.8", OpType.None),
        [0x1F] = ("ldc.i4.s", OpType.ShortI),
        [0x20] = ("ldc.i4", OpType.I),
        [0x21] = ("ldc.i8", OpType.I8),
        [0x22] = ("ldc.r4", OpType.ShortR),
        [0x23] = ("ldc.r8", OpType.R),
        [0x25] = ("dup", OpType.None),
        [0x26] = ("pop", OpType.None),
        [0x28] = ("call", OpType.Method),
        [0x29] = ("calli", OpType.Sig),
        [0x2A] = ("ret", OpType.None),
        [0x2B] = ("br.s", OpType.ShortBrTarget),
        [0x2C] = ("brfalse.s", OpType.ShortBrTarget),
        [0x2D] = ("brtrue.s", OpType.ShortBrTarget),
        [0x2E] = ("beq.s", OpType.ShortBrTarget),
        [0x2F] = ("bge.s", OpType.ShortBrTarget),
        [0x30] = ("bgt.s", OpType.ShortBrTarget),
        [0x31] = ("ble.s", OpType.ShortBrTarget),
        [0x32] = ("blt.s", OpType.ShortBrTarget),
        [0x33] = ("bne.un.s", OpType.ShortBrTarget),
        [0x34] = ("bge.un.s", OpType.ShortBrTarget),
        [0x35] = ("bgt.un.s", OpType.ShortBrTarget),
        [0x36] = ("ble.un.s", OpType.ShortBrTarget),
        [0x37] = ("blt.un.s", OpType.ShortBrTarget),
        [0x38] = ("br", OpType.BrTarget),
        [0x39] = ("brfalse", OpType.BrTarget),
        [0x3A] = ("brtrue", OpType.BrTarget),
        [0x3B] = ("beq", OpType.BrTarget),
        [0x3C] = ("bge", OpType.BrTarget),
        [0x3D] = ("bgt", OpType.BrTarget),
        [0x3E] = ("ble", OpType.BrTarget),
        [0x3F] = ("blt", OpType.BrTarget),
        [0x40] = ("bne.un", OpType.BrTarget),
        [0x45] = ("switch", OpType.Switch),
        [0x46] = ("ldind.i1", OpType.None),
        [0x47] = ("ldind.u1", OpType.None),
        [0x48] = ("ldind.i2", OpType.None),
        [0x49] = ("ldind.u2", OpType.None),
        [0x4A] = ("ldind.i4", OpType.None),
        [0x4B] = ("ldind.u4", OpType.None),
        [0x4C] = ("ldind.i8", OpType.None),
        [0x4D] = ("ldind.i", OpType.None),
        [0x4E] = ("ldind.r4", OpType.None),
        [0x4F] = ("ldind.r8", OpType.None),
        [0x50] = ("ldind.ref", OpType.None),
        [0x51] = ("stind.ref", OpType.None),
        [0x52] = ("stind.i1", OpType.None),
        [0x53] = ("stind.i2", OpType.None),
        [0x54] = ("stind.i4", OpType.None),
        [0x55] = ("stind.i8", OpType.None),
        [0x56] = ("stind.r4", OpType.None),
        [0x57] = ("stind.r8", OpType.None),
        [0x58] = ("add", OpType.None),
        [0x59] = ("sub", OpType.None),
        [0x5A] = ("mul", OpType.None),
        [0x5B] = ("div", OpType.None),
        [0x5F] = ("and", OpType.None),
        [0x60] = ("or", OpType.None),
        [0x61] = ("xor", OpType.None),
        [0x62] = ("shl", OpType.None),
        [0x63] = ("shr", OpType.None),
        [0x65] = ("neg", OpType.None),
        [0x66] = ("not", OpType.None),
        [0x67] = ("conv.i1", OpType.None),
        [0x68] = ("conv.i2", OpType.None),
        [0x69] = ("conv.i4", OpType.None),
        [0x6A] = ("conv.i8", OpType.None),
        [0x6B] = ("conv.r4", OpType.None),
        [0x6C] = ("conv.r8", OpType.None),
        [0x6F] = ("callvirt", OpType.Method),
        [0x70] = ("cpobj", OpType.Type),
        [0x72] = ("ldstr", OpType.String),
        [0x73] = ("newobj", OpType.Method),
        [0x74] = ("castclass", OpType.Type),
        [0x75] = ("isinst", OpType.Type),
        [0x79] = ("unbox", OpType.Type),
        [0x7B] = ("ldfld", OpType.Field),
        [0x7C] = ("ldflda", OpType.Field),
        [0x7D] = ("stfld", OpType.Field),
        [0x7E] = ("ldsfld", OpType.Field),
        [0x7F] = ("stsfld", OpType.Field),
        [0x80] = ("stobj", OpType.Type),
        [0x8C] = ("box", OpType.Type),
        [0x8D] = ("newarr", OpType.Type),
        [0x8E] = ("ldlen", OpType.None),
        [0x8F] = ("ldelema", OpType.Type),
        [0x90] = ("ldelem.i1", OpType.None),
        [0x91] = ("ldelem.u1", OpType.None),
        [0x92] = ("ldelem.i2", OpType.None),
        [0x94] = ("ldelem.ref", OpType.None),
        [0x9A] = ("stelem.i", OpType.None),
        [0xA2] = ("stelem.ref", OpType.None),
        [0xD0] = ("ldtoken", OpType.Tok),
        [0xD1] = ("conv.r.un", OpType.None),
        [0xD2] = ("ldelem.any", OpType.Type),
        [0xD3] = ("stelem.any", OpType.Type),
        [0xFE00 | 0x01] = ("ceq", OpType.None),
        [0xFE00 | 0x02] = ("cgt", OpType.None),
        [0xFE00 | 0x03] = ("cgt.un", OpType.None),
        [0xFE00 | 0x04] = ("clt", OpType.None),
        [0xFE00 | 0x05] = ("clt.un", OpType.None),
        [0xFE00 | 0x06] = ("ldftn", OpType.Method),
        [0xFE00 | 0x07] = ("ldvirtftn", OpType.Method),
        [0xFE00 | 0x09] = ("ldarg", OpType.Var),
        [0xFE00 | 0x0A] = ("ldarga", OpType.Var),
        [0xFE00 | 0x0B] = ("starg", OpType.Var),
        [0xFE00 | 0x0C] = ("ldloc", OpType.Var),
        [0xFE00 | 0x0D] = ("ldloca", OpType.Var),
        [0xFE00 | 0x0E] = ("stloc", OpType.Var),
        [0xFE00 | 0x15] = ("initobj", OpType.Type),
        [0xFE00 | 0x16] = ("constrained.", OpType.Type),
        [0xFE00 | 0x1C] = ("sizeof", OpType.Type),
    };
    return d;
}

enum OpType
{
    None, ShortI, I, I8, ShortR, R, ShortVar, Var, BrTarget, ShortBrTarget,
    Switch, String, Field, Method, Type, Tok, Sig
}
