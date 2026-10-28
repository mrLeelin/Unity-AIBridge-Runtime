using System;
using System.Collections.Generic;

namespace HybridCLR
{
    public enum RuntimeOptionId { MaxMethodInlineDepth = 5 }
    public sealed class HotfixMethod { public string name; public string signature; }
    public sealed class HotfixType { public string name; public List<HotfixMethod> methods; }
    public static class RuntimeApi
    {
        public static int Calls;
        public static int InlineDepth = -1;
        public static bool ThrowOnApply;
        public static List<HotfixType> LastTypes;
        public static void SetRuntimeOption(RuntimeOptionId option, int value) { InlineDepth = value; }
        public static void HotfixAssembly(string name, byte[] bytes, List<HotfixType> types)
        {
            Calls++;
            LastTypes = types;
            if (ThrowOnApply) throw new InvalidOperationException("Simulated native failure");
        }
    }
}
