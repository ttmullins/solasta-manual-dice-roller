using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security;
using HarmonyLib;
using ManualDiceRoller;

namespace ManualDiceRoller
{
    public static class Plugin {public static bool Online;public static bool Networked(){return Online;}}
    public static class MultiplayerRolls {public static bool Failed;public static string Failure;public static void Fail(string reason){Failed=true;Failure=reason;}}
    public static class RuntimeRolls
    {
        public static object NetworkOwner;
        public static bool NetworkHolding,NetworkCompletionPending,LocalHoldActive,FreezeTicks;
        public static object Exempt;
        public static bool IsHoldExempt(object coroutine){return coroutine!=null&&ReferenceEquals(coroutine,Exempt);}
        public static int Completions;
        public static void NetworkRunCompleted(object owner){if(!ReferenceEquals(owner,NetworkOwner))throw new Exception("Wrong coroutine completion owner");Completions++;}
        public static int Releases;
        public static void ReleaseNetworkHold(){Releases++;NetworkOwner=null;NetworkHolding=false;NetworkCompletionPending=false;}
    }
}
class SchedulerNativeTests
{
    static int passed;
    static void Check(bool value,string message){if(!value)throw new Exception(message);passed++;}
    sealed class Iterator:IEnumerator
    {
        internal int Ticks;internal Action Next;
        public object Current{get{return null;}}
        public bool MoveNext(){Ticks++;if(Next!=null)Next();return true;}
        public void Reset(){throw new NotSupportedException();}
    }
    sealed class FinishOwner:IEnumerator
    {
        internal bool Claimed;
        public object Current{get{return null;}}
        public bool MoveNext(){RuntimeRolls.NetworkOwner=this;RuntimeRolls.NetworkHolding=true;Claimed=NativeNetworkScheduler.Claim(this);RuntimeRolls.NetworkCompletionPending=true;return false;}
        public void Reset(){throw new NotSupportedException();}
    }
    static bool Foreign(){return true;}
    static void Main(string[] args)
    {
        string managed=Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,request)=>{string file=Path.Combine(managed,new AssemblyName(request.Name).Name+".dll");return File.Exists(file)?Assembly.LoadFrom(file):null;};
        if(args.Length>1&&args[1]=="shim")Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"UnityEngine.CoreModule.dll"));
        Run();
    }
    static void Run()
    {
        HarmonyFixtureHost.Initialize(typeof(Harmony).Assembly);
        string error;Check(NativeNetworkScheduler.Preflight(out error),"Pristine scheduler preflight failed: "+error);
        var harmony=new Harmony("network.native.scheduler.fixture");
        try{NativeNetworkScheduler.Install(harmony);Console.WriteLine("PASS complete production scheduler Install: 6 pristine game targets; Unity InternalCalls are not invoked.");}
        catch(SecurityException nativeLimit)
        {
            if(!nativeLimit.Message.Contains("ECall"))throw;
            Console.WriteLine("BLOCKED complete scheduler Install by unavailable native Unity InternalCall: "+nativeLimit.Message);
            harmony.UnpatchAll(harmony.Id);
            Func<string,HarmonyMethod> hook=name=>new HarmonyMethod(typeof(NativeNetworkScheduler).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static));
            harmony.Patch(typeof(TA.Coroutine).GetMethod("Run"),prefix:hook("BeforeRun"),postfix:hook("AfterRun"),finalizer:hook("RunFinalizer"));
            harmony.Patch(typeof(NetworkTick).GetMethod("UpdateTimeManipulation"),postfix:hook("HoldTicksPostfix"));
        }
        // 0.8.3 lockstep, on the game's genuine NetworkTick: while a shared roll
        // is pending the tick reports zero (NetworkingManager freezes gameplay);
        // ticks resume only at a fixed step, never in a frame between steps.
        {
            var fixedStep=typeof(NativeNetworkScheduler).GetField("fixedStep",BindingFlags.Static|BindingFlags.NonPublic);
            bool inFixed=true;fixedStep.SetValue(null,(Func<bool>)(()=>inFixed));
            var tick=new NetworkTick();
            Check(tick.UpdateTimeManipulation()==1f,"An idle tick is not changed");
            RuntimeRolls.FreezeTicks=true;inFixed=false;
            Check(tick.UpdateTimeManipulation()<=0f,"A pending shared roll stops the game's tick in a rendered frame");
            inFixed=true;Check(tick.UpdateTimeManipulation()<=0f,"A pending shared roll stops the game's tick at a fixed step");
            RuntimeRolls.FreezeTicks=false;inFixed=false;
            Check(tick.UpdateTimeManipulation()<=0f,"A frame between two fixed steps must not resume the ticks");
            Check(tick.UpdateTimeManipulation()<=0f,"Repeated frames keep the ticks stopped until a fixed step");
            inFixed=true;Check(tick.UpdateTimeManipulation()==1f,"The first fixed step after the roll resumes the ticks");
            inFixed=false;Check(tick.UpdateTimeManipulation()==1f,"Resumed ticks stay running in later frames");
            RuntimeRolls.FreezeTicks=true;tick.UpdateTimeManipulation();RuntimeRolls.FreezeTicks=false;NativeNetworkScheduler.Reset();
            Check(tick.UpdateTimeManipulation()==1f,"A reset (location change) never leaves the ticks stopped");
            // Unknown fixed-step state (outside the Unity player) never keeps
            // ticks stopped beyond the shared roll itself.
            fixedStep.SetValue(null,(Func<bool>)(()=>{throw new NotSupportedException();}));
            RuntimeRolls.FreezeTicks=true;tick.UpdateTimeManipulation();RuntimeRolls.FreezeTicks=false;
            Check(tick.UpdateTimeManipulation()==1f,"An unavailable fixed-step answer must not stop the ticks");
            fixedStep.SetValue(null,(Func<bool>)(()=>inFixed));inFixed=false;
        }
        Plugin.Online=true;
        var a=new Iterator();var b=new Iterator();var first=TA.Coroutine.StartCoroutine(a);var second=TA.Coroutine.StartCoroutine(b);
        first.Run();second.Run();Check(a.Ticks==1&&b.Ticks==1,"Offline/native free progression changed");
        RuntimeRolls.NetworkOwner=b;RuntimeRolls.NetworkHolding=true;
        var banter=(bool)typeof(NativeNetworkScheduler).GetMethod("BeforeBanterUpdate",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);
        Check(!banter,"Banter must defer its entire native update before unrelated deterministic RNG");
        first.Run();second.Run();Check(a.Ticks==1&&b.Ticks==2,"Only the owned coroutine may advance a network wait");
        RuntimeRolls.NetworkCompletionPending=true;first.Run();second.Run();Check(a.Ticks==1&&b.Ticks==2&&RuntimeRolls.Completions==0,"Skipped original Run emitted a premature completion ACK");
        RuntimeRolls.NetworkHolding=false;RuntimeRolls.NetworkCompletionPending=false;RuntimeRolls.NetworkOwner=null;
        var finish=new FinishOwner();var done=TA.Coroutine.StartCoroutine(finish);done.Run();
        Check(finish.Claimed&&done.IsFinished,"First admission inside a natural native Run must bind its coroutine");
        Check(RuntimeRolls.Completions==1,"Completed/popped native iterator lost its completion ACK");
        done.Run();first.Run();Check(RuntimeRolls.Completions==1&&a.Ticks==1,"Completion ACK repeat or another coroutine advanced before shared release");
        NativeNetworkScheduler.Release(finish);RuntimeRolls.NetworkHolding=false;RuntimeRolls.NetworkCompletionPending=false;RuntimeRolls.NetworkOwner=null;
        first.Run();Check(a.Ticks==2,"Shared completion release did not resume unrelated native coroutines");
        Check(!NativeNetworkScheduler.Claim(new object()),"A non-TA native entry must not claim network scheduling");
        // 0.8.1: a paused session (manual dice stopped after a disagreement)
        // holds nothing; the game keeps playing in the room with its dice.
        MultiplayerRolls.Failed=true;NativeNetworkScheduler.Reset();first.Run();Check(a.Ticks==3,"A paused multiplayer session must not freeze native gameplay");
        Check((bool)typeof(NativeNetworkScheduler).GetMethod("BeforeBanterUpdate",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null),"A paused session must not hold banter");
        Plugin.Online=false;first.Run();Check(a.Ticks==4,"Leaving the room did not restore single-player coroutine progression");
        Plugin.Online=true;MultiplayerRolls.Failed=false;
        Check((bool)typeof(NativeNetworkScheduler).GetMethod("BeforeBanterUpdate",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null),"Released banter update remains blocked");
        RuntimeRolls.NetworkOwner=new object();RuntimeRolls.NetworkHolding=true;
        // 0.8.3: inside a game tick every computer runs the same routines in
        // the same order, so a draw there is lockstep and never pauses.
        var fixedField=typeof(NativeNetworkScheduler).GetField("fixedStep",BindingFlags.Static|BindingFlags.NonPublic);
        fixedField.SetValue(null,(Func<bool>)(()=>true));
        typeof(NativeNetworkScheduler).GetMethod("BeforeRandomDraw",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        Check(!MultiplayerRolls.Failed&&RuntimeRolls.NetworkHolding,"A draw inside a game tick is lockstep and must not pause manual dice");
        fixedField.SetValue(null,(Func<bool>)(()=>false));
        // Outside the ticks an unrelated draw during the barrier pauses manual
        // dice and releases the hold; it never throws into the drawing routine.
        bool threw=false;int releases=RuntimeRolls.Releases;try{typeof(NativeNetworkScheduler).GetMethod("BeforeRandomDraw",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);}catch(TargetInvocationException){threw=true;}
        Check(!threw&&MultiplayerRolls.Failed&&RuntimeRolls.Releases==releases+1&&!RuntimeRolls.NetworkHolding,"An unrelated deterministic RNG request pauses manual dice without breaking the game routine");
        MultiplayerRolls.Failed=true;RuntimeRolls.NetworkOwner=new object();RuntimeRolls.NetworkHolding=true;threw=false;
        try{typeof(NativeNetworkScheduler).GetMethod("BeforeRandomDraw",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);}catch(TargetInvocationException){threw=true;}
        Check(!threw,"A paused session lets the game draw its own dice");
        RuntimeRolls.NetworkHolding=false;RuntimeRolls.NetworkOwner=null;MultiplayerRolls.Failed=false;
        // Deferred synchronous rolls hold every native coroutine (and banter)
        // in single-player and multiplayer, except the front roll's runner.
        var runnerTicks=new Iterator();var runner=TA.Coroutine.StartCoroutine(runnerTicks);
        foreach(bool online in new[]{false,true})
        {
            Plugin.Online=online;RuntimeRolls.LocalHoldActive=true;RuntimeRolls.Exempt=runner;
            int before=a.Ticks,runs=runnerTicks.Ticks;first.Run();runner.Run();
            Check(a.Ticks==before&&runnerTicks.Ticks==runs+1,"Deferred hold must stop other coroutines and let only its runner advance (online="+online+")");
            Check(!(bool)typeof(NativeNetworkScheduler).GetMethod("BeforeBanterUpdate",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null),"Banter must wait for a deferred roll");
            RuntimeRolls.Exempt=null;runner.Run();Check(runnerTicks.Ticks==runs+1,"Without the exemption the runner is held as well");
            RuntimeRolls.LocalHoldActive=false;first.Run();Check(a.Ticks==before+1,"Releasing the deferred hold resumes native coroutines");
        }
        Plugin.Online=true;
        harmony.UnpatchAll(harmony.Id);
        var foreign=new Harmony("network.foreign.scheduler.fixture");foreign.Patch(typeof(TA.Coroutine).GetMethod("Run"),prefix:new HarmonyMethod(typeof(SchedulerNativeTests).GetMethod("Foreign",BindingFlags.Static|BindingFlags.NonPublic)));
        Check(!NativeNetworkScheduler.Preflight(out error)&&error.Contains("Another mod"),"Foreign scheduler rewrite escaped deterministic preflight");
        foreign.UnpatchAll(foreign.Id);
        Check(NativeNetworkScheduler.Preflight(out error),"Scheduler rollback prevented clean reload");
        Console.WriteLine("PASS native network scheduler: "+passed+" assertions; production scheduler patches genuine unmodified TA.Coroutine.Run with real Harmony.");
    }
}
