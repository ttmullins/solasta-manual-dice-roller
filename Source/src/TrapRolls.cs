using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TA;
using UnityEngine;

namespace ManualDiceRoller
{
    // Traps and hazards triggered by a gadget (FunctorEnvironmentEffect): each
    // affected character's saving throw and the trap's damage dice.
    //
    // The functor calls GameLocationEnvironmentManager.TriggerEnvironmentEffect,
    // which creates the effect, finds the affected characters and then, in one
    // synchronous private call, rolls every save and damage die. The functor's
    // coroutine is replaced by an equivalent one: each public trigger runs as
    // in the game, but its final private call is captured instead of run. The
    // coroutine then waits for the dice of that call (one composite plan: per
    // target its save, then the damage) and runs the captured private call
    // inside its own prepared native step. Every target's dice are bound to
    // that target, so a die can never move to another character.
    internal static class TrapRolls
    {
        internal const string TrapSite = "Environment.Trap";
        const string FunctorHash = "9a498414ca680f92f1231c75a10aac54201315d2c964f538961f2ae8d15a74ea";
        const string PositionTriggerHash = "fbbb842a3134b18fd29a09c305c79c0e2180f8a2f70ee62f3dc54c68ef1cc729";
        const string BoxTriggerHash = "26d8bd5f56677c4e749dfd97b71914fcec886e13ec533abf3eb58701f3404a20";
        const string TriggerHash = "6fea4802eaa9359d1e56999cf0bfb5c73ada41629768b8f4e57a65f2129a656b";

        static MethodInfo functorExecute, functorMove, positionTrigger, boxTrigger, trigger, effectSave, applyForms, trySave;
        static FieldInfo affected;
        sealed class Capture { internal RulesetEffectEnvironment Effect; internal List<GameLocationCharacter> Targets; }
        [ThreadStatic] static Capture capture;
        [ThreadStatic] static int replaying;

