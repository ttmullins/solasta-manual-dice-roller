using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ExitGames.Client.Photon;
using Photon.Realtime;
using HarmonyLib;
using ManualDiceRoller;

static class PeerProcess
{
    static int actorId,members,round;
    const int Rounds=18;
    static Gateway gateway;
    static RulesetCharacterHero hero;
    static Game game;
    static IDriver driver;
    static TA.Coroutine native;
    // Deferred mode: the production deferred-roll pattern (a skipped void
    // original replayed in its own runner coroutine while every other native
    // coroutine is held) through the actual protocol, broker and scheduler.
    static bool deferredScenario,deferredPending,deferredReplaying;
    static TA.Coroutine held;static int heldTicks,heldTicksAtPending;static bool heldAdvancedWhilePending;
    static bool autoScenario;
    // Extended mode: 0.8 plan features through the actual protocol: a group
    // asked only after a success, the game master's dice (the room host) in
    // the same step as a hero's, and entered dice applied before the original
    // resumes (attack bonus dice).
    static bool extendedScenario;static int sharedBonus;
    // 0.8.3 lockstep: the game advances in ticks, one per fixed step while
    // NetworkTick allows it (the production HoldTicksPostfix decides), and
    // each peer runs a random number of fixed steps per rendered frame.
    // Every shared roll must run at the same tick, after the same amount of
    // other gameplay, on every peer. Lockstep modes add other gameplay that
    // draws game dice every tick; "late" runs gameplay before the tick
    // counter in each fixed step; "no-lockstep" ignores the stopped ticks
    // (the 0.8.2 behaviour) and must diverge.
    static ulong tick,nextStart=1;static int otherTicks;static bool lockstepScenario,lateOrder,freeRun,inFixed,frozenFlag;
    static MethodInfo holdTicks;
    internal static ulong ExecTick;internal static int ExecOther;
    static string mode;
    static int promptsBefore,waitingBefore;
    static readonly List<object> reports=new List<object>();
    public static int Anchor(int round){return 0;}
    public static void DeferredOuter(int round){}
    sealed class Ticker:IEnumerator{public object Current{get{return null;}}public bool MoveNext(){heldTicks++;return true;}public void Reset(){}}
    sealed class DeferredDriver:IDriver
    {
        readonly object owner=new object();int stage;
        public bool Executed{get;private set;}public int Result{get;private set;}public int Draws{get;private set;}
        int first,second;
        public object Current{get;private set;}
        public bool MoveNext()
        {
            if(stage==0){stage=1;var wait=RuntimeRolls.PrepareDeferredRoll(owner,"connected/deferred",typeof(PeerProcess).GetMethod("DeferredOuter"),new object[]{round},
                    typeof(PeerProcess).GetMethod("Anchor"),new object[]{round},"Deferred step",null);
                if(wait!=null){Current=wait;return true;}}
            if(stage==1){stage=2;deferredReplaying=true;ExecTick=tick;ExecOther=otherTicks;RuntimeRolls.EnterNative(owner,"connected/deferred");try{
                    UnityEngine.Random.Calls=0;var d=new Driver();Result=NativeValue(d);Draws=UnityEngine.Random.Calls;
                    if(mode=="different-result"&&actorId==2)Result++;
                    RuntimeRolls.ObserveNativeValues(owner,"connected/deferred",null,new object[0]);Executed=true;
                }finally{RuntimeRolls.ExitNative(owner,"connected/deferred");deferredReplaying=false;}
                Current=null;return true;}
            if(RuntimeRolls.NetworkHolding&&ReferenceEquals(RuntimeRolls.NetworkOwner,owner)){Current=null;return true;}
            deferredPending=false;return false;
        }
        public void Reset(){throw new NotSupportedException();}
    }
    interface IDriver:IEnumerator{bool Executed{get;}int Result{get;}int Draws{get;}}
    sealed class Gateway:IMultiplayerRoomGateway
    {
        internal readonly List<object> Outbound=new List<object>();
        readonly object identity=new object();
        internal IOnEventCallback Receiver;
        internal double Time;internal bool Active=true;
        public double Now{get{return Time;}}
        public bool TryGetRoom(out MultiplayerRoom room)
        {room=Active?new MultiplayerRoom{Instance=identity,Name="connected-process-fixture",LocalActor=actorId,MasterActor=1,Members=Enumerable.Range(1,members).ToArray()}:null;return Active;}
        public void Register(IOnEventCallback target){Receiver=target;}
        public void Unregister(IOnEventCallback target){if(ReferenceEquals(target,Receiver))Receiver=null;}
        public bool Send(int target,byte[] bytes){Outbound.Add(new{target,sender=actorId,payload=Convert.ToBase64String(bytes)});return true;}
        public bool LeaveRoom(){Active=false;return true;}
        internal void Receive(int sender,byte[] bytes){var e=new EventData{Code=MultiplayerTransport.EventCode};e.Parameters[254]=sender;e.Parameters[245]=bytes;Receiver.OnEvent(e);}
    }
    sealed class Driver:IDriver
    {
        int stage;public bool Executed{get;private set;}public int Result{get;private set;}public int Draws{get;private set;}internal int First,Second;
        public object Current{get;private set;}
        public bool MoveNext()
        {
            if(stage++==0){Current=RuntimeRolls.PrepareNativeRoll(this,"connected/step",typeof(PeerProcess).GetMethod("Anchor"),new object[]{round});return true;}
            if(stage==2){ExecTick=tick;ExecOther=otherTicks;RuntimeRolls.EnterNative(this,"connected/step");try{
                    UnityEngine.Random.Calls=0;Result=NativeValue(this);Draws=UnityEngine.Random.Calls;
                    if(mode=="different-result"&&actorId==2)Result++;
                    RuntimeRolls.ObserveNativeValues(this,"connected/step",Result,new object[0]);Executed=true;
                }finally{RuntimeRolls.ExitNative(this,"connected/step");}
                Current=null;return true;
            }
            return false;
        }
        public void Reset(){throw new NotSupportedException();}
    }
    static bool Prepare(NativeInvocation call,NativeRollPlan plan)
    {
        if(extendedScenario)return PrepareExtended(plan);
        // Different local plans for the same native roll (same call, same
        // random state): every peer lets the game roll that step.
        if(mode=="different-plan"&&actorId==2&&round%6==0)plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);
        switch(round%6){
            case 0:plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);break;
            case 1:plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,RuleDefinitions.AdvantageType.Advantage);break;
            case 2:plan.AddStaticDice(hero,RuleDefinitions.DieType.D8,5,title:"Sleep pool");break;
            case 3:plan.AddActorDice(hero,RuleDefinitions.DieType.D6,RuleDefinitions.RollContext.MagicDamageValueRoll,2);break;
            case 4:plan.AddStaticDice(hero,RuleDefinitions.DieType.D4,2);plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);break;
            case 5:plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,1);break;
        }
        return true;
    }
    static bool PrepareExtended(NativeRollPlan plan)
    {
        switch(round%6){
            case 0:case 1:
                plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,title:"Search");plan.Describe(10,true,0);
                plan.AddStaticDice(hero,RuleDefinitions.DieType.D6,2,title:"Food found",category:RuleDefinitions.RollContext.AbilityCheck);plan.AskWhen(()=>plan.CheckSucceeds(0));break;
            case 2:
                plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,title:"Ambush check");
                plan.AddMasterDice(hero,"The creatures",RuleDefinitions.DieType.D20,1,title:"Ambush: the creatures",category:RuleDefinitions.RollContext.AbilityCheck);break;
            case 3:{
                plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,1,title:"Attack roll");
                int group=plan.Leaves.Count;
                plan.AddStaticDice(hero,RuleDefinitions.DieType.D4,1,title:"Bless",category:RuleDefinitions.RollContext.AttackRoll);
                // The game rolled 3 into the modifier before the await.
                plan.OnReady.Add(()=>{var faces=plan.GroupFaces(group);sharedBonus=sharedBonus-3+faces[0];plan.Leaves[group].Used=true;});
                break;}
            case 4:plan.AddMasterDice(hero,"The creatures",RuleDefinitions.DieType.D8,2,title:"Recharge",category:RuleDefinitions.RollContext.AttackRoll);break;
            default:plan.AddActorDice(hero,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,1);break;
        }
        return true;
    }
    static int NativeExtended(Driver d)
    {
        switch(round%6){
            case 0:case 1:{
                int check=hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second);
                return check>=10?check+RuleDefinitions.RollStaticDiceAndSum(2,RuleDefinitions.DieType.D6):check;}
            case 2:return hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second)+
                RuleDefinitions.RollStaticDiceAndSum(1,RuleDefinitions.DieType.D20);
            case 3:return hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second)+sharedBonus;
            case 4:return RuleDefinitions.RollStaticDiceAndSum(2,RuleDefinitions.DieType.D8);
            default:return hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second);
        }
    }
    static int NativeValue(Driver d)
    {
        if(extendedScenario)return NativeExtended(d);
        switch(round%6){
            case 0:return hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second);
            case 1:return hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.Advantage,out d.First,out d.Second);
            case 2:return hero.RollMagicalHitPointsPool(new BaseDefinition{Name="Sleep"},5,RuleDefinitions.DieType.D8);
            case 3:return hero.RollDiceAndSum(RuleDefinitions.DieType.D6,RuleDefinitions.RollContext.MagicDamageValueRoll,2);
            case 4:return RuleDefinitions.RollStaticDiceAndSum(2,RuleDefinitions.DieType.D4)+hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second);
            default:return hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second)+
                hero.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.ConcentrationCheck,false,RuleDefinitions.AdvantageType.None,out d.First,out d.Second);
        }
    }
    static void NextStep()
    {
        hero.Features.Clear();ServiceRepository.Players.Owner=1+round%members;
        if(mode=="different-owner"&&actorId==2)ServiceRepository.Players.Owner=2;
        RollWindow.Inputs.Clear();RollWindow.AutoOnReroll=autoScenario&&round%6==0;
        int[] values;
        switch(round%6){
            case 0:values=new[]{1,17};break;
            case 1:values=new[]{1,7,19};break;
            case 2:values=new[]{8,8,8,8,8};break;
            case 3:values=new[]{2,6,5};break;
            case 4:values=new[]{2,3,9};break;
            default:values=new[]{12};break;
        }
        if(extendedScenario){
            // Each peer holds the faces of the prompts it owns: the hero's
            // controller, and the room host for the game master's dice.
            int owner=ServiceRepository.Players.Owner;bool host=actorId==1;sharedBonus=5;
            switch(round%6){
                case 0:values=actorId==owner?new[]{15,3,4}:new int[0];break;
                case 1:values=actorId==owner?new[]{4}:new int[0];break;
                case 2:values=(actorId==owner?new[]{11}:new int[0]).Concat(host?new[]{6}:new int[0]).ToArray();break;
                case 3:values=actorId==owner?new[]{12,2}:new int[0];break;
                case 4:values=host?new[]{7,8}:new int[0];break;
                default:values=actorId==owner?new[]{12}:new int[0];break;
            }
        }
        foreach(int value in values)RollWindow.Inputs.Enqueue(value);
        if(!extendedScenario&&(round%6==0||round%6==1))hero.Features.Add(new FeatureDefinitionDieRollModifier{Name="DieRollModifierHalfingLucky",ValidityContext=RuleDefinitions.RollContext.AbilityCheck,MinRerollValue=1,RerollCount=1});
        if(!extendedScenario&&round%6==3)hero.Features.Add(new FeatureDefinitionDieRollModifier{Name="DamageReroll",ValidityContext=RuleDefinitions.RollContext.MagicDamageValueRoll,MinRerollValue=2,RerollCount=1});
        ServiceRepository.Settings.RandomnessMode=round>=12?"KarmicDice":"";
        promptsBefore=RollWindow.Prompts;waitingBefore=RollWindow.Waiting;
        if(deferredScenario){deferredPending=true;heldTicksAtPending=heldTicks;driver=new DeferredDriver();}else driver=new Driver();
        native=new TA.Coroutine();native.Start(driver);
    }
    static float Hold(){var args=new object[]{1f};holdTicks.Invoke(null,args);return (float)args[0];}
    // One Unity fixed step: NetworkingManager.FixedUpdate decides from the
    // tick whether gameplay runs a tick.
    static void FixedStep()
    {
        inFixed=true;
        if(lateOrder){if(!frozenFlag)Gameplay();frozenFlag=Hold()<=0f&&!freeRun;if(!frozenFlag)tick++;}
        else{frozenFlag=Hold()<=0f&&!freeRun;if(!frozenFlag){tick++;Gameplay();}}
        inFixed=false;
    }
    // Other gameplay (AI, battle, characters) runs every tick, held or not.
    static void OtherGameplay()
    {
        otherTicks++;
        if(lockstepScenario){int first,second;RuleDefinitions.RollDie(RuleDefinitions.DieType.D6,RuleDefinitions.AdvantageType.None,out first,out second);}
    }
    static void Gameplay()
    {
        OtherGameplay();
        if(deferredScenario){int before=heldTicks;held.Run();if(deferredPending&&heldTicks!=before)heldAdvancedWhilePending=true;}
        if(round>=Rounds)return;
        // Each step starts at a tick fixed by the previous one.
        if(driver==null){if(tick<nextStart)return;NextStep();if(deferredScenario)return;}
        if(!deferredScenario)native.Run();
        Suspended();
        bool finished=deferredScenario?!deferredPending:native.IsFinished;
        if(driver.Executed&&finished&&!RuntimeRolls.NetworkHolding)Report();
    }
    static void Suspended()
    {
        // 0.8.1: nothing may ever throw into the game's coroutine, paused or not.
        if(native!=null&&native.IsSuspended)
            throw new InvalidOperationException("Connected peer stopped: "+native.LastException+"; "+string.Join("|",Plugin.Logs.TakeLast(8)));
    }
    static void Report()
    {
        int first,second;int next=RuleDefinitions.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None,out first,out second);
        reports.Add(new{round,owner=ServiceRepository.Players.Owner,result=driver.Result,draws=driver.Draws,next,state=game.RandomState.word,
            prompts=RollWindow.Prompts-promptsBefore,waiting=RollWindow.Waiting-waitingBefore,auto=RollWindow.AutoOnReroll,
            heldAdvanced=heldAdvancedWhilePending,deferred=deferredScenario,extended=extendedScenario,paused=MultiplayerRolls.Failed,
            differences=Plugin.Logs.Count(line=>line.Contains("\"event\":\"results_differ\"")),tick=ExecTick,other=ExecOther});
        nextStart=tick+1+(ulong)(round%3);
        round++;driver=null;native=null;
    }
    static void Setup()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        typeof(NativeNetworkScheduler).GetField("enumerators",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,typeof(TA.Coroutine).GetField("enumerators",BindingFlags.Instance|BindingFlags.NonPublic));
        typeof(NativeNetworkScheduler).GetField("randomWords",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,new[]{"s0","s1","s2","s3"}.Select(name=>typeof(UnityEngine.Random.State).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)).ToArray());
        var harmony=new Harmony("connected.peer.fixture");Func<string,HarmonyMethod> hook=name=>new HarmonyMethod(typeof(NativeNetworkScheduler).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
        harmony.Patch(typeof(TA.Coroutine).GetMethod("Run"),prefix:hook("BeforeRun"),postfix:hook("AfterRun"),finalizer:hook("RunFinalizer"));
        foreach(var method in typeof(TA.DeterministicRandom).GetMethods(BindingFlags.Static|BindingFlags.Public).Where(m=>m.Name=="Range"||m.Name=="get_value"))harmony.Patch(method,prefix:hook("BeforeRandomDraw"));
        RuntimeRolls.Install(harmony);
        holdTicks=typeof(NativeNetworkScheduler).GetMethod("HoldTicksPostfix",BindingFlags.Static|BindingFlags.NonPublic);
        typeof(NativeNetworkScheduler).GetField("fixedStep",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<bool>)(()=>inFixed));
        RuntimeRolls.DeferredNetworked=()=>deferredScenario&&deferredPending;
        RuntimeRolls.LocalHold=()=>deferredPending&&!deferredReplaying;
        RuntimeRolls.HoldExempt=coroutine=>ReferenceEquals(coroutine,native);
        RuntimeRolls.ExtendedOnline=()=>true;
        held=new TA.Coroutine();held.Start(new Ticker());
        game=new Game(320);ServiceRepository.Current=new TestGameService{Game=game};ServiceRepository.Settings=new TestSettings();ServiceRepository.Rules=new TestRules();ServiceRepository.Players=new FixtureOwners();
        hero=new RulesetCharacterHero{Guid=42};EffectPatches.Prepare=Prepare;
        gateway=new Gateway();
        var transport=new MultiplayerTransport(gateway,()=>ProtocolProfile.Hash("connected-process/exact-build-and-settings"+(mode=="different-profile"&&actorId==2?"/mismatched":"")),Plugin.Log,nativeHolding:()=>RuntimeRolls.NetworkHolding);
        typeof(MultiplayerRolls).GetField("transport",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,transport);
        MultiplayerRolls.Update();
    }
    static void Frame(JsonElement input)
    {
        gateway.Time+=.1;
        foreach(var packet in input.GetProperty("packets").EnumerateArray())gateway.Receive(packet.GetProperty("sender").GetInt32(),Convert.FromBase64String(packet.GetProperty("payload").GetString()));
        // Independent local UI randomness must not alter the saved native RNG.
        UnityEngine.Random.Range(0,1000);
        JsonElement count;int steps=input.TryGetProperty("steps",out count)?count.GetInt32():1;
        for(int step=0;step<steps;step++)FixedStep();
        // The rendered frame: the mod's per-frame work, a deferred roll's own
        // runner, then NetworkingManager.Update recomputes the frozen state.
        MultiplayerRolls.Update();RuntimeRolls.UpdateNetworkCompletion();
        if(mode=="transfer"&&actorId==1&&RollWindow.Visible)ServiceRepository.Players.Owner=2;
        RollWindow.Tick();
        if(deferredScenario&&deferredPending&&native!=null){native.Run();Suspended();}
        frozenFlag=Hold()<=0f&&!freeRun;
        var sends=gateway.Outbound.ToArray();gateway.Outbound.Clear();var observations=reports.ToArray();reports.Clear();
        Console.Out.WriteLine(JsonSerializer.Serialize(new{actor=actorId,done=round==Rounds,sends,reports=observations,
            halted=MultiplayerRolls.Failed,inRoom=gateway.Active,nativeExecuted=driver!=null&&driver.Executed,state=game.RandomState.word,tick}));Console.Out.Flush();
    }
    static int Main(string[] args)
    {
        try{
            actorId=int.Parse(args[0]);members=int.Parse(args[1]);mode=args[2];autoScenario=mode=="auto";deferredScenario=mode=="deferred";extendedScenario=mode=="extended";
            lockstepScenario=mode=="lockstep"||mode=="lockstep-late"||mode=="no-lockstep";lateOrder=mode=="lockstep-late";freeRun=mode=="no-lockstep";Setup();
            for(string line;(line=Console.In.ReadLine())!=null;){using(var parsed=JsonDocument.Parse(line))Frame(parsed.RootElement);}
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
