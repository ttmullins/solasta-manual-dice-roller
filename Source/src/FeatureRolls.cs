using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Raw dice of class and spell features: the Deflect Missiles reduction die,
    // the d20 a caster rolls when a feature lets casting fail, and the d20 a
    // feature rolls to preserve a spent spell slot. Each replaces exactly one
    // RuleDefinitions.RollDie call in its pinned original method, and only
    // while that method runs inside its own prepared native step.
    internal static class FeatureRolls
    {
        internal const string DeflectSite = "CharacterActionDeflectMissile/<ExecuteImpl>d__1:RollDie:018f";
        internal const string SlotSite = "CharacterActionMagicEffect/<ExecuteImpl>d__45:SpendMagicEffectUses:06ca";
        internal const string FailureSite = "CharacterActionCastSpell.CheckExecutionFailure:RollDie";
        internal static readonly string[] OuterIds = { SlotSite };
        const string FailureIteratorHash = "71b26574a81a448ed4584ae5996a3efda6830620a250ca0645f067cd7cf09a2d";
        const string FailureMethodHash = "59218a051988ac68ad3df15f6feea68f814f800c00bce6d462e6001a4ee9367a";
        const string SpendSlotHash = "84abe087edaf4c28cf520033da9bbf5baa8379ddf1b4ed71ad2015d801ddc6c8";
        const string CastSpellHash = "16f7cb786c3e50127b64c2462d02d0906f73f93b73ea2791a0e642cdd32bb8ba";
        const string SpendUsesHash = "ed70dea691c3193b6c6979d8ea5707c94e90098466ee4d15886c26bd1aa518ed";
        const string PowerUsesHash = "cf0d02c13898bc66760028a649c75099373060b864bb7c6c19ca4f444e73d984";
        const string DevicePowerHash = "74fdf43f0cf5ffe6a4613077307bf147aafd00231979f7425f1b831f676d60cf";
        const string DeviceSpellHash = "b1d0ed5dbcfbb7dbd7586897a4030fe9e3930ac0434db2aa36ed1c7a2c1cab15";
        const string DeviceFunctionHash = "3edf999d1a3558a101675ee8939557b103b8c3b3af355055d16219b1b93524ab";

        static MethodInfo rollDie, deflectMove, castFailure, failureMove, spendSlot, castSpell, spendUses, spendUsesBase, failureCheck, slotCheck,
            powerUses, devicePower, deviceSpell, deviceFunction, deviceCheck;
        static FieldInfo deflectOwner, activeSpell, activePower;
        [ThreadStatic] static int nativeFailure;

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                rollDie = AccessTools.Method(typeof(RuleDefinitions), "RollDie");
                var game = typeof(RulesetActor).Assembly;
                var deflectIterator = game.GetType("CharacterActionDeflectMissile+<ExecuteImpl>d__1", false);
                deflectMove = deflectIterator == null ? null : deflectIterator.GetMethod("MoveNext", any);
                deflectOwner = deflectIterator == null ? null : deflectIterator.GetField("<>4__this", any);
                castFailure = typeof(CharacterActionCastSpell).GetMethod("CheckExecutionFailure", any, null, Type.EmptyTypes, null);
                var failureIterator = game.GetType("CharacterActionCastSpell+<CheckExecutionFailure>d__19", false);
                failureMove = failureIterator == null ? null : failureIterator.GetMethod("MoveNext", any);
                spendSlot = typeof(RulesetCharacter).GetMethod("SpendSpellSlot", any, null, new[] { typeof(RulesetEffectSpell) }, null);
                castSpell = typeof(RulesetCharacter).GetMethod("CastSpell", any, null, new[] { typeof(RulesetEffectSpell), typeof(bool), typeof(bool) }, null);
                spendUses = typeof(CharacterActionCastSpell).GetMethod("SpendMagicEffectUses", any, null, Type.EmptyTypes, null);
                spendUsesBase = typeof(CharacterActionMagicEffect).GetMethod("SpendMagicEffectUses", any, null, Type.EmptyTypes, null);
                activeSpell = AccessTools.Field(typeof(CharacterActionCastSpell), "activeSpell");
                failureCheck = typeof(FeatureRolls).GetMethod(nameof(SpellFailureCheck), BindingFlags.Static | BindingFlags.NonPublic);
                slotCheck = typeof(FeatureRolls).GetMethod(nameof(SlotPreservationCheck), BindingFlags.Static | BindingFlags.NonPublic);
                deviceCheck = typeof(FeatureRolls).GetMethod(nameof(LastChargeCheck), BindingFlags.Static | BindingFlags.NonPublic);
                powerUses = typeof(CharacterActionUsePower).GetMethod("SpendMagicEffectUses", any, null, Type.EmptyTypes, null);
                devicePower = typeof(RulesetCharacter).GetMethod("UseDevicePower", any, null, new[] { typeof(RulesetItemDevice), typeof(FeatureDefinitionPower) }, null);
                deviceSpell = typeof(RulesetCharacter).GetMethod("UseDeviceSpell", any, null, new[] { typeof(RulesetItemDevice), typeof(SpellDefinition), typeof(int) }, null);
                deviceFunction = typeof(RulesetCharacterHero).GetMethod("UseDeviceFunction", any, null, new[] { typeof(RulesetItemDevice), typeof(RulesetDeviceFunction), typeof(int) }, null);
                activePower = AccessTools.Field(typeof(CharacterActionUsePower), "activePower");
                if (deviceCheck == null || powerUses == null || devicePower == null || deviceSpell == null || deviceFunction == null || activePower == null || activePower.FieldType != typeof(RulesetEffectPower))
                    throw new InvalidOperationException("The native device charge methods changed.");
                NativeMethodPin.Verify(powerUses, PowerUsesHash); NativeMethodPin.Verify(devicePower, DevicePowerHash);
                NativeMethodPin.Verify(deviceSpell, DeviceSpellHash); NativeMethodPin.Verify(deviceFunction, DeviceFunctionHash);
                if (rollDie == null || deflectMove == null || deflectOwner == null || deflectOwner.FieldType != typeof(CharacterActionDeflectMissile) || castFailure == null ||
                    castFailure.ReturnType != typeof(IEnumerator) || failureMove == null || spendSlot == null || castSpell == null || spendUses == null || spendUsesBase == null ||
                    activeSpell == null || activeSpell.FieldType != typeof(RulesetEffectSpell) || failureCheck == null || slotCheck == null)
                    throw new InvalidOperationException("The native feature dice methods changed.");
                NativeMethodPin.Verify(failureMove, FailureIteratorHash); NativeMethodPin.Verify(castFailure, FailureMethodHash); NativeMethodPin.Verify(spendSlot, SpendSlotHash);
                NativeMethodPin.Verify(castSpell, CastSpellHash); NativeMethodPin.Verify(spendUses, SpendUsesHash);
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        internal static void Register()
        {
            RuntimeRolls.RegisterOuterSite(new OuterSite(SlotSite, true, ResolveSlotPreservation) { Title = "Spell slot preservation", SkipUnresolved = true });
        }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (rollDie == null && !Preflight(out error)) throw new InvalidOperationException(error);
            harmony.Patch(deflectMove, transpiler: Own(nameof(DeflectTranspiler)));
            harmony.Patch(failureMove, transpiler: Own(nameof(FailureTranspiler)));
            harmony.Patch(spendSlot, transpiler: Own(nameof(SlotTranspiler)));
            harmony.Patch(castFailure, prefix: Own(nameof(FailurePrefix)));
            harmony.Patch(deviceFunction, transpiler: Own(nameof(DeviceTranspiler)));
        }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(FeatureRolls).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string face, string what)
        {
            var codes = instructions.ToList();
            int count = 0;
            foreach (var code in codes)
                if (code.opcode == OpCodes.Call && Equals(code.operand, rollDie)) { code.operand = typeof(FeatureRolls).GetMethod(face, BindingFlags.Static | BindingFlags.Public); count++; }
            if (count != 1) throw new InvalidOperationException("The native " + what + " roll changed.");
            return codes;
        }
        static IEnumerable<CodeInstruction> DeflectTranspiler(IEnumerable<CodeInstruction> instructions) { return Replace(instructions, nameof(DeflectFace), "Deflect Missiles"); }
        static IEnumerable<CodeInstruction> FailureTranspiler(IEnumerable<CodeInstruction> instructions) { return Replace(instructions, nameof(FailureFace), "spell failure"); }
        static IEnumerable<CodeInstruction> SlotTranspiler(IEnumerable<CodeInstruction> instructions) { return Replace(instructions, nameof(SlotFace), "slot preservation"); }
        static IEnumerable<CodeInstruction> DeviceTranspiler(IEnumerable<CodeInstruction> instructions) { return Replace(instructions, nameof(DeviceFace), "last charge"); }
        public static int DeviceFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(spendUsesBase, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }
        // Outside their own prepared step these are the game's RollDie.
        public static int DeflectFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(rollDie, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }
        public static int FailureFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(castFailure, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }
        public static int SlotFace(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0f)
        { return RuntimeRolls.InvocationStaticFace(spendUsesBase, diceType, advantageType, out firstRoll, out secondRoll, rollAlterationScore); }

        // ---- Deflect Missiles (registered await before the reduction die) ----
        internal static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            if (call == null) return false;
            if (call.Site == DeflectSite)
            {
                var action = deflectOwner.GetValue(call.Owner) as CharacterActionDeflectMissile;
                var actor = action == null || action.ActingCharacter == null ? null : action.ActingCharacter.RulesetCharacter;
                if (actor == null || !Plugin.Settings.Features) return true;
                var definition = action.ActionDefinition;
                int bonus = 0;
                try
                {
                    var attribute = actor.GetAttribute(definition.AbilityScore);
                    if (attribute != null) bonus = AttributeDefinitions.ComputeAbilityScoreModifier(attribute.CurrentValue);
                    if (definition.AddLevel) bonus += actor.TryGetAttributeValue("CharacterLevel");
                }
                catch (Exception) { }
                plan.RequireManualState(() => Plugin.Settings.Features);
                plan.AddStaticDice(actor, call.Arg<RuleDefinitions.DieType>("diceType"), 1, title: "Deflect Missiles", note: "Reduces the missile's damage",
                    hint: "The game adds the bonus and subtracts the total from the damage.", category: Plugin.FeatureDice);
                plan.Describe(0, true, bonus);
                return true;
            }
            if (Equals(call.Method, failureCheck))
            {
                var caster = call.Arg("caster") as RulesetCharacter;
                if (caster == null || !Plugin.Settings.Features) return true;
                plan.RequireManualState(() => Plugin.Settings.Features);
                plan.AddStaticDice(caster, RuleDefinitions.DieType.D20, 1, title: "Spell failure", note: GameNames.ActorName(caster) + " might fail to cast",
                    hint: "Roll at least the target number or the spell fails.", category: Plugin.FeatureDice);
                plan.Describe(call.Arg<int>("dc"), false, 0);
                return true;
            }
            if (Equals(call.Method, deviceCheck))
            {
                var hero = call.Arg("hero") as RulesetCharacterHero;
                if (hero == null || !Plugin.Settings.Features) return true;
                plan.RequireManualState(() => Plugin.Settings.Features);
                plan.AddStaticDice(hero, RuleDefinitions.DieType.D20, 1, title: "Last charge", note: (call.Arg<string>("item") ?? "The item") + " is destroyed on a 1",
                    hint: "The last charge is spent. Roll a d20: on a 1 the item crumbles.", category: Plugin.FeatureDice);
                plan.Describe(2, false, 0);
                return true;
            }
            if (Equals(call.Method, slotCheck))
            {
                var caster = call.Arg("caster") as RulesetCharacter;
                if (caster == null || !Plugin.Settings.Features) return true;
                plan.RequireManualState(() => Plugin.Settings.Features);
                plan.AddStaticDice(caster, RuleDefinitions.DieType.D20, 1, title: "Spell slot preservation", note: call.Arg<string>("feature") ?? "",
                    hint: "Roll at least the target number to keep the spell slot.", category: Plugin.FeatureDice);
                plan.Describe(call.Arg<int>("threshold"), false, 0);
                return true;
            }
            return false;
        }
        // Planning identities; never called.
        static void SpellFailureCheck(RulesetCharacter caster, int dc) { }
        static void SlotPreservationCheck(RulesetCharacter caster, int threshold, string feature) { }
        static void LastChargeCheck(RulesetCharacterHero hero, string item) { }

        // ---- Spell failure ------------------------------------------------
        // CharacterActionCastSpell.CheckExecutionFailure rolls inside a feature
        // loop (a native finally region), so it cannot be suspended there. The
        // whole step waits before the original iterator starts; its first
        // MoveNext (the loop and the die) runs inside the prepared native step.
        static bool FailurePrefix(CharacterActionCastSpell __instance, ref IEnumerator __result)
        {
            if (nativeFailure > 0 || __instance == null || !Plugin.Enabled || Plugin.UnsupportedRulesMod || !Plugin.Settings.Features) return true;
            int dc;
            RulesetCharacter caster;
            try { if (!ResolveSpellFailure(__instance, out caster, out dc)) return true; }
            catch (Exception error) { Plugin.Log("Spell failure keeps the game's dice: " + error.Message); return true; }
            __result = SpellFailure(__instance, caster, dc);
            return false;
        }
        // The pure part of the original loop: the first casting affinity that
        // forbids or risks the cast decides whether a die is rolled.
        internal static bool ResolveSpellFailure(CharacterActionCastSpell action, out RulesetCharacter caster, out int dc)
        {
            dc = 0;
            caster = action.ActionParams == null || action.ActionParams.ActingCharacter == null ? null : action.ActionParams.ActingCharacter.RulesetCharacter;
            if (caster == null) return false;
            var features = new List<FeatureDefinition>();
            using (var scope = NativeScratchScope.ForActor(caster))
                caster.EnumerateFeaturesToBrowse<ISpellCastingAffinityProvider>(features);
            var spell = action.ActionParams.RulesetEffect as RulesetEffectSpell;
            foreach (ISpellCastingAffinityProvider item in features)
            {
                if (item.CastingAffinity == RuleDefinitions.CastingAffinity.CannotCast && spell != null && (spell.OriginItem == null || spell.SpellDefinition.RequiresConcentration)) return false;
                if (item.CastingAffinity == RuleDefinitions.CastingAffinity.CanFail) { dc = item.SpellcastingSuccessDC; return true; }
            }
            return false;
        }
        static IEnumerator SpellFailure(CharacterActionCastSpell action, RulesetCharacter caster, int dc)
        {
            var owner = new object();
            IEnumerator wait = null;
            string spell = "";
            try { var effect = action.ActionParams.RulesetEffect as RulesetEffectSpell; if (effect != null) spell = GameNames.SpellName(effect.SpellDefinition); } catch (Exception) { }
            try { wait = RuntimeRolls.PrepareDeferredRoll(owner, FailureSite, castFailure, new object[] { action }, failureCheck, new object[] { caster, dc }, "Spell failure", spell); }
            catch (Exception error) { Plugin.Log("Manual preparation skipped at " + FailureSite + ": " + error.Message); wait = null; }
            if (wait != null) yield return wait;
            IEnumerator inner;
            nativeFailure++;
            try { inner = (IEnumerator)castFailure.Invoke(action, null); }
            finally { nativeFailure--; }
            try
            {
                bool more;
                bool completed = false;
                RuntimeRolls.EnterNative(owner, FailureSite);
                try
                {
                    more = inner.MoveNext();
                    RuntimeRolls.ObserveNativeValues(owner, FailureSite, Failed(action), new object[0]);
                    completed = true;
                }
                finally { if (completed) RuntimeRolls.ExitNative(owner, FailureSite); else RuntimeRolls.AbortNative(owner); }
                while (more)
                {
                    yield return inner.Current;
                    more = inner.MoveNext();
                }
            }
            finally { var disposable = inner as IDisposable; if (disposable != null) disposable.Dispose(); }
        }
        static PropertyInfo executionFailed;
        static bool Failed(CharacterActionMagicEffect action)
        {
            if (executionFailed == null) executionFailed = AccessTools.Property(typeof(CharacterActionMagicEffect), "ExecutionFailed");
            return executionFailed != null && (bool)executionFailed.GetValue(action, null);
        }

        // ---- A device's last charge ------------------------------------
        // The same outer await: a spell or power cast from a charged item
        // (UseDeviceSpell/UseDevicePower -> RulesetCharacterHero.
        // UseDeviceFunction) rolls a d20 when it spends the last charge of an
        // item that crumbles on a 1. Item casts spend no spell slot.
        static NativeInvocation ResolveLastCharge(NativeInvocation outer)
        {
            var cast = outer.Receiver as CharacterActionCastSpell;
            var use = outer.Receiver as CharacterActionUsePower;
            if (cast == null && use == null || cast != null && cast.GetType() != typeof(CharacterActionCastSpell) || use != null && use.GetType() != typeof(CharacterActionUsePower)) return null;
            var action = (CharacterActionMagicEffect)outer.Receiver;
            var hero = action.ActingCharacter == null ? null : action.ActingCharacter.RulesetCharacter as RulesetCharacterHero;
            if (hero == null) return null;
            RulesetItemDevice device; Func<RulesetDeviceFunction, bool> matches; int additional;
            if (cast != null)
            {
                var spell = (RulesetEffectSpell)activeSpell.GetValue(cast);
                if (spell == null || spell.OriginItem == null) return null;
                device = spell.OriginItem; additional = spell.SlotLevel - spell.SpellDefinition.SpellLevel;
                var definition = spell.SpellDefinition;
                matches = f => f.DeviceFunctionDescription.Type == DeviceFunctionDescription.FunctionType.Spell &&
                    (f.DeviceFunctionDescription.SpellDefinition == definition || definition.IsSubSpellOf(f.DeviceFunctionDescription.SpellDefinition));
            }
            else
            {
                var power = (RulesetEffectPower)activePower.GetValue(use);
                if (power == null || power.OriginItem == null || power.UsablePower == null) return null;
                device = power.OriginItem; additional = 0;
                var definition = power.UsablePower.PowerDefinition;
                matches = f => f.DeviceFunctionDescription.Type == DeviceFunctionDescription.FunctionType.Power && f.DeviceFunctionDescription.FeatureDefinitionPower == definition;
            }
            var description = device.UsableDeviceDescription;
            if (description == null || description.Usage != EquipmentDefinitions.ItemUsage.Charges || description.OutOfChargesConsequence != EquipmentDefinitions.ItemOutOfCharges.DestroyOnRoll1) return null;
            RulesetAttribute charges;
            if (!device.Attributes.TryGetValue("ItemCharges", out charges) || charges == null) return null;
            // Each matching function spends its cost; the die is rolled when
            // the charges reach exactly zero (later functions find the item
            // already spent or destroyed and roll nothing more).
            int remaining = charges.CurrentValue;
            foreach (var function in device.UsableFunctions)
            {
                if (function == null || !matches(function)) continue;
                remaining -= function.DeviceFunctionDescription.UseAmount + additional;
                if (remaining == 0)
                    return new NativeInvocation(outer.Owner, outer.Site, deviceCheck, new object[] { hero, GameNames.SpellName(device.ItemDefinition) }, outer.Generation);
                if (remaining < 0) return null;
            }
            return null;
        }

        // ---- Spell slot preservation --------------------------------------
        // Outer await before CharacterActionMagicEffect.SpendMagicEffectUses:
        // CharacterActionCastSpell -> RulesetCharacter.CastSpell ->
        // SpendSpellSlot rolls a d20 for the first casting affinity that can
        // preserve a slot of this level.
        static NativeInvocation ResolveSlotPreservation(NativeInvocation outer)
        {
            if (!Plugin.Settings.Features) return null;
            var device = ResolveLastCharge(outer);
            if (device != null) return device;
            var action = outer.Receiver as CharacterActionCastSpell;
            if (action == null || action.ActingCharacter == null) return null;
            var spell = (RulesetEffectSpell)activeSpell.GetValue(action);
            var caster = action.ActingCharacter.RulesetCharacter;
            if (spell == null || caster == null || spell.OriginItem != null) return null;
            // Legendary casting, invocations, rituals and mystic arcana spend no slot.
            if (spell.SlotLevel < 0 || spell.SpellRepertoire == null || spell.RulesetInvocation != null) return null;
            if (spell.SpellRepertoire.IsMysticArcanumSpell(spell.SpellDefinition)) return null;
            var features = new List<FeatureDefinition>();
            int level;
            // EffectLevel itself enumerates the caster's casting features.
            using (var scope = NativeScratchScope.ForActor(caster))
            {
                level = spell.EffectLevel;
                caster.EnumerateFeaturesToBrowse<ISpellCastingAffinityProvider>(features);
            }
            if (level <= 0) return null;
            foreach (ISpellCastingAffinityProvider item in features)
                if (item.PreserveSlotRoll && item.PreserveSlotLevelCap >= level)
                {
                    var feature = item as FeatureDefinition;
                    return new NativeInvocation(outer.Owner, outer.Site, slotCheck, new object[] { caster, item.PreserveSlotThreshold, feature == null ? "" : GameNames.Title(feature) ?? "" }, outer.Generation);
                }
            return null;
        }
    }
}
