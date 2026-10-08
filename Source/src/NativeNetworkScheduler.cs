using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace ManualDiceRoller
{
    // The Photon pump is independent of TA.Coroutine.Run. During a replicated
    // roll only its native coroutine may advance. Once the owned native call
    // reaches its next natural yield, all peers compare their resulting game
    // state before any coroutine can draw the next deterministic result.
    //
    // Lockstep (0.8.3): Solasta's multiplayer games stay identical by running
    // the same game ticks in the same order; every action is scheduled for a
    // tick. While a shared roll is being decided the game's tick count stops
    // on every computer at the same tick, and the roll runs at the next tick,
    // so no game advances past another while players enter dice.
    internal static class NativeNetworkScheduler
    {
        static FieldInfo enumerators;
        static FieldInfo[] randomWords;
        static MethodInfo run;
        static MethodInfo banterUpdate;
        static MethodInfo[] randomMethods;
        static MethodInfo timeManipulation;
        static Func<bool> fixedStep;
        static bool ticksHeld;
        static object claimedOwner;
        static TA.Coroutine claimedCoroutine;
        [ThreadStatic] static Stack<RunState> executing;
        sealed class RunState
        {
            internal TA.Coroutine Coroutine;
            internal object Owner;
            internal bool ActuallyRan, Popped;
        }
        internal static bool Preflight(out string error)
        {
            error=null;
            try
            {
                enumerators=typeof(TA.Coroutine).GetField("enumerators",BindingFlags.Instance|BindingFlags.NonPublic);
                run=typeof(TA.Coroutine).GetMethod("Run",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                if(enumerators==null||enumerators.FieldType!=typeof(Stack<IEnumerator>)||run==null||run.ReturnType!=typeof(void))
                    throw new InvalidOperationException("The native deterministic coroutine ABI changed.");
                if(Hash(run.GetMethodBody().GetILAsByteArray())!=RunFingerprint)
                    throw new InvalidOperationException("The native deterministic coroutine body changed.");
                CheckPatchOwners(run);
                banterUpdate=typeof(GameLocationBanterManager).GetMethod("UpdateImpl",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                if(banterUpdate==null||banterUpdate.ReturnType!=typeof(void)||Hash(banterUpdate.GetMethodBody().GetILAsByteArray())!="89fa4bc2d741b8d6ae430c1edf3abd36fa309eb806d7e271d9274c268bfe5b55")
                    throw new InvalidOperationException("The native banter scheduler body changed.");
                CheckPatchOwners(banterUpdate);
                randomMethods=new[]{typeof(TA.DeterministicRandom).GetMethod("Range",new[]{typeof(int),typeof(int)}),typeof(TA.DeterministicRandom).GetMethod("Range",new[]{typeof(float),typeof(float)}),typeof(TA.DeterministicRandom).GetProperty("value").GetGetMethod()};
                var hashes=new[]{"2f4927e9480a3b9eeef0be957a774067435854bcac64470df9924a1ad0c0d3e6","12c70808a2440b92c9069e7eef0d9b80515fc80805a534adcfa3a8e4a88c1f5c","918de17bfc6bb79ec43968395c4e1d385fe5d47131a310504c9d2e43d1e90873"};
                for(int i=0;i<randomMethods.Length;i++)
                {
                    if(randomMethods[i]==null||Hash(randomMethods[i].GetMethodBody().GetILAsByteArray())!=hashes[i])throw new InvalidOperationException("The native deterministic random body changed.");
                    CheckPatchOwners(randomMethods[i]);
                }
                randomWords=new[]{"s0","s1","s2","s3"}.Select(name=>typeof(UnityEngine.Random.State).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)).ToArray();
                if(randomWords.Any(f=>f==null||f.FieldType!=typeof(int))||typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Length!=4)
                    throw new InvalidOperationException("The native Unity random state ABI changed.");
                // The game's lockstep tick: NetworkingManager.Update and
                // FixedUpdate both freeze gameplay when this returns zero.
                var tick=typeof(GameLocationBanterManager).Assembly.GetType("NetworkTick");
                timeManipulation=tick==null?null:tick.GetMethod("UpdateTimeManipulation",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                if(timeManipulation==null||timeManipulation.ReturnType!=typeof(float)||Hash(timeManipulation.GetMethodBody().GetILAsByteArray())!=TickFingerprint)
                    throw new InvalidOperationException("The native lockstep tick body changed.");
                CheckPatchOwners(timeManipulation);
                var property=typeof(UnityEngine.Time).GetProperty("inFixedTimeStep",BindingFlags.Static|BindingFlags.Public);
                if(property==null||property.PropertyType!=typeof(bool)||property.GetGetMethod()==null)throw new InvalidOperationException("The Unity fixed time step ABI changed.");
                fixedStep=UnityFixedStep;
                return true;
            }
            catch(Exception exception){error=exception.Message;return false;}
        }
        // Pinned to the pristine supplied assembly, not to rewritten fixture IL.
        const string RunFingerprint="a921326a4c0da3313088d64f87656902da53a0bbb6f33a3532fb8189bada1098";
        const string TickFingerprint="4b3c0c0c5cd4c13fbd30059a5209fdd055afb29a6e26f76f1202b52e226ed5f7";
        static string Hash(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static void CheckPatchOwners(MethodBase method)
        {
            var patches=Harmony.GetPatchInfo(method);
            if(patches!=null&&patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers).Any(p=>p.PatchMethod.DeclaringType!=typeof(NativeNetworkScheduler)))
                throw new InvalidOperationException("Another mod changes the native coroutine scheduler: "+method.DeclaringType.FullName+"."+method.Name);
        }
        internal static void Install(Harmony harmony)
        {
            string error;if(!Preflight(out error))throw new InvalidOperationException(error);
            harmony.Patch(run,prefix:Own(nameof(BeforeRun)),postfix:Own(nameof(AfterRun)),finalizer:Own(nameof(RunFinalizer)));
            harmony.Patch(banterUpdate,prefix:Own(nameof(BeforeBanterUpdate)));
            foreach(var method in randomMethods)harmony.Patch(method,prefix:Own(nameof(BeforeRandomDraw)));
            harmony.Patch(timeManipulation,postfix:Own(nameof(HoldTicksPostfix)));
        }
        static HarmonyMethod Own(string name){return new HarmonyMethod(typeof(NativeNetworkScheduler).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)){priority=Priority.First};}
        internal static bool Claim(object owner)
        {
            if(owner==null||executing==null||executing.Count==0)return false;
            return Claim(owner,executing.Peek().Coroutine);
        }
        // The native coroutine running right now (null outside TA.Coroutine.Run).
        internal static object CurrentRun { get { return executing==null||executing.Count==0?null:executing.Peek().Coroutine; } }
        // A shared roll is decided between two game ticks, outside the native
        // run that reached it; it claims the coroutine recorded at that point.
        internal static bool Claim(object owner,object coroutine)
        {
            var native=coroutine as TA.Coroutine;
            if(owner==null||native==null)return false;
            if(claimedOwner!=null&&!ReferenceEquals(claimedOwner,owner))return false;
            claimedOwner=owner;claimedCoroutine=native;
            if(executing!=null)foreach(var state in executing)if(ReferenceEquals(state.Coroutine,native)){state.Owner=owner;break;}
            return true;
        }
        internal static void Release(object owner)
        {
            if(owner==null||ReferenceEquals(claimedOwner,owner)){claimedOwner=null;claimedCoroutine=null;}
        }
        internal static void Reset(){claimedOwner=null;claimedCoroutine=null;ticksHeld=false;}
        // Unity answers this only inside the player (a fixture throws).
        static bool UnityFixedStep(){return UnityEngine.Time.inFixedTimeStep;}
        // True inside a fixed time step, where the game runs its ticks. Unknown
        // (outside the Unity player) counts as inside, which never holds ticks
        // longer than the shared roll itself.
        internal static bool InFixedStep
        {
            get { var hook=fixedStep; if(hook==null)return true; try { return hook(); } catch(Exception) { return true; } }
        }
        // While a shared roll is pending every computer reports a stopped
        // tick, so gameplay stops at the same tick everywhere. Ticks resume
        // only where the game decides whether a fixed step runs a tick, never
        // in a rendered frame between two steps, so the first tick after the
        // roll is the same tick on every computer whatever the frame timing.
        static void HoldTicksPostfix(ref float __result)
        {
            if(RuntimeRolls.FreezeTicks){ticksHeld=true;__result=0f;return;}
            if(!ticksHeld)return;
            if(InFixedStep)ticksHeld=false;
            else __result=0f;
        }
        internal static bool TicksHeld { get { return ticksHeld; } }
        static bool HasOwner(TA.Coroutine coroutine,object owner)
        {
            if(coroutine==null||owner==null)return false;
            if(ReferenceEquals(coroutine,claimedCoroutine)&&ReferenceEquals(owner,claimedOwner))return true;
            var stack=enumerators.GetValue(coroutine) as Stack<IEnumerator>;
            return stack!=null&&stack.Any(frame=>ReferenceEquals(frame,owner));
        }
        static bool BeforeRun(TA.Coroutine __instance,out RunState __state)
        {
            __state=new RunState{Coroutine=__instance};
            // Deferred synchronous rolls (concentration, travel, camp, repeat
            // saves, identification) hold every native action coroutine at its
            // next yield until answered. In multiplayer only the front deferred
            // roll's own runner coroutine may advance.
            if(RuntimeRolls.LocalHoldActive&&!RuntimeRolls.IsHoldExempt(__instance))return false;
            // A paused multiplayer session (manual dice stopped after a
            // disagreement) holds nothing: the game plays on with its dice.
            if(RuntimeRolls.NetworkHolding)
            {
                if(RuntimeRolls.NetworkCompletionPending||!HasOwner(__instance,RuntimeRolls.NetworkOwner))return false;
                __state.Owner=RuntimeRolls.NetworkOwner;
            }
            __state.ActuallyRan=true;
            if(executing==null)executing=new Stack<RunState>();executing.Push(__state);
            return true;
        }
        static void Pop(RunState state)
        {
            if(state==null||state.Popped||!state.ActuallyRan)return;
            state.Popped=true;
            if(executing!=null&&executing.Count>0&&ReferenceEquals(executing.Peek(),state))executing.Pop();
        }
        static void AfterRun(RunState __state)
        {
            Pop(__state);
            if(__state==null||!__state.ActuallyRan||__state.Owner==null)return;
            if(__state.Coroutine.IsSuspended&&__state.Coroutine.LastException!=null)
            {
                MultiplayerRolls.Fail("The native multiplayer coroutine failed: "+__state.Coroutine.LastException.Message);
                RuntimeRolls.ReleaseNetworkHold();
                return;
            }
            if(RuntimeRolls.NetworkCompletionPending&&ReferenceEquals(RuntimeRolls.NetworkOwner,__state.Owner))
                RuntimeRolls.NetworkRunCompleted(__state.Owner);
        }
        static Exception RunFinalizer(Exception __exception,RunState __state){Pop(__state);return __exception;}
        internal static bool InOwnedRun
        {
            get
            {
                var owner=RuntimeRolls.NetworkOwner;
                return owner!=null&&executing!=null&&executing.Any(state=>ReferenceEquals(state.Owner,owner));
            }
        }
        static bool BeforeBanterUpdate()
        {
            if(RuntimeRolls.LocalHoldActive)return false;
            return !RuntimeRolls.NetworkHolding||InOwnedRun;
        }
        static void BeforeRandomDraw()
        {
            if(!Plugin.Networked()||TA.DeterministicRandom.LockFlags.Count!=0)return;
            if(MultiplayerRolls.Failed||!RuntimeRolls.NetworkHolding||InOwnedRun)return;
            // Inside a game tick every computer runs the same routines in the
            // same order (the game's lockstep), so their draws stay identical.
            if(InFixedStep)return;
            // Outside the ticks another routine draws while a roll is held.
            // Throwing would break that routine; manual dice pause instead.
            MultiplayerRolls.Fail("Another game routine rolled while a multiplayer roll was waiting.");
            RuntimeRolls.ReleaseNetworkHold();
        }
        internal static string RandomStamp()
        {
            if(randomWords==null){string error;if(!Preflight(out error))throw new InvalidOperationException(error);}
            var service=ServiceRepository.GetService<IGameService>();var game=service==null?null:service.Game;
            if(game==null||TA.DeterministicRandom.LockFlags.Count!=0)
                throw new InvalidOperationException("Multiplayer dice require an active deterministic game random state.");
            var rules=ServiceRepository.GetService<IRulesetImplementationService>();
            var settings=ServiceRepository.GetService<IGameSettingsService>();
            if(rules==null||settings==null)
                throw new InvalidOperationException("Multiplayer dice require the native random rules and settings services.");
            // Reading the stored per-game state performs no Unity RNG calls.
            object state=game.RandomState;
            var value=new StringBuilder(game.RandomSeed.ToString(CultureInfo.InvariantCulture));
            foreach(var word in randomWords)value.Append('/').Append(((int)word.GetValue(state)).ToString(CultureInfo.InvariantCulture));
            // These native fields can change the result independently of the
            // raw RNG state. Preserve adjacent floating-point values exactly.
            int luckBits=BitConverter.ToInt32(BitConverter.GetBytes(rules.DiceLuckScore),0);
            string mode=settings.RandomnessMode;
            value.Append('/').Append(luckBits.ToString(CultureInfo.InvariantCulture))
                .Append('/').Append(rules.LastCriticalCounter.ToString(CultureInfo.InvariantCulture))
                .Append('/').Append((mode==null?-1:mode.Length).ToString(CultureInfo.InvariantCulture)).Append(':').Append(mode??string.Empty)
                .Append('/').Append(settings.ForceCritEveryFewRolls?'1':'0');
            return Hash(Encoding.UTF8.GetBytes(value.ToString()));
        }
    }
}
