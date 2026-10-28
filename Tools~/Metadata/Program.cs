using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
    if (args.Length < 2 || args[0] is not ("inspect" or "compare" or "plan"))
        throw new ArgumentException("Usage: inspect <dll> | compare <baseline.dll> <candidate.dll> | plan <baseline.dll> <candidate.dll>");
    var planning = args[0] == "plan";
    var baseline = Image.Read(args[1]);
    object output;
    if (args[0] == "inspect")
    {
        output = new { assemblyName = baseline.Name, identity = baseline.Identity, mvid = baseline.Mvid, sha256 = baseline.Sha256, structureSha256 = baseline.StructureHash, types = baseline.Types.Keys.ToArray(), assemblyReferences = baseline.AssemblyReferences };
    }
    else
    {
        if (args.Length != 3) throw new ArgumentException(args[0] + " requires baseline and candidate DLLs.");
        var candidate = Image.Read(args[2]);
        var differences = new List<string>();
        if (baseline.Identity != candidate.Identity) differences.Add("Assembly identity changed.");
        foreach (var name in baseline.Types.Keys.Except(candidate.Types.Keys)) differences.Add("Removed type: " + name);
        foreach (var name in candidate.Types.Keys.Except(baseline.Types.Keys)) differences.Add("Added type: " + name);
        foreach (var name in baseline.Types.Keys.Intersect(candidate.Types.Keys))
            if (baseline.Types[name] != candidate.Types[name]) differences.Add("Changed type/member definition: " + name);
        if (differences.Count == 0 && baseline.StructureHash != candidate.StructureHash)
            differences.Add("Assembly reference or attribute definitions changed.");
        if (planning && differences.Count == 0)
        {
            var bodyBaseline = Image.Read(args[1], true);
            var bodyCandidate = Image.Read(args[2], true);
            if (bodyBaseline.Sha256 != baseline.Sha256 || bodyCandidate.Sha256 != candidate.Sha256)
                throw new InvalidDataException("A DLL changed during metadata planning.");
            baseline = bodyBaseline;
            candidate = bodyCandidate;
        }
        var comparison = new
        {
            compatible = differences.Count == 0,
            assemblyName = baseline.Name,
            baselineMvid = baseline.Mvid,
            candidateMvid = candidate.Mvid,
            baselineSha256 = baseline.Sha256,
            candidateSha256 = candidate.Sha256,
            structureSha256 = baseline.StructureHash,
            differences
        };
        output = planning ? new
        {
            comparison.compatible, comparison.assemblyName, comparison.baselineMvid,
            comparison.candidateMvid, comparison.baselineSha256, comparison.candidateSha256,
            comparison.structureSha256, comparison.differences,
            baselineBodySha256 = differences.Count == 0 ? baseline.BodyHash : null,
            candidateBodySha256 = differences.Count == 0 ? candidate.BodyHash : null,
            changedMethods = differences.Count == 0
                ? baseline.Methods.Values.Where(method => method.Body != candidate.Methods[method.Token].Body)
                    .Select(method => new { typeName = method.TypeName, name = method.Name, token = method.Token }).ToArray()
                : []
        } : comparison;
    }
    Console.WriteLine(JsonSerializer.Serialize(output));
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = error.GetType().Name + ": " + error.Message }));
    return 1;
}

internal sealed record MethodImage(int Token, string TypeName, string Name, string Body);

