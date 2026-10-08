using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

// Development-only check of the actual pinned Harmony runtime generator.
// This proves .NET8 fixture execution, not Unity/Mono compatibility.
class HarmonyRuntimeProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NativeSum(int first, int second) { return first + second; }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ret)
            {
                var extra = new CodeInstruction(OpCodes.Ldc_I4_7);
                extra.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                yield return extra;
                yield return new CodeInstruction(OpCodes.Add);
            }
            yield return instruction;
        }
    }

    static int Main()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        if (typeof(Harmony).Assembly.GetName().Version != new Version(2, 3, 6, 0))
            throw new InvalidOperationException("Different Harmony release loaded");
        var original = AccessTools.Method(typeof(HarmonyRuntimeProbe), "NativeSum");
        var harmony = new Harmony("ManualDiceRoller.GeneratorProbe");
        if (NativeSum(2, 3) != 5) throw new InvalidOperationException("Pristine fixture mismatch");
        harmony.Patch(original, transpiler: new HarmonyMethod(AccessTools.Method(typeof(HarmonyRuntimeProbe), "Transpiler")));
        if (NativeSum(2, 3) != 12) throw new InvalidOperationException("Generated patch did not execute");
        var patched = PatchProcessor.GetCurrentInstructions(original);
        if (!patched.Exists(i => i.opcode == OpCodes.Ldc_I4_7))
            throw new InvalidOperationException("Generated instruction dump lost the transpiler");
        harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id);
        if (NativeSum(2, 3) != 5) throw new InvalidOperationException("Unpatch did not restore fixture");
        Console.WriteLine("PASS genuine Harmony 2.3.6.0 generated IL executes, GetCurrentInstructions exposes the patch, and unpatch restores native fixture behavior.");
        return 0;
    }
}
