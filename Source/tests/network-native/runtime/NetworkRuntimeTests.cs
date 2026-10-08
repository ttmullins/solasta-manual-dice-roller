using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ManualDiceRoller;

class NetworkRuntimeTests
{
    static int passed;
    static void Check(bool condition,string text){if(!condition)throw new Exception(text);passed++;}
    public static int Anchor(){return 0;}
    public static int AnchorValue(int value){return value;}
    public enum Outcome {Success,Failure}
    public static int AnchorOutcome(out int diceRoll,out int firstRoll,out int secondRoll,out Outcome outcome,out int successDelta)
    {diceRoll=firstRoll=secondRoll=successDelta=0;outcome=Outcome.Failure;return 0;}
    sealed class Driver:IEnumerator
    {
        int stage;internal int Result,First,Second;internal Func<int> Native;internal string Site="RuntimeNetwork";
        internal MethodInfo Method=typeof(NetworkRuntimeTests).GetMethod("Anchor");internal object[] Args=new object[0];
        internal Action BeforeNative;internal bool DeferNative;
        internal object[] Outputs=new object[0];
        public object Current{get;private set;}
        public bool MoveNext()
        {
            if(stage++==0){Current=RuntimeRolls.PrepareNativeRoll(this,Site,Method,Args);return true;}
            if(stage==2){if(DeferNative){stage--;Current=null;return true;}if(BeforeNative!=null)BeforeNative();RuntimeRolls.EnterNative(this,Site);try{Result=Native();RuntimeRolls.ObserveNativeValues(this,Site,Result,Outputs);}finally{RuntimeRolls.ExitNative(this,Site);}Current=null;return true;}
            return false;
        }
        public void Reset(){throw new NotSupportedException();}
    }
    sealed class Tick:IEnumerator{internal int Count;public object Current{get{return null;}}public bool MoveNext(){Count++;return true;}public void Reset(){throw new NotSupportedException();}}
    static void Start(Game game)
    {
        Plugin.Multiplayer=false;RuntimeRolls.Reset();Plugin.Enabled=true;Plugin.Multiplayer=true;Plugin.Logs.Clear();Plugin.DisabledContexts.Clear();
        RollWindow.Inputs.Clear();RollWindow.Prompts.Clear();RollWindow.BeforeAccept=null;RollWindow.CancelNext=false;RollWindow.DeferAccept=false;
        ServiceRepository.Current=new TestGameService{Game=game};ServiceRepository.Settings=new TestSettings();ServiceRepository.Rules=new TestRules();
    }
    // 0.8.3 lockstep: the game coroutine reaches a shared roll in one tick;
    // the roll is decided between ticks (rendered frames pump it while the
    // ticks are stopped) and the native call runs at the next tick.
    static void Ticks(TA.Coroutine coroutine)
    {
        int before=UnityEngine.Random.Calls;
        coroutine.Run();
        Check(UnityEngine.Random.Calls==before||!RuntimeRolls.NetworkHolding&&!RuntimeRolls.NetworkCompletionPending,"A shared roll ran its native dice within the tick that reached it");
        for(int frame=0;RuntimeRolls.FreezeTicks&&!RuntimeRolls.NetworkCompletionPending;frame++){Check(frame<64,"A shared roll never finished between two ticks");RuntimeRolls.UpdateNetworkCompletion();}
        coroutine.Run();
    }
    static void Complete(Driver driver,Game game,int expectedDraws)
    {
        UnityEngine.Random.Calls=0;var coroutine=new TA.Coroutine();coroutine.Start(driver);Ticks(coroutine);
        Check(UnityEngine.Random.Calls==expectedDraws,"Native burn/automatic raw draw count differs: "+UnityEngine.Random.Calls+" vs "+expectedDraws);
        Check(RuntimeRolls.NetworkHolding&&RuntimeRolls.NetworkCompletionPending,"Native handoff lost its shared completion barrier");
        var other=new Tick();var unrelated=new TA.Coroutine();unrelated.Start(other);unrelated.Run();
        Check(other.Count==0,"Another native coroutine advanced before all-peer completion");
        RuntimeRolls.UpdateNetworkCompletion();Check(!RuntimeRolls.NetworkHolding&&!RuntimeRolls.NetworkCompletionPending,"Shared native completion failed to release gameplay; nativeError="+coroutine.LastException+"; logs="+string.Join("|",Plugin.Logs));
        unrelated.Run();Check(other.Count==1,"Shared completion did not resume native coroutine scheduling");
        Check(MultiplayerRolls.Rounds>=2,"Admission and completion rendezvous were bypassed");
    }
    static int Raw(RuleDefinitions.DieType die,RuleDefinitions.AdvantageType adv,float karma=0){int first,second;return RuleDefinitions.RollDie(die,adv,out first,out second,karma);}
    static void Main()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        // ABI remapping changes fixture IL token bytes. The separate genuine
        // scheduler fixture exercises production Preflight and Install exactly.
        typeof(NativeNetworkScheduler).GetField("enumerators",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,typeof(TA.Coroutine).GetField("enumerators",BindingFlags.Instance|BindingFlags.NonPublic));
        typeof(NativeNetworkScheduler).GetField("randomWords",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,new[]{"s0","s1","s2","s3"}.Select(name=>typeof(UnityEngine.Random.State).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)).ToArray());
        var harmony=new Harmony("network.runtime.integration.fixture");
        Func<string,HarmonyMethod> hook=name=>new HarmonyMethod(typeof(NativeNetworkScheduler).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
        harmony.Patch(typeof(TA.Coroutine).GetMethod("Run"),prefix:hook("BeforeRun"),postfix:hook("AfterRun"),finalizer:hook("RunFinalizer"));
        foreach(var method in typeof(TA.DeterministicRandom).GetMethods(BindingFlags.Static|BindingFlags.Public).Where(method=>method.Name=="Range"||method.Name=="get_value"))
            harmony.Patch(method,prefix:hook("BeforeRandomDraw"));
        RuntimeRolls.Install(harmony);
        // Rendered frames, not game ticks, unless a check says otherwise.
        var fixedStep=typeof(NativeNetworkScheduler).GetField("fixedStep",BindingFlags.Static|BindingFlags.NonPublic);
        fixedStep.SetValue(null,(Func<bool>)(()=>false));
        {
            var game=new Game(320);Start(game);
            string initial=NativeNetworkScheduler.RandomStamp();
            var culture=System.Globalization.CultureInfo.CurrentCulture;
            try{System.Globalization.CultureInfo.CurrentCulture=new System.Globalization.CultureInfo("ar-SA");Check(NativeNetworkScheduler.RandomStamp()==initial,"Local culture changed the canonical multiplayer random stamp");}
            finally{System.Globalization.CultureInfo.CurrentCulture=culture;}
            UnityEngine.Random.InitState(948);UnityEngine.Random.Range(0,999);
            Check(NativeNetworkScheduler.RandomStamp()==initial,"Global UI noise changed the stored native multiplayer state stamp");
            ServiceRepository.Rules.LastCriticalCounter=1;
            Check(NativeNetworkScheduler.RandomStamp()!=initial,"Native critical counters were omitted from multiplayer consensus");
            ServiceRepository.Rules.LastCriticalCounter=0;ServiceRepository.Rules.DiceLuckScore=.5f;
            string half=NativeNetworkScheduler.RandomStamp();ServiceRepository.Rules.DiceLuckScore=BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(BitConverter.GetBytes(.5f),0)+1),0);
            Check(NativeNetworkScheduler.RandomStamp()!=half,"Adjacent native luck score float values collided in the multiplayer stamp");
            ServiceRepository.Rules.DiceLuckScore=0;ServiceRepository.Settings.RandomnessMode="NiceDice";
            Check(NativeNetworkScheduler.RandomStamp()!=initial,"Native randomness mode was omitted from the multiplayer stamp");
            ServiceRepository.Settings.RandomnessMode="";ServiceRepository.Settings.ForceCritEveryFewRolls=true;
            Check(NativeNetworkScheduler.RandomStamp()!=initial,"Native forced critical setting was omitted from multiplayer consensus");
            ServiceRepository.Settings.ForceCritEveryFewRolls=false;ServiceRepository.Rules=null;
            bool refused=false;try{NativeNetworkScheduler.RandomStamp();}catch(InvalidOperationException){refused=true;}
            Check(refused,"Missing native random rules service did not refuse multiplayer consensus");
            ServiceRepository.Rules=new TestRules();
        }
        foreach(var adv in new[]{RuleDefinitions.AdvantageType.None,RuleDefinitions.AdvantageType.Advantage,RuleDefinitions.AdvantageType.Disadvantage})
        foreach(var karma in new[]{0f,.6f,-.6f})
        {
            var game=new Game(591);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,1,adv);return true;};
            RollWindow.Inputs.Enqueue(4);if(adv!=RuleDefinitions.AdvantageType.None)RollWindow.Inputs.Enqueue(17);
            ServiceRepository.Settings.RandomnessMode=karma==0?"":"NiceDice";ServiceRepository.Rules.DiceLuckScore=Math.Abs(karma);actor.Side=karma<0?RuleDefinitions.Side.Enemy:RuleDefinitions.Side.Ally;
            var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,false,adv,out driver.First,out driver.Second);
            int draws=adv==RuleDefinitions.AdvantageType.None?1:2;Complete(driver,game,draws);
            Check(driver.First==4&&driver.Second==(draws==1?-1:17),"Original native actor lost accepted MP faces");
            Check(driver.Result==(adv==RuleDefinitions.AdvantageType.Advantage?17:4),"Original native advantage selected the wrong accepted face");
            uint state=game.RandomState.word;var expected=new Game(591);ServiceRepository.Current.Game=expected;Raw(RuleDefinitions.DieType.D20,adv,karma);
            Check(state==expected.RandomState.word,"Production MP burn did not preserve original karma/advantage RNG arguments");
        }
        {
            var game=new Game(808);Start(game);var actor=new RulesetCharacterHero();actor.Features.Add(new FeatureDefinitionDieRollModifier{Name="DieRollModifierHalfingLucky",ValidityContext=RuleDefinitions.RollContext.AbilityCheck,MinRerollValue=1,RerollCount=1});
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,RuleDefinitions.AdvantageType.Advantage);return true;};
            foreach(var face in new[]{1,1,3,7})RollWindow.Inputs.Enqueue(face);
            var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.Advantage,out driver.First,out driver.Second);
            Complete(driver,game,4);Check(driver.Result==7&&driver.First==3&&driver.Second==7,"MP Lucky replacement faces were not manually consumed");
            Check(RollWindow.Prompts.Count==3,"MP Lucky initial pair and individual replacement prompts differ");
            uint state=game.RandomState.word;var expected=new Game(808);ServiceRepository.Current.Game=expected;Raw(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.Advantage);Raw(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);Raw(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);
            Check(state==expected.RandomState.word,"MP Lucky burns advanced the wrong native reroll count");
        }
        {
            var game=new Game(801);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddStaticDice(actor,RuleDefinitions.DieType.D4,3);return true;};
            foreach(var face in new[]{1,3,4})RollWindow.Inputs.Enqueue(face);
            var driver=new Driver{Native=()=>RuleDefinitions.RollStaticDiceAndSum(3,RuleDefinitions.DieType.D4)};
            Complete(driver,game,3);Check(driver.Result==8,"MP static spell pool retained automatic raw faces");
            uint state=game.RandomState.word;var expected=new Game(801);ServiceRepository.Current.Game=expected;RuleDefinitions.RollStaticDiceAndSum(3,RuleDefinitions.DieType.D4);
            Check(state==expected.RandomState.word,"MP static batch burns drifted the next native random state");
        }
        {
            var game=new Game(803);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(17);
            var driver=new Driver{Method=typeof(NetworkRuntimeTests).GetMethod("AnchorOutcome"),Args=new object[]{0,0,0,Outcome.Failure,0}};
            driver.Native=()=>{int face=actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);int total=face+4;driver.Outputs=new object[]{face,driver.First,driver.Second,total>=16?Outcome.Success:Outcome.Failure,total-16};return total;};
            Complete(driver,game,1);var observed=NativeNetworkDescriptor.LastCompletedPlan;
            Check(observed!=null&&observed.NativeObserved&&(int)observed.NativeReturn==21,"Accepted native return value was not retained for completion consensus");
            Check(observed.NativeOutputs.Length==5&&(int)observed.NativeOutputs[0]==17&&(int)observed.NativeOutputs[1]==17&&(int)observed.NativeOutputs[2]==-1&&(Outcome)observed.NativeOutputs[3]==Outcome.Success&&(int)observed.NativeOutputs[4]==5,"Completion observation lost native raw face, selected outcome, or success delta");
            driver.Outputs[0]=999;driver.Outputs[4]=999;
            Check((int)observed.NativeOutputs[0]==17&&(int)observed.NativeOutputs[4]==5,"Caller output-array mutation changed the completed native observation");
        }
        {
            var game=new Game(809);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            MultiplayerRolls.Automatic=true;
            var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);
            Complete(driver,game,1);Check(RollWindow.Prompts.Count==0,"Shared automatic admission still showed a manual prompt");
            uint state=game.RandomState.word;var expected=new Game(809);ServiceRepository.Current.Game=expected;int native=Raw(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);
            Check(state==expected.RandomState.word&&driver.Result==native,"Shared automatic fallback either burnt twice or skipped original native dice");
        }
        {
            var game=new Game(777);Start(game);var actor=new RulesetCharacterHero();
            var feature=new FeatureDefinitionDieRollModifier{Name="DieRollModifierHalfingLucky",ValidityContext=RuleDefinitions.RollContext.AbilityCheck,MinRerollValue=1,RerollCount=1};actor.Features.Add(feature);
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(17);MultiplayerRolls.AfterInput=()=>feature.RerollCount=2;
            var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);
            Complete(driver,game,1);
            Check(!MultiplayerRolls.Failed&&feature.RerollCount==2,"A shared native feature change during the UI wait caused a fatal committed-face error");
            Check(RollWindow.Prompts.Count==1&&!Plugin.Logs.Any(line=>line.StartsWith("native_face/",StringComparison.Ordinal)),"The changed feature still consumed the accepted face or displayed another replacement prompt");
            uint state=game.RandomState.word;var expected=new Game(777);ServiceRepository.Current.Game=expected;int first,second;
            int original=actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out first,out second);
            Check(driver.Result==original&&driver.Result!=17&&state==expected.RandomState.word,"A changed pre-handoff native feature failed to return the entire step to original automatic dice");
        }
        {
            var game=new Game(504);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(13);
            var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);
            var routine=new TA.Coroutine();routine.Start(driver);Ticks(routine);
            uint state=game.RandomState.word;UnityEngine.Random.Calls=0;
            TA.DeterministicRandom.LockFlags.Add(TA.DeterministicRandom.DeterministicLockFlag.Cursor);
            TA.DeterministicRandom.Range(0,100);
            Check(UnityEngine.Random.Calls==1&&game.RandomState.word==state&&!MultiplayerRolls.Failed,"Non-deterministic cursor RNG should not trip the held gameplay guard");
            TA.DeterministicRandom.LockFlags.Clear();UnityEngine.Random.Calls=0;
            // 0.8.3: inside a game tick every computer runs the same routines in
            // the same order (lockstep): such a draw never pauses manual dice.
            Check(RuntimeRolls.NetworkHolding,"The completion barrier is held before the unrelated draw");
            fixedStep.SetValue(null,(Func<bool>)(()=>true));
            TA.DeterministicRandom.Range(0,20);
            Check(!MultiplayerRolls.Failed&&RuntimeRolls.NetworkHolding,"A draw inside a game tick is lockstep and keeps manual dice");
            fixedStep.SetValue(null,(Func<bool>)(()=>false));UnityEngine.Random.Calls=0;
            // 0.8.1: outside the ticks an unrelated draw during the barrier
            // pauses manual dice and releases the hold without throwing.
            int rejected=0;
            foreach(Action call in new Action[]{()=>TA.DeterministicRandom.Range(0,20),()=>TA.DeterministicRandom.Range(0f,20f),()=>{var value=TA.DeterministicRandom.value;}})
                try{call();}catch(InvalidOperationException){rejected++;}
            Check(rejected==0&&MultiplayerRolls.Failed&&!RuntimeRolls.NetworkHolding,"Every unrelated native random entry pauses manual dice and releases the barrier without throwing");
            var other=new Tick();var unrelated=new TA.Coroutine();unrelated.Start(other);unrelated.Run();
            Check(other.Count==1,"A paused session lets other native coroutines continue");
            routine.Run();Check(!routine.IsSuspended,"The held roll's own coroutine finishes without an exception");
            Plugin.Multiplayer=false;RuntimeRolls.Reset();unrelated.Run();Check(other.Count==2,"Leaving the room keeps native single-player scheduling");
        }
        {
            // 0.8.2.0: the games' results differ after a roll with entered
            // dice (the game's own synchronization). Reported once; manual
            // dice continue, nothing stays held and nothing throws.
            var game=new Game(612);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(14);MultiplayerRolls.CompletionDiffers=true;
            var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);
            Complete(driver,game,1);
            Check(driver.Result==14&&MultiplayerRolls.Differences==1&&!MultiplayerRolls.Failed,"A different result after the agreed dice is reported without pausing manual dice");
            Check(!RuntimeRolls.NetworkHolding&&!RuntimeRolls.NetworkCompletionPending,"Nothing stays held after a reported difference");
            RollWindow.Inputs.Enqueue(9);var next=new Driver();next.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out next.First,out next.Second);
            Complete(next,game,1);Check(next.Result==9,"The next roll still uses the entered die");
        }
        foreach(bool receiverChange in new[]{false,true})
        {
            var game=new Game(640);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(12);var driver=new Driver();
            if(receiverChange)
            {
                driver.Method=typeof(RulesetCharacter).GetMethod("RollAbilityCheck");
                driver.Args=new object[]{actor,"Dexterity","Acrobatics",new List<RuleDefinitions.TrendInfo>(),false};
                driver.BeforeNative=()=>RuntimeRolls.ValidateNativeArguments(driver,driver.Site,new object[]{new RulesetCharacterHero(),"Dexterity","Acrobatics",driver.Args[3],false});
            }
            else
            {
                driver.Method=typeof(NetworkRuntimeTests).GetMethod("AnchorValue");driver.Args=new object[]{1};
                driver.BeforeNative=()=>RuntimeRolls.ValidateNativeArguments(driver,driver.Site,new object[]{2});
            }
            driver.Native=()=>{int a,b;return actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out a,out b);};
            UnityEngine.Random.Calls=0;var coroutine=new TA.Coroutine();coroutine.Start(driver);Ticks(coroutine);
            // 0.8.1: changed arguments pause manual dice; the game rolls its
            // own die and its action is never broken by an exception.
            Check(MultiplayerRolls.Failed&&!coroutine.IsSuspended,"Actual native argument changes must pause manual dice without throwing into the game's action");
            Check(UnityEngine.Random.Calls==1,"After changed native arguments the game rolls its own die: "+UnityEngine.Random.Calls);
        }
        foreach(bool transferAfterInput in new[]{false,true})
        {
            var game=new Game(646);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(18);var driver=new Driver();
            if(transferAfterInput)MultiplayerRolls.AfterInput=()=>MultiplayerRolls.Controller=2;
            else driver.BeforeNative=()=>MultiplayerRolls.Controller=2;
            bool ran=false;driver.Native=()=>{ran=true;return Raw(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);};
            UnityEngine.Random.Calls=0;var coroutine=new TA.Coroutine();coroutine.Start(driver);Ticks(coroutine);
            Check(MultiplayerRolls.Failed&&!coroutine.IsSuspended,"A native ownership transfer must pause manual dice without throwing into the game's action");
            Check(ran&&UnityEngine.Random.Calls==1,"A transferred hero's roll is made by the game, never with the stale controller's faces");
        }
        foreach(bool abort in new[]{false,true})
        {
            var game=new Game(648);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(10);var driver=new Driver{DeferNative=true};
            var coroutine=new TA.Coroutine();coroutine.Start(driver);Ticks(coroutine);
            Check(RuntimeRolls.NetworkHolding&&!RuntimeRolls.NetworkCompletionPending,"Handoff fixture did not stop before native execution");
            if(abort)RuntimeRolls.AbortNative(driver);else RuntimeRolls.CancelOwner(driver);
            Check(MultiplayerRolls.Failed,"Canceling/aborting a handed-off multiplayer owner left the peer barrier waiting forever");
            Plugin.Multiplayer=false;RuntimeRolls.UpdateNetworkCompletion();
            Check(!RuntimeRolls.NetworkHolding&&!RuntimeRolls.NetworkCompletionPending,"Photon room loss did not clear holding when native completion had not begun");
        }
        {
            var game=new Game(670);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(16);var driver=new Driver();driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);
            var coroutine=new TA.Coroutine();coroutine.Start(driver);Ticks(coroutine);
            Check(RuntimeRolls.NetworkCompletionPending,"Watchdog fixture did not finish the native call");
            var stack=(Stack<IEnumerator>)typeof(RuntimeRolls).GetField("completionStack",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);stack.Clear();
            typeof(RuntimeRolls).GetField("networkExitStarted",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,System.Diagnostics.Stopwatch.GetTimestamp()-31L*System.Diagnostics.Stopwatch.Frequency);
            RuntimeRolls.UpdateNetworkCompletion();
            Check(MultiplayerRolls.Failed&&Plugin.Logs.Any(line=>line.Contains("did not reach")),"Missing native scheduler completion did not trigger its 30-second watchdog");
            Plugin.Multiplayer=false;RuntimeRolls.Reset();
        }
        {
            var game=new Game(690);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            MultiplayerRolls.EndAdmission=true;
            var abandoned=new Driver();abandoned.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out abandoned.First,out abandoned.Second);
            var stopped=new TA.Coroutine();stopped.Start(abandoned);UnityEngine.Random.Calls=0;Ticks(stopped);
            // 0.8.1 (live 0.8.0.0 failure): an admission without agreement used
            // to throw into the game's action (the game's "Mod Warning") and
            // leave the room. Now manual dice pause and the game rolls.
            Check(MultiplayerRolls.Failed&&!stopped.IsSuspended,"An admission without agreement must pause manual dice without throwing into the game's action");
            Check(UnityEngine.Random.Calls==1&&!RuntimeRolls.NetworkHolding,"The game rolls its own die and nothing stays held");
            Check(typeof(NativeNetworkScheduler).GetField("claimedOwner",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==null,"An abandoned admission retained the scheduler claim after clearing its runtime owner");
            var other=new Tick();var blocked=new TA.Coroutine();blocked.Start(other);blocked.Run();
            Check(other.Count==1,"A paused session must not freeze other native coroutines");
            Plugin.Multiplayer=false;RuntimeRolls.UpdateNetworkCompletion();blocked.Run();
            Check(other.Count==2,"Room loss keeps ordinary scheduling after an abandoned admission");
            // Model the transport observing a new room, without any location
            // lifecycle/Runtime Reset. A distinct owner must acquire the claim.
            MultiplayerRolls.Failed=false;Plugin.Multiplayer=true;RollWindow.Inputs.Enqueue(13);
            var retry=new Driver();retry.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out retry.First,out retry.Second);
            Complete(retry,game,1);Check(retry.Result==13,"A fresh room's new owner could not roll after an abandoned admission");
        }
        {
            // 0.8.3 lockstep (live 0.8.1/0.8.2 divergence): while players decide
            // a shared roll the game's ticks stay stopped, and the native call
            // runs at the next tick, however many frames the decision takes.
            var game=new Game(720);Start(game);var actor=new RulesetCharacterHero();
            EffectPatches.Prepare=(call,plan)=>{plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1);return true;};
            RollWindow.Inputs.Enqueue(11);MultiplayerRolls.Frames=3;
            bool openInside=false;var driver=new Driver();driver.BeforeNative=()=>openInside=RuntimeRolls.NetworkStepOpen;
            driver.Native=()=>actor.RollDie(RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,false,RuleDefinitions.AdvantageType.None,out driver.First,out driver.Second);
            var coroutine=new TA.Coroutine();coroutine.Start(driver);UnityEngine.Random.Calls=0;
            Check(!RuntimeRolls.FreezeTicks,"Idle multiplayer play keeps the game's ticks running");
            coroutine.Run();
            Check(RuntimeRolls.FreezeTicks&&RuntimeRolls.NetworkHolding&&UnityEngine.Random.Calls==0,"Reaching a shared roll stops the ticks before its native dice");
            Check(!RuntimeRolls.NetworkStepOpen,"Another routine cannot queue a deferred roll behind a pending shared step");
            coroutine.Run();Check(UnityEngine.Random.Calls==0,"A stopped tick never runs the native call");
            int frames=0;
            while(RuntimeRolls.FreezeTicks&&!RuntimeRolls.NetworkCompletionPending){RuntimeRolls.UpdateNetworkCompletion();frames++;Check(frames<64,"The shared decision never finished");}
            Check(frames>=3&&UnityEngine.Random.Calls==0&&RuntimeRolls.NetworkHolding,"Rendered frames decide the roll without any native dice ("+frames+" frames)");
            coroutine.Run();
            Check(UnityEngine.Random.Calls==1&&driver.Result==11&&openInside,"At the next tick the native call runs with the entered die");
            Check(RuntimeRolls.FreezeTicks&&RuntimeRolls.NetworkCompletionPending,"The ticks stop again for the shared completion");
            frames=0;while(RuntimeRolls.FreezeTicks){RuntimeRolls.UpdateNetworkCompletion();frames++;Check(frames<64,"The shared completion never finished");}
            Check(!RuntimeRolls.NetworkHolding&&!MultiplayerRolls.Failed&&RuntimeRolls.NetworkStepOpen,"The completion resumes the ticks with nothing held");
            // Pending multiplayer deferred rolls keep the ticks stopped, even
            // once paused (they finish with the game's dice first); outside a
            // room nothing ever stops the ticks.
            RuntimeRolls.DeferredNetworked=()=>true;Check(RuntimeRolls.FreezeTicks,"Pending deferred rolls stop the ticks");
            MultiplayerRolls.Failed=true;Check(RuntimeRolls.FreezeTicks,"Paused deferred rolls still finish before the next tick");
            Plugin.Multiplayer=false;Check(!RuntimeRolls.FreezeTicks,"Single-player play never stops the ticks");
            RuntimeRolls.DeferredNetworked=null;Plugin.Multiplayer=true;MultiplayerRolls.Failed=false;
            // A shared roll whose game coroutine is canceled while it waits
            // pauses manual dice and never leaves the ticks stopped.
            RollWindow.Inputs.Enqueue(5);MultiplayerRolls.Frames=4;
            var canceled=new Driver();canceled.Native=()=>Raw(RuleDefinitions.DieType.D20,RuleDefinitions.AdvantageType.None);
            var gone=new TA.Coroutine();gone.Start(canceled);gone.Run();
            Check(RuntimeRolls.FreezeTicks,"The second roll stops the ticks");
            RuntimeRolls.CancelOwner(canceled);
            frames=0;do{RuntimeRolls.UpdateNetworkCompletion();frames++;Check(frames<64,"A canceled shared roll left the ticks stopped");}while(RuntimeRolls.FreezeTicks);
            Check(frames==1,"A canceled shared roll stops nothing after the next frame");
            Check(MultiplayerRolls.Failed&&!RuntimeRolls.NetworkHolding,"A canceled shared roll pauses manual dice and releases the hold");
            Plugin.Multiplayer=false;RuntimeRolls.Reset();
        }
        {
            // 0.8.4.0 (third live session): a trap's damage dice are asked after
            // the target's saving throw in the same plan. Once the save's face
            // is in, the damage prompt says what the save does to them.
            var game=new Game(731);Start(game);var actor=new RulesetCharacterHero();
            var plan=new NativeRollPlan(new NativeInvocation(new object(),"Environment.Trap",typeof(NetworkRuntimeTests).GetMethod("Anchor"),new object[0],1));
            plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.SavingThrow,1);plan.Describe(12,true,3);
            var form=new object();plan.AddStaticDice(actor,RuleDefinitions.DieType.D6,2,form);
            plan.HintAfterSave(new object(),actor,true,"Gimli");
            Check(plan.Leaves[1].HintAt==null,"another form's dice are not labelled");
            plan.HintAfterSave(form,actor,true,"Gimli");
            var damage=plan.Leaves[1];
            Check(damage.HintAt()=="If Gimli made the saving throw, the game halves this damage.","before the save's face the damage hint is conditional: "+damage.HintAt());
            var save=plan.Leaves[0];save.Identities.Add(new FaceIdentity(save.Binding,save.Ordinal,0));save.Faces.Add(16);
            Check(damage.HintAt()=="Gimli made the saving throw: the game halves this damage.","a successful save halves the damage it precedes: "+damage.HintAt());
            save.Faces[0]=2;
            Check(damage.HintAt()=="Gimli failed the saving throw: full damage.","a failed save means full damage");
            plan.HintAfterSave(form,actor,false,"Gimli");save.Faces[0]=16;
            Check(plan.Leaves[2].HintAt()=="Gimli made the saving throw: this damage is not dealt.","a negating save says the damage is not dealt");
            Plugin.Multiplayer=false;RuntimeRolls.Reset();
        }
        Console.WriteLine("PASS production MP runtime/native RNG: "+passed+" assertions; actual RuntimeRolls/broker/scheduler plus pristine native dice/RNG IL; transport and descriptors are explicit boundary doubles.");
    }
}
