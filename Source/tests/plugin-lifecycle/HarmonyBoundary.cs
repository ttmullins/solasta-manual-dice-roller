// Test-only owner bookkeeping and injectable rollback boundary; no real detours.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
[assembly: AssemblyVersion("2.3.6.0")]
namespace HarmonyLib
{
    public sealed class HarmonyMethod { public HarmonyMethod(MethodInfo method) { } }
    public sealed class Patches
    {
        public readonly HashSet<string> Owners = new HashSet<string>();
    }
    public sealed class Harmony
    {
        static readonly Dictionary<MethodBase, Patches> patches = new Dictionary<MethodBase, Patches>();
        public static bool FailUnpatch;
        public static int UnpatchAttempts;
        readonly string owner;
        public Harmony(string id) { owner = id; }
        public void Patch(MethodBase method, HarmonyMethod prefix = null)
        {
            Patches info; if (!patches.TryGetValue(method, out info)) patches.Add(method, info = new Patches());
            info.Owners.Add(owner);
        }
        public void UnpatchAll(string id)
        {
            UnpatchAttempts++;
            if (FailUnpatch) throw new InvalidOperationException("injected unpatch");
            foreach (var info in patches.Values) info.Owners.Remove(id);
        }
        public static Patches GetPatchInfo(MethodBase method) { Patches info; return patches.TryGetValue(method, out info) ? info : null; }
        public static IEnumerable<MethodBase> GetAllPatchedMethods() { return patches.Where(p => p.Value.Owners.Count > 0).Select(p => p.Key).ToArray(); }
    }
}
