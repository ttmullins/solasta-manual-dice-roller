using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ManualDiceRoller
{
    // A probe runs an original method up to its first dice call and then
    // unwinds it with a private exception. It is only used for code whose
    // preparation is a pure calculation apart from the actor's scratch lists
    // (isolated by NativeScratchScope) and deterministic random draws (saved and
    // restored here). Bonus dice summed before the capture are recorded and
    // answered with their minimum face, without consuming any random value.
    internal static class NativeProbe
    {
        internal sealed class Group { internal RuleDefinitions.DieType Die; internal int Count; }
        internal sealed class DieCall
        {
            internal RulesetActor Actor; internal RuleDefinitions.DieType Die; internal RuleDefinitions.RollContext Context;
            internal bool Proficient, EnumerateFeatures, CanReroll; internal RuleDefinitions.AdvantageType Advantage; internal string Skill;
        }
        internal sealed class Result
        {
            internal DieCall Roll;
            internal object Receiver; internal object[] Args;
            internal readonly List<Group> Bonuses = new List<Group>();
            internal int BonusMinimum;
            internal bool HasSaveModifier; internal int SaveModifier;
        }
        sealed class State
        {
            internal MethodBase Target; internal Result Result = new Result(); internal int SumDepth; internal bool Captured;
            internal string SaveAbility;
        }
        sealed class Captured : Exception { internal Captured() : base("Manual Dice Roller probe captured its native call.") { } }
        // A probe never writes a journal entry: it stops before one (no capture).
        sealed class Stopped : Exception { internal Stopped() : base("Manual Dice Roller probe stopped before a lasting side effect.") { } }

        [ThreadStatic] static State state;
        internal static bool Active { get { return state != null; } }
        static FieldInfo gameRandomState;

        // Runs invoke() with the probe active. Returns null when the method
        // finished without reaching the requested call.
        internal static Result Run(Action invoke, MethodBase target, IEnumerable<RulesetActor> scratchActors, string saveAbility = null)
        {
            if (state != null) throw new InvalidOperationException("A native probe is already active.");
            var game = ServiceRepository.GetService<IGameService>();
            object savedGameRandom = null; UnityEngine.Random.State savedUnity = default(UnityEngine.Random.State);
            bool restoreRandom = false;
            var probe = new State { Target = target, SaveAbility = saveAbility };
            using (var scratch = new NativeScratchScope())
            {
                foreach (var actor in scratchActors ?? Enumerable.Empty<RulesetActor>())
                {
                    if (actor == null) continue;
                    scratch.AddActor(actor);
                    var character = actor as RulesetCharacter;
                    if (character != null && character.IsSubstitute) scratch.AddActor(character.OriginalFormCharacter);
                }
                try
                {
                    if (gameRandomState == null) gameRandomState = AccessTools.Field(typeof(Game), "randomState");
                    if (game != null && game.Game != null && gameRandomState != null) { savedGameRandom = gameRandomState.GetValue(game.Game); savedUnity = UnityEngine.Random.state; restoreRandom = true; }
                    state = probe;
                    invoke();
                }
                catch (Exception error)
                {
                    var root = error; while (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                    if (!(root is Captured) && !(root is Stopped)) throw new InvalidOperationException("The native roll could not be previewed: " + root.Message, root);
                }
                finally
                {
                    state = null;
                    if (restoreRandom) { try { gameRandomState.SetValue(game.Game, savedGameRandom); UnityEngine.Random.state = savedUnity; } catch (Exception) { } }
                }
            }
            return probe.Captured ? probe.Result : null;
        }

        internal static void Install(Harmony harmony)
        {
            var first = Priority.First + 1;
            harmony.Patch(AccessTools.Method(typeof(RulesetActor), "RollDie"), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(ActorDiePrefix)) { priority = first });
            harmony.Patch(AccessTools.Method(typeof(RulesetCharacter), "RollAbilityCheck"), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(MethodPrefix)) { priority = first });
            harmony.Patch(AccessTools.Method(typeof(GameTravelScheduler), "RollSurprise"), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(MethodPrefix)) { priority = first });
            harmony.Patch(AccessTools.Method(typeof(GameTravelJournal), "AddEntry"), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(StopPrefix)) { priority = first });
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions), "RollDie"), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(RawPrefix)) { priority = first });
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions), "RollStaticDiceAndSum"), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(SumPrefix)) { priority = first },
                finalizer: new HarmonyMethod(typeof(NativeProbe), nameof(SumFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(RulesetActor), "ComputeSavingThrowModifier"), postfix: new HarmonyMethod(typeof(NativeProbe), nameof(SaveModifierPostfix)));
        }
        static void ActorDiePrefix(RulesetActor __instance, RuleDefinitions.DieType dieType, RuleDefinitions.RollContext rollContext, bool isProficient,
            RuleDefinitions.AdvantageType advantageType, bool enumerateFeatures, bool canRerollDice, string skill)
        {
            var p = state; if (p == null || p.Target != null || p.Captured || RuntimeRolls.Max(dieType) <= 1) return;
            p.Result.Roll = new DieCall { Actor = __instance, Die = dieType, Context = rollContext, Proficient = isProficient, Advantage = advantageType,
                EnumerateFeatures = enumerateFeatures, CanReroll = canRerollDice, Skill = skill ?? "" };
            p.Captured = true;
            throw new Captured();
        }
        static void MethodPrefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            var p = state; if (p == null || p.Target == null || p.Captured || !Equals(p.Target, __originalMethod)) return;
            p.Result.Receiver = __instance; p.Result.Args = (object[])__args.Clone(); p.Captured = true;
            throw new Captured();
        }
        static void StopPrefix()
        {
            var p = state; if (p == null || p.Captured) return;
            throw new Stopped();
        }
        static bool RawPrefix(RuleDefinitions.DieType diceType, RuleDefinitions.AdvantageType advantageType, ref int firstRoll, ref int secondRoll, ref int __result)
        {
            if (state == null) return true;
            int minimum = RuleDefinitions.DiceMinValue[(int)diceType];
            firstRoll = minimum; secondRoll = advantageType == RuleDefinitions.AdvantageType.None ? -1 : minimum; __result = minimum;
            return false;
        }
        static void SumPrefix(int diceNumber, RuleDefinitions.DieType diceType)
        {
            var p = state; if (p == null) return;
            if (p.SumDepth++ == 0 && diceNumber > 0 && RuntimeRolls.Max(diceType) > 1 && !p.Captured)
            {
                if (diceNumber > 256 || p.Result.Bonuses.Sum(g => g.Count) + diceNumber > RollBroker.MaxFacesPerSession / 2)
                    throw new InvalidOperationException("The native bonus dice exceed the supported face budget.");
                p.Result.Bonuses.Add(new Group { Die = diceType, Count = diceNumber });
                p.Result.BonusMinimum += diceNumber * RuleDefinitions.DiceMinValue[(int)diceType];
            }
        }
        static Exception SumFinalizer(Exception __exception) { var p = state; if (p != null && p.SumDepth > 0) p.SumDepth--; return __exception; }
        static void SaveModifierPostfix(object[] __args)
        {
            var p = state; if (p == null || p.Captured || string.IsNullOrEmpty(p.SaveAbility)) return;
            foreach (var argument in __args)
            {
                var modifier = argument as ActionModifier;
                if (modifier != null) { p.Result.SaveModifier = modifier.GetSavingThrowModifier(p.SaveAbility); p.Result.HasSaveModifier = true; return; }
            }
        }
    }

    // Plans calls whose only dice are a single actor d20 roll found by probing
    // the original method (currently concentration checks).
    internal static class NativeProbePlanner
    {
        internal static bool Previewing { get { return NativeProbe.Active; } }

        internal static bool TryPrepare(NativeInvocation call, NativeRollPlan plan)
        {
            if (call.Method.DeclaringType != typeof(RulesetCharacter) || call.Method.Name != "RollConcentrationCheck") return false;
            var actor = call.Receiver as RulesetCharacter;
            if (actor == null || !RuntimeRolls.CanManual(actor, RuleDefinitions.RollContext.ConcentrationCheck)) return true;
            var result = Probe(call, actor);
            if (result == null || result.Roll == null || !ReferenceEquals(result.Roll.Actor, actor)) return true;
            var roll = result.Roll;
            string spell = "";
            try { if (actor.ConcentratedSpell != null) spell = GameNames.SpellName(actor.ConcentratedSpell.SpellDefinition); } catch (Exception) { }
            string note = RollText.Save("Constitution") + (spell.Length > 0 ? " to keep concentrating on " + spell : " to keep concentrating");
            string hint = "Taking damage tests concentration. On a failure " + (spell.Length > 0 ? spell : "the spell") + " ends; the damage is already taken either way.";
            for (int index = 0; index < result.Bonuses.Count; index++)
                plan.AddStaticDice(actor, result.Bonuses[index].Die, result.Bonuses[index].Count, title: "Concentration bonus", note: note,
                    role: "ConcentrationBonus/" + index, category: RuleDefinitions.RollContext.ConcentrationCheck, hint: "Enter the bonus die granted by a feature or spell.");
            plan.AddActorDice(actor, roll.Die, roll.Context, 1, roll.Advantage, roll.Proficient, roll.Skill, roll.CanReroll,
                title: "Concentration", note: note, enumerate: roll.EnumerateFeatures, hint: hint);
            int bonus = 0; bool known = false;
            try
            {
                using (var scope = NativeScratchScope.ForActor(actor))
                    bonus = actor.ComputeBaseSavingThrowBonus("Constitution", new List<RuleDefinitions.TrendInfo>());
                if (result.HasSaveModifier) { bonus += result.SaveModifier - result.BonusMinimum; known = true; }
            }
            catch (Exception) { known = false; }
            plan.Describe(call.Arg<int>("saveDC"), known, bonus);
            plan.RequireNativeState(delegate {
                var current = Probe(call, actor);
                return current != null && current.Roll != null && ReferenceEquals(current.Roll.Actor, actor) && current.Roll.Die == roll.Die &&
                    current.Roll.Advantage == roll.Advantage && current.Roll.Context == roll.Context && current.Bonuses.Count == result.Bonuses.Count &&
                    current.Bonuses.Zip(result.Bonuses, (a, b) => a.Die == b.Die && a.Count == b.Count).All(same => same);
            });
            return true;
        }
        static NativeProbe.Result Probe(NativeInvocation call, RulesetCharacter actor)
        {
            var args = call.Args.Skip(1).ToArray();
            return NativeProbe.Run(delegate { call.Method.Invoke(actor, args); }, null, new RulesetActor[] { actor }, "Constitution");
        }
    }
}
