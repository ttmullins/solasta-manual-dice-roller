using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Native planners, GUI, and full-scene descriptors are declared boundaries.
    // Transport, consensus, request ownership, leases, scheduler, and raw dice
    // consumption below are the unchanged production sources.
    public static class Plugin
    {
        public static bool Enabled=true;
        public static bool Networked(){return true;}
        public static bool UnsupportedRulesMod{get{return false;}}
        public static readonly ModSettings Settings=new ModSettings();
        public sealed class ModSettings
        { public bool Attack=true,Ability=true,SavingThrows=true,Initiative=true,Death=true,Damage=true,Healing=true,SpellPools=true,HitDice=true,EnemyRolls=true,MultiplayerExtras=true,Concentration=true,Downtime=true,Lore=true,RepeatSaves=true,LingeringEffects=true,Features=true,Stealth=true,CharacterCreation=true,DamageSaves,HiddenPerception,LevelUpHitPoints,Traps=true; }
        public static bool Allows(RuleDefinitions.RollContext context){return true;}
        public static readonly List<string> Logs=new List<string>();
        public static void Log(string message){Logs.Add(message);}
        public static void Warn(string message){Logs.Add(message);}
    }
    public sealed class RollPrompt
    {
        public readonly string Title,Actor,Detail,Note;
        readonly int[] maxima;
        public RollPrompt(string title,string actor,string detail,string note,params int[] faces)
        {Title=title;Actor=actor;Detail=detail;Note=note;maxima=(int[])faces.Clone();}
        public int[] CopyDieFaces(){return (int[])maxima.Clone();}
    }
    public static class RollWindow
    {
        public static bool Visible{get;private set;}
        static RollPrompt prompt,waiting;
        static Action<int[]> accepted;
        static Action automatic;
        static int age;
        public static int Prompts,Waiting;
        public static readonly Queue<int> Inputs=new Queue<int>();
        public static bool AutoOnReroll;
        public static void Display(RollPrompt value,Action<int[]> accept,Action useNative,bool networkAuthorized=false)
        {
            if(Visible||!networkAuthorized)throw new InvalidOperationException("Unowned or overlapping multiplayer modal.");
            Visible=true;prompt=value;accepted=accept;automatic=useNative;age=0;Prompts++;
        }
        public static void Tick()
        {
            if(!Visible||++age<3)return;
            var current=prompt;var done=accepted;var native=automatic;
            var faces=current.CopyDieFaces();
            bool useAuto=AutoOnReroll&&current.Title.Contains("reroll");
            if(!useAuto)for(int i=0;i<faces.Length;i++) {
                if(Inputs.Count==0)throw new InvalidOperationException("The controlling peer has no physical face for this request.");
                faces[i]=Inputs.Dequeue();
            }
            Close(current);
            if(useAuto)native();else done(faces);
        }
        public static void Close(RollPrompt expected)
        {if(!ReferenceEquals(prompt,expected))return;Visible=false;prompt=null;accepted=null;automatic=null;}
        public static void Close(){if(prompt!=null)Close(prompt);}
        public static void DisplayWaiting(RollPrompt expected,string message){waiting=expected;Waiting++;}
        public static void CloseWaiting(RollPrompt expected){if(ReferenceEquals(waiting,expected))waiting=null;}
    }
    public static class EffectPatches
    {
        public static Func<NativeInvocation,NativeRollPlan,bool> Prepare;
        public static bool TryPrepare(NativeInvocation call,NativeRollPlan plan){return Prepare(call,plan);}
        public static bool IsEligibleNativeDraw(RulesetActor actor,RuleDefinitions.RollContext context,bool isStatic){return true;}
    }
    public static class RestPatches
    { public static bool CanConsumeNativeHitDie{get{return false;}}public static bool TryPrepare(NativeInvocation call,NativeRollPlan plan){return false;}public static void Reset(){} }
    public static class NativeSavePlanner
    { public static bool Previewing{get{return false;}}public static bool AllowsStaticBonus{get{return true;}}public static bool TryPrepare(NativeInvocation call,NativeRollPlan plan){return false;}public static void Install(Harmony harmony){} }
    public static class NativeAbilityPlanner
    { public static bool Previewing{get{return false;}}public static bool AllowsStaticBonus{get{return true;}}public static bool TryPrepare(NativeInvocation call,NativeRollPlan plan){return false;}public static void Install(Harmony harmony){} }
    internal static class NativeNetworkDescriptor
    {
        static string Number(object value){return Convert.ToString(value,CultureInfo.InvariantCulture);}
        // Identity: the site and the step (the fixture's "characters").
        internal static string Identity(NativeInvocation call){return call.Site+"/"+ProtocolProfile.Hash(call.Site+"/"+Number(call.Arg("round")));}
        internal static string ActionKey(NativeInvocation call,string state)
        {return ProtocolProfile.Hash(call.Site+"/"+Number(call.Arg("round"))+"/"+state);}
        internal static string Parts(NativeInvocation call,string state){return "rng="+ProtocolProfile.Hash(state).Substring(0,8)+" round="+Number(call.Arg("round"));}
        internal static string CompletedParts(NativeRollPlan plan,string state){return Parts(plan.Invocation,state)+" return="+Number(plan.NativeReturn);}
        internal static string Fallback(string identity,int ordinal){return ProtocolProfile.Hash("unavailable/"+identity+"/"+Number(ordinal));}
        internal static string Signature(NativeRollPlan plan)
        {
            var values=new List<string>{Identity(plan.Invocation)};
            foreach(var leaf in plan.Leaves){values.Add(Number(leaf.Ordinal));values.Add(Number(leaf.Die));values.Add(Number(leaf.Context));values.Add(Number(leaf.Advantage));values.Add(leaf.Static?"static":"actor");
                foreach(var f in leaf.Features)values.Add(f.Name+"/"+Number(f.MinRerollValue)+"/"+Number(f.RerollCount));}
            return ProtocolProfile.Hash(string.Join("/",values));
        }
        internal static string PromptSignature(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities)
        {return ProtocolProfile.Hash(plan.NetworkSignature+"/"+Number(leaf.Ordinal)+"/"+string.Join(";",identities.Select(x=>Number(x.DieIndex)+":"+Number(x.FaceIndex)+":"+Number(x.FeatureIndex)+":"+Number(x.RerollAttempt))));}
        internal static string CompletedSignature(NativeRollPlan plan,string state)
        {
            if(!plan.NativeObserved)throw new InvalidOperationException("The original native values were not observed.");
            return ProtocolProfile.Hash("completed/"+plan.NetworkKey+"/"+state+"/"+Number(plan.NativeReturn)+"/"+string.Join(";",plan.NativeOutputs.Select(Number))+"/"+plan.NetworkNativeManual);
        }
    }
}
