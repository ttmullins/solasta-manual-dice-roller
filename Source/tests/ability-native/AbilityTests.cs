using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;

static class AbilityTests
{
    static int checks, comparisons;
    static Harmony harmony;
    sealed class Flat : IEnumerator, IDisposable
    {
        readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
        public Flat(IEnumerator root) { if(root!=null) stack.Push(root); }
        public object Current {get;private set;}
        public bool MoveNext() { while(stack.Count>0) { var item=stack.Peek();if(!item.MoveNext()){stack.Pop();(item as IDisposable)?.Dispose();continue;}if(item.Current is IEnumerator){stack.Push((IEnumerator)item.Current);continue;}Current=item.Current;return true;}return false; }
        public void Reset(){throw new NotSupportedException();}public void Dispose(){while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();}
    }
    static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    static void Reset()
    {
        RuntimeRolls.Reset();Plugin.Enabled=true;Plugin.Multiplayer=false;Plugin.Logs.Clear();Plugin.Settings.EnemyRolls=true;
        RollWindow.Inputs.Clear();RollWindow.Prompts.Clear();RollWindow.BeforeAccept=null;RollWindow.CancelNext=false;RollWindow.MutateAfterAccept=false;
        TA.DeterministicRandom.Calls=0;TA.DeterministicRandom.Values.Clear();ServiceRepository.Settings=new TestSettings();ServiceRepository.Rules=new TestRules();
    }
    static RulesetCharacterHero Hero()
    {
        var actor=new RulesetCharacterHero{Name="Ellie",Side=RuleDefinitions.Side.Ally};actor.Attributes["Wisdom"]=new RulesetAttribute{CurrentValue=16};actor.Attributes["ProficiencyBonus"]=new RulesetAttribute{CurrentValue=3};actor.SkillProficiencies.Add("Insight");return actor;
    }
    static FeatureDefinitionAbilityCheckAffinity Feature(string name,int count=0,RuleDefinitions.DieType die=RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity affinity=RuleDefinitions.CharacterAbilityCheckAffinity.None,RuleDefinitions.AbilityCheckContext context=RuleDefinitions.AbilityCheckContext.None,RuleDefinitions.LightingContext light=RuleDefinitions.LightingContext.Irrelevant,bool subtract=false)
    {
        var feature=new FeatureDefinitionAbilityCheckAffinity{Name=name};feature.AffinityGroups.Add(new FeatureDefinitionAbilityCheckAffinity.AbilityCheckAffinityGroup{abilityScoreName="Wisdom",proficiencyName="Insight",affinity=affinity,abilityCheckModifierDiceNumber=count,abilityCheckModifierDieType=die,abilityCheckGroupOperation=subtract?RuleDefinitions.AbilityCheckGroupOperation.SubstractDie:RuleDefinitions.AbilityCheckGroupOperation.AddDie,abilityCheckContext=context,lightingContext=light});return feature;
    }
    static MethodInfo GL(bool expanded=false){return typeof(GameLocationCharacter).GetMethods().Single(m=>m.Name=="RollAbilityCheck"&&m.GetParameters().Length==(expanded?16:12));}
    static MethodInfo Basic(bool expanded=false){return typeof(RulesetCharacter).GetMethods().Single(m=>m.Name=="RollBasicAbilityCheck"&&m.GetParameters().Length==(expanded?8:6));}
    static object[] Args(GameLocationCharacter actor,ActionModifier modifier,bool expanded=false,bool passive=false,RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None)
    {return expanded?new object[]{actor,"Wisdom","Insight",15,advantage,modifier,passive,-1,0,0,0,0,RuleDefinitions.RollOutcome.Neutral,0,true,true,true}:new object[]{actor,"Wisdom","Insight",15,advantage,modifier,passive,-1,RuleDefinitions.RollOutcome.Neutral,0,true,true,true};}
    static IEnumerator Prepare(object owner,MethodInfo method,object[] args){return RuntimeRolls.PrepareNativeRoll(owner,"ability-native",method,args);}
    static void Drain(IEnumerator wait){using(var flat=new Flat(wait)){int frames=0;while(flat.MoveNext())if(++frames>3000)throw new Exception("Ability prompt did not complete");}}
    static object Invoke(MethodInfo method,object[] args){var values=args.Skip(1).ToArray();var result=method.Invoke(args[0],values);for(int i=0;i<values.Length;i++)args[i+1]=values[i];return result;}
    static object Run(object owner,MethodInfo method,object[] args){RuntimeRolls.EnterNative(owner,"ability-native");try{return Invoke(method,args);}finally{RuntimeRolls.ExitNative(owner,"ability-native");}}
    static void LocationMatrix()
    {
        foreach(bool expanded in new[]{false,true})foreach(bool heavy in new[]{false,true})foreach(LocationDefinitions.LightingState light in Enum.GetValues(typeof(LocationDefinitions.LightingState)))
        {
            Reset();var actor=Hero();actor.HeavyArmor=heavy;actor.ExpertiseProficiencies.Add("Insight");
            actor.Features.Add(Feature("Guidance",2,RuleDefinitions.DieType.D4));
            actor.Features.Add(Feature("LightAdvantage",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Advantage,RuleDefinitions.AbilityCheckContext.None,RuleDefinitions.LightingContext.BrightLight));
            actor.Features.Add(Feature("ArmorDisadvantage",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Disadvantage,RuleDefinitions.AbilityCheckContext.NotWearingHeavyArmor));
            actor.Features.Add(Feature("Penalty",1,RuleDefinitions.DieType.D6,subtract:true));
            var location=new GameLocationCharacter{RulesetCharacter=actor,Lighting=light};var modifier=new ActionModifier{AbilityCheckModifier=2};var args=Args(location,modifier,expanded);var method=GL(expanded);var raw=new[]{3,4,2,17,7};
            Plugin.Enabled=false;TA.DeterministicRandom.Values=new Queue<int>(raw.Select(v=>v-1));int expected=(int)Invoke(method,args);var expectedTrends=modifier.AbilityCheckModifierTrends.Select(t=>t.value).ToArray();var expectedAdv=RuleDefinitions.ComputeAdvantage(modifier.AbilityCheckAdvantageTrends);int expectedRng=TA.DeterministicRandom.Calls;
            Reset();actor=Hero();actor.HeavyArmor=heavy;actor.ExpertiseProficiencies.Add("Insight");
            actor.Features.Add(Feature("Guidance",2,RuleDefinitions.DieType.D4));actor.Features.Add(Feature("LightAdvantage",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Advantage,RuleDefinitions.AbilityCheckContext.None,RuleDefinitions.LightingContext.BrightLight));actor.Features.Add(Feature("ArmorDisadvantage",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Disadvantage,RuleDefinitions.AbilityCheckContext.NotWearingHeavyArmor));actor.Features.Add(Feature("Penalty",1,RuleDefinitions.DieType.D6,subtract:true));
            location=new GameLocationCharacter{RulesetCharacter=actor,Lighting=light};modifier=new ActionModifier{AbilityCheckModifier=2};args=Args(location,modifier,expanded);var owner=new object();
            var scratch=actor.FeaturesToBrowse;var origins=actor.FeaturesOrigin;var conditions=actor.AllConditionsForEnumeration;scratch.Add(new FeatureDefinition{Name="sentinel"});var cursor=scratch.GetEnumerator();cursor.MoveNext();
            RollWindow.Inputs=new Queue<int>(raw);var wait=Prepare(owner,method,args);
            Check(wait!=null,"Native GL wrapper was silently bypassed: "+string.Join(";",Plugin.Logs));
            Check(TA.DeterministicRandom.Calls==0&&actor.Notifications==0&&actor.Interruptions==0,"Preview executed native RNG/notifications/interruptions");
            Check(ReferenceEquals(scratch,actor.FeaturesToBrowse)&&ReferenceEquals(origins,actor.FeaturesOrigin)&&ReferenceEquals(conditions,actor.AllConditionsForEnumeration)&&!cursor.MoveNext(),"Preview changed native scratch references or enumeration version");
            Check(modifier.AbilityCheckModifier==2&&modifier.AbilityCheckModifierTrends.Count==0&&modifier.AbilityCheckAdvantageTrends.Count==0,"Preview mutated caller's modifier");
            Drain(wait);int actual=(int)Run(owner,method,args);
            Check(actual==expected&&modifier.AbilityCheckModifierTrends.Select(t=>t.value).SequenceEqual(expectedTrends)&&RuleDefinitions.ComputeAdvantage(modifier.AbilityCheckAdvantageTrends)==expectedAdv,"Native ability bonus/advantage result differs from pristine baseline");
            Check(TA.DeterministicRandom.Calls==0&&RollWindow.Prompts.Sum(p=>p.DieFaces.Length)==expectedRng,"Native GL bonus face plan was not consumed exactly once");
            Check(actor.Notifications==1&&actor.Interruptions==1,"Final native callbacks lost or repeated");comparisons++;
        }
        Console.WriteLine("PASS: "+comparisons+" actual location-wrapper comparisons (lighting, armor, expertise, added/subtracted bonuses).");
    }
    static void BasicChecks()
    {
        foreach(bool expanded in new[]{false,true})
        {
            Reset();var actor=Hero();actor.Features.Add(Feature("Adv1",1,RuleDefinitions.DieType.D4,RuleDefinitions.CharacterAbilityCheckAffinity.Advantage));actor.Features.Add(Feature("Adv2",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Advantage));actor.Features.Add(Feature("Dis1",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Disadvantage));
            var method=Basic(expanded);object[] args=expanded?new object[]{actor,6,new List<RuleDefinitions.TrendInfo>{new RuleDefinitions.TrendInfo(6)},"Wisdom","Insight",15,RuleDefinitions.RollOutcome.Neutral,0,true}:new object[]{actor,"Wisdom","Insight",15,RuleDefinitions.RollOutcome.Neutral,0,true};
            var owner=new object();RollWindow.Inputs=new Queue<int>(new[]{3,17,7});Drain(Prepare(owner,method,args));
            Check(RollWindow.Prompts.Count==2&&RollWindow.Prompts[0].DieFaces.SequenceEqual(new[]{4})&&RollWindow.Prompts[1].DieFaces.SequenceEqual(new[]{20,20}),"Basic native mixed advantage requires bonus plus two d20 faces: "+string.Join(";",Plugin.Logs)+" prompts="+string.Join("|",RollWindow.Prompts.Select(p=>string.Join(",",p.DieFaces))));
            Run(owner,method,args);Check(TA.DeterministicRandom.Calls==0,"Basic modifier dice incorrectly bypassed owned static scope");Check((RuleDefinitions.RollOutcome)args[expanded?6:4]==RuleDefinitions.RollOutcome.Success&&Convert.ToInt32(args[expanded?7:5])==11,"Basic original accepted bonus + d20 result wrong");
        }
        foreach(var automatic in new[]{RuleDefinitions.CharacterAbilityCheckAffinity.AutomaticFail,RuleDefinitions.CharacterAbilityCheckAffinity.AutomaticSuccess})
        {
            Reset();var actor=Hero();actor.Features.Add(Feature("Automatic",affinity:automatic));var args=new object[]{actor,"Wisdom","Insight",15,RuleDefinitions.RollOutcome.Neutral,0,true};var owner=new object();Check(Prepare(owner,Basic(),args)==null&&TA.DeterministicRandom.Calls==0,"Basic automatic result fabricated a manual dice request");Invoke(Basic(),args);Check((RuleDefinitions.RollOutcome)args[4]==(automatic==RuleDefinitions.CharacterAbilityCheckAffinity.AutomaticFail?RuleDefinitions.RollOutcome.Failure:RuleDefinitions.RollOutcome.Success)&&actor.Notifications==0&&actor.Interruptions==0,"Native automatic Basic branch changed");
        }
    }
    static void PassiveAndForeign()
    {
        Reset();var actor=Hero();var location=new GameLocationCharacter{RulesetCharacter=actor};var args=Args(location,new ActionModifier(),passive:true);Check(Prepare(new object(),GL(),args)==null,"Passive check incorrectly prompts");Invoke(GL(),args);Check(TA.DeterministicRandom.Calls==0&&actor.Notifications==1&&actor.Interruptions==1,"Native passive callbacks changed");
        Reset();var patch=new Harmony("ability-fixture-foreign");patch.Patch(GL(),prefix:new HarmonyMethod(typeof(AbilityTests),nameof(ForeignPrefix)));actor=Hero();location=new GameLocationCharacter{RulesetCharacter=actor};args=Args(location,new ActionModifier());
        try{Check(Prepare(new object(),GL(),args)==null&&TA.DeterministicRandom.Calls==0&&actor.Notifications==0,"Foreign patch preview must be rejected before invoking callbacks");TA.DeterministicRandom.Values.Enqueue(6);Check((int)Invoke(GL(),args)==13&&TA.DeterministicRandom.Calls==1&&actor.Notifications==1,"Foreign patch fallback did not use original native execution");}finally{patch.UnpatchAll(patch.Id);}
    }
    static void BardicAndBonusBranches()
    {
        Reset();var actor=Hero();var bardic=Feature("StoredNegativeBardic");bardic.SubstractBardicDieRoll=true;actor.Features.Add(bardic);actor.Conditions.Add(new RulesetCondition{BardicInspirationDie=RuleDefinitions.DieType.D6,BardicInspirationRoll=4,ConditionDefinition=new ConditionDefinition{ConditionType=RuleDefinitions.ConditionType.Detrimental,Features=new List<FeatureDefinition>{bardic}}});
        var args=Args(new GameLocationCharacter{RulesetCharacter=actor},new ActionModifier());var owner=new object();RollWindow.Inputs.Enqueue(17);Drain(Prepare(owner,GL(),args));Check(RollWindow.Prompts.Count==1&&TA.DeterministicRandom.Calls==0&&actor.Notifications==0,"Stored Bardic penalty was rolled or notified during preview");int actual=(int)Run(owner,GL(),args);Check(actual==19&&actor.Conditions[0].BardicInspirationRoll==4&&actor.Notifications==1,"Stored Bardic penalty native value/state changed");
        Reset();actor=Hero();actor.SkillProficiencies.Clear();actor.Features.Add(Feature("HalfProficiency",affinity:RuleDefinitions.CharacterAbilityCheckAffinity.HalfProficiencyWhenNotProficient));args=Args(new GameLocationCharacter{RulesetCharacter=actor},new ActionModifier());owner=new object();RollWindow.Inputs.Enqueue(17);Drain(Prepare(owner,GL(),args));Check((int)Run(owner,GL(),args)==22&&TA.DeterministicRandom.Calls==0,"Native odd proficiency half bonus was not preserved");
        Reset();actor=Hero();actor.Features.Add(Feature("Constant",3,RuleDefinitions.DieType.D1));args=Args(new GameLocationCharacter{RulesetCharacter=actor},new ActionModifier());owner=new object();RollWindow.Inputs.Enqueue(17);Drain(Prepare(owner,GL(),args));Check(RollWindow.Prompts.Count==1&&(int)Run(owner,GL(),args)==26&&TA.DeterministicRandom.Calls==0,"Constant bonus generated an unnecessary face request or consumed d20");
    }
    static void ChangedStateAndNestedCallbacks()
    {
        for(int kind=0;kind<7;kind++)
        {
            Reset();var actor=Hero();var bonus=Feature("Guidance",1,RuleDefinitions.DieType.D4);actor.Features.Add(bonus);actor.Features.Add(Feature("ArmorAdvantage",0,RuleDefinitions.DieType.D1,RuleDefinitions.CharacterAbilityCheckAffinity.Advantage,RuleDefinitions.AbilityCheckContext.NotWearingHeavyArmor));var location=new GameLocationCharacter{RulesetCharacter=actor};var modifier=new ActionModifier();var args=Args(location,modifier);var owner=new object();RollWindow.Inputs=new Queue<int>(new[]{4,17,7});Drain(Prepare(owner,GL(),args));
            if(kind==0) actor.HeavyArmor=true;
            if(kind==1) actor.Attributes["Wisdom"].CurrentValue=18;
            if(kind==2) actor.SkillProficiencies.Clear();
            if(kind==3) ServiceRepository.Settings.AbilityCheckAllyModifier=2;
            if(kind==4) actor.Features[0]=Feature("OtherGuidance",1,RuleDefinitions.DieType.D4);
            if(kind==5) location.Lighting=LocationDefinitions.LightingState.Bright;
            if(kind==6) bonus.AffinityGroups[0].abilityCheckGroupOperation=RuleDefinitions.AbilityCheckGroupOperation.SubstractDie;
            TA.DeterministicRandom.Values=new Queue<int>(new[]{0,4,7});int result=(int)Run(owner,GL(),args);
            Check(TA.DeterministicRandom.Calls==(kind==0?2:3),"Changed native ability state must invalidate entire bonus+d20 plan before consuming any accepted face, kind="+kind+" result="+result);
            Check(actor.Notifications==1&&actor.Interruptions==1&&!RollWindow.Visible,"Invalidated plan duplicated callbacks or left modal");
        }
        Reset();var hero=Hero();var callArgs=Args(new GameLocationCharacter{RulesetCharacter=hero},new ActionModifier());var callOwner=new object();RollWindow.Inputs.Enqueue(17);Drain(Prepare(callOwner,GL(),callArgs));int callback=0;hero.OnNotification=()=>{int first,second;callback=RuleDefinitions.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None,out first,out second);};TA.DeterministicRandom.Values.Enqueue(7);
        Check((int)Run(callOwner,GL(),callArgs)==23&&callback==8&&TA.DeterministicRandom.Calls==1&&RollWindow.Prompts.Count==1,"Nested native notification raw draw stole face or generated modal");
    }
    public static void ForeignPrefix(){ }
    static void Main(){HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);harmony=new Harmony("ability-native-production");RuntimeRolls.Install(harmony);LocationMatrix();BasicChecks();PassiveAndForeign();BardicAndBonusBranches();ChangedStateAndNestedCallbacks();Console.WriteLine("PASS: "+checks+" actual-native ability assertions.");}
}
