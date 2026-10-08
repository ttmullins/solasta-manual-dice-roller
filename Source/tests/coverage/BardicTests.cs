using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ManualDiceRoller;

class BardicTests
{
    static int checks;
    static readonly string[] sites={"GameLocationBattleManager/<HandleBardicInspirationForAttack>d__103:RollBardicInspirationDie:0150","GameLocationBattleManager/<HandleFailedSavingThrow>d__104:RollBardicInspirationDie:07e9","GameLocationBattleManager/<HandleFailedAbilityCheck>d__105:RollBardicInspirationDie:0138"};
    static readonly System.Reflection.MethodInfo native=AccessTools.Method(typeof(RulesetCharacter),"RollBardicInspirationDie");
    static void Check(bool condition,string message) { checks++;if(!condition)throw new Exception(message); }
    static void Reset()
    {
        RuntimeRolls.Reset();Plugin.Enabled=true;Plugin.Multiplayer=false;Plugin.DisabledContexts.Clear();Plugin.Logs.Clear();
        RollWindow.Inputs.Clear();RollWindow.Prompts.Clear();RollWindow.BeforeAccept=null;RollWindow.DeferAccept=false;RollWindow.CancelNext=false;
        TA.DeterministicRandom.Calls=0;TA.DeterministicRandom.Values.Clear();
    }
    static void Drain(IEnumerator root)
    {
        if(root==null)return;var stack=new Stack<IEnumerator>();stack.Push(root);int loops=0;
        while(stack.Count>0) {
            if(++loops>3000)throw new Exception("Manual wait did not finish");
            var current=stack.Peek();if(!current.MoveNext()){stack.Pop();(current as IDisposable)?.Dispose();continue;}
            var next=current.Current as IEnumerator;if(next!=null)stack.Push(next);
        }
    }
    static RulesetCharacterHero Actor(out RulesetCondition condition,RuleDefinitions.DieType die=RuleDefinitions.DieType.D6)
    {
        var actor=new RulesetCharacterHero();condition=new RulesetCondition {BardicInspirationDie=die};
        actor.ConditionsByCategory.Add("Effect",new List<RulesetCondition>{condition});return actor;
    }
    static object Prepare(RulesetCharacterHero actor,RulesetCondition condition,string site,bool advantage=false,bool max=false)
    {
        object owner=new object();Drain(RuntimeRolls.PrepareNativeRoll(owner,site,native,new object[]{actor,condition,-3,max,advantage}));return owner;
    }
    static int Run(object owner,string site,RulesetCharacter actor,RulesetCondition condition,bool advantage=false,bool max=false)
    {
        RuntimeRolls.EnterNative(owner,site);try{return actor.RollBardicInspirationDie(condition,-3,max,advantage);}finally{RuntimeRolls.ExitNative(owner,site);}
    }
    static void InputsAndNativeEvents()
    {
        foreach(var site in sites)foreach(var die in new[]{RuleDefinitions.DieType.D6,RuleDefinitions.DieType.D8,RuleDefinitions.DieType.D10,RuleDefinitions.DieType.D12})foreach(bool advantage in new[]{false,true}) {
            Reset();RulesetCondition condition;var actor=Actor(out condition,die);int max=RuleDefinitions.DiceMaxValue[(int)die];
            actor.Features.Add(new FeatureDefinitionDieRollModifier {MinRerollValue=max,RerollCount=9,ValidityContext=(RuleDefinitions.RollContext)short.MaxValue});
            var events=new List<string>();actor.BardicInspirationDieUsed=(a,d,first,second,success,adv)=>events.Add(first+":"+second+":"+success+":"+adv);
            RollWindow.Inputs=new Queue<int>(new[]{2,max});var owner=Prepare(actor,condition,site,advantage);int result=Run(owner,site,actor,condition,advantage);
            Check(result==(advantage?max:2),"Bardic result differs from native selection");Check(TA.DeterministicRandom.Calls==0,"Manual inspiration consumed RNG");
            Check(RollWindow.Prompts.Count==1&&RollWindow.Prompts[0].DieFaces.SequenceEqual(advantage?new[]{max,max}:new[]{max}),"Bardic pair/card count incorrect");
            Check(actor.Interruptions==1&&actor.Removals==1&&!actor.ConditionsByCategory["Effect"].Contains(condition),"Native condition spending changed");
            Check(events.SequenceEqual(new[]{"2:"+(advantage?max:-1)+":"+advantage+":"+advantage}),"Native event faces/success/advantage changed");
        }
    }
    static void FixedAndAutomatic()
    {
        foreach(var site in sites)foreach(bool advantage in new[]{false,true}) {
            Reset();RulesetCondition condition;var actor=Actor(out condition,RuleDefinitions.DieType.D12);int eventFirst=0,eventSecond=0;
            actor.BardicInspirationDieUsed=(a,d,first,second,s,adv)=>{eventFirst=first;eventSecond=second;};var owner=Prepare(actor,condition,site,advantage,true);
            Check(RollWindow.Prompts.Count==0,"Forced maximum offered manual input");Check(Run(owner,site,actor,condition,advantage,true)==12,"Forced maximum changed");
            Check(eventFirst==12&&eventSecond==-1&&TA.DeterministicRandom.Calls==0&&actor.Removals==1,"Native fixed event/RNG/spending changed");
        }
        foreach(var site in sites) {
            Reset();RulesetCondition condition;var actor=Actor(out condition);RollWindow.CancelNext=true;TA.DeterministicRandom.Values.Enqueue(3);
            var owner=Prepare(actor,condition,site);Check(Run(owner,site,actor,condition)==4&&TA.DeterministicRandom.Calls==1,"Auto choice did not preserve native RNG");
        }
    }
    static void RevalidationAndIsolation()
    {
        Reset();RulesetCondition condition;var actor=Actor(out condition);int nested=0;
        actor.BardicInspirationDieUsed=(a,d,first,second,s,adv)=>{int f,b;nested=RuleDefinitions.RollDie(RuleDefinitions.DieType.D6,RuleDefinitions.AdvantageType.None,out f,out b);};
        RollWindow.Inputs.Enqueue(6);TA.DeterministicRandom.Values.Enqueue(2);var owner=Prepare(actor,condition,sites[0]);
        Check(Run(owner,sites[0],actor,condition)==6&&nested==3&&TA.DeterministicRandom.Calls==1,"Native event stole scoped input or altered RNG");
        Reset();actor=Actor(out condition);var pending=condition;RollWindow.BeforeAccept=p=>pending.BardicInspirationDie=RuleDefinitions.DieType.D8;
        RollWindow.Inputs.Enqueue(6);TA.DeterministicRandom.Values.Enqueue(3);owner=Prepare(actor,condition,sites[1]);
        Check(Run(owner,sites[1],actor,condition)==4&&TA.DeterministicRandom.Calls==1,"Changed Bardic die consumed stale input");
        Reset();actor=Actor(out condition);var oldActor=actor;var oldCondition=condition;RollWindow.BeforeAccept=p=>oldActor.ConditionsByCategory["Effect"].Remove(oldCondition);
        TA.DeterministicRandom.Values.Enqueue(4);owner=Prepare(actor,condition,sites[2]);
        Check(Run(owner,sites[2],actor,condition)==5&&TA.DeterministicRandom.Calls==1,"Retired Bardic condition accepted stale input");
        Reset();actor=Actor(out condition);Plugin.DisabledContexts.Add(RuleDefinitions.RollContext.AttackRoll);owner=Prepare(actor,condition,sites[0]);
        Check(RollWindow.Prompts.Count==0,"Attack setting did not gate Bardic attack reaction");
        Reset();actor=Actor(out condition);owner=Prepare(actor,condition,"unregistered-site");Check(RollWindow.Prompts.Count==0,"Unknown Bardic caller was made manual");
        Reset();actor=Actor(out condition);var foreign=new Harmony("foreign.bardic.regression");foreign.Patch(native,prefix:new HarmonyMethod(typeof(BardicTests),nameof(ForeignPrefix)));
        try {owner=Prepare(actor,condition,sites[0]);Check(RollWindow.Prompts.Count==0,"Foreign patched native Bardic provider previewed");}
        finally{foreign.UnpatchAll("foreign.bardic.regression");}
        Reset();actor=Actor(out condition);var raw=AccessTools.Method(typeof(RuleDefinitions),"RollDie");foreign.Patch(raw,prefix:new HarmonyMethod(typeof(BardicTests),nameof(ForeignPrefix)));
        try {owner=Prepare(actor,condition,sites[0]);Check(RollWindow.Prompts.Count==0,"Foreign patched raw Bardic RNG previewed");}
        finally{foreign.UnpatchAll("foreign.bardic.regression");}
        Reset();actor=Actor(out condition);RollWindow.Inputs.Enqueue(6);owner=Prepare(actor,condition,sites[0]);TA.DeterministicRandom.Values.Enqueue(1);
        Check(actor.RollBardicInspirationDie(condition,-3)==2&&TA.DeterministicRandom.Calls==1,"Unregistered sync Bardic caller stole pending input");
        RuntimeRolls.CancelOwner(owner);
        // 0.8.1: an unusable multiplayer route never throws into the game's
        // coroutine; manual dice pause and the game rolls.
        Reset();actor=Actor(out condition);Plugin.Multiplayer=true;bool rejected=false,paused=false;
        try {Prepare(actor,condition,sites[0]);paused=MultiplayerRolls.Failed;} catch(InvalidOperationException){rejected=true;}
        finally {Plugin.Multiplayer=false;RuntimeRolls.Reset();}
        Check(!rejected&&paused&&RollWindow.Prompts.Count==0,"Offline fixture simulated an uninitialized multiplayer handshake");
    }
    static void ForeignPrefix() { }
    static void Main()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);RuntimeRolls.Install(new Harmony("ManualDiceRoller.Standalone"));InputsAndNativeEvents();FixedAndAutomatic();RevalidationAndIsolation();
        Console.WriteLine("PASS "+checks+" pristine native Bardic Inspiration / production adapter checks");
        Console.WriteLine("The native notification and condition use routines run against declared boundary fixture implementations; no live Unity/Photon gameplay.");
    }
}
