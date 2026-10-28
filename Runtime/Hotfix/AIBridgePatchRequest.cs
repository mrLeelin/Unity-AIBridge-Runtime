using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIBridge.Runtime
{
    internal sealed class AIBridgePatchRequest
    {
        internal const int MaxAssemblyBytes = 8 * 1024 * 1024;
        internal string AssemblyName;
        internal string PatchId;
        internal string Sha256;
        internal Guid BaselineMvid;
        internal byte[] AssemblyBytes;
        internal readonly List<AIBridgePatchType> Types = new List<AIBridgePatchType>();

        internal int MethodCount => Types.Sum(type => type.Signatures.Count);

        internal static AIBridgePatchRequest Parse(AIBridgeRuntimeCommand command)
        {
            var parameters = command.Params;
            if (parameters == null || !ReadBool(parameters, "riskAccepted"))
                throw new InvalidOperationException("runtime.patch.apply requires riskAccepted=true.");
            if (!ReadBool(parameters, "structureVerified"))
                throw new InvalidOperationException("The trusted client must compare baseline and patch metadata, then set structureVerified=true.");

            var request = new AIBridgePatchRequest
            {
                AssemblyName = ReadString(parameters, "assemblyName", 256),
                Sha256 = ReadString(parameters, "sha256", 64).ToLowerInvariant(),
                PatchId = ReadOptionalString(parameters, "patchId", 128)
            };
            if (request.Sha256.Length != 64 || request.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidOperationException("sha256 must contain exactly 64 hexadecimal characters.");
            if (!Guid.TryParse(ReadString(parameters, "baselineMvid", 64), out request.BaselineMvid))
                throw new InvalidOperationException("baselineMvid must be the loaded target assembly ModuleVersionId GUID.");

            var encoded = ReadString(parameters, "assemblyBase64", ((MaxAssemblyBytes + 2) / 3) * 4);
            request.AssemblyBytes = Convert.FromBase64String(encoded);
            if (request.AssemblyBytes.Length == 0 || request.AssemblyBytes.Length > MaxAssemblyBytes)
                throw new InvalidOperationException("Patch DLL size must be between 1 byte and 8 MiB.");
            if (!string.Equals(ComputeSha256(request.AssemblyBytes), request.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Patch assembly sha256 does not match the transmitted bytes.");

            ParseTypes(parameters, request);
            return request;
        }

        internal string GetDeduplicationKey()
        {
            var text = new StringBuilder(AssemblyName).Append('\n').Append(BaselineMvid.ToString("N"))
                .Append('\n').Append(Sha256);
            foreach (var type in Types.OrderBy(type => type.Name, StringComparer.Ordinal))
                foreach (var signature in type.Signatures.OrderBy(signature => signature, StringComparer.Ordinal))
                    text.Append('\n').Append(type.Name).Append('\n').Append(signature);
            return ComputeSha256(Encoding.UTF8.GetBytes(text.ToString()));
        }

        internal static string ComputeSha256(byte[] bytes)
        {
            using (var hasher = SHA256.Create())
                return BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void ParseTypes(IDictionary<string, object> parameters, AIBridgePatchRequest request)
        {
            object value;
            if (!parameters.TryGetValue("types", out value) || !(value is IList types) || types.Count == 0 || types.Count > 128)
                throw new InvalidOperationException("types must contain between 1 and 128 target type manifests.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in types)
            {
                var data = item as IDictionary<string, object>;
                if (data == null)
                    throw new InvalidOperationException("Each types entry must be an object.");
                var type = new AIBridgePatchType { Name = ReadString(data, "name", 1024) };
                if (!names.Add(type.Name))
                    throw new InvalidOperationException("Duplicate target type: " + type.Name);
                ParseMethods(data, type);
                request.Types.Add(type);
                if (request.MethodCount > 512)
                    throw new InvalidOperationException("A patch may select at most 512 target methods.");
            }
        }

        private static void ParseMethods(IDictionary<string, object> data, AIBridgePatchType type)
        {
            object value;
            if (!data.TryGetValue("methods", out value) || !(value is IList methods) || methods.Count == 0 || methods.Count > 512)
                throw new InvalidOperationException("Each target type must select between 1 and 512 exact method signatures.");

            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in methods)
            {
                var method = item as IDictionary<string, object>;
                if (method == null)
                    throw new InvalidOperationException("Each methods entry must be an object with a signature.");
                if (method.ContainsKey("name"))
                    throw new InvalidOperationException("Select methods by signature only; name-based overload selection is not supported.");
                var signature = ReadString(method, "signature", 2048);
                if (!signatures.Add(signature))
                    throw new InvalidOperationException("Duplicate target method signature: " + signature);
                type.Signatures.Add(signature);
            }
        }

        private static bool ReadBool(IDictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static string ReadString(IDictionary<string, object> data, string key, int maxLength)
        {
            var value = ReadOptionalString(data, key, maxLength);
            if (value == null)
                throw new InvalidOperationException("Missing required patch parameter: " + key);
            return value;
        }

        private static string ReadOptionalString(IDictionary<string, object> data, string key, int maxLength)
        {
            object raw;
            if (!data.TryGetValue(key, out raw) || raw == null)
                return null;
            var value = raw as string;
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
                throw new InvalidOperationException("Invalid string parameter: " + key);
            return value;
        }
    }

    internal sealed class AIBridgePatchType
    {
        internal string Name;
        internal readonly List<string> Signatures = new List<string>();
    }
}
