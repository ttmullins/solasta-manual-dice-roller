using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public interface IGameSettingsService { string RandomnessMode { get; } bool ForceCritEveryFewRolls { get; } int SavingThrowAllyModifier {get;} int SavingThrowEnemyModifier {get;} }
public class TestSettings : IGameSettingsService { public int SavingThrowAllyModifier {get;set;} public int SavingThrowEnemyModifier {get;set;} public string RandomnessMode { get; set; } = ""; public bool ForceCritEveryFewRolls { get; set; } }
public partial interface IRulesetImplementationService { float DiceLuckScore { get; set; } int LastCriticalCounter { get; set; } }
public partial class TestRules : IRulesetImplementationService { public float DiceLuckScore { get; set; } public int LastCriticalCounter { get; set; } }
public static class ServiceRepository
{
    public static TestSettings Settings = new TestSettings(); public static TestRules Rules = new TestRules(); public static RulesetImplementationManager Manager = new RulesetImplementationManager();
    public static T GetService<T>() { return typeof(T) == typeof(IGameSettingsService) ? (T)(object)Settings : typeof(T)==typeof(IRulesetImplementationService) ? (T)(object)Manager : typeof(T)==typeof(IGameLoreService) ? (T)(object)Lore : (T)(object)Rules; }
    public static IGameLoreService Lore = new TestLore();
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
        public static int CeilToInt(float value) { return (int)Math.Ceiling(value); }
        public static int Clamp(int value, int min, int max) { return value < min ? min : value > max ? max : value; }
        public static float Clamp(float value, float min, float max) { return value < min ? min : value > max ? max : value; }
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
    public enum DieType { D1, D2, D3, D4, D6, D8, D10, D12, D20 }
    public enum AdvantageType { None, Advantage, Disadvantage }
    public enum RollForcedDamage { None, Half, Full }
    [Flags] public enum RollContext : short { None = 0, SavingThrow = 2, AbilityCheck = 4, AttackRoll = 8, ConcentrationCheck = 16, InitiativeRoll = 32, DeathSavingThrow = 64, HealValueRoll = 1024, MagicDamageValueRoll = 2048, TemporaryHPValueRoll = 4096, AttackDamageValueRoll = 8192 }
    public struct FeatureOrigin {public FeatureSourceType sourceType;public string sourceName,tags;public object source;}
    public struct TrendInfo { public int value;public FeatureSourceType sourceType;public string sourceName;public object source;public TrendInfoDieFlag dieFlag;public DieType dieType;public TrendInfo(int amount){value=amount;sourceType=0;sourceName="";source=null;dieFlag=0;dieType=0;}public TrendInfo(int value,FeatureSourceType type,string name,object source):this(value,type,name,source,""){}public TrendInfo(int value,FeatureSourceType type,string name,object source,string details){this.value=value;sourceType=type;sourceName=name;this.source=source;dieFlag=0;dieType=0;} }
    public static int[] DiceMaxValue = { 1, 2, 3, 4, 6, 8, 10, 12, 20 };
    public static int[] DiceMinValue = { 1, 1, 1, 1, 1, 1, 1, 1, 1 };
    public static AdvantageType ComputeAdvantage(List<TrendInfo> trends) { bool positive=false,negative=false;if(trends!=null)foreach(var t in trends){positive|=t.value>0;negative|=t.value<0;}return positive==negative?AdvantageType.None:positive?AdvantageType.Advantage:AdvantageType.Disadvantage;}
    [MethodImpl(MethodImplOptions.NoInlining)] public static int RollDie(DieType diceType, AdvantageType advantageType, out int firstRoll, out int secondRoll, float rollAlterationScore = 0)
    { throw new Exception("Pristine native raw RollDie IL not extracted."); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int RollStaticDiceAndSum(int diceNumber, DieType dieType, List<int> rolledValues = null)
    { throw new Exception("Pristine native static sum IL not extracted."); }
    public static int RollKarmicDie(DieType diceType, float score) { throw new NotSupportedException("Karmic random draws are outside this fixture."); }
}
public class FeatureDefinition : BaseDefinition { }
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
public partial class RulesetActor : RulesetEntity
{
    List<FeatureDefinition> featuresToBrowse = new List<FeatureDefinition>();
    public List<FeatureDefinition> FeaturesToBrowse { get { return featuresToBrowse; } }
    public List<FeatureDefinition> Features = new List<FeatureDefinition>();
    public RuleDefinitions.Side Side { get; set; }
    public delegate void DieRerolledHandler(RulesetActor actor, RuleDefinitions.DieType dieType, int previousValue, int newValue, string localizationKey);
    public DieRerolledHandler DieRerolled { get; set; }
    public virtual void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> features, Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> origins = null)
    { features.Clear(); if(origins!=null)origins.Clear(); GetAllConditions(AllConditionsForEnumeration); foreach (var f in Features) if (f is T) { features.Add(f); if (origins != null) origins[f] = new RuleDefinitions.FeatureOrigin(); } }
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
public partial class RulesetCharacter : RulesetActor
{
    public List<string> Proficiencies = new List<string>(); public bool IsProficient(string skill) { return Proficiencies.Contains(skill); }
    public int RollAbilityCheck(string abilityScoreName, string proficiencyName, List<RuleDefinitions.TrendInfo> advantageTrends, bool passive)
    { int first, second; return RollDie(RuleDefinitions.DieType.D20, RuleDefinitions.RollContext.AbilityCheck, IsProficient(proficiencyName), RuleDefinitions.ComputeAdvantage(advantageTrends), out first, out second, true, true, proficiencyName); }
}
public class RulesetCharacterHero : RulesetCharacter { }
