using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace UnityEngine { public class Object { public static bool operator ==(Object a,Object b){return object.ReferenceEquals(a,b);}public static bool operator !=(Object a,Object b){return !object.ReferenceEquals(a,b);}public override bool Equals(object value){return object.ReferenceEquals(this,value);}public override int GetHashCode(){return base.GetHashCode();} } }
public class BaseDefinition : UnityEngine.Object { public string Name { get; set; } = "";public static bool operator ==(BaseDefinition a,BaseDefinition b){return object.ReferenceEquals(a,b);}public static bool operator !=(BaseDefinition a,BaseDefinition b){return !object.ReferenceEquals(a,b);}public override bool Equals(object value){return object.ReferenceEquals(this,value);}public override int GetHashCode(){return base.GetHashCode();} }
public class SpellDefinition : BaseDefinition { }
public class FeatureDefinitionPower : BaseDefinition { }
public class MetamagicOptionDefinition : BaseDefinition { public RuleDefinitions.MetamagicType Type { get; set; } }
public class SchoolOfMagicDefinition : BaseDefinition { }
public static class Gui { public static string Format(string text, params string[] args) { return string.Format(text, args); } }
public static class DatabaseRepository { public static Database<T> GetDatabase<T>() where T : BaseDefinition { return new Database<T>(); } }
public class Database<T> where T : BaseDefinition { public T GetElement(string name,bool required=true) { throw new Exception("Fixture database lookup is not configured: " + name); } }
public static class AttributeDefinitions { public static int ComputeAbilityScoreModifier(int score) { return (int)Math.Floor((score - 10) / 2f); } }
public class RulesetAttribute { public int CurrentValue { get; set; } }
public class RulesetEntity {public virtual string Name{get;set;}="Ellie";public string SystemName {get{return Name;}}public RulesetAttribute GetAttribute(string ability,bool required=true){return ((RulesetActor)this).Attributes[ability];}}
public partial class RuleDefinitions
{
    public enum MetamagicType { CarefulSpell = 0, HeightenedSpell = 4 }
    public enum FeatureSourceType { CharacterFeature = 0, Spell = 6, ExplicitFeature = 12, Difficulty = 19, Metamagic = 20 }
    public enum RollOutcome { Neutral, Success, Failure, CriticalSuccess, CriticalFailure }
    public enum CharacterSavingThrowAffinity { None, Advantage, Disadvantage, AutomaticFail, SpecialHalfDamage }
    public enum TrendInfoDieFlag { None, AddDie, SubstractDie }
    public enum SavingThrowContext { None }
    public enum ConditionInterruption { SavingThrow }
    public enum SpellParamsModifierType { None, HalfKnowledgeLevelOfTarget }
    public enum EffectSavingThrowType { None, Negates, HalfDamage }
}
public static class SpellAndPowersDefinitions { public enum RulesetEffectSaveDCBonusType { None, Spell, Power } }
public class SenseMode {public enum Type { None,Darkvision } }
public class SaveAffinityBySenseDescription { public RuleDefinitions.AdvantageType AdvantageType { get; set; } public SenseMode.Type SenseType { get; set; } }
public class SaveAffinityByFamilyDescription { public RuleDefinitions.AdvantageType AdvantageType { get; set; } public string Family { get; set; } }
public class MonsterDefinition { public string CharacterFamily { get; set; } }
public class RulesetCharacterMonster : RulesetCharacter
{
    public MonsterDefinition MonsterDefinition { get; set; }
    public override void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> features,Dictionary<FeatureDefinition,RuleDefinitions.FeatureOrigin> origins=null)
    {
        base.EnumerateFeaturesToBrowse<T>(features,origins);
        if(OriginalFormCharacter==null||typeof(T)==typeof(ISpellCastingAffinityProvider))return;
        OriginalFormCharacter.EnumerateFeaturesToBrowse<T>(OriginalFormCharacter.FeaturesToBrowse,origins==null?null:OriginalFormCharacter.FeaturesOrigin);
        features.AddRange(OriginalFormCharacter.FeaturesToBrowse);
        if(origins!=null)foreach(var pair in OriginalFormCharacter.FeaturesOrigin)if(!origins.ContainsKey(pair.Key))origins.Add(pair.Key,pair.Value);
    }
}
public class OverrideSavingThrowInfo
{
    public string SavingThrowAbility { get; set; }
    public int SavingThrowDC { get; set; }
    public string SourceDefinitionName { get; set; }
    public RuleDefinitions.FeatureSourceType FeatureSourceType { get; set; }
}
public class EffectForm
{
    public enum EffectFormType { Damage, Condition, Healing }
    public EffectFormType FormType { get; set; }
    public OverrideSavingThrowInfo OverrideSavingThrowInfo { get; set; }
    public DamageForm DamageForm { get; set; } = new DamageForm();
    public ConditionForm ConditionForm { get; set; } = new ConditionForm();
    public RuleDefinitions.EffectSavingThrowType SavingThrowAffinity { get; set; } = RuleDefinitions.EffectSavingThrowType.HalfDamage;
}
public class DamageForm { public string DamageType { get; set; } = "Fire"; }
public class ConditionForm { public ConditionDefinition ConditionDefinition { get; set; } }
public class ConditionDefinition : BaseDefinition
{
    List<FeatureDefinition> features=new List<FeatureDefinition>();ConditionDefinition parentCondition;
    public List<FeatureDefinition> Features{get{return features;}}
    [MethodImpl(MethodImplOptions.NoInlining)]public bool HasFeature(FeatureDefinition feature){throw new Exception("Native condition feature IL missing");}
}
public class RulesetCondition { public int SourceAbilityBonus { get; set; } public ConditionDefinition ConditionDefinition{get;set;} }
public interface ISavingThrowPerformanceProvider { bool UseControllerSavingThrows{get;} bool CanPerformSaveForAbilityScore(string abilityScoreName); }
public interface ISavingThrowAffinityProvider
{
    string PriorityAbilityScore { get; }
    void ComputeSavingThrowModifier(RulesetActor saver, string abilityType, EffectForm.EffectFormType formType, string sourceName, string schoolOfMagic, string damageType, string conditionType, string sourceFamily, int sourceAbilityBonus, ActionModifier actionModifier, RuleDefinitions.FeatureOrigin featureOrigin, int contextField);
}
public interface IEffectAffinityProvider
{
    SpellAndPowersDefinitions.RulesetEffectSaveDCBonusType AddBonusToEffectSaveDC { get; }
    FeatureDefinitionPower PowerWithModifiedSaveDC { get; }
    SpellDefinition SpellWithModifiedSaveDC { get; }
    int BonusToEffectSaveDC { get; }
}
public interface ISpellCastingAffinityProvider
{
    SpellDefinition ForcedSpellDefinition { get; }
    RuleDefinitions.AdvantageType ForcedSavingThrowAffinity { get; }
}
public class NativeSpellAffinity : FeatureDefinition,ISpellCastingAffinityProvider
{
    public bool Throw;
    public SpellDefinition ForcedSpellDefinition {get{if(Throw)throw new Exception("Provider preview failed");return null;}}
    public RuleDefinitions.AdvantageType ForcedSavingThrowAffinity {get;set;}
}
public class NativeEffectAffinity : FeatureDefinition,IEffectAffinityProvider
{
    public SpellAndPowersDefinitions.RulesetEffectSaveDCBonusType AddBonusToEffectSaveDC {get;set;}
    public FeatureDefinitionPower PowerWithModifiedSaveDC {get;set;}
    public SpellDefinition SpellWithModifiedSaveDC {get;set;}
    public int BonusToEffectSaveDC {get;set;}
}
public class FeatureDefinitionSavingThrowAffinity : FeatureDefinition, ISavingThrowAffinityProvider, ISavingThrowPerformanceProvider
{
    public bool UseControllerSavingThrows{get;set;}
    public enum ModifierType { AddDice, RemoveDice, SourceAbility }
    public class SavingThrowAffinityGroup
    {
        public string abilityScoreName;
        public RuleDefinitions.CharacterSavingThrowAffinity affinity;
        public ModifierType savingThrowModifierType;
        public int savingThrowModifierDiceNumber;
        public RuleDefinitions.DieType savingThrowModifierDieType;
        public List<EffectForm.EffectFormType> restrictedForms = new List<EffectForm.EffectFormType>();
        public List<string> restrictedSchools = new List<string>(), restrictedSpells = new List<string>(), restrictedPowers = new List<string>();
        public RuleDefinitions.SavingThrowContext savingThrowContext;
    }
    List<SavingThrowAffinityGroup> affinityGroups = new List<SavingThrowAffinityGroup>();
    public List<SavingThrowAffinityGroup> AffinityGroups { get { return affinityGroups; } }
    public string PriorityAbilityScore { get; set; } = "";
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ComputeSavingThrowModifier(RulesetActor saver, string abilityType, EffectForm.EffectFormType formType, string sourceName, string schoolOfMagic, string damageType, string conditionType, string sourceFamily, int sourceAbilityBonus, ActionModifier actionModifier, RuleDefinitions.FeatureOrigin featureOrigin, int contextField) { throw new Exception("Native affinity IL missing"); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool CanPerformSaveForAbilityScore(string abilityScoreName) { throw new Exception("Native auto-fail IL missing"); }
}
public class ActionModifier
{
    public int SavingThrowModifier { get; set; }
    public List<RuleDefinitions.TrendInfo> SavingThrowModifierTrends { get; set; } = new List<RuleDefinitions.TrendInfo>();
    public List<RuleDefinitions.TrendInfo> SavingThrowAdvantageTrends { get; set; } = new List<RuleDefinitions.TrendInfo>();
    public int GetRawSavingThrowModifier() { return SavingThrowModifier; }
    public int GetSavingThrowModifier(string ability, bool ignoreCover) { return SavingThrowModifier; }
    public ActionModifier Clone() { return new ActionModifier { SavingThrowModifier = SavingThrowModifier, SavingThrowModifierTrends = new List<RuleDefinitions.TrendInfo>(SavingThrowModifierTrends), SavingThrowAdvantageTrends = new List<RuleDefinitions.TrendInfo>(SavingThrowAdvantageTrends) }; }
}
public partial class RulesetActor
{
    Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> featuresOrigin = new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>();
    List<RulesetCondition> allConditionsForEnumeration = new List<RulesetCondition>();
    public Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> FeaturesOrigin {get { return featuresOrigin; }}
    public List<RulesetCondition> AllConditionsForEnumeration {get { return allConditionsForEnumeration; }}
    public List<RulesetCondition> Conditions = new List<RulesetCondition>();
    Dictionary<string,List<RulesetCondition>> conditionsByCategory=new Dictionary<string,List<RulesetCondition>>();
    public Dictionary<string,List<RulesetCondition>> ConditionsByCategory{get{return conditionsByCategory;}}
    public void GetAllConditions(List<RulesetCondition> destination, bool discardHidden = false) { destination.Clear(); destination.AddRange(Conditions); }
    public readonly Dictionary<string, RulesetAttribute> Attributes = new Dictionary<string, RulesetAttribute>();
    public delegate void ReplacedAbilityScoreForSaveHandler(RulesetActor actor, FeatureDefinition feature, string oldAbility, string newAbility);
    public ReplacedAbilityScoreForSaveHandler ReplacedAbilityScoreForSave { get; set; }
    public delegate void AdditionalSaveDieRolledHandler(RulesetActor actor, RuleDefinitions.TrendInfo trend);
    public AdditionalSaveDieRolledHandler AdditionalSaveDieRolled { get; set; }
    public delegate void SaveRolledHandler(RulesetActor actor, string ability, BaseDefinition source, RuleDefinitions.RollOutcome outcome, int dc, int total, int roll, int first, int second, int bonus, List<RuleDefinitions.TrendInfo> modifier, List<RuleDefinitions.TrendInfo> advantage, bool visual);
    public SaveRolledHandler SaveRolled { get; set; }
    public int Interruptions;
    [MethodImpl(MethodImplOptions.NoInlining)]public RulesetCondition FindFirstConditionHoldingFeature(FeatureDefinition feature) { throw new Exception("Native condition lookup IL missing"); }
    public int ComputeBaseSavingThrowBonus(string ability, List<RuleDefinitions.TrendInfo> trends) { return AttributeDefinitions.ComputeAbilityScoreModifier(GetAttribute(ability).CurrentValue); }
    public void ProcessConditionsMatchingInterruption(RuleDefinitions.ConditionInterruption type,int amount=0) { Interruptions++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public virtual bool IsAutomaticallyFailingSavingThrow(string abilityScore) { throw new Exception("Native auto-fail calculation missing"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ComputeSavingThrowModifier(string abilityType, EffectForm.EffectFormType formType, string sourceName, string schoolOfMagic, string damageType, string conditionType, string sourceFamily, ActionModifier effectModifier, List<ISavingThrowAffinityProvider> accountedProviders, int savingThrowContextField = 0) { throw new Exception("Native modifier IL missing"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RollSavingThrow(int saveBonus, string abilityScoreName, BaseDefinition sourceDefinition, List<RuleDefinitions.TrendInfo> modifierTrends, List<RuleDefinitions.TrendInfo> advantageTrends, int rollModifier, int saveDC, bool hasHitVisual, out RuleDefinitions.RollOutcome outcome, out int outcomeDelta) { throw new Exception("Native RollSavingThrow missing"); }
}
public partial class RulesetCharacter
{
    public bool IsSubstitute{get;set;} public RulesetCharacter OriginalFormCharacter{get;set;}
    public string CharacterFamily { get; set; } = "Humanoid";
    public bool HasSenseType(SenseMode.Type type) { return type == SenseMode.Type.Darkvision; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ComputeSavingThrowModifierImposedOnTarget(string effectName, ActionModifier spellModifier, MetamagicOptionDefinition metamagicOption) { throw new Exception("Native imposed modifier missing"); }
}
public class RulesetImplementationManager : IRulesetImplementationService
{
    public float DiceLuckScore { get; set; }
    public int LastCriticalCounter { get; set; }
    List<ISavingThrowAffinityProvider> accountedProviders = new List<ISavingThrowAffinityProvider>();
    public List<ISavingThrowAffinityProvider> Providers { get { return accountedProviders; } }
    public virtual int ComputeCharacterSavingThrowContext(RulesetActor entity) { return 0; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryRollSavingThrow(RulesetCharacter caster, RuleDefinitions.Side sourceSide, RulesetActor target, ActionModifier actionModifier, bool hasHitVisual, bool hasSavingThrow, string savingThrowAbility, int saveDC, bool disableSavingThrowOnAllies, bool advantageForEnemies, bool ignoreCover, RuleDefinitions.FeatureSourceType featureSourceType, List<EffectForm> effectForms, List<SaveAffinityBySenseDescription> savingThrowAffinitiesBySense, List<SaveAffinityByFamilyDescription> savingThrowAffinitiesByFamily, string sourceName, BaseDefinition sourceDefinition, string schoolOfMagic, MetamagicOptionDefinition metamagicOption, out RuleDefinitions.RollOutcome saveOutcome, out int saveOutcomeDelta) { throw new Exception("Native TryRollSavingThrow IL missing"); }
}
public partial interface IRulesetImplementationService
{
    bool TryRollSavingThrow(RulesetCharacter caster, RuleDefinitions.Side sourceSide, RulesetActor target, ActionModifier actionModifier, bool hasHitVisual, bool hasSavingThrow, string savingThrowAbility, int saveDC, bool disableSavingThrowOnAllies, bool advantageForEnemies, bool ignoreCover, RuleDefinitions.FeatureSourceType featureSourceType, List<EffectForm> effectForms, List<SaveAffinityBySenseDescription> savingThrowAffinitiesBySense, List<SaveAffinityByFamilyDescription> savingThrowAffinitiesByFamily, string sourceName, BaseDefinition sourceDefinition, string schoolOfMagic, MetamagicOptionDefinition metamagicOption, out RuleDefinitions.RollOutcome saveOutcome, out int saveOutcomeDelta);
}
public partial class TestRules
{
    public bool TryRollSavingThrow(RulesetCharacter caster, RuleDefinitions.Side sourceSide, RulesetActor target, ActionModifier actionModifier, bool hasHitVisual, bool hasSavingThrow, string savingThrowAbility, int saveDC, bool disableSavingThrowOnAllies, bool advantageForEnemies, bool ignoreCover, RuleDefinitions.FeatureSourceType featureSourceType, List<EffectForm> effectForms, List<SaveAffinityBySenseDescription> savingThrowAffinitiesBySense, List<SaveAffinityByFamilyDescription> savingThrowAffinitiesByFamily, string sourceName, BaseDefinition sourceDefinition, string schoolOfMagic, MetamagicOptionDefinition metamagicOption, out RuleDefinitions.RollOutcome saveOutcome, out int saveOutcomeDelta) { throw new Exception("Unexpected service boundary"); }
}
public class EffectDescription
{
    public bool HasSavingThrow {get;set;}=true; public bool RollSaveOnlyIfRelevantForms {get;set;}
    public string SavingThrowAbility {get;set;}="Dexterity";public bool DisableSavingThrowOnAllies{get;set;}public bool AdvantageForEnemies{get;set;}public bool IgnoreCover{get;set;}=true;
    public List<SaveAffinityBySenseDescription> SavingThrowAffinitiesBySense {get;set;}=new List<SaveAffinityBySenseDescription>();
    public List<SaveAffinityByFamilyDescription> SavingThrowAffinitiesByFamily {get;set;}=new List<SaveAffinityByFamilyDescription>();
}
public class FeatureDefinitionMagicAffinity:FeatureDefinition,ISpellCastingAffinityProvider
{
    public RuleDefinitions.SpellParamsModifierType SpellAttackModifierType {get;set;}
    public SpellDefinition ForcedSpellDefinition {get{return null;}} public RuleDefinitions.AdvantageType ForcedSavingThrowAffinity {get{return RuleDefinitions.AdvantageType.None;}}
}
public class KnowledgeLevelDefinition {public int Level{get;set;}=3;}
public class GameBestiaryEntry {public KnowledgeLevelDefinition KnowledgeLevelDefinition{get;set;}=new KnowledgeLevelDefinition();}
public class GameBestiary {public int Calls;public GameBestiaryEntry Entry=new GameBestiaryEntry();[MethodImpl(MethodImplOptions.NoInlining)]public bool TryGetBestiaryEntry(RulesetCharacter creature,out GameBestiaryEntry entry){Calls++;entry=creature is RulesetCharacterMonster?Entry:null;return entry!=null;}}
public interface IGameLoreService {GameBestiary Bestiary{get;}}
public class TestLore:IGameLoreService {public GameBestiary Bestiary{get;set;}=new GameBestiary();}
public class RulesetEffect:RulesetEntity
{
    List<FeatureDefinition> featuresToBrowse=new List<FeatureDefinition>(); Dictionary<FeatureDefinition,RuleDefinitions.FeatureOrigin> featuresOrigin=new Dictionary<FeatureDefinition,RuleDefinitions.FeatureOrigin>();
    public virtual EffectDescription EffectDescription{get;set;}=new EffectDescription();public virtual int SaveDC{get;set;}=15;public virtual RuleDefinitions.FeatureSourceType SourceType{get;set;}=RuleDefinitions.FeatureSourceType.Spell;
    public override string Name{get;set;}="Spell";public virtual BaseDefinition SourceDefinition{get;set;}public virtual string SchoolOfMagic{get;set;}="Evocation";public virtual MetamagicOptionDefinition MetamagicOption{get;set;}
    [MethodImpl(MethodImplOptions.NoInlining)]public bool TryRollSavingThrow(RulesetCharacter caster,RuleDefinitions.Side casterSide,RulesetActor target,ActionModifier actionModifier,List<EffectForm> actualEffectForms,bool hasHitVisual,out RuleDefinitions.RollOutcome saveOutcome,out int saveOutcomeDelta){throw new Exception("Native effect save IL missing");}
}
public static class EventLog
{
    public enum Level { Info }
    public enum Type { SavingThrow }
    public static int Calls;
    public static void Send(Level level, Type type, string actor, string source, string outcome, string more, string other) { Calls++; }
}
