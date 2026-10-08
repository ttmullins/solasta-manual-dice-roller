using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public interface IGameSettingsService { string RandomnessMode { get; } bool ForceCritEveryFewRolls { get; } bool GroupedMonsterInitiatives { get; } }
public class TestSettings : IGameSettingsService { public string RandomnessMode { get; set; } = ""; public bool ForceCritEveryFewRolls { get; set; } public bool GroupedMonsterInitiatives { get; set; } }
public interface IRulesetImplementationService { float DiceLuckScore { get; set; } int LastCriticalCounter { get; set; } }
public class TestRules : IRulesetImplementationService { public float DiceLuckScore { get; set; } public int LastCriticalCounter { get; set; } }
public static class ServiceRepository
{
    public static TestSettings Settings = new TestSettings(); public static TestRules Rules = new TestRules();
    public static T GetService<T>() { return typeof(T) == typeof(IGameSettingsService) ? (T)(object)Settings : (T)(object)Rules; }
}
namespace TA
{
    public static class DeterministicRandom
    {
        public static Queue<int> Values = new Queue<int>(); public static int Calls;
        public static int Range(int minimum, int maximum) { Calls++; return Values.Count == 0 ? minimum : Values.Dequeue(); }
    }
}
namespace UnityEngine
{
    public static class Mathf
    {
        public static int Clamp(int value, int min, int max) { return value < min ? min : value > max ? max : value; }
        public static float Clamp(float value, float min, float max) { return value < min ? min : value > max ? max : value; }
        public static int Abs(int value) { return Math.Abs(value); }
        public static int Max(int first, int second) { return Math.Max(first, second); }
        public static int Min(int first, int second) { return Math.Min(first, second); }
    }
}
public static class FloatExtensions
{ public static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax) { return toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin); } }
public class DifficultyConfiguration { public float MaxLuckModificationPerRoll { get; set; } public float NegativePlayerAttackRollMultiplier { get; set; } }
public static class GameConfiguration { public static DifficultyConfiguration Difficulty { get; set; } = new DifficultyConfiguration(); }
public static partial class RuleDefinitions
{
    public enum Side { Ally, Enemy, Neutral }
    public enum ConditionInterruption { BardicInspirationUsed }
    public enum DieType { D1, D2, D3, D4, D6, D8, D10, D12, D20 }
    public enum AdvantageType { None, Advantage, Disadvantage }
    public enum RollForcedDamage { None, Half, Full }
    public enum TargetFilteringMethod { None, CharacterIncreasingHitPointsFromPool }
    public enum AbilityCheckContext { None }
    public enum FeatureSourceType { None, MonsterFeature }
    public enum CharacterAbilityCheckAffinity { None, Advantage, Disadvantage, AutomaticFail, AutomaticSuccess }
    [Flags] public enum RollContext : short { None = 0, SavingThrow = 2, AbilityCheck = 4, AttackRoll = 8, ConcentrationCheck = 16, InitiativeRoll = 32, DeathSavingThrow = 64, HealValueRoll = 1024, MagicDamageValueRoll = 2048, TemporaryHPValueRoll = 4096, AttackDamageValueRoll = 8192 }
    public struct FeatureOrigin { }
    public struct TrendInfo { public int value; public TrendInfo(int amount) { value = amount; } }
    public static int[] DiceMaxValue = { 1, 2, 3, 4, 6, 8, 10, 12, 20 };
    public static int[] DiceMinValue = { 1, 1, 1, 1, 1, 1, 1, 1, 1 };
    public static AdvantageType ComputeAdvantage(List<TrendInfo> trends) { int sum = 0; if (trends != null) foreach (var t in trends) sum += t.value; return sum > 0 ? AdvantageType.Advantage : sum < 0 ? AdvantageType.Disadvantage : AdvantageType.None; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int RollDie(DieType diceType, AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0)
    { throw new Exception("Pristine native raw RollDie IL not extracted."); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int RollStaticDiceAndSum(int diceNumber, DieType diceType, List<int> rolledValues = null)
    { throw new Exception("Pristine native static sum IL not extracted."); }
    public static int RollKarmicDie(DieType diceType, float score) { throw new NotSupportedException("Karmic random draws are outside this fixture."); }
}
public class BaseDefinition { public string Name { get; set; } = ""; }
public class FeatureDefinition { public string Name { get; set; } = ""; }
public class FeatureDefinitionAbilityCheckAffinity : FeatureDefinition
{
    public struct AbilityCheckAffinityGroup
    {
        public string abilityScoreName, proficiencyName;
        public RuleDefinitions.AbilityCheckContext abilityCheckContext;
        public RuleDefinitions.CharacterAbilityCheckAffinity affinity;
        public RuleDefinitions.DieType abilityCheckModifierDieType;
        public int abilityCheckModifierDiceNumber;
    }
    public List<AbilityCheckAffinityGroup> AffinityGroups = new List<AbilityCheckAffinityGroup>();
}
public interface IDieRollModificationProvider { }
public class FeatureDefinitionDieRollModifier : FeatureDefinition, IDieRollModificationProvider
{
    public RuleDefinitions.RollContext ValidityContext { get; set; }
    public List<string> ValidSkills { get; set; } = new List<string>(); public bool RequireProficiency { get; set; }
    public int MinRollValue { get; set; } = 1; public int MaxRollValue { get; set; }
    public int MinRerollValue { get; set; } public int RerollCount { get; set; }
    public string RerollLocalizationKey { get; set; } public RuleDefinitions.RollForcedDamage ForcedDamageOnDice { get; set; }
}
public interface IInitiativeModifier { void RefreshInitiativeBonus(ref int trend); }
public interface IDeathSavingThrowAffinityProvider { void ComputeSavingThrowTrend(List<RuleDefinitions.TrendInfo> trends, RuleDefinitions.FeatureOrigin origin); }
public interface ISenseModesProvider { }
public interface ISpellCastingAffinityProvider { }
public interface IMoveThroughEnemyModifierProvider { }
public interface IMoveModesProvider { }
public class RulesetCondition { public RuleDefinitions.DieType BardicInspirationDie { get; set; } = RuleDefinitions.DieType.D6; public ConditionDefinition ConditionDefinition { get; set; } = new ConditionDefinition(); }
public class ConditionDefinition
{ public void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> features, Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> origins, object actor) { } }
public class FeatureDefinitionFeatureSet : FeatureDefinition
{
    public static void EnumerateFeaturesToBrowseHierarchicaly<T>(List<FeatureDefinition> source, List<FeatureDefinition> features, RuleDefinitions.FeatureSourceType sourceType, Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> origins, object actor, string name = "")
    { foreach (var feature in source) if (feature is T) { features.Add(feature); if (origins != null && !origins.ContainsKey(feature)) origins.Add(feature, new RuleDefinitions.FeatureOrigin()); } }
}
namespace System.Collections.Generic
{
    public static class DictionaryExtensions
    { public static bool TryAdd<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TKey key, TValue value) { if (dictionary.ContainsKey(key)) return false; dictionary.Add(key, value); return true; } }
}
public class RulesetActor
{
    public Dictionary<string,List<RulesetCondition>> ConditionsByCategory { get; } = new Dictionary<string,List<RulesetCondition>>();
    public int Interruptions, Removals;
    public void ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption interruption,int amount=0) { Interruptions++; }
    public void RemoveCondition(RulesetCondition condition,bool refresh=true,bool showGraphics=true) { Removals++; foreach(var list in ConditionsByCategory.Values) list.Remove(condition); }
    List<FeatureDefinition> featuresToBrowse = new List<FeatureDefinition>();
    Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> featuresOrigin = new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>();
    List<RulesetCondition> allConditionsForEnumeration = new List<RulesetCondition>();
    public List<FeatureDefinition> FeaturesToBrowse { get { return featuresToBrowse; } }
    public Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> FeaturesOrigin { get { return featuresOrigin; } }
    public List<RulesetCondition> AllConditionsForEnumeration { get { return allConditionsForEnumeration; } }
    public void GetAllConditions(List<RulesetCondition> conditions, bool discardHidden = false) { conditions.Clear(); }
    public List<FeatureDefinition> Features = new List<FeatureDefinition>();
    public string Name { get; set; } = "Ellie"; public RuleDefinitions.Side Side { get; set; }
    public delegate void DieRerolledHandler(RulesetActor actor, RuleDefinitions.DieType dieType, int previousValue, int newValue, string localizationKey);
    public DieRerolledHandler DieRerolled { get; set; }
    public virtual void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> features, Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> origins = null)
    { features.Clear(); foreach (var f in Features) if (f is T) { features.Add(f); if (origins != null) origins[f] = new RuleDefinitions.FeatureOrigin(); } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int RollDie(RuleDefinitions.DieType dieType, RuleDefinitions.RollContext rollContext, bool isProficient, RuleDefinitions.AdvantageType advantageType, out int firstRoll, out int secondRoll, bool enumerateFeatures = true, bool canRerollDice = true, string skill = "")
    { throw new Exception("Pristine native actor RollDie IL not extracted."); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    int RerollDieAsNeeded(FeatureDefinitionDieRollModifier dieRollModifier, RuleDefinitions.DieType dieType, int rollScore)
    { throw new Exception("Pristine native reroll IL not extracted."); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int RollDiceAndSum(RuleDefinitions.DieType diceType, RuleDefinitions.RollContext context, int diceNumber, List<int> rolledValues = null, bool canRerollDice = true, bool maximumDamage = false, string skill = "")
    { throw new Exception("Pristine native actor sum IL not extracted."); }
}
public class RulesetCharacter : RulesetActor
{
    public delegate void BardicInspirationDieUsedHandler(RulesetCharacter character, RuleDefinitions.DieType die,int first,int second,bool success,bool advantage);
    public BardicInspirationDieUsedHandler BardicInspirationDieUsed { get; set; }
    [MethodImpl(MethodImplOptions.NoInlining)] public int RollBardicInspirationDie(RulesetCondition sourceCondition,int successDelta,bool forceMaxValue=false,bool advantage=false) { throw new Exception("Extract actual Bardic IL"); }
    public virtual bool IsSubstitute { get { return false; } }
    public virtual RulesetCharacter OriginalFormCharacter { get { return null; } }
    public delegate void MagicalHitPointsPoolRolledHandler(RulesetCharacter character, BaseDefinition source, int rolled);
    public MagicalHitPointsPoolRolledHandler MagicalHitPointsPoolRolled { get; set; }
    [MethodImpl(MethodImplOptions.NoInlining)] public int RollMagicalHitPointsPool(BaseDefinition source, int diceNumber, RuleDefinitions.DieType dieType) { throw new Exception("Extract native magical hit point pool"); }
    public List<string> Proficiencies = new List<string>(); public bool IsProficient(string skill) { return Proficiencies.Contains(skill); }
    public int RollAbilityCheck(string abilityScoreName, string proficiencyName, List<RuleDefinitions.TrendInfo> advantageTrends, bool passive)
    { int first, second; return RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, IsProficient(proficiencyName), RuleDefinitions.ComputeAdvantage(advantageTrends), out first, out second, true, true, proficiencyName); }
}
public class RulesetCharacterHero : RulesetCharacter { }
public class RulesetCharacterMonster : RulesetCharacter
{
    private RulesetCharacter originalFormCharacter;
    private List<FeatureDefinition> activeFeatures = new List<FeatureDefinition>();
    public RulesetCharacterMonster(RulesetCharacter original = null) { originalFormCharacter = original; }
    public override bool IsSubstitute { get { throw new Exception("Extract original native substitute getter"); } }
    public override RulesetCharacter OriginalFormCharacter { get { throw new Exception("Extract original native original-form getter"); } }
    public override void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> features, Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> origins = null)
    { throw new Exception("Extract original native monster feature enumeration"); }
}
public partial class GameLocationCharacter { public string Name; public RulesetCharacter RulesetCharacter; }
public class GameLocationBattle { public Dictionary<string, int> initiativeByName = new Dictionary<string, int>(); }
public class CharacterActionMagicEffect { public GameLocationCharacter ActingCharacter; public int AddDice { get; set; } }
public class EffectDescription { public RuleDefinitions.TargetFilteringMethod TargetFilteringMethod; public RuleDefinitions.DieType PoolFilterDieType; public int PoolFilterDiceNumber; }
