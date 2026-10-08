using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public class RulesetAttribute { public int CurrentValue { get; set; } }
public class RulesetEntity
{
    public RulesetAttribute GetAttribute(string name, bool required = true) { return ((RulesetActor)this).Attributes[name]; }
    public int TryGetAttributeValue(string name) { RulesetAttribute value; return ((RulesetActor)this).Attributes.TryGetValue(name, out value) ? value.CurrentValue : 0; }
}
public static class AttributeDefinitions { public static int ComputeAbilityScoreModifier(int score) { return (int)Math.Floor((score - 10) / 2.0); } }
public class ItemDefinition : BaseDefinition { }
public interface IAbilityCheckPerformanceProvider { bool UseControllerAbilityChecks { get; } }
public class ToolTypeDefinition : BaseDefinition { }
public class ArmorTypeDefinition : BaseDefinition { }
public class ArmorCategoryDefinition : BaseDefinition { }
public static class DatabaseRepository { public static Database<T> GetDatabase<T>() { return new Database<T>(); } }
public class Database<T> { public T GetElement(string name, bool required = true) { return Activator.CreateInstance<T>(); } }
public class RulesetInventory { public int CountToolsOfType(ToolTypeDefinition definition) { return 1; } }
public static class LocationDefinitions { public enum LightingState { Unlit, Dim, Bright } }
public static partial class RuleDefinitions
{
    public enum RollOutcome { Neutral, Success, Failure, CriticalSuccess, CriticalFailure }
    public enum LightingContext { Irrelevant, Unlit, DimLight, BrightLight }
    public enum AbilityCheckGroupOperation { AddDie, SubstractDie }
    public enum ConditionType { Beneficial, Detrimental }
    public enum ConditionInterruption { AbilityCheck }
    public enum TrendInfoDieFlag { None, AddDie, SubstractDie }
}
public class ActionModifier
{
    public int AbilityCheckModifier { get; set; }
    public List<RuleDefinitions.TrendInfo> AbilityCheckModifierTrends { get; set; } = new List<RuleDefinitions.TrendInfo>();
    public List<RuleDefinitions.TrendInfo> AbilityCheckAdvantageTrends { get; set; } = new List<RuleDefinitions.TrendInfo>();
    public ActionModifier Clone() { return new ActionModifier { AbilityCheckModifier = AbilityCheckModifier, AbilityCheckModifierTrends = new List<RuleDefinitions.TrendInfo>(AbilityCheckModifierTrends), AbilityCheckAdvantageTrends = new List<RuleDefinitions.TrendInfo>(AbilityCheckAdvantageTrends) }; }
}
public interface IAbilityCheckAffinityProvider
{
    bool SubstractBardicDieRoll { get; }
    void ComputeAbilityCheckModifier(string ability, string skill, ActionModifier modifier, RuleDefinitions.FeatureOrigin origin, int context, LocationDefinitions.LightingState lighting, int bardic, bool ignoreDieRolls = false);
}
public class FeatureDefinitionAbilityCheckAffinity : FeatureDefinition, IAbilityCheckAffinityProvider, IAbilityCheckPerformanceProvider
{
    public class AbilityCheckAffinityGroup
    {
        public string abilityScoreName, proficiencyName;
        public RuleDefinitions.CharacterAbilityCheckAffinity affinity;
        public RuleDefinitions.AbilityCheckGroupOperation abilityCheckGroupOperation;
        public int abilityCheckModifierDiceNumber;
        public RuleDefinitions.DieType abilityCheckModifierDieType;
        public RuleDefinitions.AbilityCheckContext abilityCheckContext;
        public RuleDefinitions.LightingContext lightingContext;
    }
    private List<AbilityCheckAffinityGroup> affinityGroups = new List<AbilityCheckAffinityGroup>();
    private bool substractBardicDieRoll;
    public bool UseControllerAbilityChecks { get; set; }
    public List<AbilityCheckAffinityGroup> AffinityGroups { get { return affinityGroups; } }
    public bool SubstractBardicDieRoll { get { return substractBardicDieRoll; } set { substractBardicDieRoll = value; } }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ComputeAbilityCheckModifier(string abilityScoreName, string proficiencyName, ActionModifier checkModifier, RuleDefinitions.FeatureOrigin featureOrigin, int contextField, LocationDefinitions.LightingState lightingState, int bardicDieRoll, bool ignoreDieRolls = false) { throw new Exception("Extract native ability provider"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ComputeAbilityCheckAdvantageTrendAndModifier(string abilityScoreName, string proficiencyName, List<RuleDefinitions.TrendInfo> advantageTrends, List<RuleDefinitions.TrendInfo> modifierTrends, out int checkModifier, RuleDefinitions.FeatureOrigin featureOrigin, int contextField, LocationDefinitions.LightingState lightingState, int bardicDieRoll, bool ignoreDieRolls = false) { throw new Exception("Extract native ability provider decisions"); }
}
public class RulesetCondition
{
    public ConditionDefinition ConditionDefinition { get; set; } = new ConditionDefinition();
    public RuleDefinitions.DieType BardicInspirationDie { get; set; }
    public int BardicInspirationRoll { get; set; }
}
public class ConditionDefinition
{
    public RuleDefinitions.ConditionType ConditionType { get; set; }
    public List<FeatureDefinition> Features { get; set; } = new List<FeatureDefinition>();
    public void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> destination, Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> origins, object actor) { foreach (var feature in Features) if (feature is T) destination.Add(feature); }
}
public partial class RulesetActor
{
    public readonly Dictionary<string, RulesetAttribute> Attributes = new Dictionary<string, RulesetAttribute>();
    public readonly List<RulesetCondition> Conditions = new List<RulesetCondition>();
    public int Interruptions;
    public void ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption interruption, int amount = 0) { Interruptions++; }
}
public partial class RulesetCharacter
{
    public bool HeavyArmor;
    public bool IsWearingHeavyArmor() { return HeavyArmor; }
    public int Notifications;
    public Action OnNotification;
    public void EvaluateAndNotifyBardicNegativeInspiration(RuleDefinitions.RollContext context) { Notifications++; if (OnNotification != null) OnNotification(); }
    [MethodImpl(MethodImplOptions.NoInlining)] public virtual int ComputeBaseAbilityCheckBonus(string abilityScoreName, List<RuleDefinitions.TrendInfo> modifierTrends, string proficiencyName = null, bool doubleProficiency = false, bool checkInventory = true, bool checkFeatures = true) { throw new Exception("Extract native base ability bonus"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int RollAbilityCheck(int baseBonus, string abilityScoreName, string proficiencyName, List<RuleDefinitions.TrendInfo> modifierTrends, List<RuleDefinitions.TrendInfo> advantageTrends, int rollModifier, int checkDC, bool passive, int minRoll, out int diceRoll, out int firstRoll, out int secondRoll, out RuleDefinitions.RollOutcome outcome, out int successDelta, bool rollDie, bool notify = true, bool displayDieOutcome = true, int minResult = 0) { throw new Exception("Extract native final ability roll"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RollBasicAbilityCheck(string abilityScoreName, string proficiencyName, int checkDC, out RuleDefinitions.RollOutcome outcome, out int successDelta, bool notify = true) { throw new Exception("Extract native basic ability wrapper"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RollBasicAbilityCheck(int baseBonus, List<RuleDefinitions.TrendInfo> modifierTrends, string abilityScoreName, string proficiencyName, int checkDC, out RuleDefinitions.RollOutcome outcome, out int successDelta, bool notify = true) { throw new Exception("Extract native basic ability helper"); }
    public int VerifyAbilityCheck(ref int diceRoll, ref int firstRoll, ref int secondRoll, int baseBonus, string abilityScoreName, string proficiencyName, List<RuleDefinitions.TrendInfo> modifierTrends, List<RuleDefinitions.TrendInfo> advantageTrends, int rollModifier, int checkDC, bool passive, int minRoll, out RuleDefinitions.RollOutcome outcome, out int successDelta, bool rollDie, bool notify = true, bool displayDieOutcome = true, int minResult = 0)
    { int total = Math.Max(minResult, Math.Max(diceRoll, minRoll) + baseBonus + rollModifier); outcome = total >= checkDC ? RuleDefinitions.RollOutcome.Success : RuleDefinitions.RollOutcome.Failure; successDelta = total - checkDC; return total; }
}
public partial class RulesetCharacterHero
{
    private List<string> skillProficiencies = new List<string>(), toolTypeProficiencies = new List<string>(), expertiseProficiencies = new List<string>();
    private RulesetInventory characterInventory = new RulesetInventory();
    public List<string> SkillProficiencies { get { return skillProficiencies; } }
    public List<string> ToolTypeProficiencies { get { return toolTypeProficiencies; } }
    public List<string> ExpertiseProficiencies { get { return expertiseProficiencies; } }
    [MethodImpl(MethodImplOptions.NoInlining)] public override int ComputeBaseAbilityCheckBonus(string abilityScoreName, List<RuleDefinitions.TrendInfo> modifierTrends, string proficiencyName = null, bool doubleProficiency = false, bool checkInventory = true, bool checkFeatures = true) { throw new Exception("Extract native hero ability bonus"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public override bool IsProficient(string proficiencyName) { throw new Exception("Extract native hero proficiency"); }
}
public partial class GameLocationCharacter
{
    private LocationDefinitions.LightingState lightingState;
    public RuleDefinitions.Side Side { get { return RulesetCharacter.Side; } }
    public LocationDefinitions.LightingState Lighting { get { return lightingState; } set { lightingState = value; } }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ComputeAbilityCheckActionModifier(string abilityScoreName, string proficiencyName, ActionModifier actionModifier, int contextField = 0) { throw new Exception("Extract native location ability modifier"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void PrepareActionModifier(string abilityScoreName, string proficiencyName, RuleDefinitions.AdvantageType baseAffinity, ActionModifier actionModifier, int contextField = 0) { throw new Exception("Extract native location modifier preparation"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int RollAbilityCheck(string abilityScoreName, string proficiencyName, int checkDC, RuleDefinitions.AdvantageType baseAffinity, ActionModifier checkModifier, bool passive, int minRoll, out RuleDefinitions.RollOutcome outcome, out int successDelta, bool rollDie, bool notify = true, bool displayDieOutcome = true) { throw new Exception("Extract native location ability wrapper"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int RollAbilityCheck(string abilityScoreName, string proficiencyName, int checkDC, RuleDefinitions.AdvantageType baseAffinity, ActionModifier checkModifier, bool passive, int minRoll, out int baseBonus, out int checkRoll, out int firstRoll, out int secondRoll, out RuleDefinitions.RollOutcome outcome, out int successDelta, bool rollDie, bool notify = true, bool displayDieOutcome = true) { throw new Exception("Extract native location expanded ability wrapper"); }
}
