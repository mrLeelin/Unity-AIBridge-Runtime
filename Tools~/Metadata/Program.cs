using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
    if (args.Length < 2 || args[0] is not ("inspect" or "compare"))
        throw new ArgumentException("Usage: inspect <dll> | compare <baseline.dll> <candidate.dll>");
    var baseline = Image.Read(args[1]);
    object output;
    if (args[0] == "inspect")
    {
        output = new { assemblyName = baseline.Name, identity = baseline.Identity, mvid = baseline.Mvid, sha256 = baseline.Sha256, structureSha256 = baseline.StructureHash, types = baseline.Types.Keys.ToArray() };
    }
    else
    {
        if (args.Length != 3) throw new ArgumentException("compare requires baseline and candidate DLLs.");
        var candidate = Image.Read(args[2]);
        var differences = new List<string>();
        if (baseline.Identity != candidate.Identity) differences.Add("Assembly identity changed.");
        foreach (var name in baseline.Types.Keys.Except(candidate.Types.Keys)) differences.Add("Removed type: " + name);
        foreach (var name in candidate.Types.Keys.Except(baseline.Types.Keys)) differences.Add("Added type: " + name);
        foreach (var name in baseline.Types.Keys.Intersect(candidate.Types.Keys))
            if (baseline.Types[name] != candidate.Types[name]) differences.Add("Changed type/member definition: " + name);
        output = new
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
    }
    Console.WriteLine(JsonSerializer.Serialize(output));
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = error.GetType().Name + ": " + error.Message }));
    return 1;
}

internal sealed record Image(string Name, string Identity, string Mvid, string Sha256, string StructureHash, SortedDictionary<string, string> Types)
{
    public static Image Read(string path)
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
                text.AppendLine($"field|{MetadataTokens.GetToken(fieldHandle)}|{reader.GetString(field.Name)}|{(int)field.Attributes}|{field.DecodeSignature(provider, null)}|{field.GetOffset()}|{Constant(reader, field.GetDefaultValue())}|{Attributes(reader, provider, field.GetCustomAttributes())}");
            }
            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
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
        var structure = identity + "\n" + string.Join("\n", types.Select(pair => pair.Value));
        return new Image(name, identity, reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D"), Hash(bytes), Hash(Encoding.UTF8.GetBytes(structure)), types);
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
            HandleKind.MemberReference => Member((MemberReferenceHandle)handle),
            _ => throw new InvalidDataException("Unsupported metadata handle: " + handle.Kind)
        };
    }

    public string Method(MethodDefinitionHandle handle)
    {
        if (handle.IsNil) return "";
        var method = reader.GetMethodDefinition(handle);
        return DefinitionName(method.GetDeclaringType()) + "::" + reader.GetString(method.Name) + "|" + Signature(method.DecodeSignature(this, null));
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