internal sealed record Image(string Name, string Identity, string Mvid, string Sha256, string StructureHash,
    SortedDictionary<string, string> Types, SortedDictionary<int, MethodImage> Methods, string[] AssemblyReferences)
{
    public string BodyHash => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Methods.Values
        .Select(method => new { method.Token, method.TypeName, method.Name, method.Body }).ToArray())));
    public static Image Read(string path, bool includeBodies = false)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("A plain managed DLL is required for metadata validation.");
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly) throw new InvalidDataException("Managed netmodules are unsupported.");
        var assembly = reader.GetAssemblyDefinition();
        var provider = new TypeNames(reader);
        var name = reader.GetString(assembly.Name);
        var identity = string.Join("|", name, assembly.Version, reader.GetString(assembly.Culture), Convert.ToHexString(reader.GetBlobBytes(assembly.PublicKey)), (int)assembly.Flags);
        var types = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var methods = new SortedDictionary<int, MethodImage>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var fullName = provider.DefinitionName(handle);
            var text = new StringBuilder();
            var layout = definition.GetLayout();
            text.AppendLine($"type|{MetadataTokens.GetToken(handle)}|{fullName}|{(int)definition.Attributes}|{provider.Entity(definition.BaseType)}|{layout.PackingSize}|{layout.Size}");
            text.AppendLine("generic|" + GenericParameters(reader, provider, definition.GetGenericParameters()));
            text.AppendLine("attributes|" + Attributes(reader, provider, definition.GetCustomAttributes()));
            foreach (var implementation in definition.GetInterfaceImplementations())
                text.AppendLine("interface|" + MetadataTokens.GetToken(implementation) + "|" + provider.Entity(reader.GetInterfaceImplementation(implementation).Interface));
            // Definition order is intentionally retained: reordering fields can change
            // layout. Explicit global metadata tokens must also match: sorting types
            // by name otherwise hides token shifts when existing types are reordered.
            foreach (var fieldHandle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                text.AppendLine($"field|{MetadataTokens.GetToken(fieldHandle)}|{reader.GetString(field.Name)}|{(int)field.Attributes}|{field.DecodeSignature(provider, null)}|{field.GetOffset()}|{Constant(reader, field.GetDefaultValue())}|{Attributes(reader, provider, field.GetCustomAttributes())}|rva:{FieldData(pe, reader, provider, field)}");
            }
            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var token = MetadataTokens.GetToken(methodHandle);
                if (includeBodies)
                    methods.Add(token, new MethodImage(token, fullName, reader.GetString(method.Name),
                        MethodBodies.Read(pe, reader, provider, method)));
                var signature = TypeNames.Signature(method.DecodeSignature(provider, null));
                text.AppendLine($"method|{MetadataTokens.GetToken(methodHandle)}|{reader.GetString(method.Name)}|{(int)method.Attributes}|{(int)method.ImplAttributes}|{signature}|{GenericParameters(reader, provider, method.GetGenericParameters())}|{Attributes(reader, provider, method.GetCustomAttributes())}");
                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = reader.GetParameter(parameterHandle);
                    text.AppendLine($"parameter|{MetadataTokens.GetToken(parameterHandle)}|{parameter.SequenceNumber}|{reader.GetString(parameter.Name)}|{(int)parameter.Attributes}|{Constant(reader, parameter.GetDefaultValue())}|{Attributes(reader, provider, parameter.GetCustomAttributes())}");
                }
                var import = method.GetImport();
                if (!import.Module.IsNil)
                    text.AppendLine($"import|{(int)import.Attributes}|{reader.GetString(import.Name)}|{reader.GetString(reader.GetModuleReference(import.Module).Name)}");
            }
            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();
                text.AppendLine($"property|{MetadataTokens.GetToken(propertyHandle)}|{reader.GetString(property.Name)}|{(int)property.Attributes}|{TypeNames.Signature(property.DecodeSignature(provider, null))}|{Constant(reader, property.GetDefaultValue())}|{provider.Method(accessors.Getter)}|{provider.Method(accessors.Setter)}|{Attributes(reader, provider, property.GetCustomAttributes())}");
            }
            foreach (var eventHandle in definition.GetEvents())
            {
                var value = reader.GetEventDefinition(eventHandle);
                var accessors = value.GetAccessors();
                text.AppendLine($"event|{MetadataTokens.GetToken(eventHandle)}|{reader.GetString(value.Name)}|{(int)value.Attributes}|{provider.Entity(value.Type)}|{provider.Method(accessors.Adder)}|{provider.Method(accessors.Remover)}|{provider.Method(accessors.Raiser)}|{Attributes(reader, provider, value.GetCustomAttributes())}");
            }
            foreach (var methodImplementation in definition.GetMethodImplementations())
            {
                var implementation = reader.GetMethodImplementation(methodImplementation);
                text.AppendLine("override|" + MetadataTokens.GetToken(methodImplementation) + "|" + provider.Entity(implementation.MethodDeclaration) + "|" + provider.Entity(implementation.MethodBody));
            }
            types.Add(fullName, text.ToString());
        }
        var assemblyReferences = reader.AssemblyReferences.Select(handle =>
        {
            var reference = reader.GetAssemblyReference(handle);
            return string.Join("|", reader.GetString(reference.Name), reference.Version, reader.GetString(reference.Culture),
                Convert.ToHexString(reader.GetBlobBytes(reference.PublicKeyOrToken)), (int)reference.Flags);
        }).OrderBy(value => value, StringComparer.Ordinal);
        var structure = identity + "\n" + string.Join("\n", assemblyReferences) + "\n" +
            Attributes(reader, provider, assembly.GetCustomAttributes()) + "\n" + string.Join("\n", types.Select(pair => pair.Value));
        return new Image(name, identity, reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D"), Hash(bytes), Hash(Encoding.UTF8.GetBytes(structure)), types, methods,
            reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static string FieldData(PEReader pe, MetadataReader reader, TypeNames provider, FieldDefinition field)
    {
        var rva = field.GetRelativeVirtualAddress();
        if (rva == 0) return "";
        var type = field.DecodeSignature(provider, null);
        var size = type switch
        {
            "Boolean" or "Byte" or "SByte" => 1,
            "Char" or "Int16" or "UInt16" => 2,
            "Int32" or "UInt32" or "Single" => 4,
            "Int64" or "UInt64" or "Double" => 8,
            "IntPtr" or "UIntPtr" => pe.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus ? 8 : 4,
            _ => 0
        };
        if (size == 0)
        {
            foreach (var handle in reader.TypeDefinitions)
                if (provider.DefinitionName(handle) == type) { size = reader.GetTypeDefinition(handle).GetLayout().Size; break; }
        }
        if (size <= 0) throw new InvalidDataException("Cannot safely determine initialized field size: " + type);
        return size + ":" + Hash(pe.GetSectionData(rva).GetContent(0, size).ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Constant(MetadataReader reader, ConstantHandle handle)
    {
        if (handle.IsNil) return "";
        var value = reader.GetConstant(handle);
        return value.TypeCode + ":" + Convert.ToHexString(reader.GetBlobBytes(value.Value));
    }

    private static string GenericParameters(MetadataReader reader, TypeNames provider, GenericParameterHandleCollection parameters)
    {
        return string.Join(";", parameters.Select(handle =>
        {
            var parameter = reader.GetGenericParameter(handle);
            return $"{MetadataTokens.GetToken(handle)}:{parameter.Index}:{reader.GetString(parameter.Name)}:{(int)parameter.Attributes}:" + string.Join(",", parameter.GetConstraints().Select(c => provider.Entity(reader.GetGenericParameterConstraint(c).Type)).OrderBy(value => value, StringComparer.Ordinal));
        }));
    }

    private static string Attributes(MetadataReader reader, TypeNames provider, CustomAttributeHandleCollection attributes)
    {
        return string.Join(";", attributes.Select(handle =>
        {
            var attribute = reader.GetCustomAttribute(handle);
            return provider.Entity(attribute.Constructor) + ":" + Convert.ToHexString(reader.GetBlobBytes(attribute.Value));
        }).OrderBy(value => value, StringComparer.Ordinal));
    }
}

internal sealed class TypeNames(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    public string DefinitionName(TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(parent) + "+" + reader.GetString(definition.Name);
        var ns = reader.GetString(definition.Namespace);
        return (ns.Length == 0 ? "" : ns + ".") + reader.GetString(definition.Name);
    }

    public string Entity(EntityHandle handle)
    {
        if (handle.IsNil) return "";
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => DefinitionName((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
            HandleKind.MethodDefinition => Method((MethodDefinitionHandle)handle),
            HandleKind.FieldDefinition => Field((FieldDefinitionHandle)handle),
            HandleKind.MemberReference => Member((MemberReferenceHandle)handle),
            HandleKind.MethodSpecification => MethodSpecification((MethodSpecificationHandle)handle),
            HandleKind.ModuleReference => "module:" + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)handle).Name),
            _ => throw new InvalidDataException("Unsupported metadata handle: " + handle.Kind)
        };
    }

    public string Method(MethodDefinitionHandle handle)
    {
        if (handle.IsNil) return "";
        var method = reader.GetMethodDefinition(handle);
        return DefinitionName(method.GetDeclaringType()) + "::" + reader.GetString(method.Name) + "|" + Signature(method.DecodeSignature(this, null));
    }

    private string Field(FieldDefinitionHandle handle)
    {
        var field = reader.GetFieldDefinition(handle);
        return DefinitionName(field.GetDeclaringType()) + "::" + reader.GetString(field.Name) + "|" + field.DecodeSignature(this, null);
    }

    private string MethodSpecification(MethodSpecificationHandle handle)
    {
        var method = reader.GetMethodSpecification(handle);
        return Entity(method.Method) + "[" + string.Join(",", method.DecodeSignature(this, null)) + "]";
    }

    private string Member(MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        var signature = member.GetKind() == MemberReferenceKind.Method
            ? Signature(member.DecodeMethodSignature(this, null))
            : member.DecodeFieldSignature(this, null);
        return Entity(member.Parent) + "::" + reader.GetString(member.Name) + "|" + signature;
    }

    public static string Signature(MethodSignature<string> signature) => $"{signature.Header.RawValue}:{signature.GenericParameterCount}:{signature.RequiredParameterCount}:{signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[rank={shape.Rank};sizes={string.Join(",", shape.Sizes)};lower={string.Join(",", shape.LowerBounds)}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr:" + Signature(signature);
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => $"{unmodifiedType} mod{(isRequired ? "req" : "opt")}({modifier})";
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind) => DefinitionName(handle);
    public string GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = metadataReader.GetTypeReference(handle);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(metadataReader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind) + "+" + metadataReader.GetString(reference.Name);
        var ns = metadataReader.GetString(reference.Namespace);
        var assembly = reference.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? metadataReader.GetString(metadataReader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name)
            : "";
        return "[" + assembly + "]" + (ns.Length == 0 ? "" : ns + ".") + metadataReader.GetString(reference.Name);
    }
    public string GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}

