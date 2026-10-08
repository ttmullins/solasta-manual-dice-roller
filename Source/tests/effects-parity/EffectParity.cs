// Development-only native descriptor parity fixture. NativeRollPlan and
// RuntimeRolls below are capture-only adapters; they are never runtime code.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;

class EffectParity
{
    sealed class Stop:Exception{}
    static RulesetActor observedActor; static RuleDefinitions.DieType observedDie; static RuleDefinitions.RollContext observedContext; static int observedCount; static bool observedReroll; static bool observedMaximum;
    static bool checkScope; static bool finishDamage; static int nativeSums; static readonly List<Tuple<RuleDefinitions.DieType,RuleDefinitions.RollContext,int,bool>> actorCalls=new List<Tuple<RuleDefinitions.DieType,RuleDefinitions.RollContext,int,bool>>();
    static RulesetActor bonusSource; static readonly List<FeatureDefinition> bonuses=new List<FeatureDefinition>(); static readonly List<Tuple<RuleDefinitions.DieType,int>> staticCalls=new List<Tuple<RuleDefinitions.DieType,int>>();
    static bool EmptyHealingFeatures(RulesetActor __instance,List<FeatureDefinition> featuresToBrowse){featuresToBrowse.Clear();if(ReferenceEquals(__instance,bonusSource))featuresToBrowse.AddRange(bonuses);return false;}
    static bool RecordStaticSum(int diceNumber,RuleDefinitions.DieType diceType,ref int __result){Check(EffectPatches.IsEligibleNativeDraw(null,RuleDefinitions.RollContext.None,true),"Actual healing bonus static scope did not activate");staticCalls.Add(Tuple.Create(diceType,diceNumber));__result=0;return false;}
    static bool RecordSum(RulesetActor __instance, RuleDefinitions.DieType diceType, RuleDefinitions.RollContext context,int diceNumber,bool canRerollDice,bool maximumDamage,ref int __result)
    {if(checkScope)Check(EffectPatches.IsEligibleNativeDraw(__instance,context,false),"Actual native helper scope did not activate"); observedActor=__instance;observedDie=diceType;observedContext=context;observedCount=diceNumber;observedReroll=canRerollDice;observedMaximum=maximumDamage; actorCalls.Add(Tuple.Create(diceType,context,diceNumber,canRerollDice)); nativeSums++; if(finishDamage){__result=diceNumber*3;return false;} throw new Stop();}
    static bool SkipInflict(ref int __result,ref bool damageAbsorbedByTemporaryHitPoints){Check(!EffectPatches.IsEligibleNativeDraw(observedActor,observedContext,false),"InflictDamage retained draw ownership");__result=0;damageAbsorbedByTemporaryHitPoints=false;return false;}
    static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);}
    static object Uninitialized(Type t){var value=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);for(Type parent=t;parent!=null;parent=parent.BaseType)foreach(var field in parent.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)){if(field.GetValue(value)!=null||!field.FieldType.IsGenericType)continue;var generic=field.FieldType.GetGenericTypeDefinition();if(generic==typeof(List<>)||generic==typeof(Dictionary<,>)||generic==typeof(HashSet<>))field.SetValue(value,Activator.CreateInstance(field.FieldType));}return value;}
    public static int Run()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        Check(typeof(Harmony).Assembly.GetName().Version==new Version(2,3,6,0),"The fixture requires official Harmony 2.3.6.0 for the .NET 8 test host.");
        var harmony=new Harmony("ManualDiceRoller.EffectNativeParity");
        harmony.Patch(AccessTools.Method(typeof(RulesetActor),"RollDiceAndSum"),prefix:new HarmonyMethod(typeof(EffectParity),"RecordSum"));
        var source=(RulesetCharacterHero)Uninitialized(typeof(RulesetCharacterHero));
        var target=(RulesetCharacterHero)Uninitialized(typeof(RulesetCharacterHero));
        var manager=new RulesetImplementationManager();
        EffectPatches.Install(harmony); checkScope=true; int cases=0;
        var enumerate=AccessTools.Method(typeof(RulesetCharacterHero),"EnumerateFeaturesToBrowse").MakeGenericMethod(typeof(IHealingModificationProvider));
        harmony.Patch(enumerate,prefix:new HarmonyMethod(typeof(EffectParity),"EmptyHealingFeatures"));
        AccessTools.Field(typeof(RulesetActor),"featuresToBrowse").SetValue(source,new List<FeatureDefinition>());
        foreach(bool critical in new[]{false,true})foreach(bool ignoreCritical in new[]{false,true})foreach(bool versatile in new[]{false,true})
        foreach(int extra in new[]{0,3})foreach(var level in new[]{EffectForm.LevelApplianceType.No,EffectForm.LevelApplianceType.MultiplyDice})
        foreach(var sourceType in new[]{RuleDefinitions.LevelSourceType.ClassLevel,RuleDefinitions.LevelSourceType.ClassLevelHalfUp,RuleDefinitions.LevelSourceType.EffectLevel})
        {
            var form=new EffectForm {FormType=EffectForm.EffectFormType.Damage};
            form.DamageForm=new DamageForm {DiceNumber=2,DieType=RuleDefinitions.DieType.D6,VersatileDieType=RuleDefinitions.DieType.D8,IgnoreCriticalDoubleDice=ignoreCritical,DamageType="DamageFire"};
            AccessTools.Field(typeof(EffectForm),"applyLevel").SetValue(form,level); AccessTools.Field(typeof(EffectForm),"levelType").SetValue(form,sourceType);
            var p=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,criticalSuccess=critical,useVersatileDamage=versatile,addDice=extra,classLevel=5,effectLevel=4};
            bool absorbed;
            observedCount=-1;
            try{manager.ApplyEffectForms(new List<EffectForm>{form},p,null,out absorbed,out var terminated);}catch(Stop){}
            Check(!EffectPatches.IsEligibleNativeDraw(source,RuleDefinitions.RollContext.MagicDamageValueRoll,false),"Native exceptional exit leaked helper scope");
            Check(observedCount>=0,"Native recorder not reached");
            var method=AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms");
            var invocation=new NativeInvocation(new object(),"nativeparity",method,new object[]{manager,new List<EffectForm>{form},p,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});
            var plan=new NativeRollPlan();
            Check(EffectPatches.TryPrepare(invocation,plan),"Descriptor did not handle native invocation");
            Check(plan.Dice.Count==1,"Descriptor expected one dice group but got "+plan.Dice.Count);
            var leaf=plan.Dice[0];
            Check(ReferenceEquals(leaf.Actor,observedActor)&&leaf.Die==observedDie&&leaf.Context==observedContext&&leaf.Count==observedCount&&leaf.Reroll==observedReroll,
                "Descriptor/native mismatch at critical="+critical+" ignore="+ignoreCritical+" versatile="+versatile+" extra="+extra+" level="+level+" source="+sourceType);
            cases++;
        }
        foreach(bool temporary in new[]{false,true}) foreach(int extra in new[]{0,4}) foreach(var die in new[]{RuleDefinitions.DieType.D4,RuleDefinitions.DieType.D6,RuleDefinitions.DieType.D8})
        {
            var form=new EffectForm {FormType=temporary?EffectForm.EffectFormType.TemporaryHitPoints:EffectForm.EffectFormType.Healing};
            if(temporary)AccessTools.Field(typeof(EffectForm),"temporaryHitPointsForm").SetValue(form,new TemporaryHitPointsForm {DiceNumber=3,DieType=die});
            else AccessTools.Field(typeof(EffectForm),"healingForm").SetValue(form,new HealingForm {DiceNumber=3,DieType=die});
            var p=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,addDice=extra};
            observedCount=-1;
            try{manager.ApplyEffectForms(new List<EffectForm>{form},p,null,out var absorbed,out var terminated);}catch(Stop){}
            Check(observedCount>=0,"Native healing/temp recorder not reached");
            var invocation=new NativeInvocation(new object(),"nativeparity",AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms"),new object[]{manager,new List<EffectForm>{form},p,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});
            var plan=new NativeRollPlan();EffectPatches.TryPrepare(invocation,plan);
            Check(plan.Dice.Count==1,"Healing/temp preparation expected one group");var leaf=plan.Dice[0];
            Check(ReferenceEquals(leaf.Actor,observedActor)&&leaf.Die==observedDie&&leaf.Count==observedCount&&leaf.Context==observedContext,"Native healing/temp descriptor mismatch");
            Check(!EffectPatches.IsEligibleNativeDraw(source,observedContext,false),"Healing/temp exception leaked native scope");cases++;
        }
        // Exercise the actual stock magic overrides and ApplyForms parameter
        // construction, rather than assuming ActionParams still owns the effect.
        ServiceRepository.RemoveService<IRulesetImplementationService>();ServiceRepository.AddService<IRulesetImplementationService>(manager);
        var constitution=new RulesetAttribute();AccessTools.Field(typeof(RulesetAttribute),"currentValue").SetValue(constitution,14);AccessTools.Field(typeof(RulesetAttribute),"upToDate").SetValue(constitution,true);
        ((Dictionary<string,RulesetAttribute>)AccessTools.Field(typeof(RulesetActor),"attributes").GetValue(source))["Constitution"]=constitution;
        foreach(bool power in new[]{false,true})foreach(bool positions in new[]{false,true})
        {
            var action=(CharacterActionMagicEffect)Uninitialized(power?typeof(CharacterActionUsePower):typeof(CharacterActionCastSpell));
            var parameters=(CharacterActionParams)Uninitialized(typeof(CharacterActionParams));var sourceLocation=(GameLocationCharacter)Uninitialized(typeof(GameLocationCharacter));var targetLocation=(GameLocationCharacter)Uninitialized(typeof(GameLocationCharacter));
            AccessTools.Field(typeof(GameLocationCharacter),"rulesetActor").SetValue(sourceLocation,source);AccessTools.Field(typeof(GameLocationCharacter),"rulesetActor").SetValue(targetLocation,target);
            AccessTools.Field(typeof(CharacterActionParams),"actingCharacter").SetValue(parameters,sourceLocation);AccessTools.Field(typeof(CharacterAction),"actionParams").SetValue(action,parameters);AccessTools.Field(typeof(CharacterActionMagicEffect),"<AddDice>k__BackingField").SetValue(action,2);
            var form=new EffectForm {FormType=EffectForm.EffectFormType.Damage,DamageForm=new DamageForm {DiceNumber=2,DieType=RuleDefinitions.DieType.D6,DamageType="DamageFire"}};AccessTools.Field(typeof(EffectForm),"applyLevel").SetValue(form,EffectForm.LevelApplianceType.MultiplyDice);AccessTools.Field(typeof(EffectForm),"levelType").SetValue(form,RuleDefinitions.LevelSourceType.EffectLevel);
            var forms=new List<EffectForm>{form};var description=new EffectDescription();AccessTools.Field(typeof(EffectDescription),"effectForms").SetValue(description,forms);
            ((Dictionary<int,List<EffectForm>>)AccessTools.Field(typeof(CharacterActionMagicEffect),"actualEffectForms").GetValue(action))[0]=forms;
            if(power){var definition=(FeatureDefinitionPower)Uninitialized(typeof(FeatureDefinitionPower));AccessTools.Field(typeof(FeatureDefinitionPower),"effectDescription").SetValue(definition,description);AccessTools.Field(typeof(FeatureDefinitionPower),"abilityScore").SetValue(definition,"Constitution");var usable=(RulesetUsablePower)Uninitialized(typeof(RulesetUsablePower));AccessTools.Field(typeof(RulesetUsablePower),"powerDefinition").SetValue(usable,definition);var effect=(RulesetEffectPower)Uninitialized(typeof(RulesetEffectPower));AccessTools.Field(typeof(RulesetEffectPower),"usablePower").SetValue(effect,usable);AccessTools.Field(typeof(RulesetEffectPower),"user").SetValue(effect,source);AccessTools.Field(typeof(CharacterActionUsePower),"activePower").SetValue(action,effect);}
            else{var definition=(SpellDefinition)Uninitialized(typeof(SpellDefinition));AccessTools.Field(typeof(SpellDefinition),"effectDescription").SetValue(definition,description);var effect=(RulesetEffectSpell)Uninitialized(typeof(RulesetEffectSpell));AccessTools.Field(typeof(RulesetEffectSpell),"spellDefinition").SetValue(effect,definition);AccessTools.Field(typeof(RulesetEffectSpell),"slotLevel").SetValue(effect,3);AccessTools.Field(typeof(RulesetEffectSpell),"caster").SetValue(effect,source);AccessTools.Field(typeof(CharacterActionCastSpell),"activeSpell").SetValue(action,effect);}
            var method=action.GetType().GetMethods(BindingFlags.NonPublic|BindingFlags.Instance).Single(m=>m.Name=="ApplyMagicEffect"&&m.GetParameters().Length==(positions?2:11));
            object[] args=positions?new object[]{default(TA.int3),targetLocation}:new object[]{targetLocation,new ActionModifier(),0,1,RuleDefinitions.RollOutcome.CriticalSuccess,false,RuleDefinitions.RollOutcome.Neutral,0,0,false,false};
            var invocation=new NativeInvocation(new object(),"stockmagic",method,new object[]{action}.Concat(args).ToArray());var plan=new NativeRollPlan();Check(EffectPatches.TryPrepare(invocation,plan)&&plan.Dice.Count==1,"Stock magic descriptor did not prepare exact active effect");observedCount=-1;
            try{method.Invoke(action,args);}catch(TargetInvocationException error){if(!(error.InnerException is Stop))throw;}
            Check(observedCount==(power?2:5)*(positions?1:2),"Native stock spell/power effect level or critical argument changed");var leaf=plan.Dice[0];Check(ReferenceEquals(leaf.Actor,observedActor)&&leaf.Die==observedDie&&leaf.Context==observedContext&&leaf.Count==observedCount&&leaf.Reroll==observedReroll,"Descriptor differs from native stock magic construction");cases++;
        }
        foreach(var bardic in new[]{RuleDefinitions.DieType.D1,RuleDefinitions.DieType.D4})foreach(bool versatile in new[]{false,true})
        {
            var attribute=new RulesetAttribute();AccessTools.Field(typeof(RulesetAttribute),"currentValue").SetValue(attribute,(int)bardic);AccessTools.Field(typeof(RulesetAttribute),"upToDate").SetValue(attribute,true);((Dictionary<string,RulesetAttribute>)AccessTools.Field(typeof(RulesetActor),"attributes").GetValue(source))["BardicInspirationDie"]=attribute;
            var form=new EffectForm {FormType=EffectForm.EffectFormType.Damage,DamageForm=new DamageForm {DiceNumber=2,DieType=RuleDefinitions.DieType.D12,VersatileDieType=RuleDefinitions.DieType.D8,OverrideWithBardicInspirationDie=true,DamageType="DamageFire"}};var p=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,useVersatileDamage=versatile};
            observedCount=-1;try{manager.ApplyEffectForms(new List<EffectForm>{form},p,null,out var absorbed,out var terminated);}catch(Stop){}
            var invocation=new NativeInvocation(new object(),"bardicdamage",AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms"),new object[]{manager,new List<EffectForm>{form},p,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});var plan=new NativeRollPlan();EffectPatches.TryPrepare(invocation,plan);Check(plan.Dice.Count==1&&plan.Dice[0].Die==observedDie&&observedCount==2,"Bardic damage die override differs from native helper");Check(observedDie==(bardic==RuleDefinitions.DieType.D1?(versatile?RuleDefinitions.DieType.D8:RuleDefinitions.DieType.D12):bardic),"Native hero Bardic override always wins when non-D1, including a smaller die");cases++;
        }
        bonusSource=source;
        foreach(var die in new[]{RuleDefinitions.DieType.D6,RuleDefinitions.DieType.D8})
        {
            var bonus=(FeatureDefinitionHealingModifier)Uninitialized(typeof(FeatureDefinitionHealingModifier));
            AccessTools.Field(typeof(FeatureDefinitionHealingModifier),"healingBonusDiceNumber").SetValue(bonus,2);
            AccessTools.Field(typeof(FeatureDefinitionHealingModifier),"healingBonusDiceType").SetValue(bonus,die);
            bonuses.Add(bonus);
        }
        harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollStaticDiceAndSum"),prefix:new HarmonyMethod(typeof(EffectParity),"RecordStaticSum"){priority=Priority.Last});
        var heal=new EffectForm {FormType=EffectForm.EffectFormType.Healing};AccessTools.Field(typeof(EffectForm),"healingForm").SetValue(heal,new HealingForm {DiceNumber=3,DieType=RuleDefinitions.DieType.D10});
        var healP=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,effectLevel=1};
        try{manager.ApplyEffectForms(new List<EffectForm>{heal},healP,null,out var absorbed,out var terminated);}catch(Stop){}
        var bonusInvocation=new NativeInvocation(new object(),"nativebonus",AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms"),new object[]{manager,new List<EffectForm>{heal},healP,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});
        var bonusPlan=new NativeRollPlan();EffectPatches.TryPrepare(bonusInvocation,bonusPlan);
        Check(staticCalls.Count==2&&bonusPlan.Dice.Count==3,"Additional healing must retain two static groups plus base actor group");
        for(int i=0;i<2;i++)Check(bonusPlan.Dice[i].Actor==null&&bonusPlan.Dice[i].Die==staticCalls[i].Item1&&bonusPlan.Dice[i].Count==staticCalls[i].Item2,"Healing bonus native descriptor mismatch");
        Check(bonusPlan.Dice[2].Context==observedContext&&bonusPlan.Dice[2].Die==observedDie&&bonusPlan.Dice[2].Count==observedCount,"Base healing following bonuses shifted native descriptor");cases++;
        bonusSource=null;bonuses.Clear();
        finishDamage=true;
        harmony.Patch(AccessTools.Method(typeof(RulesetActor),"InflictDamage"),prefix:new HarmonyMethod(typeof(EffectParity),"SkipInflict"));
        foreach(bool attack in new[]{false,true})
        {
            manager.ClearDamageFormsByIndex();actorCalls.Clear();
            var forms=new List<EffectForm>();for(int i=0;i<2;i++)forms.Add(new EffectForm {FormType=EffectForm.EffectFormType.Damage,DamageForm=new DamageForm {DiceNumber=i+1,DieType=RuleDefinitions.DieType.D6,VersatileDieType=RuleDefinitions.DieType.D8,DamageType="DamageFire"}});
            var p=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,addDice=3,useVersatileDamage=true,criticalSuccess=true};
            if(attack)p.attackMode=new RulesetAttackMode {SourceDefinition=(ItemDefinition)Uninitialized(typeof(ItemDefinition))};
            var invocation=new NativeInvocation(new object(),"nativebatch",AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms"),new object[]{manager,forms,p,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});
            var plan=new NativeRollPlan();EffectPatches.TryPrepare(invocation,plan);Check(plan.Dice.Count==2,"Pure damage batch expected two reference-owned groups");
            manager.ApplyEffectForms(forms,p,null,out var absorbed,out var terminated);
            Check(actorCalls.Count==2,"Native pure damage batch did not call two groups");for(int i=0;i<2;i++)Check(plan.Dice[i].Die==actorCalls[i].Item1&&plan.Dice[i].Context==actorCalls[i].Item2&&plan.Dice[i].Count==actorCalls[i].Item3&&plan.Dice[i].Reroll==actorCalls[i].Item4,"Second damage form flags/count differ from native");cases++;
        }
        foreach(bool ignoreAdvancement in new[]{false,true})
        {
            manager.ClearDamageFormsByIndex();actorCalls.Clear();var forms=new List<EffectForm>();for(int i=0;i<2;i++)forms.Add(new EffectForm {FormType=EffectForm.EffectFormType.Damage,DamageForm=new DamageForm {DiceNumber=i+1,DieType=RuleDefinitions.DieType.D6,DamageType="DamageFire",IgnoreSpellAdvancementDamageDice=i>0&&ignoreAdvancement}});
            var definition=(SpellDefinition)Uninitialized(typeof(SpellDefinition));var description=new EffectDescription();AccessTools.Field(typeof(EffectDescription),"effectForms").SetValue(description,forms);AccessTools.Field(typeof(SpellDefinition),"effectDescription").SetValue(definition,description);var spell=(RulesetEffectSpell)Uninitialized(typeof(RulesetEffectSpell));AccessTools.Field(typeof(RulesetEffectSpell),"spellDefinition").SetValue(spell,definition);AccessTools.Field(typeof(RulesetEffectSpell),"caster").SetValue(spell,source);
            var p=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,activeEffect=spell,addDice=3};var invocation=new NativeInvocation(new object(),"spelladvancement",AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms"),new object[]{manager,forms,p,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});var plan=new NativeRollPlan();EffectPatches.TryPrepare(invocation,plan);manager.ApplyEffectForms(forms,p,null,out var absorbed,out var terminated);
            Check(actorCalls.Count==2&&plan.Dice.Count==2,"Native advancement batch must contain two distinct groups");for(int i=0;i<2;i++)Check(plan.Dice[i].Count==actorCalls[i].Item3&&plan.Dice[i].Reroll==actorCalls[i].Item4,"Spell advancement/ignore flag differs on subsequent damage form");Check(actorCalls[0].Item3==4&&actorCalls[1].Item3==(ignoreAdvancement?2:5),"Native extra slot dice apply to later spell forms unless explicitly ignored");cases++;
        }
        manager.ClearDamageFormsByIndex();nativeSums=0;
        var shared=new EffectForm {FormType=EffectForm.EffectFormType.Damage,DamageForm=new DamageForm {DiceNumber=2,DieType=RuleDefinitions.DieType.D6,DamageType="DamageFire"}};
        var sharedP=new RulesetImplementationDefinitions.ApplyFormsParams {sourceCharacter=source,targetCharacter=target,targetType=RuleDefinitions.TargetType.Sphere};
        for(int targetIndex=0;targetIndex<2;targetIndex++)
        {
            sharedP.targetIndex=targetIndex;
            var invocation=new NativeInvocation(new object(),"nativecache",AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForms"),new object[]{manager,new List<EffectForm>{shared},sharedP,null,null,null,false,false,false,RuleDefinitions.EffectApplication.All,null});
            var plan=new NativeRollPlan();EffectPatches.TryPrepare(invocation,plan);
            Check(plan.Dice.Count==(targetIndex==0?1:0),"Area native cache was not honored at preparation");
            manager.ApplyEffectForms(new List<EffectForm>{shared},sharedP,null,out var absorbed,out var terminated);
            Check(nativeSums==1,"An existing native area pool rolled again");
            Check(!EffectPatches.IsEligibleNativeDraw(source,RuleDefinitions.RollContext.MagicDamageValueRoll,false),"Normal native effect exit leaked draw ownership");cases++;
        }
        Console.WriteLine("Harmony "+typeof(Harmony).Assembly.GetName().Version);
        Console.WriteLine("PASS "+cases+" actual pristine effect descriptor cases: damage/healing/temporary HP, additional healing bonus groups, helper scope cleanup, and native shared area cache.");
        return 0;
    }
}
namespace ManualDiceRoller
{
    public sealed class NativeInvocation { public object Owner;public string Site;public MethodBase Method;public object[] Args; public object Receiver{get{return Method.IsStatic?null:Args[0];}} public NativeInvocation(object owner,string site,MethodBase method,object[] args){Owner=owner;Site=site;Method=method;Args=args;} public object Arg(string name){var ps=Method.GetParameters();for(int i=0;i<ps.Length;i++)if(ps[i].Name==name)return Args[i+(Method.IsStatic?0:1)];return null;} public T Arg<T>(string n,T fallback=default(T)){var v=Arg(n);return v is T?(T)v:fallback;} }
    public sealed class NativeRollPlan
    {
        public sealed class DiceGroup { public RulesetActor Actor; public RuleDefinitions.DieType Die; public RuleDefinitions.RollContext Context;public int Count; public bool Reroll;public object Form; }
        public List<DiceGroup> Dice=new List<DiceGroup>();
        public void RequireNativeState(Func<bool> validator){}
        public void RequireManualState(Func<bool> validator){}
        public void Describe(int dc,bool hasBonus,int bonus){}
        public void HintAfterSave(object form,RulesetActor target,bool half,string targetName){}
        public void AddActorDice(RulesetActor actor,RuleDefinitions.DieType die,RuleDefinitions.RollContext context,int count,RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None,bool proficient=false,string skill="",bool canReroll=true,object form=null,string title=null,string note=null,bool enumerate=true,string hint=null){if(count>0)Dice.Add(new DiceGroup{Actor=actor,Die=die,Context=context,Count=count,Reroll=canReroll,Form=form});}
        public void AddStaticDice(RulesetActor actor,RuleDefinitions.DieType die,int count,object form=null,string title=null,string note=null,RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None,string role=null,RuleDefinitions.RollContext category=RuleDefinitions.RollContext.None,string hint=null){if(count>0)Dice.Add(new DiceGroup{Die=die,Count=count,Form=form});}
    }
    public static class Plugin { public sealed class SettingsData {public bool SpellPools=true,Traps=true,Features=true;} public static SettingsData Settings=new SettingsData(); internal const RuleDefinitions.RollContext FeatureDice=(RuleDefinitions.RollContext)0x100,TrapDice=(RuleDefinitions.RollContext)0x200;}
    public static class RuntimeRolls { public static int Max(RuleDefinitions.DieType die){int i=(int)die;return i>=0&&i<RuleDefinitions.DiceMaxValue.Length?RuleDefinitions.DiceMaxValue[i]:0;} public static int FormStaticFace(object form,RuleDefinitions.DieType d,RuleDefinitions.AdvantageType a,out int first,out int second,float alt=0f){return RuleDefinitions.RollDie(d,a,out first,out second,alt);} public static bool HasPreparedForm(object form){return true;} public static bool CanManual(RulesetActor actor,RuleDefinitions.RollContext context){return true;} public static object EnterForm(object form){return null;} public static void ExitForm(object previous){} }
}

class Bootstrap { static int Main(){ var managed=Environment.GetEnvironmentVariable("MANUAL_DICE_PARITY_MANAGED");if(string.IsNullOrEmpty(managed))throw new InvalidOperationException("Run this fixture through tests/effects-parity/run.py so the native managed directory is set."); AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=System.IO.Path.Combine(managed,new AssemblyName(e.Name).Name+".dll");return System.IO.File.Exists(p)?Assembly.LoadFrom(p):null;}; return (int)Assembly.GetExecutingAssembly().GetType("EffectParity").GetMethod("Run").Invoke(null,null);}}