        internal static bool Preflight(out string error)
        {
            error = null;
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                functorExecute = typeof(FunctorEnvironmentEffect).GetMethod("Execute", any, null, new[] { typeof(FunctorParametersDescription), typeof(Functor.FunctorExecutionContext) }, null);
                var iterator = typeof(FunctorEnvironmentEffect).GetNestedType("<Execute>d__0", BindingFlags.NonPublic);
                functorMove = iterator == null ? null : iterator.GetMethod("MoveNext", any);
                var manager = typeof(GameLocationEnvironmentManager);
                positionTrigger = manager.GetMethod("TriggerEnvironmentEffect", any, null, new[] { typeof(GameLocationCharacter), typeof(EnvironmentEffectDefinition), typeof(int3), typeof(int), typeof(int), typeof(GameGadget), typeof(bool) }, null);
                boxTrigger = manager.GetMethod("TriggerEnvironmentEffect", any, null, new[] { typeof(GameLocationCharacter), typeof(EnvironmentEffectDefinition), typeof(BoxInt), typeof(int), typeof(int), typeof(GameGadget), typeof(bool) }, null);
                trigger = manager.GetMethod("TriggerEnvironmentEffect", any, null, new[] { typeof(RulesetEffectEnvironment) }, null);
                affected = AccessTools.Field(manager, "affectedCharacters");
                effectSave = typeof(RulesetEffect).GetMethod("TryRollSavingThrow", BindingFlags.Instance | BindingFlags.Public);
                applyForms = typeof(RulesetImplementationManager).GetMethod("ApplyEffectForms", BindingFlags.Instance | BindingFlags.Public);
                trySave = typeof(RulesetImplementationManager).GetMethod("TryRollSavingThrow", BindingFlags.Instance | BindingFlags.Public);
                if (functorExecute == null || functorMove == null || positionTrigger == null || boxTrigger == null || trigger == null || affected == null ||
                    affected.FieldType != typeof(List<GameLocationCharacter>) || effectSave == null || effectSave.GetParameters().Length != 8 ||
                    applyForms == null || applyForms.GetParameters().Length != 10 || trySave == null)
                    throw new InvalidOperationException("The native trap methods changed.");
                NativeMethodPin.Verify(functorMove, FunctorHash); NativeMethodPin.Verify(positionTrigger, PositionTriggerHash);
                NativeMethodPin.Verify(boxTrigger, BoxTriggerHash); NativeMethodPin.Verify(trigger, TriggerHash);
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        internal static void Install(Harmony harmony)
        {
            string error;
            if (trigger == null && !Preflight(out error)) throw new InvalidOperationException(error);
            harmony.Patch(functorExecute, prefix: Own(nameof(ExecutePrefix)));
            harmony.Patch(trigger, prefix: Own(nameof(TriggerPrefix)));
            harmony.Patch(applyForms, prefix: Own(nameof(FormsAnchor)), finalizer: Own(nameof(AnchorFinalizer)));
            harmony.Patch(trySave, prefix: Own(nameof(SaveAnchor)), finalizer: Own(nameof(AnchorFinalizer)));
        }
        static HarmonyMethod Own(string name) { return new HarmonyMethod(typeof(TrapRolls).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }

        // ---- the functor ---------------------------------------------------
        static bool ExecutePrefix(FunctorEnvironmentEffect __instance, FunctorParametersDescription functorParameters, Functor.FunctorExecutionContext context, ref IEnumerator __result)
        {
            if (__instance == null || functorParameters == null || !Plugin.Enabled || Plugin.UnsupportedRulesMod || !Plugin.Settings.Traps) return true;
            if (Plugin.Networked() && !RuntimeRolls.ExtendedOnlineActive) return true;
            __result = Execute(functorParameters);
            return false;
        }
        // FunctorEnvironmentEffect.Execute, statement for statement; only the
        // last private step of each trigger waits for the dice.
        static IEnumerator Execute(FunctorParametersDescription functorParameters)
        {
            GameLocationCharacter actingCharacter = null;
            if (functorParameters.ActingCharacters.Count > 0) actingCharacter = functorParameters.ActingCharacters[0];
            IGameLocationEnvironmentService environmentService = ServiceRepository.GetService<IGameLocationEnvironmentService>();
            if (environmentService == null)
            {
                Trace.LogError("Missing environmentService in FunctorEnvironmentEffect");
                yield break;
            }
            IRulesetImplementationService rulesetImplementationService = ServiceRepository.GetService<IRulesetImplementationService>();
            while (rulesetImplementationService.IsApplyingEffects()) yield return null;
            if (functorParameters.IsGlobalEnvironmentEffect)
            {
                var captured = Trigger(() => environmentService.TriggerEnvironmentEffect(actingCharacter, functorParameters.EnvironmentEffectDefinition, int3.zero, functorParameters.SavingThrowOverride, functorParameters.AddDice, functorParameters.SourceGadget.GameGadget, functorParameters.IsGlobalEnvironmentEffect));
                if (captured != null) { var step = Resolve(environmentService, captured); if (step != null) yield return step; }
            }
            if (functorParameters.BoxColliders.Count > 0)
            {
                for (int i = 0; i < functorParameters.BoxColliders.Count; i++)
                {
                    if (functorParameters.BoxColliders[i] == null) continue;
                    BoxCollider boxCollider = functorParameters.BoxColliders[i];
                    Vector3 min = boxCollider.bounds.min;
                    Vector3 max = boxCollider.bounds.max;
                    int3 min2 = new int3(Mathf.RoundToInt(min.x), Mathf.RoundToInt(min.y), Mathf.RoundToInt(min.z));
                    int3 max2 = new int3(Mathf.RoundToInt(max.x) - 1, Mathf.RoundToInt(max.y) - 1, Mathf.RoundToInt(max.z) - 1);
                    var box = new BoxInt(min2, max2);
                    var captured = Trigger(() => environmentService.TriggerEnvironmentEffect(actingCharacter, functorParameters.EnvironmentEffectDefinition, box, functorParameters.SavingThrowOverride, functorParameters.AddDice, functorParameters.SourceGadget.GameGadget, functorParameters.IsGlobalEnvironmentEffect));
                    if (captured != null) { var step = Resolve(environmentService, captured); if (step != null) yield return step; }
                }
            }
            if (functorParameters.Nodes.Count > 0)
            {
                for (int j = 0; j < functorParameters.Nodes.Count; j++)
                {
                    if (functorParameters.Nodes[j] == null) continue;
                    int3 position = ServiceRepository.GetService<IGameLocationPositioningService>().GetGridPositionFromWorldPosition(functorParameters.Nodes[j].transform);
                    var captured = Trigger(() => environmentService.TriggerEnvironmentEffect(actingCharacter, functorParameters.EnvironmentEffectDefinition, position, functorParameters.SavingThrowOverride, functorParameters.AddDice, functorParameters.SourceGadget.GameGadget, functorParameters.IsGlobalEnvironmentEffect));
                    if (captured != null) { var step = Resolve(environmentService, captured); if (step != null) yield return step; }
                }
            }
            if (functorParameters.Nodes.Empty() && functorParameters.BoxColliders.Empty())
            {
                int3 position = ServiceRepository.GetService<IGameLocationPositioningService>().GetGridPositionFromWorldPosition(functorParameters.SourceGadget.transform);
                var captured = Trigger(() => environmentService.TriggerEnvironmentEffect(actingCharacter, functorParameters.EnvironmentEffectDefinition, position, functorParameters.SavingThrowOverride, functorParameters.AddDice, functorParameters.SourceGadget.GameGadget, functorParameters.IsGlobalEnvironmentEffect));
                if (captured != null) { var step = Resolve(environmentService, captured); if (step != null) yield return step; }
            }
        }
        // Runs one public trigger with its final private call captured.
        static Capture Trigger(Action invoke)
        {
            var previous = capture;
            var current = new Capture();
            capture = current;
            try { invoke(); }
            finally { capture = previous; }
            return current.Effect == null ? null : current;
        }
        static bool TriggerPrefix(GameLocationEnvironmentManager __instance, RulesetEffectEnvironment activeEnvironmentEffect)
        {
            var current = capture;
            if (current == null || replaying > 0 || __instance == null || activeEnvironmentEffect == null) return true;
            capture = null;
            current.Effect = activeEnvironmentEffect;
            current.Targets = new List<GameLocationCharacter>((List<GameLocationCharacter>)affected.GetValue(__instance));
            return false;
        }
        // Prepares the captured trigger's dice. Without a manual die the
        // trigger runs at once, exactly where the game ran it; otherwise the
        // returned routine waits for the dice and then runs it.
        static IEnumerator Resolve(IGameLocationEnvironmentService service, Capture captured)
        {
            var manager = service as GameLocationEnvironmentManager;
            if (ReferenceEquals(manager, null)) return null;
            var owner = new object();
            IEnumerator wait = null;
            try
            {
                var parts = Parts(captured.Effect, captured.Targets);
                if (AnyManual(parts))
                {
                    string name = captured.Effect.SourceDefinition == null ? "" : GameNames.SpellName(captured.Effect.SourceDefinition);
                    wait = RuntimeRolls.PrepareDeferredRoll(owner, TrapSite, trigger, new object[] { manager, captured.Effect }, DeferredRolls.CompositeMethod, new object[] { parts.ToArray() }, "Trap", name);
                }
            }
            catch (Exception error) { Plugin.Log("Manual preparation skipped at " + TrapSite + ": " + error.Message); wait = null; }
            if (wait == null) { RunTrigger(owner, manager, captured); return null; }
            return WaitThenRun(wait, owner, manager, captured);
        }
        static IEnumerator WaitThenRun(IEnumerator wait, object owner, GameLocationEnvironmentManager manager, Capture captured)
        {
            yield return wait;
            RunTrigger(owner, manager, captured);
        }
        static void RunTrigger(object owner, GameLocationEnvironmentManager manager, Capture captured)
        {
            RuntimeRolls.EnterNative(owner, TrapSite);
            bool completed = false;
            replaying++;
            try
            {
                var list = (List<GameLocationCharacter>)affected.GetValue(manager);
                list.Clear(); list.AddRange(captured.Targets);
                try { trigger.Invoke(manager, new object[] { captured.Effect }); }
                catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
                RuntimeRolls.ObserveNativeValues(owner, TrapSite, null, new object[0]);
                completed = true;
            }
            finally
            {
                replaying--;
                if (completed) RuntimeRolls.ExitNative(owner, TrapSite); else RuntimeRolls.AbortNative(owner);
            }
        }

        // ---- the private trigger's dice, per target ------------------------
        // GameLocationEnvironmentManager.TriggerEnvironmentEffect(effect): for
        // every affected character activated by the effect, the effect's save
        // (no caster, neutral side), then its forms with no source character.
        internal static List<DeferredRolls.RollPart> Parts(RulesetEffectEnvironment effect, List<GameLocationCharacter> targets)
        {
            var parts = new List<DeferredRolls.RollPart>();
            if (effect == null || targets == null || effect.EffectDescription == null || !Plugin.Settings.Traps) return parts;
            var description = effect.EffectDescription;
            var recurrent = description.RecurrentEffect;
            if (recurrent != RuleDefinitions.RecurrentEffect.No && (recurrent & RuleDefinitions.RecurrentEffect.OnActivation) == 0) return parts;
            var service = ServiceRepository.GetService<IRulesetImplementationService>() as RulesetImplementationManager;
            if (service == null) return parts;
            string name = effect.SourceDefinition == null ? "" : GameNames.SpellName(effect.SourceDefinition);
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i] == null ? null : targets[i].RulesetActor;
                if (target == null) continue;
                if (description.HasSavingThrow)
                    parts.Add(new DeferredRolls.RollPart(effectSave, new object[] { effect, null, RuleDefinitions.Side.Neutral, target, new ActionModifier(), description.EffectForms,
                        true, RuleDefinitions.RollOutcome.Neutral, 0 }) { Anchor = target, Title = "Saving throw", Detail = name });
                var formsParams = default(RulesetImplementationDefinitions.ApplyFormsParams);
                using (var scratch = new NativeScratchScope())
                {
                    scratch.AddActor(target);
                    formsParams.FillSourceAndTarget(null, target);
                    formsParams.FillFromActiveEffect(effect);
                }
                // Planned as if the save failed; dice of damage a successful
                // save negates are simply not used.
                formsParams.FillSpecialParameters(description.HasSavingThrow, effect.AddDice, 0, 0, 0, new ActionModifier(), description.HasSavingThrow ? RuleDefinitions.RollOutcome.Failure : RuleDefinitions.RollOutcome.Neutral,
                    0, false, i, targets.Count, null);
                formsParams.effectSourceType = RuleDefinitions.EffectSourceType.Environment;
                parts.Add(new DeferredRolls.RollPart(applyForms, new object[] { service, description.EffectForms, formsParams, null, false, false, false, false, false,
                    description.EffectApplication, description.EffectFormFilters }) { Anchor = target, Title = "Trap or hazard damage", Detail = name });
            }
            return parts;
        }
        // Is any die here one a player rolls?
        internal static bool AnyManual(List<DeferredRolls.RollPart> parts)
        {
            foreach (var part in parts)
            {
                if (part.Method == effectSave)
                { if (RuntimeRolls.CanManual(part.Args[3] as RulesetActor, RuleDefinitions.RollContext.SavingThrow)) return true; }
                else if (part.Method == applyForms)
                {
                    var p = (RulesetImplementationDefinitions.ApplyFormsParams)part.Args[2];
                    var forms = part.Args[1] as List<EffectForm>;
                    if (forms != null && forms.Any(form => form != null && form.FormType == EffectForm.EffectFormType.Damage) && RuntimeRolls.CanManual(p.targetCharacter, Plugin.TrapDice)) return true;
                }
            }
            return false;
        }

        // While the captured trigger runs, each target's save and forms are
        // bound to that target.
        sealed class AnchorState { internal object Previous; internal bool Entered; }
        static void FormsAnchor(RulesetImplementationDefinitions.ApplyFormsParams formsParams, out AnchorState __state)
        {
            __state = new AnchorState();
            if (replaying > 0 && formsParams.sourceCharacter == null && formsParams.targetCharacter != null)
            { __state.Previous = RuntimeRolls.EnterAnchor(formsParams.targetCharacter); __state.Entered = true; }
        }
        static void SaveAnchor(RulesetCharacter caster, RulesetActor target, out AnchorState __state)
        {
            __state = new AnchorState();
            if (replaying > 0 && caster == null && target != null)
            { __state.Previous = RuntimeRolls.EnterAnchor(target); __state.Entered = true; }
        }
        static Exception AnchorFinalizer(Exception __exception, AnchorState __state)
        {
            if (__state != null && __state.Entered) RuntimeRolls.ExitAnchor(__state.Previous);
            return __exception;
        }
    }
}