/// <summary>只读取 PE 中的方法体，不把待验证程序集加载到宿主进程。</summary>
internal static class MethodBodies
{
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    private sealed record Instruction(int Offset, string Code, object? Operand);
    private sealed record Branch(int Offset);
    private sealed record Branches(int[] Offsets);

    public static string Read(PEReader pe, MetadataReader reader, TypeNames names, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0) return "no-managed-body";
        if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
            throw new InvalidDataException("Only managed IL method bodies can be planned.");
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        var il = body.GetILBytes() ?? throw new InvalidDataException("Method has no IL stream.");
        var instructions = Decode(il, reader, names);
        var indices = instructions.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(pair => pair.Offset, pair => pair.index);
        indices.Add(il.Length, instructions.Count);
        int Position(int offset) => indices.TryGetValue(offset, out var index) ? index
            : throw new InvalidDataException("IL branch or exception boundary is not an instruction boundary.");
        object? Operand(object? value) => value switch
        {
            Branch branch => new { target = BranchPosition(branch.Offset) },
            Branches branches => new { targets = branches.Offsets.Select(BranchPosition).ToArray() },
            _ => value
        };
        int BranchPosition(int offset) => offset != il.Length ? Position(offset)
            : throw new InvalidDataException("An IL branch cannot target the end of the method.");
        string CatchType(ExceptionRegion region)
        {
            if (region.Kind != ExceptionRegionKind.Catch) return "";
            if (region.CatchType.IsNil || region.CatchType.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification))
                throw new InvalidDataException("Invalid IL exception catch type.");
            return names.Entity(region.CatchType);
        }
        var locals = body.LocalSignature.IsNil ? [] : reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null).ToArray();
        return JsonSerializer.Serialize(new
        {
            body.LocalVariablesInitialized, body.MaxStack, locals,
            instructions = instructions.Select(instruction => new { instruction.Code, operand = Operand(instruction.Operand) }).ToArray(),
            exceptions = body.ExceptionRegions.Select(region => new
            {
                kind = region.Kind.ToString(), start = Position(region.TryOffset), end = Position(checked(region.TryOffset + region.TryLength)),
                handlerStart = Position(region.HandlerOffset), handlerEnd = Position(checked(region.HandlerOffset + region.HandlerLength)),
                filter = region.Kind == ExceptionRegionKind.Filter ? BranchPosition(region.FilterOffset) : -1,
                caught = CatchType(region)
            }).ToArray()
        });
    }

    private static List<Instruction> Decode(byte[] bytes, MetadataReader reader, TypeNames names)
    {
        var result = new List<Instruction>();
        var position = 0;
        byte ReadByte()
        {
            if (position >= bytes.Length) throw new InvalidDataException("Truncated IL instruction.");
            return bytes[position++];
        }
        byte[] ReadBytes(int count)
        {
            if (count < 0 || count > bytes.Length - position) throw new InvalidDataException("Truncated IL operand.");
            var data = bytes.AsSpan(position, count).ToArray();
            position += count;
            return data;
        }
        int Int32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
        while (position < bytes.Length)
        {
            var offset = position;
            ushort value = ReadByte();
            if (value == 0xfe) value = (ushort)(0xfe00 | ReadByte());
            if (!Codes.TryGetValue(value, out var code) || code.Name == null)
                throw new InvalidDataException("Unsupported IL opcode: " + value.ToString("x4"));
            object? operand;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: operand = null; break;
                case OperandType.ShortInlineI: operand = ReadByte(); break;
                case OperandType.InlineI: operand = Int32(); break;
                case OperandType.InlineI8: operand = BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8)); break;
                case OperandType.ShortInlineR: operand = Convert.ToHexString(ReadBytes(4)); break;
                case OperandType.InlineR: operand = Convert.ToHexString(ReadBytes(8)); break;
                case OperandType.ShortInlineVar: operand = ReadByte(); break;
                case OperandType.InlineVar: operand = BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2)); break;
                case OperandType.ShortInlineBrTarget:
                {
                    var delta = unchecked((sbyte)ReadByte());
                    operand = new Branch(checked(position + delta));
                    break;
                }
                case OperandType.InlineBrTarget:
                {
                    var delta = Int32();
                    operand = new Branch(checked(position + delta));
                    break;
                }
                case OperandType.InlineSwitch:
                {
                    var count = Int32();
                    if (count < 0 || count > (bytes.Length - position) / 4) throw new InvalidDataException("Invalid IL switch length.");
                    var deltas = Enumerable.Range(0, count).Select(_ => Int32()).ToArray();
                    var end = position;
                    operand = new Branches(deltas.Select(delta => checked(end + delta)).ToArray());
                    break;
                }
                case OperandType.InlineString:
                {
                    var token = Int32();
                    if ((token & unchecked((int)0xff000000)) != 0x70000000) throw new InvalidDataException("Invalid IL string token.");
                    operand = reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff));
                    break;
                }
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                {
                    var handle = MetadataTokens.EntityHandle(Int32());
                    ValidateEntityOperand(handle, code.OperandType, reader);
                    operand = names.Entity(handle);
                    break;
                }
                case OperandType.InlineSig:
                {
                    var token = Int32();
                    var handle = MetadataTokens.EntityHandle(token);
                    if (handle.Kind != HandleKind.StandaloneSignature) throw new InvalidDataException("Invalid IL call-site signature token.");
                    operand = TypeNames.Signature(reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(names, null));
                    break;
                }
                default: throw new InvalidDataException("Cannot safely compare IL operand: " + code.OperandType);
            }
            result.Add(new Instruction(offset, code.Name, operand));
        }
        return result;
    }

    private static void ValidateEntityOperand(EntityHandle handle, OperandType operand, MetadataReader reader)
    {
        if (handle.IsNil) throw new InvalidDataException("An IL metadata operand cannot be nil.");
        var type = handle.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification;
        var method = handle.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification
            || handle.Kind == HandleKind.MemberReference && reader.GetMemberReference((MemberReferenceHandle)handle).GetKind() == MemberReferenceKind.Method;
        var field = handle.Kind == HandleKind.FieldDefinition
            || handle.Kind == HandleKind.MemberReference && reader.GetMemberReference((MemberReferenceHandle)handle).GetKind() == MemberReferenceKind.Field;
        var valid = operand switch
        {
            OperandType.InlineType => type,
            OperandType.InlineMethod => method,
            OperandType.InlineField => field,
            OperandType.InlineTok => type || method || field,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid metadata token kind for IL operand: " + operand);
    }
}
