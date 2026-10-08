using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

class NetworkNativeTests
{
    static int passed;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;}
    static void Activate(Game game,int noise){UnityEngine.Random.InitState(noise);for(int i=0;i<noise%7;i++)UnityEngine.Random.Range(0,99);ServiceRepository.Current=new TestGameService{Game=game};}
    static int[] Die(RuleDefinitions.DieType die,RuleDefinitions.AdvantageType advantage,float karma=0){int a,b;int v=RuleDefinitions.RollDie(die,advantage,out a,out b,karma);return new[]{v,a,b};}
    static void Run(IEnumerator e){int count=0;while(e.MoveNext()){if(++count>16)throw new Exception("Unexpected replicated rest wait");}}
    static void Main()
    {
        foreach(var die in new[]{RuleDefinitions.DieType.D4,RuleDefinitions.DieType.D8,RuleDefinitions.DieType.D20})
        foreach(var adv in new[]{RuleDefinitions.AdvantageType.None,RuleDefinitions.AdvantageType.Advantage,RuleDefinitions.AdvantageType.Disadvantage})
        foreach(var karma in new[]{0f,.6f,-.6f})
        {
            var first=new Game(91827);var second=new Game(91827);
            Activate(first,31);var a=Die(die,adv,karma);var firstState=first.RandomState;
            Activate(second,91);var b=Die(die,adv,karma);
            Check(a.SequenceEqual(b),"Native peer raw faces diverged after different local Unity UI draws");
            Check(firstState.word==second.RandomState.word,"Native per-game state restore/record diverged");
            int count=adv==RuleDefinitions.AdvantageType.None?1:2;UnityEngine.Random.Calls=0;
            Activate(first,2);UnityEngine.Random.Calls=0;Die(die,adv,karma);
            Check(UnityEngine.Random.Calls==count,"MP burn must preserve one native draw per actual face, including karma");
            Activate(second,83);Die(die,adv,karma);
            Check(first.RandomState.word==second.RandomState.word,"MP burn advanced peer game states differently");
            Activate(first,201);a=Die(die,RuleDefinitions.AdvantageType.None);
            Activate(second,314);b=Die(die,RuleDefinitions.AdvantageType.None);
            Check(a.SequenceEqual(b),"Next automatic result diverged after coordinated native burns");
        }
        var p1=new Game(99);var p2=new Game(99);
        Activate(p1,23);Die(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.Advantage);
        Check(p1.RandomState.word!=p2.RandomState.word,"One-peer manual skip must be detectable as divergent native state");
        Activate(p1,1);var start=p1.RandomState;UnityEngine.Random.Calls=0;var fixedDie=Die(RuleDefinitions.DieType.D1,RuleDefinitions.AdvantageType.Advantage);
        Check(fixedDie.SequenceEqual(new[]{1,1,1})&&UnityEngine.Random.Calls==0&&p1.RandomState.word==start.word,"Fixed D1 must consume no native RNG");
        TA.DeterministicRandom.LockFlags.Add(TA.DeterministicRandom.DeterministicLockFlag.Cursor);
        Activate(p1,10);start=p1.RandomState;var outside=Die(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);
        Check(p1.RandomState.word==start.word,"Native cursor lock unexpectedly changed deterministic game state");
        Activate(p2,500);var outside2=Die(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);
        Check(!outside.SequenceEqual(outside2),"Boundary test did not demonstrate global RNG under lock");
        TA.DeterministicRandom.LockFlags.Clear();
        var games=new[]{new Game(821),new Game(821)};var heroes=new[]{new RulesetCharacterHero{Guid=42},new RulesetCharacterHero{Guid=42}};
        var managers=new[]{new NetworkingManager(),new NetworkingManager()};
        for(int i=0;i<2;i++){Activate(games[i],i+39);RulesetEntity.Entities[42]=heroes[i];Run(managers[i].InvokeRest(42,i==1));}
        Check(heroes.All(h=>h.Count==1),"Native RollHitDieAsync must execute on every peer, not just the initiating UI");
        Check(heroes[0].HitPoints==heroes[1].HitPoints,"Native replicated rest faces must agree");
        Check(managers[0].CallbackCount==0&&managers[1].CallbackCount==1,"Initiator-local UI callback must remain local while all peers spend the hit die");
        var missing=new NetworkingManager();RulesetEntity.Entities.Clear();Run(missing.InvokeRest(999,true));
        Check(missing.CallbackCount==0,"Missing native hero must neither spend nor execute rest callback");
        Console.WriteLine("PASS network native behavior: "+passed+" assertions; copied pristine raw/Karmic/state/RollHitDieAsync IL; Unity random API is a managed boundary double.");
    }
}
