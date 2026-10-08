using System;
using System.Collections;
using System.Collections.Generic;

public static partial class RuleDefinitions
{
    public enum FeatureSourceType {CharacterFeature}
    public struct FeatureOrigin { }
    public struct TrendInfo
    {
        public int value;
        public TrendInfo(int value,FeatureSourceType type,string name,object source)
        {this.value=value;}
    }
}
public class BaseDefinition {public string Name{get;set;}="";}
public class FeatureDefinition:BaseDefinition { }
public class FeatureDefinitionMagicAffinity:FeatureDefinition
{public RuleDefinitions.AdvantageType CounterspellAffinity{get;set;}}
public partial class RulesetActor
{
    public List<FeatureDefinition> FeaturesToBrowse{get;set;}=new List<FeatureDefinition>();
    public virtual void EnumerateFeaturesToBrowse<T>(List<FeatureDefinition> features,Dictionary<FeatureDefinition,RuleDefinitions.FeatureOrigin> origins=null)
    {features.Clear();}
}
public partial class RulesetCharacter:RulesetActor
{
    public delegate void SpellCounterHandler(RulesetCharacter counterer,RulesetCharacter caster,SpellDefinition spell,bool success,bool unknown);
    public SpellCounterHandler SpellCounter{get;set;}
}
public partial class GameLocationCharacter
{public RulesetCharacter RulesetCharacter{get;set;}=new RulesetCharacter();}
public partial class ActionModifier
{public List<RuleDefinitions.TrendInfo> AbilityCheckAdvantageTrends{get;set;}=new List<RuleDefinitions.TrendInfo>();}
public partial class CharacterActionParams
{
    public CharacterAction TargetAction{get;set;}
    public RulesetEffect RulesetEffect{get;set;}
}
public partial class CharacterAction {public bool Countered{get;set;}}
public class SpellDefinition:BaseDefinition { }
public class RulesetSpellRepertoire {public string SpellCastingAbility{get;set;}="Intelligence";}
public class RulesetEffect {public EffectDescription EffectDescription{get;set;}=new EffectDescription();}
public class RulesetEffectSpell:RulesetEffect
{
    public SpellDefinition SpellDefinition{get;set;}=new SpellDefinition();
    public RulesetSpellRepertoire SpellRepertoire{get;set;}=new RulesetSpellRepertoire();
    public int SlotLevel{get;set;}=5;
    public RuleDefinitions.AdvantageType CounterAffinity{get;set;}
    public string CounterAffinityOrigin{get;set;}="";
    public string IdentifiedBy{get;set;}="";
}
public class CounterForm
{
    public int AutomaticSpellLevel{get;set;}=3;
    public int CheckBaseDC{get;set;}=10;
    public bool AddProficiencyBonus{get;set;}
}
public class EffectForm
{
    public enum EffectFormType {Damage,Healing,Condition,LightSource,Summon,Counter}
    public EffectFormType FormType{get;set;}=EffectFormType.Counter;
    public CounterForm CounterForm{get;set;}=new CounterForm();
}
public class EffectDescription
{public List<EffectForm> EffectForms{get;set;}=new List<EffectForm>{new EffectForm()};}
public class CharacterActionMagicEffect:CharacterAction
{protected virtual IEnumerator CounterEffectAction(CharacterAction counterAction){throw new Exception("Native method must be copied");}}
public class CharacterActionCastSpell:CharacterActionMagicEffect
{
    private RulesetEffectSpell activeSpell;
    private int addSpellLevel;
    public CharacterActionCastSpell()
    {
        activeSpell=new RulesetEffectSpell();ActionParams.RulesetEffect=activeSpell;
        ActionParams.TargetAction=new CharacterAction();ActionParams.TargetAction.ActionParams.RulesetEffect=new RulesetEffectSpell();
    }
    protected override IEnumerator CounterEffectAction(CharacterAction counterAction){throw new Exception("Native CounterEffectAction iterator must be copied");}
    public IEnumerator RunCounter(){return CounterEffectAction(this);}
}
