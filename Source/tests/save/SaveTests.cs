using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;

static class SaveTests
{
    sealed class ExternalPerformance:FeatureDefinition,ISavingThrowPerformanceProvider
    {public int Calls;public bool UseControllerSavingThrows{get{return false;}}public bool CanPerformSaveForAbilityScore(string ability){Calls++;return true;}}
    sealed class ExternalCasting:FeatureDefinition,ISpellCastingAffinityProvider
    {public int Calls;public SpellDefinition ForcedSpellDefinition {get{Calls++;return null;}}public RuleDefinitions.AdvantageType ForcedSavingThrowAffinity{get{return RuleDefinitions.AdvantageType.Disadvantage;}}}
    static int checks, replaced, saved, bonus, total, interruption;
    static int foreignCalls;
    static void ForeignPrefix(){foreignCalls++;}
    static RulesetCharacterHero hero;
    static RulesetImplementationManager manager;
    static object[] args;
    static MethodInfo method=typeof(RulesetImplementationManager).GetMethod("TryRollSavingThrow");
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static FeatureDefinitionSavingThrowAffinity Feature(string ability,int number=0,RuleDefinitions.CharacterSavingThrowAffinity affinity=RuleDefinitions.CharacterSavingThrowAffinity.None)
    {
        var feature=new FeatureDefinitionSavingThrowAffinity{Name="Bless"};
        feature.AffinityGroups.Add(new FeatureDefinitionSavingThrowAffinity.SavingThrowAffinityGroup{abilityScoreName=ability,savingThrowModifierDiceNumber=number,savingThrowModifierDieType=RuleDefinitions.DieType.D4,affinity=affinity});
        return feature;
    }
    static void Setup()
    {
        RuntimeRolls.Enabled=true;manager=new RulesetImplementationManager();ServiceRepository.Manager=manager;
        ServiceRepository.Settings=new TestSettings();TA.DeterministicRandom.Calls=0;TA.DeterministicRandom.Values.Clear();EventLog.Calls=0;
        replaced=saved=bonus=total=interruption=0;
        hero=new RulesetCharacterHero{Side=RuleDefinitions.Side.Ally};
        hero.Attributes["Dexterity"]=new RulesetAttribute{CurrentValue=10};hero.Attributes["Wisdom"]=new RulesetAttribute{CurrentValue=18};
        hero.ReplacedAbilityScoreForSave=(a,f,old,next)=>replaced++;
        hero.SaveRolled=(a,ability,source,result,dc,sum,roll,first,second,b,mods,advs,v)=>{saved++;total=sum;};
        hero.AdditionalSaveDieRolled=(a,t)=>bonus++;
        args=new object[]{null,RuleDefinitions.Side.Enemy,hero,new ActionModifier(),false,true,"Dexterity",15,false,false,true,RuleDefinitions.FeatureSourceType.Spell,
            new List<EffectForm>{new EffectForm{FormType=EffectForm.EffectFormType.Damage},new EffectForm{FormType=EffectForm.EffectFormType.Condition}},null,null,"Fireball",null,"Evocation",null,RuleDefinitions.RollOutcome.Neutral,0};
    }
    static NativeRollPlan Preview()
    {
        var all=new object[args.Length+1];all[0]=manager;Array.Copy(args,0,all,1,args.Length);
        var plan=new NativeRollPlan();Check(NativeSavePlanner.TryPrepare(new NativeInvocation(new object(),"Save",method,all,1),plan),"Planner handled native save");return plan;
    }
    static bool Native(){bool result=(bool)method.Invoke(manager,args);interruption=hero.Interruptions;return result;}
    static void BasicAndMultiForm()
    {
        Setup();var feature=Feature("Wisdom",1,RuleDefinitions.CharacterSavingThrowAffinity.Advantage);feature.PriorityAbilityScore="Wisdom";hero.Features.Add(feature);
        var sentinel=new FeatureDefinition{Name="OldScratch"};hero.FeaturesToBrowse.Add(sentinel);hero.FeaturesOrigin[sentinel]=new RuleDefinitions.FeatureOrigin{sourceName="Previous"};
        var oldProvider=Feature("Dexterity",0);manager.Providers.Add(oldProvider);
        var modifier=(ActionModifier)args[3];modifier.SavingThrowModifier=2;modifier.SavingThrowAdvantageTrends.Add(new RuleDefinitions.TrendInfo(1));
        var oldDelegate=hero.ReplacedAbilityScoreForSave;
        var plan=Preview();
        Check(plan.Groups.Count==2&&plan.Groups[0].Static&&plan.Groups[0].Count==1&&plan.Groups[0].Die==RuleDefinitions.DieType.D4,"Native provider is accounted once across multiple forms");
        Check(!plan.Groups[1].Static&&plan.Groups[1].Advantage==RuleDefinitions.AdvantageType.Advantage&&plan.Groups[1].Note.Contains("Wisdom")&&plan.Groups[1].DC==15,"Primary captured after native ability replacement and advantage computation");
        Check(TA.DeterministicRandom.Calls==0&&replaced==0&&saved==0&&bonus==0&&hero.Interruptions==0&&EventLog.Calls==0,"Preview causes no RNG/outcome/events/condition interruption");
        Check(hero.FeaturesToBrowse.SequenceEqual(new[]{sentinel})&&hero.FeaturesOrigin.Count==1&&hero.FeaturesOrigin[sentinel].sourceName=="Previous"&&manager.Providers.SequenceEqual(new[]{oldProvider})&&ReferenceEquals(oldDelegate,hero.ReplacedAbilityScoreForSave),"Native preview restores scratch collections, origins, providers and event delegate");
        Check(modifier.SavingThrowModifier==2&&modifier.SavingThrowAdvantageTrends.Count==1&&modifier.SavingThrowModifierTrends.Count==0,"Caller ActionModifier remains unchanged");
        TA.DeterministicRandom.Values=new Queue<int>(new[]{3,11,10});Check(Native(),"Actual native save remains present");
        Check((RuleDefinitions.RollOutcome)args[19]==RuleDefinitions.RollOutcome.Success&&total==22&&TA.DeterministicRandom.Calls==3,"After preview native uses one Bless die plus native advantage and modifier");
        Check(replaced==1&&saved==1&&bonus==1&&interruption==1&&EventLog.Calls==1,"Native final events/bookkeeping happen exactly once");
    }
    static void AutomaticAndOverride()
    {
        Setup();args[1]=RuleDefinitions.Side.Ally;args[18]=new MetamagicOptionDefinition{Type=RuleDefinitions.MetamagicType.CarefulSpell};
        Check(Preview().Groups.Count==0&&TA.DeterministicRandom.Calls==0,"Careful Spell automatically succeeding ally never prompts");Check(Native()&&(RuleDefinitions.RollOutcome)args[19]==RuleDefinitions.RollOutcome.Success&&saved==0,"Careful native success keeps no die/event");
        Setup();args[1]=RuleDefinitions.Side.Ally;args[8]=true;Check(Preview().Groups.Count==0&&!Native(),"Disabled ally saving throw never gets manual die");
        Setup();hero.Features.Add(Feature("Dexterity",0,RuleDefinitions.CharacterSavingThrowAffinity.AutomaticFail));
        Check(Preview().Groups.Count==0&&Native()&&(RuleDefinitions.RollOutcome)args[19]==RuleDefinitions.RollOutcome.Failure&&TA.DeterministicRandom.Calls==0&&saved==0,"Native automatic failure never prompts or rolls");
        Setup();((List<EffectForm>)args[12])[0].OverrideSavingThrowInfo=new OverrideSavingThrowInfo{SavingThrowAbility="Wisdom",SavingThrowDC=21,FeatureSourceType=RuleDefinitions.FeatureSourceType.Spell,SourceDefinitionName="Override"};
        Check(Preview().Groups.Single().Note.Contains("Wisdom")&&Preview().Groups.Single().DC==21,"Native effect form ability/DC override controls prompt");
    }
    static void RestrictedFormsAndFailure()
    {
        Setup();var feature=Feature("Dexterity",1);feature.AffinityGroups[0].restrictedForms.Add(EffectForm.EffectFormType.Damage);hero.Features.Add(feature);
        args[12]=new List<EffectForm>{new EffectForm{FormType=EffectForm.EffectFormType.Condition},new EffectForm{FormType=EffectForm.EffectFormType.Damage},new EffectForm{FormType=EffectForm.EffectFormType.Damage}};
        Check(Preview().Groups.Count==2,"Unmatched first form does not suppress later applicable bonus; repeated matching form dedupes");
        Setup();feature=Feature("Dexterity",2);feature.AffinityGroups[0].savingThrowModifierType=FeatureDefinitionSavingThrowAffinity.ModifierType.RemoveDice;hero.Features.Add(feature);
        var plan=Preview();Check(plan.Groups[0].Static&&plan.Groups[0].Count==2,"Native Bane/subtracted raw dice preserve count instead of signed-total input");
        Setup();var sentinel=new FeatureDefinition{Name="Before"};hero.FeaturesToBrowse.Add(sentinel);hero.FeaturesOrigin[sentinel]=new RuleDefinitions.FeatureOrigin{sourceName="Before"};var old=hero.ReplacedAbilityScoreForSave;args[6]="MissingAttribute";
        bool threw=false;try{Preview();}catch(InvalidOperationException){threw=true;}
        Check(threw&&!NativeSavePlanner.Previewing&&ReferenceEquals(old,hero.ReplacedAbilityScoreForSave)&&hero.FeaturesToBrowse.SequenceEqual(new[]{sentinel})&&hero.FeaturesOrigin.Count==1,"Failing native calculation restores state and releases preview mode");
        TA.DeterministicRandom.Values.Enqueue(6);int first,second;Check(RuleDefinitions.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None,out first,out second)==7&&TA.DeterministicRandom.Calls==1,"Preview failure leaves next native RNG untouched");
        RuntimeRolls.Enabled=false;Setup();RuntimeRolls.Enabled=false;Check(Preview().Groups.Count==0,"Disabled manual setting never previews actor");
    }
    static void CasterOpaqueAndDepth()
    {
        Setup();var caster=new RulesetCharacterHero();var source=new SpellDefinition{Name="Fireball"};
        caster.Features.Add(new NativeEffectAffinity{AddBonusToEffectSaveDC=SpellAndPowersDefinitions.RulesetEffectSaveDCBonusType.Spell,SpellWithModifiedSaveDC=source,BonusToEffectSaveDC=3});
        caster.Features.Add(new NativeSpellAffinity{ForcedSavingThrowAffinity=RuleDefinitions.AdvantageType.Disadvantage});
        var sentinel=new FeatureDefinition{Name="CasterBefore"};caster.FeaturesToBrowse.Add(sentinel);caster.FeaturesOrigin[sentinel]=new RuleDefinitions.FeatureOrigin{sourceName="Before"};
        args[0]=caster;args[16]=source;
        var plan=Preview();Check(plan.Groups.Single().Advantage==RuleDefinitions.AdvantageType.Disadvantage&&plan.Groups.Single().DC==18,"Native caster forced affinity and source-specific DC bonus enter primary prompt");
        Check(caster.FeaturesToBrowse.SequenceEqual(new[]{sentinel})&&caster.FeaturesOrigin.Count==1&&caster.FeaturesOrigin[sentinel].sourceName=="Before","Caster native scratch/origins restored after imposed modifier");
        Setup();var external=new ExternalPerformance();hero.Features.Add(external);Check(Preview().Groups.Count==0&&external.Calls==0,"Opaque auto-failure provider bypassed before any side effect");
        Setup();caster=new RulesetCharacterHero();var cast=new ExternalCasting();caster.Features.Add(cast);args[0]=caster;Check(Preview().Groups.Count==0&&cast.Calls==0,"Opaque caster forced-affinity provider bypassed before getter side effects");
        Setup();caster=new RulesetCharacterHero();var broken=new NativeSpellAffinity{Throw=true};caster.Features.Add(broken);caster.FeaturesToBrowse.Add(sentinel);caster.FeaturesOrigin[sentinel]=new RuleDefinitions.FeatureOrigin{sourceName="Before"};args[0]=caster;
        bool failed=false;try{Preview();}catch(InvalidOperationException){failed=true;}
        Check(failed&&!NativeSavePlanner.Previewing&&!NativeSavePlanner.AllowsStaticBonus&&caster.FeaturesToBrowse.SequenceEqual(new[]{sentinel})&&caster.FeaturesOrigin.Count==1,"Late caster provider failure releases modifier depth and restores caster state");
        broken.Throw=false;Check(Preview().Groups.Count==1&&!NativeSavePlanner.AllowsStaticBonus,"Fresh save previews normally after late calculation failure");
        var type=typeof(NativeSavePlanner);var previewType=type.GetNestedType("Preview",BindingFlags.NonPublic);var state=Activator.CreateInstance(previewType,true);var depth=previewType.GetField("SumDepth",BindingFlags.Instance|BindingFlags.NonPublic);depth.SetValue(state,1);
        var previewField=type.GetField("preview",BindingFlags.Static|BindingFlags.NonPublic);previewField.SetValue(null,state);
        var nested=new object[]{1,RuleDefinitions.DieType.D4,null};failed=false;
        try{type.GetMethod("SumPrefix",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,nested);}catch(TargetInvocationException){failed=true;}
        type.GetMethod("SumFinalizer",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{new Exception("Nested failure"),nested[2]});
        Check(failed&&nested[2]==null&&(int)depth.GetValue(state)==1,"Rejected nested sum cannot borrow/decrement outer preview depth");
        type.GetMethod("SumFinalizer",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{null,state});Check((int)depth.GetValue(state)==0,"Outer finalizer alone releases its owned depth");previewField.SetValue(null,null);
    }
    static void ForeignPatchedCalculations()
    {
        var targets=new MethodBase[]{method,typeof(RulesetActor).GetMethod("RollSavingThrow"),typeof(FeatureDefinitionSavingThrowAffinity).GetMethod("ComputeSavingThrowModifier"),typeof(FeatureDefinitionSavingThrowAffinity).GetProperty("PriorityAbilityScore").GetGetMethod(),typeof(RuleDefinitions).GetMethod("RollDie")};
        foreach(var target in targets)
        {
            Setup();if(target.DeclaringType==typeof(FeatureDefinitionSavingThrowAffinity))hero.Features.Add(Feature("Dexterity",1));
            // An untrusted patch cannot gain approval by copying our owner ID.
            var foreign=new Harmony("ManualDiceRoller.Standalone");foreignCalls=0;
            var hook=new HarmonyMethod(typeof(SaveTests).GetMethod("ForeignPrefix",BindingFlags.Static|BindingFlags.NonPublic));foreign.Patch(target,prefix:hook);
            try
            {
                Check(Preview().Groups.Count==0&&foreignCalls==0&&!NativeSavePlanner.Previewing&&TA.DeterministicRandom.Calls==0,"Foreign "+target.Name+" bypasses preview before external code or RNG runs");
                TA.DeterministicRandom.Values=new Queue<int>(new[]{2,11});Check(Native()&&foreignCalls>0,"Real native save still runs foreign "+target.Name+" normally");
                Check(foreignCalls==1,"Foreign "+target.Name+" has only its one real native invocation, never a preview duplicate");
            }
            finally{foreign.Unpatch(target,hook.method);}
        }
        Setup();var postfixOwner=new Harmony("DifferentMod.Postfix");foreignCalls=0;var postfix=new HarmonyMethod(typeof(SaveTests).GetMethod("ForeignPrefix",BindingFlags.Static|BindingFlags.NonPublic));postfixOwner.Patch(method,postfix:postfix);
        try{Check(Preview().Groups.Count==0&&foreignCalls==0,"Foreign postfix also bypasses preview");TA.DeterministicRandom.Values.Enqueue(11);Native();Check(foreignCalls==1,"Foreign postfix receives native call exactly once");}
        finally{postfixOwner.Unpatch(method,postfix.method);}
        Setup();Check(Preview().Groups.Count==1,"Removing foreign patches restores manual preview without sticky rejection");
    }
    static void ScratchIdentity()
    {
        Setup(); hero.Features.Add(Feature("Dexterity",1));
        var sentinel=new FeatureDefinition{Name="Original"}; var oldCondition=new RulesetCondition();
        hero.FeaturesToBrowse.Add(sentinel);hero.FeaturesOrigin[sentinel]=new RuleDefinitions.FeatureOrigin{sourceName="Original"};hero.AllConditionsForEnumeration.Add(oldCondition);
        hero.Conditions.Add(new RulesetCondition()); manager.Providers.Add(Feature("Dexterity"));
        var features=hero.FeaturesToBrowse;var origins=hero.FeaturesOrigin;var conditions=hero.AllConditionsForEnumeration;var providers=manager.Providers;
        var featureIterator=features.GetEnumerator();var originIterator=origins.GetEnumerator();var conditionIterator=conditions.GetEnumerator();var providerIterator=providers.GetEnumerator();
        featureIterator.MoveNext();originIterator.MoveNext();conditionIterator.MoveNext();providerIterator.MoveNext();
        Check(Preview().Groups.Count==2,"Scratch isolation keeps real native save planning");
        Check(ReferenceEquals(features,hero.FeaturesToBrowse)&&ReferenceEquals(origins,hero.FeaturesOrigin)&&ReferenceEquals(conditions,hero.AllConditionsForEnumeration)&&ReferenceEquals(providers,manager.Providers),"Preview restores exact original scratch objects");
        Check(!featureIterator.MoveNext()&&!originIterator.MoveNext()&&!conditionIterator.MoveNext()&&!providerIterator.MoveNext(),"Preview preserves already-active native scratch enumerator versions");
        Check(conditions.Count==1&&ReferenceEquals(conditions[0],oldCondition),"Preview does not overwrite native all-conditions scratch cache");
        Setup(); hero.Features.Add(Feature("Dexterity",1));
        var service=new RulesetImplementationManager();var prior=Feature("Dexterity"); manager.Providers.Add(prior); ServiceRepository.Manager=service;
        var savedProviders=manager.Providers;
        Check(Preview().Groups.Count==2&&ReferenceEquals(savedProviders,manager.Providers)&&manager.Providers.SequenceEqual(new[]{prior}),"Preview isolates its actual manager receiver when service manager differs");
        Check(service.Providers.Count==0,"Receiver preview leaves an unrelated service manager untouched");
        Setup(); hero.FeaturesToBrowse.Add(sentinel); var original=hero.FeaturesToBrowse;var iterator=original.GetEnumerator();iterator.MoveNext();args[6]="MissingAttribute";
        bool failed=false;try{Preview();}catch(InvalidOperationException){failed=true;}
        Check(failed&&ReferenceEquals(original,hero.FeaturesToBrowse)&&!iterator.MoveNext(),"Failed native preview also preserves original reference and active enumerator");
    }
    static void ForeignHelpersAndEffect()
    {
        Setup();hero.Features.Add(Feature("Dexterity",1));
        var helper=typeof(RulesetActor).GetMethod("GetAllConditions");var owner=new Harmony("Foreign.NestedHelper");
        var hook=new HarmonyMethod(typeof(SaveTests).GetMethod("ForeignPrefix",BindingFlags.Static|BindingFlags.NonPublic));foreignCalls=0;owner.Patch(helper,prefix:hook);
        try { Check(Preview().Groups.Count==0&&foreignCalls==0,"Foreign condition-enumeration helper never executes during save preview");TA.DeterministicRandom.Values=new Queue<int>(new[]{2,11});Native();Check(foreignCalls>0,"Real native save still executes its condition helper"); }
        finally {owner.Unpatch(helper,hook.method);}
        foreach(var nested in new[]{typeof(ConditionDefinition).GetMethod("HasFeature"),typeof(TestSettings).GetProperty("SavingThrowAllyModifier").GetGetMethod()})
        {
            Setup();var feature=Feature("Dexterity",1);hero.Features.Add(feature);var definition=new ConditionDefinition();definition.Features.Add(feature);hero.ConditionsByCategory["Effect"]=new List<RulesetCondition>{new RulesetCondition{ConditionDefinition=definition}};
            ServiceRepository.Settings.SavingThrowAllyModifier=1;foreignCalls=0;owner.Patch(nested,prefix:hook);
            try{Check(Preview().Groups.Count==0&&foreignCalls==0,"Foreign condition-membership/settings getter is rejected before readonly preview");TA.DeterministicRandom.Values=new Queue<int>(new[]{2,11});Native();Check(foreignCalls>0,"Real native save still invokes its condition-membership/settings getter");}
            finally{owner.Unpatch(nested,hook.method);}
        }
        Setup(); var caster=new RulesetCharacterHero();caster.Features.Add(new FeatureDefinitionMagicAffinity{SpellAttackModifierType=RuleDefinitions.SpellParamsModifierType.HalfKnowledgeLevelOfTarget});
        var effect=new RulesetEffect();var effectMethod=typeof(RulesetEffect).GetMethod("TryRollSavingThrow");
        var shape=new RulesetCharacterMonster{OriginalFormCharacter=hero,IsSubstitute=true,Side=RuleDefinitions.Side.Ally};shape.Attributes["Dexterity"]=new RulesetAttribute{CurrentValue=10};
        var values=new object[]{effect,caster,RuleDefinitions.Side.Enemy,shape,new ActionModifier(),new List<EffectForm>{new EffectForm()},false,RuleDefinitions.RollOutcome.Neutral,0};
        var plan=new NativeRollPlan();
        Check(NativeSavePlanner.TryPrepare(new NativeInvocation(new object(),"EffectSave",effectMethod,values,1),plan)&&plan.Groups.Count==1&&plan.Groups[0].DC==17,"Pristine native effect wrapper includes half-knowledge DC bonus");
        foreach(var nested in new[]{typeof(GameBestiary).GetMethod("TryGetBestiaryEntry"),typeof(TestLore).GetProperty("Bestiary").GetGetMethod()})
        {
        helper=nested;foreignCalls=0;owner.Patch(helper,prefix:hook);
        try {
            plan=new NativeRollPlan();NativeSavePlanner.TryPrepare(new NativeInvocation(new object(),"EffectSave",effectMethod,values,1),plan);
            Check(plan.Groups.Count==0&&foreignCalls==0,"Foreign bestiary lookup/service getter never executes during effect save preview");
            TA.DeterministicRandom.Values.Enqueue(11);effectMethod.Invoke(effect,values.Skip(1).ToArray());
            Check(foreignCalls==1,"Foreign bestiary lookup/service getter executes exactly once in real native effect save");
        } finally {owner.Unpatch(helper,hook.method);}
        }
    }
    static void PendingStateChanges()
    {
        Setup();var first=Feature("Dexterity",1);var second=Feature("Dexterity",1);second.Name="Bane";second.AffinityGroups[0].savingThrowModifierType=FeatureDefinitionSavingThrowAffinity.ModifierType.RemoveDice;
        hero.Features.Add(first);hero.Features.Add(second);var plan=Preview();
        Check(plan.NativeStateValid!=null&&plan.NativeStateValid()&&TA.DeterministicRandom.Calls==0&&saved==0,"Unchanged save handoff validator uses no RNG/events and preserves accepted plan");
        hero.Features.Reverse();Check(!plan.NativeStateValid()&&TA.DeterministicRandom.Calls==0,"Reordered same-D4 Bless/Bane providers cannot borrow each other's accepted faces");
        hero.Features.Reverse();Check(plan.NativeStateValid(),"Restoring unchanged provider order permits the original plan");
        first.AffinityGroups[0].savingThrowModifierType=FeatureDefinitionSavingThrowAffinity.ModifierType.RemoveDice;
        Check(!plan.NativeStateValid(),"Same-count same-die provider modifier mutation invalidates accepted native plan");
        Setup();hero.Features.Add(Feature("Dexterity",1));plan=Preview();hero.Features.Clear();Check(!plan.NativeStateValid(),"Removed save bonus provider invalidates the complete accepted plan");
        Setup();plan=Preview();hero.Attributes["Dexterity"].CurrentValue=18;Check(!plan.NativeStateValid(),"Changed native save base bonus invalidates accepted plan before handoff");
        Setup();plan=Preview();var foreign=new Harmony("Foreign.AfterAccept");var hook=new HarmonyMethod(typeof(SaveTests).GetMethod("ForeignPrefix",BindingFlags.Static|BindingFlags.NonPublic));foreignCalls=0;foreign.Patch(method,prefix:hook);
        try{Check(!plan.NativeStateValid()&&foreignCalls==0&&TA.DeterministicRandom.Calls==0,"New foreign save prefix while prompt is open rejects repreview before any external invocation");}
        finally{foreign.Unpatch(method,hook.method);}
        Setup();for(int i=0;i<17;i++)hero.Features.Add(Feature("Dexterity",256));bool failed=false;try{Preview();}catch(InvalidOperationException){failed=true;}
        Check(failed&&!NativeSavePlanner.Previewing&&!NativeSavePlanner.AllowsStaticBonus&&TA.DeterministicRandom.Calls==0,"Unbounded bonus preview is stopped at the aggregate face limit and releases all guards");
    }
    static void OriginalFormAndController()
    {
        Setup();hero.Features.Add(Feature("Dexterity",1));
        var sentinel=new FeatureDefinition{Name="HeroScratch"};hero.FeaturesToBrowse.Add(sentinel);hero.FeaturesOrigin[sentinel]=new RuleDefinitions.FeatureOrigin();var original=hero.FeaturesToBrowse;var iterator=original.GetEnumerator();iterator.MoveNext();
        var shape=new RulesetCharacterMonster{IsSubstitute=true,OriginalFormCharacter=hero,Side=RuleDefinitions.Side.Ally};shape.Attributes["Dexterity"]=new RulesetAttribute{CurrentValue=10};args[2]=shape;
        Check(Preview().Groups.Count==2&&ReferenceEquals(original,hero.FeaturesToBrowse)&&hero.FeaturesToBrowse.Single()==sentinel&&!iterator.MoveNext(),"Transformed character save isolates original hero scratch and active enumerator");
        Setup();shape=new RulesetCharacterMonster{Side=RuleDefinitions.Side.Ally};shape.Attributes["Dexterity"]=new RulesetAttribute{CurrentValue=10};var provider=Feature("Dexterity",1);provider.UseControllerSavingThrows=true;shape.Features.Add(provider);args[2]=shape;
        Check(Preview().Groups.Count==0&&TA.DeterministicRandom.Calls==0&&!NativeSavePlanner.Previewing,"Controller-based monster save safely stays native without previewing an untracked controller");
    }
    static void Main()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);var harmony=new Harmony("ManualDiceRoller.SavePreviewFixture");NativeSavePlanner.Install(harmony);
        try{BasicAndMultiForm();AutomaticAndOverride();RestrictedFormsAndFailure();CasterOpaqueAndDepth();ForeignPatchedCalculations();ScratchIdentity();ForeignHelpersAndEffect();PendingStateChanges();OriginalFormAndController();}
        finally{harmony.UnpatchAll(harmony.Id);}
        Console.WriteLine("PASS "+checks+" production save preview assertions over pristine native TryRollSavingThrow, affinity/provider calculations, actor RollSavingThrow/dice IL. Engine scene and UI are fixture boundaries.");
    }
}
