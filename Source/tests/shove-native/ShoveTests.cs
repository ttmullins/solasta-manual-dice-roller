using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ManualDiceRoller;

class ShoveTests
{
    static int checks, comparisons;
    static readonly System.Reflection.MethodInfo native=AccessTools.Method(typeof(CharacterActionShove),"ResolveRolls");
    static void Check(bool value,string message) {checks++;if(!value)throw new Exception(message);}
    static void Reset(){RuntimeRolls.Reset();Plugin.Enabled=true;Plugin.Multiplayer=false;Plugin.Settings.EnemyRolls=true;Plugin.Logs.Clear();RollWindow.Inputs.Clear();RollWindow.Prompts.Clear();RollWindow.BeforeAccept=null;RollWindow.CancelNext=false;TA.DeterministicRandom.Calls=0;TA.DeterministicRandom.Values.Clear();ServiceRepository.Settings=new TestSettings();}
    static GameLocationCharacter Hero(int strength,int dexterity,string skill="Athletics") {
        var actor=new RulesetCharacterHero {Name="Contestant",Side=RuleDefinitions.Side.Ally};actor.Attributes["Strength"]=new RulesetAttribute{CurrentValue=strength};actor.Attributes["Dexterity"]=new RulesetAttribute{CurrentValue=dexterity};actor.Attributes["ProficiencyBonus"]=new RulesetAttribute{CurrentValue=3};actor.SkillProficiencies.Add(skill);return new GameLocationCharacter {RulesetCharacter=actor};
    }
    static FeatureDefinitionAbilityCheckAffinity Affinity(string ability,string skill,RuleDefinitions.CharacterAbilityCheckAffinity affinity,int count=0) {
        var feature=new FeatureDefinitionAbilityCheckAffinity {Name=ability+skill+affinity};feature.AffinityGroups.Add(new FeatureDefinitionAbilityCheckAffinity.AbilityCheckAffinityGroup {abilityScoreName=ability,proficiencyName=skill,affinity=affinity,abilityCheckModifierDiceNumber=count,abilityCheckModifierDieType=count==0?RuleDefinitions.DieType.D1:RuleDefinitions.DieType.D4});return feature;
    }
    static void Drain(IEnumerator root) {
        if(root==null)return;var stack=new Stack<IEnumerator>();stack.Push(root);int loops=0;while(stack.Count>0){if(++loops>3000)throw new Exception("Shove request did not finish");var current=stack.Peek();if(!current.MoveNext()){stack.Pop();(current as IDisposable)?.Dispose();continue;}if(current.Current is IEnumerator)stack.Push((IEnumerator)current.Current);}
    }
    static object Prepare(GameLocationCharacter actor,GameLocationCharacter opponent,string site="shove-native") {
        var owner=new object();Drain(RuntimeRolls.PrepareNativeRoll(owner,site,native,new object[]{actor,opponent,ActionDefinitions.Id.Shove}));return owner;
    }
    static bool Run(object owner,GameLocationCharacter actor,GameLocationCharacter opponent,string site="shove-native") {
        RuntimeRolls.EnterNative(owner,site);try{return CharacterActionShove.ResolveRolls(actor,opponent,ActionDefinitions.Id.Shove);}finally{RuntimeRolls.ExitNative(owner,site);}
    }
    static void Setup(GameLocationCharacter actor,GameLocationCharacter opponent,bool defendDexterity,int actorAdv,int opponentAdv) {
        string defenseAbility=defendDexterity?"Dexterity":"Strength",defenseSkill=defendDexterity?"Acrobatics":"Athletics";
        actor.RulesetCharacter.Features.Add(Affinity("Strength","Athletics",actorAdv==1?RuleDefinitions.CharacterAbilityCheckAffinity.Advantage:actorAdv==-1?RuleDefinitions.CharacterAbilityCheckAffinity.Disadvantage:RuleDefinitions.CharacterAbilityCheckAffinity.None));
        opponent.RulesetCharacter.Features.Add(Affinity(defenseAbility,defenseSkill,opponentAdv==1?RuleDefinitions.CharacterAbilityCheckAffinity.Advantage:opponentAdv==-1?RuleDefinitions.CharacterAbilityCheckAffinity.Disadvantage:RuleDefinitions.CharacterAbilityCheckAffinity.None));
    }
    static void Matrix() {
        foreach(bool dex in new[]{false,true})foreach(int aa in new[]{-1,0,1})foreach(int oa in new[]{-1,0,1}) {
            Reset();var actor=Hero(16,10);var opponent=dex?Hero(8,20,"Acrobatics"):Hero(20,8);Setup(actor,opponent,dex,aa,oa);var raw=new List<int>{17};if(aa!=0)raw.Add(9);raw.Add(5);if(oa!=0)raw.Add(12);
            var expectedEvents=new List<string>();actor.RulesetCharacter.ContestCheckRolled=(a,o,ab,sk,oab,osk,outcome,total,face,ototal,oface,adv,mod,oadv,omod)=>expectedEvents.Add(osk+":"+total+":"+face+":"+ototal+":"+oface+":"+outcome);
            Plugin.Enabled=false;TA.DeterministicRandom.Values=new Queue<int>(raw.Select(v=>v-1));bool expected=CharacterActionShove.ResolveRolls(actor,opponent,ActionDefinitions.Id.Shove);int draws=TA.DeterministicRandom.Calls;
            Reset();actor=Hero(16,10);opponent=dex?Hero(8,20,"Acrobatics"):Hero(20,8);Setup(actor,opponent,dex,aa,oa);var events=new List<string>();actor.RulesetCharacter.ContestCheckRolled=(a,o,ab,sk,oab,osk,outcome,total,face,ototal,oface,adv,mod,oadv,omod)=>events.Add(osk+":"+total+":"+face+":"+ototal+":"+oface+":"+outcome);
            var first=actor.RulesetCharacter.FeaturesToBrowse;var second=opponent.RulesetCharacter.FeaturesToBrowse;first.Add(new FeatureDefinition{Name="first"});second.Add(new FeatureDefinition{Name="second"});var one=first.GetEnumerator();var two=second.GetEnumerator();one.MoveNext();two.MoveNext();
            RollWindow.Inputs=new Queue<int>(raw);object owner=Prepare(actor,opponent);
            Check(RollWindow.Prompts.Count==2,"Original shove wrapper was bypassed: "+string.Join(";",Plugin.Logs)+" params="+string.Join(",",native.GetParameters().Select(p=>p.Name))+" guard="+NativeAbilityPlanner.ForeignPatchGuard(new NativeInvocation(owner,"debug",native,new object[]{actor,opponent,ActionDefinitions.Id.Shove},0),actor)+" actor="+NativeAbilityPlanner.ForeignActorGuard(actor.RulesetCharacter));
            Check(RollWindow.Prompts[1].Detail.Contains(dex?"Acrobatics":"Athletics")&&!RollWindow.Prompts[1].Detail.Contains(dex?"Athletics":"Acrobatics"),"Defending shove prompt displayed the native raw binding instead of the selected defense skill");
            Check(TA.DeterministicRandom.Calls==0&&actor.RulesetCharacter.Interruptions==0&&opponent.RulesetCharacter.Interruptions==0&&events.Count==0,"Shove preview rolled/notified/spent a condition");
            Check(ReferenceEquals(first,actor.RulesetCharacter.FeaturesToBrowse)&&ReferenceEquals(second,opponent.RulesetCharacter.FeaturesToBrowse)&&!one.MoveNext()&&!two.MoveNext(),"Shove preview invalidated native scratch/active enumerators");
            bool actual=Run(owner,actor,opponent);Check(actual==expected&&events.SequenceEqual(expectedEvents),"Original skill choice, contest totals or notifications changed dex="+dex+" aa="+aa+" oa="+oa+" expected="+expected+" actual="+actual+" expectedEvent="+string.Join(";",expectedEvents)+" actualEvent="+string.Join(";",events)+" RNG="+TA.DeterministicRandom.Calls+" logs="+string.Join(";",Plugin.Logs));
            Check(TA.DeterministicRandom.Calls==0&&RollWindow.Prompts.Sum(p=>p.DieFaces.Length)==draws,"Native contest did not consume exact raw faces");
            Check(actor.RulesetCharacter.Interruptions==1&&opponent.RulesetCharacter.Interruptions==1,"Native contest condition interruptions lost or doubled");comparisons++;
        }
    }
    static void TraitsAndChanges() {
        Reset();var actor=Hero(16,10);var opponent=Hero(10,16,"Acrobatics");actor.RulesetCharacter.Features.Add(new FeatureDefinitionDieRollModifier {Name="Lucky",ValidityContext=RuleDefinitions.RollContext.AbilityCheck,MinRerollValue=1,RerollCount=1});RollWindow.Inputs=new Queue<int>(new[]{1,17,5});var owner=Prepare(actor,opponent);
        Check(RollWindow.Prompts.Count==3&&Run(owner,actor,opponent)&&TA.DeterministicRandom.Calls==0,"Shove Lucky replacement was automatic");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");actor.RulesetCharacter.Features.Add(new FeatureDefinitionActionAffinity {ActionExecutionModifiers=new List<ActionDefinitions.ActionExecutionModifier>{new ActionDefinitions.ActionExecutionModifier{actionId=ActionDefinitions.Id.Shove,advantageType=RuleDefinitions.AdvantageType.Advantage}}});RollWindow.Inputs=new Queue<int>(new[]{17,3,5});owner=Prepare(actor,opponent);
        Check(RollWindow.Prompts.Count==2&&RollWindow.Prompts[0].DieFaces.Length==2&&Run(owner,actor,opponent)&&TA.DeterministicRandom.Calls==0,"Native action-performance shove advantage changed");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");var defenderTrait=new FeatureDefinitionDieRollModifier {Name="Native defender skill filter",ValidityContext=RuleDefinitions.RollContext.AbilityCheck,RequireProficiency=true,MinRollValue=12};defenderTrait.ValidSkills.Add("Athletics");opponent.RulesetCharacter.Features.Add(defenderTrait);RollWindow.Inputs=new Queue<int>(new[]{17,5});owner=Prepare(actor,opponent);var nativeEvents=new List<int>();actor.RulesetCharacter.ContestCheckRolled=(a,o,ab,sk,oab,osk,outcome,total,face,ototal,oface,adv,mod,oadv,omod)=>nativeEvents.Add(oface);
        Check(RollWindow.Prompts[1].Detail.Contains("Acrobatics")&&!RollWindow.Prompts[1].Detail.Contains("Athletics")&&Run(owner,actor,opponent)&&nativeEvents.SequenceEqual(new[]{12})&&TA.DeterministicRandom.Calls==0,"Native defender proficiency/acting-skill trait binding or selected-skill label changed");
        Reset();actor=Hero(16,10);var nonParty=new RulesetCharacter {Name="Opponent",Side=RuleDefinitions.Side.Enemy};nonParty.Attributes["Strength"]=new RulesetAttribute {CurrentValue=10};nonParty.Attributes["Dexterity"]=new RulesetAttribute {CurrentValue=16};opponent=new GameLocationCharacter {RulesetCharacter=nonParty};Plugin.Settings.EnemyRolls=false;RollWindow.Inputs=new Queue<int>(new[]{17});owner=Prepare(actor,opponent);TA.DeterministicRandom.Values=new Queue<int>(new[]{4});
        Check(RollWindow.Prompts.Count==1&&Run(owner,actor,opponent)&&TA.DeterministicRandom.Calls==1,"Enemy-rolls-off did not retain original automatic opposing draw");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");owner=Prepare(actor,opponent);opponent.RulesetCharacter.Attributes["Strength"].CurrentValue=24;
        TA.DeterministicRandom.Values=new Queue<int>(new[]{9,12});Run(owner,actor,opponent);Check(TA.DeterministicRandom.Calls==2,"Changed defending skill consumed a stale prepared contest");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");actor.RulesetCharacter.Features.Add(Affinity("Strength","Athletics",RuleDefinitions.CharacterAbilityCheckAffinity.None,1));owner=Prepare(actor,opponent);
        Check(RollWindow.Prompts.Count==0&&TA.DeterministicRandom.Calls==0,"Random affinity preview advanced RNG or prompted invalid skill plan");
        TA.DeterministicRandom.Values=new Queue<int>(new[]{2,16,4});Run(owner,actor,opponent);Check(TA.DeterministicRandom.Calls==3,"Unsupported random shove did not execute original automatic bonus plus contest");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");owner=Prepare(actor,opponent);opponent.RulesetCharacter.IsIncapacitated=true;TA.DeterministicRandom.Values=new Queue<int>(new[]{9,12});Run(owner,actor,opponent);Check(TA.DeterministicRandom.Calls==2,"Changed incapacitation consumed accepted contest faces");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");owner=Prepare(actor,opponent);opponent.RulesetCharacter.Side=RuleDefinitions.Side.Enemy;TA.DeterministicRandom.Values=new Queue<int>(new[]{9,12});Run(owner,actor,opponent);Check(TA.DeterministicRandom.Calls==2,"Changed faction consumed accepted contest faces");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");RollWindow.CancelNext=true;TA.DeterministicRandom.Values=new Queue<int>(new[]{9,12});owner=Prepare(actor,opponent);Run(owner,actor,opponent);Check(TA.DeterministicRandom.Calls==2,"Auto choice did not cancel the entire contest");
        Reset();actor=Hero(16,10);opponent=Hero(10,16,"Acrobatics");var patch=new Harmony("foreign.shove.fixture");patch.Patch(native,prefix:new HarmonyMethod(typeof(ShoveTests),nameof(ForeignPrefix)));
        try{owner=Prepare(actor,opponent);Check(RollWindow.Prompts.Count==0&&TA.DeterministicRandom.Calls==0,"Foreign original shove provider previewed");}finally{patch.UnpatchAll(patch.Id);}
    }
    static void ForeignPrefix() { }
    static void Main(){HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);RuntimeRolls.Install(new Harmony("ManualDiceRoller.Standalone"));Matrix();TraitsAndChanges();Console.WriteLine("PASS "+checks+" actual-native shove assertions; "+comparisons+" pristine original wrapper/contest comparisons.");}
}
