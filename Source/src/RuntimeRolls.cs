using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ManualDiceRoller
{
    // One occurrence of one original call in one original iterator. Reference
    // identity deliberately distinguishes two effects/forms with equal values.
    public sealed class NativeInvocation
    {
        public readonly object Owner;
        public readonly string Site;
        public readonly MethodBase Method;
        public readonly object[] Args;
        public readonly long Generation;
        public object Receiver { get { return Method.IsStatic ? null : Args[0]; } }
        public NativeInvocation(object owner, string site, MethodBase method, object[] args, long generation)
        { Owner=owner; Site=site; Method=method; Args=(object[])args.Clone(); Generation=generation; }
        public object Arg(string name)
        {
            var parameters=Method.GetParameters();
            for(int i=0;i<parameters.Length;i++) if(parameters[i].Name==name) return Args[i+(Method.IsStatic?0:1)];
            return null;
        }
        public T Arg<T>(string name,T fallback=default(T))
        { object value=Arg(name); return value is T ? (T)value : fallback; }
    }

    public sealed class NativeRollPlan
    {
        internal sealed class Leaf
        {
            internal RulesetActor Actor, DisplayActor;
            internal object Form;
            internal RuleDefinitions.DieType Die;
            internal RuleDefinitions.RollContext Context, RequestCategory;
            internal RuleDefinitions.AdvantageType Advantage;
            internal bool Proficient, CanReroll, Static, Enumerate;
            internal string Skill,DisplaySkill,Title,Note,Hint,Role,StateStamp;
            // Display only: a hint that depends on dice entered earlier in the
            // same plan (a trap's damage after its saving throw). Null: Hint.
            internal Func<string> HintAt;
            internal RollBinding Binding;
            internal int Ordinal,NextDraw,GroupStart,GroupCount;
            internal bool Used;
            // Composite plans (several native calls replayed in one step) bind
            // each part's dice to the object the game is processing when it
            // rolls them (a condition, an effect). Null: any point of the call.
            internal object Anchor;
            // The planned native call this die belongs to; null means Target.
            internal NativeInvocation PartTarget;
            // Display-only context for the roll window. It never changes the
            // native calculation; an unknown value is simply not shown.
            internal int DisplayDC;
            internal bool HasDisplayBonus;
            internal int DisplayBonus;
            internal List<FeatureDefinitionDieRollModifier> Features;
            // A group asked only if this holds once the earlier groups' faces
            // are known (food yield after a successful search). It reads only
            // shared state and agreed faces, so every peer decides alike. A
            // skipped group is marked used and never consumes a native draw.
            internal Func<bool> When;
            internal bool Skipped;
            // The game master's die (enemy and NPC dice with no creature to
            // name): asked only with enemy dice on, entered by the room host.
            internal bool MasterOwned;
            internal string ActorLabel;
            internal readonly List<FaceIdentity> Identities=new List<FaceIdentity>();
            internal readonly List<int> Faces=new List<int>();
            internal readonly List<int> DrawCounts=new List<int>();
        }
        internal readonly NativeInvocation Invocation;
        // The original call whose dice are planned. For an ordinary await site
        // this is the awaited call itself; for an outer site (a call that
        // eventually performs the roll) it is the resolved inner native call.
        internal NativeInvocation Target;
        internal readonly List<Leaf> Leaves=new List<Leaf>();
        internal RollSession Session;
        internal RollPrompt DisplayedPrompt;
        internal bool NativeFallback, Ready, Networked, NetworkAdmitted, NativeFinished, NetworkNativeManual, NetworkSupported;
        // The native coroutine that reached this shared roll.
        internal object NetworkRun;
        internal string NetworkKey, NetworkSignature;
        internal int NetworkOrdinal;
        internal readonly Dictionary<RulesetActor,int> NetworkOwners=new Dictionary<RulesetActor,int>();
        internal bool NativeObserved;
        // Outer and deferred sites run more native code than the planned inner
        // roll. An unrelated die there (another actor, context or skill) is
        // left to the game instead of revoking the planned dice.
        internal bool Scoped;
        internal object NativeReturn;
        internal object[] NativeOutputs;
        internal int PotentialFaces;
        internal object CurrentForm;
        internal object CurrentAnchor;
        internal Func<bool> NativeStateValid;
        internal Func<bool> ManualStateValid;
        // Run once every face is known and the native step is about to resume
        // with manual dice (never on fallback): applies entered dice that the
        // game had already rolled into a modifier before the await (Bless).
        internal readonly List<Action> OnReady=new List<Action>();
        public NativeRollPlan(NativeInvocation invocation) { Invocation=invocation; Target=invocation; }
        // Ask the most recently added dice group only when the condition holds.
        internal void AskWhen(Func<bool> condition)
        {
            if(condition==null||Leaves.Count==0) return;
            var last=Leaves[Leaves.Count-1];
            for(int i=last.GroupStart;i<last.GroupStart+last.GroupCount&&i<Leaves.Count;i++) Leaves[i].When=condition;
        }
        // The faces accepted so far for a group (empty until it was asked).
        internal int[] GroupFaces(int groupStart)
        {
            var result=new List<int>();
            if(groupStart<0||groupStart>=Leaves.Count) return result.ToArray();
            var first=Leaves[groupStart];
            for(int i=first.GroupStart;i<first.GroupStart+first.GroupCount&&i<Leaves.Count;i++) result.AddRange(Leaves[i].Faces);
            return result.ToArray();
        }
        // The final face of an actor die after its accepted rerolls, taking the
        // higher/lower face with advantage/disadvantage; null until asked.
        internal int? FinalFace(int leafIndex)
        {
            if(leafIndex<0||leafIndex>=Leaves.Count) return null;
            var leaf=Leaves[leafIndex];
            int n=leaf.Advantage==RuleDefinitions.AdvantageType.None?1:2;
            if(leaf.Faces.Count<n||leaf.Identities.Count!=leaf.Faces.Count) return null;
            var simulated=leaf.Faces.Take(n).ToArray();
            for(int i=n;i<leaf.Faces.Count;i++) { int index=leaf.Identities[i].FaceIndex; if(index>=0&&index<n) simulated[index]=leaf.Faces[i]; }
            if(n==1) return simulated[0];
            return leaf.Advantage==RuleDefinitions.AdvantageType.Advantage?Math.Max(simulated[0],simulated[1]):Math.Min(simulated[0],simulated[1]);
        }
        // Would the check of this d20 leaf meet its displayed DC? Used to ask
        // follow-up dice only after a success (a mismatch only means the game
        // rolls them itself or an entered die stays unused).
        internal bool CheckSucceeds(int leafIndex)
        {
            var face=FinalFace(leafIndex);
            if(face==null) return false;
            var leaf=Leaves[leafIndex];
            int bonus=leaf.HasDisplayBonus?leaf.DisplayBonus:0;
            for(int i=0;i<Leaves.Count;i++)
            {
                var other=Leaves[i];
                if(other.Static&&ReferenceEquals(other.PartTarget,leaf.PartTarget)&&other.Ordinal<leafIndex) bonus+=other.Faces.Sum();
            }
            return face.Value+bonus>=leaf.DisplayDC;
        }
        // Display only: the dice group just added for `form` is damage the
        // game halves (or skips) if `target`'s saving throw, asked earlier in
        // this plan, succeeds. Once that save's faces are in, say which.
        internal void HintAfterSave(object form,RulesetActor target,bool half,string targetName)
        {
            if(Leaves.Count==0||!ReferenceEquals(Leaves[Leaves.Count-1].Form,form)) return;
            var last=Leaves[Leaves.Count-1];
            int save=-1;
            for(int i=last.GroupStart-1;i>=0;i--)
            {
                var leaf=Leaves[i];
                if(!leaf.Static&&leaf.Context==RuleDefinitions.RollContext.SavingThrow&&ReferenceEquals(leaf.DisplayActor,target)) { save=i; break; }
            }
            string who=string.IsNullOrEmpty(targetName)?"The target":targetName;
            string effect=half?"the game halves this damage":"this damage is not dealt";
            string either="If "+(string.IsNullOrEmpty(targetName)?"the target":targetName)+" made the saving throw, "+effect+".";
            Func<string> hint=delegate {
                if(save<0||save>=Leaves.Count) return either;
                var saveLeaf=Leaves[save];
                if(FinalFace(save)==null||!saveLeaf.HasDisplayBonus||saveLeaf.DisplayDC<=0) return either;
                return CheckSucceeds(save)?who+" made the saving throw: "+effect+".":who+" failed the saving throw: full damage.";
            };
            for(int i=last.GroupStart;i<last.GroupStart+last.GroupCount&&i<Leaves.Count;i++) Leaves[i].HintAt=hint;
        }
        // The game master's static dice (see Leaf.MasterOwned).
        public void AddMasterDice(RulesetActor nameActor,string label,RuleDefinitions.DieType die,int count,string title=null,string note=null,
            RuleDefinitions.RollContext category=RuleDefinitions.RollContext.None,string hint=null)
        {
            if(nameActor==null||!RuntimeRolls.CanManualMaster(category)||count<=0||RuntimeRolls.Max(die)<=1) return;
            if(count>256) throw new InvalidOperationException("The requested dice count exceeds the supported safe limit.");
            int start=Leaves.Count;
            for(int i=0;i<count;i++)
            {
                Add(null,nameActor,die,RuleDefinitions.RollContext.None,RuleDefinitions.AdvantageType.None,false,"",false,null,title,note,true,false,start,count);
                var leaf=Leaves[Leaves.Count-1];
                leaf.RequestCategory=category; leaf.Hint=hint??""; leaf.MasterOwned=true; leaf.ActorLabel=label??"";
            }
        }
        // Attach display context to the most recently added dice group.
        // Renames the plan's d20 leaves for a site-specific heading.
        internal void Label(string title,string detail) { LabelRange(0,title,detail); }
        internal void LabelRange(int start,string title,string detail)
        {
            if(string.IsNullOrEmpty(title)&&string.IsNullOrEmpty(detail)) return;
            for(int index=start<0?0:start;index<Leaves.Count;index++)
            {
                var leaf=Leaves[index];
                if(leaf.Static||leaf.Die!=RuleDefinitions.DieType.D20) continue;
                if(!string.IsNullOrEmpty(title)) leaf.Title=title;
                if(!string.IsNullOrEmpty(detail)&&(leaf.Note==null||leaf.Note.IndexOf(detail,StringComparison.Ordinal)<0)) leaf.Note=string.IsNullOrEmpty(leaf.Note)?detail:leaf.Note+" · "+detail;
            }
        }
        internal void Describe(int dc,bool hasBonus,int bonus)
        {
            if(Leaves.Count==0) return;
            var last=Leaves[Leaves.Count-1];
            for(int i=last.GroupStart;i<last.GroupStart+last.GroupCount&&i<Leaves.Count;i++)
            { Leaves[i].DisplayDC=dc>0?dc:0; Leaves[i].HasDisplayBonus=hasBonus; Leaves[i].DisplayBonus=bonus; }
        }
        public void RequireNativeState(Func<bool> validator)
        {
            if(validator==null) throw new ArgumentNullException("validator");
            var previous=NativeStateValid;
            NativeStateValid=previous==null?validator:(Func<bool>)(()=>previous()&&validator());
        }
        public void RequireManualState(Func<bool> validator)
        {
            if(validator==null) throw new ArgumentNullException("validator");
            var previous=ManualStateValid;
            ManualStateValid=previous==null?validator:(Func<bool>)(()=>previous()&&validator());
        }
        public void AddActorDice(RulesetActor actor,RuleDefinitions.DieType die,RuleDefinitions.RollContext context,int count,
            RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None,bool proficient=false,string skill="",bool canReroll=true,
            object form=null,string title=null,string note=null,bool enumerate=true,string hint=null)
        {
            if(actor==null||!RuntimeRolls.CanManual(actor,context)||count<=0||RuntimeRolls.Max(die)<=1) return;
            if(count>256) throw new InvalidOperationException("The requested dice count exceeds the supported safe limit.");
            int start=Leaves.Count;
            for(int i=0;i<count;i++) { Add(actor,actor,die,context,advantage,proficient,skill,canReroll,form,title,note,false,enumerate,start,count); Leaves[Leaves.Count-1].Hint=hint??""; }
        }
        public void AddStaticDice(RulesetActor displayActor,RuleDefinitions.DieType die,int count,object form=null,string title=null,string note=null,
            RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None,string role=null,RuleDefinitions.RollContext category=RuleDefinitions.RollContext.None,string hint=null)
        {
            if(!RuntimeRolls.CanManual(displayActor,category)||count<=0||RuntimeRolls.Max(die)<=1) return;
            if(count>256) throw new InvalidOperationException("The requested dice count exceeds the supported safe limit.");
            int start=Leaves.Count;
            for(int i=0;i<count;i++)
            {
                Add(null,displayActor,die,RuleDefinitions.RollContext.None,advantage,false,"",false,form,title,note,true,false,start,count);
                Leaves[Leaves.Count-1].RequestCategory=category;
                Leaves[Leaves.Count-1].Hint=hint??"";
                if(role!=null)
                {
                    var leaf=Leaves[Leaves.Count-1]; leaf.Role=role;
                    leaf.Binding=new RollBinding(displayActor,Invocation.Owner,Invocation,form,leaf.Role,RuntimeRolls.Max(die),leaf.Ordinal,leaf.StateStamp);
                }
            }
        }
        void Add(RulesetActor actor,RulesetActor display,RuleDefinitions.DieType die,RuleDefinitions.RollContext context,
            RuleDefinitions.AdvantageType advantage,bool proficient,string skill,bool canReroll,object form,string title,string note,bool isStatic,bool enumerate,int groupStart,int groupCount)
        {
            var leaf=new Leaf {Actor=actor,DisplayActor=display,Die=die,Context=context,RequestCategory=context,Advantage=advantage,Proficient=proficient,
                Skill=skill??"",CanReroll=canReroll,Form=form,Title=title??context.ToString(),Note=note??"",Static=isStatic,Enumerate=enumerate,
                Ordinal=Leaves.Count,GroupStart=groupStart,GroupCount=groupCount,Role=context+"/"+(skill??"")};
            leaf.Features=RuntimeRolls.Features(actor);
            // Bound the complete plan, including every possible replacement,
            // before showing its first prompt. A feature cannot ask the player
            // for an unbounded sequence which the broker would later reject.
            long possible=advantage==RuleDefinitions.AdvantageType.None?1:2;
            if(!isStatic&&canReroll)
                foreach(var feature in leaf.Features)
                    if((feature.ValidityContext&context)!=0&&feature.MinRerollValue>0&&feature.RerollCount>0)
                        possible=checked(possible+(advantage==RuleDefinitions.AdvantageType.None?1L:2L)*feature.RerollCount);
            if(possible>RollBroker.MaxFacesPerSession-PotentialFaces)
                throw new InvalidOperationException("The native roll exceeds the supported face budget.");
            PotentialFaces+=checked((int)possible);
            leaf.StateStamp=Invocation.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)+"/"+RuntimeRolls.FeatureStamp(leaf.Features);
            leaf.Binding=new RollBinding(actor??(object)display,Invocation.Owner,Invocation,form,leaf.Role,RuntimeRolls.Max(die),leaf.Ordinal,leaf.StateStamp);
            Leaves.Add(leaf);
        }
    }

    // An outer await site suspends a native iterator before a call that will
    // later perform an ability check or saving throw synchronously (a dialog
    // state change, a gadget functor). Its resolver reproduces only the pure
    // argument selection of that inner call, so the existing planners preview
    // the original calculation. If the replica disagrees with the game, the
    // inner roll's actor/skill/advantage binding does not match the prepared
    // die and the game rolls automatically; a die is never applied elsewhere.
    internal sealed class OuterSite
    {
        internal readonly string Id;
        // Kept native in multiplayer unless the shared extended-rolls setting
        // (part of every peer's profile) is on.
        internal readonly bool SinglePlayerOnly;
        // Optional window heading for the d20 this site asks for.
        internal string Title;
        // Optional heading chosen from the resolved inner call (one outer
        // call can lead to different rolls). Null keeps Title.
        internal Func<NativeInvocation,string> Titler;
        // A site on a very common path (every action) does not enter the
        // multiplayer agreement when its resolver finds no roll at all. The
        // resolver reads only shared game state, so every peer skips alike.
        internal bool SkipUnresolved;
        readonly Func<NativeInvocation,NativeInvocation> resolver;
        internal OuterSite(string id,bool singlePlayerOnly,Func<NativeInvocation,NativeInvocation> resolve)
        { Id=id; SinglePlayerOnly=singlePlayerOnly; resolver=resolve; }
        internal NativeInvocation Resolve(NativeInvocation outer)
        {
            try { return resolver(outer); }
            catch(Exception error) { Plugin.Log("Manual preparation skipped at "+Id+": "+error.Message); return null; }
        }
    }

    // Display categories of a prompt: the window colours its accents by kind
    // (healing green, damage red, saves blue...). Display only.
    public static class RollKinds
    {
        public const string Attack="attack", Check="check", Save="save", Death="death", Initiative="initiative",
            Damage="damage", Healing="healing", TemporaryHitPoints="temphp", Feature="feature", Pool="pool", Other="other";
        public static string Of(RuleDefinitions.RollContext context,string title)
        {
            switch(context)
            {
                case RuleDefinitions.RollContext.AttackRoll: return Attack;
                case RuleDefinitions.RollContext.AbilityCheck: return Check;
                case RuleDefinitions.RollContext.SavingThrow:
                case RuleDefinitions.RollContext.ConcentrationCheck: return Save;
                case RuleDefinitions.RollContext.DeathSavingThrow: return Death;
                case RuleDefinitions.RollContext.InitiativeRoll: return Initiative;
                case RuleDefinitions.RollContext.AttackDamageValueRoll:
                case RuleDefinitions.RollContext.MagicDamageValueRoll: return Damage;
                case RuleDefinitions.RollContext.HealValueRoll: return Healing;
                case RuleDefinitions.RollContext.TemporaryHPValueRoll: return TemporaryHitPoints;
            }
            if((int)context==0x200) return Damage;
            if((int)context==0x100) return Feature;
            string text=(title??"").ToLowerInvariant();
            if(text.Contains("damage")) return Damage;
            if(text.Contains("heal")||text.Contains("hit die")||text.Contains("hit dice")) return Healing;
            if(text.Contains("temporary")) return TemporaryHitPoints;
            if(text.Contains("pool")) return Pool;
            if(text.Contains("save")||text.Contains("saving")) return Save;
            if(text.Contains("attack")) return Attack;
            if(text.Contains("check")||text.Contains("stealth")||text.Contains("perception")) return Check;
            return Other;
        }
    }

    // Display-only context passed to the roll window.
    public sealed class PromptInfo
    {
        public int Advantage, Target, Bonus;
        public bool IsReroll, HasBonus;
        public string TargetLabel="DC";
        // Display category for the window's colour coding (see RollKinds).
        public string Kind=RollKinds.Other;
    }

    public static class RuntimeRolls
    {
        static readonly RollBroker broker=new RollBroker();
        static ConditionalWeakTable<object,NativeRollPlan> plans=new ConditionalWeakTable<object,NativeRollPlan>();
        static readonly List<NativeRollPlan> alive=new List<NativeRollPlan>();
        static long nextGeneration;
        static NativeRollPlan networkOwner, networkCompleted;
        static long networkExitStarted;
        static readonly Stack<IEnumerator> completionStack=new Stack<IEnumerator>();
        public static object NetworkOwner { get { return networkOwner==null?null:networkOwner.Invocation.Owner; } }
        public static bool NetworkHolding { get { return networkOwner!=null; } }
        public static bool NetworkCompletionPending { get { return networkCompleted!=null; } }
        // ---- Lockstep ----------------------------------------------------
        // A shared roll is decided between two game ticks. The game coroutine
        // that reached it waits at the gate; the decision itself is pumped once
        // per rendered frame while every computer's tick count stays stopped
        // (FreezeTicks), and the roll runs at the next tick on every computer.
        sealed class NetworkWait { internal NativeRollPlan Plan; internal readonly Stack<IEnumerator> Stack=new Stack<IEnumerator>(); internal bool Done; }
        static readonly List<NetworkWait> networkWaits=new List<NetworkWait>();
        // Multiplayer deferred rolls (concentration, repeat saves) are pending.
        internal static Func<bool> DeferredNetworked;
        static bool DeferredNetworkedActive { get { var hook=DeferredNetworked; try { return hook!=null&&hook(); } catch(Exception) { return false; } } }
        internal static bool FreezeTicks
        {
            get
            {
                if(!Plugin.Networked()) return false;
                // Queued deferred rolls finish (with the game's dice once
                // paused) before the next tick, on every computer.
                if(DeferredNetworkedActive) return true;
                if(MultiplayerRolls.Failed) return false;
                return networkWaits.Count>0||networkCompleted!=null||completionStack.Count>0;
            }
        }
        // Outside the pending shared step (its own native run excepted) no new
        // deferred roll may queue: it would wait on a step that needs a tick.
        internal static bool NetworkStepOpen { get { return networkOwner==null||NativeNetworkScheduler.InOwnedRun; } }
        sealed class ActiveCall { internal object Owner; internal string Site; internal NativeRollPlan Plan; }
        [ThreadStatic] static Stack<ActiveCall> active;
        [ThreadStatic] static Stack<ActorFrame> actors;
        sealed class ActorFrame { internal NativeRollPlan.Leaf Leaf; internal NativeRollLease Lease; internal FeatureDefinitionDieRollModifier RerollFeature; internal bool Manual; }
        // True inside RulesetActor.RollDie once this outer draw used entered faces.
        internal static bool CurrentDrawWasManual { get { return actors!=null&&actors.Count>0&&actors.Peek().Manual; } }
        internal static bool IsPartyMember(RulesetActor actor)
        {
            actor=Controller(actor);
            var character=actor as RulesetCharacter;
            return actor is RulesetCharacterHero || character!=null&&character.IsSubstitute&&character.OriginalFormCharacter is RulesetCharacterHero;
        }
        // A spell's effect proxy (Flaming Sphere, a summoned weapon, a moving
        // Moonbeam) rolls for the character controlling it.
        internal static RulesetActor Controller(RulesetActor actor)
        {
            var map=ControllerOf;
            if(map==null||actor==null) return actor;
            try { return map(actor)??actor; }
            catch(Exception) { return actor; }
        }
        [ThreadStatic] static Stack<bool> staticSums;

        // ---- extension points (wired by the plugin's ModFeatures) ----------
        static readonly Dictionary<string,OuterSite> outerSites=new Dictionary<string,OuterSite>(StringComparer.Ordinal);
        internal static void RegisterOuterSite(OuterSite site)
        { if(site==null) throw new ArgumentNullException("site"); outerSites[site.Id]=site; }
        internal static OuterSite FindOuterSite(string id) { OuterSite site; return id!=null&&outerSites.TryGetValue(id,out site)?site:null; }
        // Nonzero while the game pumps an iterator synchronously on this thread.
        [ThreadStatic] internal static int SyncPumpDepth;
        internal static readonly List<Func<NativeInvocation,NativeRollPlan,bool>> ExtraPlanners=new List<Func<NativeInvocation,NativeRollPlan,bool>>();
        internal static Func<bool> ExtraPreviewing;
        internal static bool ExtraPreviewingActive { get { var hook=ExtraPreviewing; return hook!=null&&hook(); } }
        // Single-player deferred rolls hold native action coroutines.
        internal static Func<bool> LocalHold;
        internal static bool LocalHoldActive { get { var hook=LocalHold; try { return hook!=null&&hook(); } catch(Exception) { return false; } } }
        internal static Func<string,string,string,string,int[],PromptInfo,RollPrompt> PromptFactory;
        // Multiplayer use of the single-player extras (outer and deferred
        // sites). The setting is part of the shared multiplayer profile.
        internal static Func<bool> ExtendedOnline;
        internal static bool ExtendedOnlineActive { get { var hook=ExtendedOnline; try { return hook!=null&&hook(); } catch(Exception) { return false; } } }
        // The one native coroutine allowed to run while deferred rolls hold
        // the others: the multiplayer runner of the front deferred roll.
        internal static Func<object,bool> HoldExempt;
        internal static bool IsHoldExempt(object coroutine) { var hook=HoldExempt; try { return hook!=null&&hook(coroutine); } catch(Exception) { return false; } }
        static bool TryExtraPlanners(NativeInvocation call,NativeRollPlan plan)
        {
            foreach(var planner in ExtraPlanners.ToArray()) if(planner(call,plan)) return true;
            return false;
        }

        public static int Max(RuleDefinitions.DieType die)
        { int index=(int)die; return index>=0&&index<RuleDefinitions.DiceMaxValue.Length ? RuleDefinitions.DiceMaxValue[index] : 0; }
        public static bool CanManual(RulesetActor actor,RuleDefinitions.RollContext context)
        {
            if(!Plugin.Enabled||Plugin.UnsupportedRulesMod) return false;
            if(actor==null) return false;
            if(!IsPartyMember(actor)&&!Plugin.Settings.EnemyRolls) return false;
            return Plugin.Allows(context);
        }
        // The game master's dice: only with enemy dice enabled.
        public static bool CanManualMaster(RuleDefinitions.RollContext context)
        {
            if(!Plugin.Enabled||Plugin.UnsupportedRulesMod||!Plugin.Settings.EnemyRolls) return false;
            return Plugin.Allows(context);
        }
        static bool LeafAllowed(NativeRollPlan.Leaf leaf)
        { return leaf.MasterOwned?CanManualMaster(leaf.RequestCategory):CanManual(leaf.DisplayActor,leaf.RequestCategory); }
        internal static List<FeatureDefinitionDieRollModifier> Features(RulesetActor actor)
        {
            var result=new List<FeatureDefinitionDieRollModifier>();
            if(actor==null) return result;
            var scratch=new List<FeatureDefinition>();
            using(var scope=NativeScratchScope.ForActor(actor))
            {
                var character=actor as RulesetCharacter;
                if(character!=null&&character.IsSubstitute) scope.AddActor(character.OriginalFormCharacter);
                actor.EnumerateFeaturesToBrowse<IDieRollModificationProvider>(scratch);
            }
            foreach(var feature in scratch)
            {
                var modifier=feature as FeatureDefinitionDieRollModifier;
                if(modifier==null) throw new InvalidOperationException("An unsupported dice modifier was found.");
                result.Add(modifier);
            }
            return result;
        }
        internal static string FeatureStamp(IEnumerable<FeatureDefinitionDieRollModifier> features)
        {
            return string.Join(";",features.Select(f=>RuntimeHelpers.GetHashCode(f)+":"+(int)f.ValidityContext+":"+
                f.RequireProficiency+":"+f.MinRollValue+":"+f.MaxRollValue+":"+f.MinRerollValue+":"+f.RerollCount+":"+
                (int)f.ForcedDamageOnDice+":"+string.Join(",",f.ValidSkills.ToArray())).ToArray());
        }

        public static IEnumerator PrepareNativeRoll(object iterator,string site,MethodBase method,object[] args)
        {
            var outer=FindOuterSite(site);
            return Prepare(iterator,site,method,args,outer==null?null:(Func<NativeInvocation,NativeInvocation>)outer.Resolve,
                outer!=null&&outer.SinglePlayerOnly,outer==null?null:outer.Title,null,outer==null?null:outer.Titler,outer!=null&&outer.SkipUnresolved);
        }
        // A deferred synchronous roll: the skipped original call is the native
        // invocation (its identity, arguments and observed result); the probed
        // inner call is what the planners prepare.
        internal static IEnumerator PrepareDeferredRoll(object owner,string site,MethodBase outerMethod,object[] outerArgs,
            MethodBase targetMethod,object[] targetArgs,string title,string detail)
        {
            if(targetMethod==null||targetArgs==null) return null;
            return Prepare(owner,site,outerMethod,outerArgs,
                outer=>new NativeInvocation(outer.Owner,outer.Site,targetMethod,targetArgs,outer.Generation),true,title,detail,null,false);
        }
        // The planner chain for one native call. Composite plans call it once
        // for each of their parts.
        internal static bool PlanTarget(NativeInvocation target,NativeRollPlan plan)
        {
            int start=plan.Leaves.Count;
            bool planned=PrepareD20(target,plan)||NativeAbilityPlanner.TryPrepare(target,plan)||EffectPatches.TryPrepare(target,plan)||
                RestPatches.TryPrepare(target,plan)||NativeSavePlanner.TryPrepare(target,plan)||BardicPatches.TryPrepare(target,plan)||
                ShovePatches.TryPrepare(target,plan)||TryExtraPlanners(target,plan);
            // Display only: what these dice are for (see RollReasons).
            var explain=Explainer;
            if(explain!=null&&plan.Leaves.Count>start)
                try { explain(target,plan,start); } catch(Exception error) { Plugin.Log("A roll description was skipped: "+error.Message); }
            return planned;
        }
        internal static Action<NativeInvocation,NativeRollPlan,int> Explainer;
        static string HintText(NativeRollPlan.Leaf leaf)
        {
            var dynamic=leaf.HintAt;
            if(dynamic!=null) { try { var text=dynamic(); if(!string.IsNullOrEmpty(text)) return text; } catch(Exception) { } }
            return leaf.Hint;
        }
        static IEnumerator Prepare(object iterator,string site,MethodBase method,object[] args,
            Func<NativeInvocation,NativeInvocation> resolve,bool singlePlayerOnly,string title,string detail,
            Func<NativeInvocation,string> titler,bool skipUnresolved)
        {
            bool online=Plugin.Networked();
            if(!online&&(!Plugin.Enabled||Plugin.UnsupportedRulesMod)) return null;
            // Manual multiplayer dice are paused: the game rolls everything.
            if(online&&MultiplayerRolls.Failed) return null;
            // Single-player-only sites never enter the multiplayer agreement
            // unless every peer's profile enables extended multiplayer rolls.
            // Every peer runs the same build and reaches the same decision here.
            if(resolve!=null&&singlePlayerOnly&&online&&!ExtendedOnlineActive) return null;
            // A synchronous pump (IEnumerator.ExecuteUntilDone, RunUntilIsFinished)
            // would spin forever on a modal wait. Such frames keep native dice.
            if(SyncPumpDepth>0) return null;
            CancelOwner(iterator);
            var call=new NativeInvocation(iterator,site,method,args,checked(++nextGeneration));
            var plan=new NativeRollPlan(call) { Networked=online };
            try
            {
                var target=call;
                if(resolve!=null)
                {
                    plan.Scoped=true;
                    try { target=resolve(call); }
                    catch(Exception error) { Plugin.Log("Manual preparation skipped at "+site+": "+error.Message); target=null; }
                    if(target!=null) plan.Target=target;
                    // No roll follows on this common path: nothing to agree on.
                    else if(skipUnresolved) return null;
                    if(target!=null&&titler!=null)
                    {
                        try { title=titler(target)??title; }
                        catch(Exception) { }
                    }
                }
                bool prepared=target!=null&&Plugin.Enabled&&!Plugin.UnsupportedRulesMod&&PlanTarget(target,plan);
                if(!prepared||plan.Leaves.Count==0) plan.NativeFallback=true;
                else plan.Label(title,detail);
                if(!online&&plan.NativeFallback) return null;
                if(!plan.NativeFallback) plan.Session=broker.BeginSession(iterator);
            }
            catch(Exception error)
            {
                if(plan.Session!=null) plan.Session.Dispose();
                plan.NativeFallback=true;
                Plugin.Log("Manual preparation was safely bypassed at "+site+": "+error.Message);
                if(!online) return null;
            }
            plans.Add(iterator,plan); alive.Add(plan);
            return online?NetworkGate(plan):Collect(plan,iterator);
        }
        // The game coroutine's side of a shared roll. The first decision step
        // runs inside the native run that reached the roll (it claims that
        // coroutine and holds the others for the rest of this tick). The game
        // always continues at a later tick, never within this one, so the
        // native call runs at the same tick on every computer.
        static IEnumerator NetworkGate(NativeRollPlan plan)
        {
            NetworkWait wait=null;
            try
            {
                plan.NetworkRun=NativeNetworkScheduler.CurrentRun;
                wait=new NetworkWait{Plan=plan};
                wait.Stack.Push(CollectNetwork(plan));
                networkWaits.Add(wait);
                PumpWait(wait);
                do yield return null; while(!wait.Done);
            }
            finally
            {
                if(wait!=null) { networkWaits.Remove(wait); if(!wait.Done) DisposeWait(wait); }
            }
        }
        static void PumpWait(NetworkWait wait)
        {
            if(wait.Done) return;
            // The game canceled the roll (its coroutine was reset): the
            // decision ends here and nothing keeps the ticks stopped.
            NativeRollPlan live;
            if(!plans.TryGetValue(wait.Plan.Invocation.Owner,out live)||!ReferenceEquals(live,wait.Plan)) { DisposeWait(wait); return; }
            try
            {
                for(int budget=0;budget<256&&wait.Stack.Count>0;budget++)
                {
                    var top=wait.Stack.Peek();
                    if(!top.MoveNext()) { wait.Stack.Pop(); var finished=top as IDisposable; if(finished!=null) finished.Dispose(); continue; }
                    var child=top.Current as IEnumerator;
                    if(child!=null) { wait.Stack.Push(child); continue; }
                    return;
                }
                if(wait.Stack.Count==0) { wait.Done=true; networkWaits.Remove(wait); }
            }
            catch(Exception error)
            {
                DisposeWait(wait);
                Pause("A multiplayer roll could not be decided: "+error.Message);
                GameDice(wait.Plan);
            }
        }
        static void DisposeWait(NetworkWait wait)
        {
            wait.Done=true; networkWaits.Remove(wait);
            while(wait.Stack.Count>0) { var disposable=wait.Stack.Pop() as IDisposable; if(disposable!=null) try { disposable.Dispose(); } catch(Exception) { } }
        }
        // Once per rendered frame: advance every pending shared decision.
        internal static void PumpNetwork()
        {
            if(networkWaits.Count==0) return;
            foreach(var wait in networkWaits.ToArray()) PumpWait(wait);
        }
        static bool StateValid(NativeRollPlan plan)
        {
            if(!OwnersValid(plan)) return false;
            try {
                if(!PlanAllowed(plan)||(plan.NativeStateValid!=null&&!plan.NativeStateValid())) return false;
                var stamps=new Dictionary<RulesetActor,string>();
                foreach(var leaf in plan.Leaves) {
                    if(leaf.Static) continue;
                    string stamp;
                    if(!stamps.TryGetValue(leaf.Actor,out stamp)) {stamp=FeatureStamp(Features(leaf.Actor));stamps.Add(leaf.Actor,stamp);}
                    if(leaf.StateStamp!=plan.Invocation.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)+"/"+stamp) return false;
                }
                return true;
            }
            catch(Exception error) { Plugin.Log("The native roll snapshot could not be verified: "+error.Message); return false; }
        }
        // A multiplayer roll never throws into the game's own coroutine (the
        // game reports any exception as a mod instability). When the players
        // cannot agree, manual dice pause for the session and this roll, like
        // every later one, uses the game's dice; nobody leaves the room.
        static IEnumerator CollectNetwork(NativeRollPlan plan)
        {
            bool complete=false;
            try
            {
                // Hold the native coroutine family through the entire action
                // step, not merely through a single UI ticket.
                while(networkOwner!=null) {
                    if(MultiplayerRolls.Failed||!Plugin.Networked()) { GameDice(plan); complete=true; yield break; }
                    yield return null;
                }
                if(MultiplayerRolls.Failed||!Plugin.Networked()) { GameDice(plan); complete=true; yield break; }
                networkOwner=plan;
                string failure=Admit(plan);
                if(failure!=null) { Pause(failure); GameDice(plan); complete=true; yield break; }
                bool decided=false, automatic=true;
                // Same native call and random state (the action key) but a
                // different local plan: every player lets the game roll it.
                yield return MultiplayerRolls.Rendezvous(plan.NetworkKey,-1,plan.NetworkSignature,null,null,
                    plan.NetworkSupported,values=>{},value=>{automatic=value;decided=true;},allowAutomaticOnMismatch:true);
                if(!decided) { Pause("Multiplayer admission ended without a shared decision."); GameDice(plan); complete=true; yield break; }
                if(MultiplayerRolls.Failed) { GameDice(plan); complete=true; yield break; }
                plan.NetworkAdmitted=true;
                if(!automatic) {
                    for(int i=0;i<plan.Leaves.Count;) {
                        var leaf=plan.Leaves[i];
                        var group=plan.Leaves.Skip(i).Take(leaf.GroupCount).ToArray();
                        i+=group.Length;
                        if(SkipGroup(plan,group)) continue;
                        yield return CollectGroup(plan,group);
                        if(plan.NativeFallback) break;
                    }
                    if(MultiplayerRolls.Failed) { GameDice(plan); complete=true; yield break; }
                    if(!plan.NativeFallback) {
                        decided=false; automatic=true;
                        string signature; bool supported;
                        failure=Handoff(plan,out signature,out supported);
                        if(failure!=null) { Pause(failure); GameDice(plan); complete=true; yield break; }
                        yield return MultiplayerRolls.Rendezvous(plan.NetworkKey,plan.NetworkOrdinal++,signature,null,null,
                            supported,values=>{},value=>{automatic=value;decided=true;});
                        if(!decided) { Pause("The native multiplayer handoff ended without consensus."); GameDice(plan); complete=true; yield break; }
                        plan.NativeFallback=automatic;
                    }
                    if(!plan.NativeFallback&&!RunReady(plan)) Pause("Entered dice could not be applied to the native step.");
                } else plan.NativeFallback=true;
                plan.Ready=true; complete=true;
            }
            finally {
                if(!complete) {
                    Pause("A multiplayer action was canceled before the native handoff.");
                    ReleaseOwner(plan);
                    DisposePlan(plan); plans.Remove(plan.Invocation.Owner);
                }
            }
        }
        // Claim the native step, record the owners and describe the roll.
        static string Admit(NativeRollPlan plan)
        {
            try
            {
                if(!NativeNetworkScheduler.Claim(plan.Invocation.Owner,plan.NetworkRun)) return "The multiplayer roll is outside its native coroutine scheduler.";
                foreach(var leaf in plan.Leaves)
                    if(!leaf.MasterOwned&&!plan.NetworkOwners.ContainsKey(leaf.DisplayActor))
                        plan.NetworkOwners.Add(leaf.DisplayActor,MultiplayerRolls.OwnerFor(leaf.DisplayActor));
                // Players agree on who rolls what dice; each game's own state
                // is described in the log only (see NativeNetworkDescriptor).
                plan.NetworkKey=NativeNetworkDescriptor.Identity(plan.Invocation);
                plan.NetworkSignature=NativeNetworkDescriptor.Signature(plan);
                plan.NetworkSupported=!plan.NativeFallback&&StateValid(plan);
                try {
                    string randomState=NativeNetworkScheduler.RandomStamp();
                    MultiplayerRolls.LogDescriptor(plan.NetworkKey,NativeNetworkDescriptor.ActionKey(plan.Invocation,randomState),NativeNetworkDescriptor.Parts(plan.Invocation,randomState));
                } catch(Exception error) { MultiplayerRolls.LogDescriptor(plan.NetworkKey,"",""+error.GetType().Name); }
                return null;
            }
            catch(Exception error) { return "The roll could not be prepared for multiplayer: "+error.Message; }
        }
        static string Handoff(NativeRollPlan plan,out string signature,out bool supported)
        {
            signature=null; supported=false;
            try { signature=NativeNetworkDescriptor.Signature(plan); supported=StateValid(plan); return null; }
            catch(Exception error) { return "The entered dice could not be prepared for multiplayer: "+error.Message; }
        }
        // This roll uses the game's dice (the session is paused or the roll
        // could not be shared) and holds nothing back.
        static void GameDice(NativeRollPlan plan)
        {
            plan.NativeFallback=true; plan.Ready=true; plan.NetworkAdmitted=false;
            ReleaseOwner(plan);
        }
        static void ReleaseOwner(NativeRollPlan plan)
        {
            if(!ReferenceEquals(networkOwner,plan)) return;
            NativeNetworkScheduler.Release(plan.Invocation.Owner);
            networkOwner=null;
            if(ReferenceEquals(networkCompleted,plan)) networkCompleted=null;
        }
        static void Pause(string reason) { MultiplayerRolls.Fail(reason); }
        // Called once manual multiplayer dice pause: nothing stays held.
        internal static void ReleaseNetworkHold()
        {
            var owner=networkOwner;
            networkOwner=null; networkCompleted=null;
            while(completionStack.Count>0) { var disposable=completionStack.Pop() as IDisposable; if(disposable!=null) try { disposable.Dispose(); } catch(Exception) { } }
            if(owner!=null) NativeNetworkScheduler.Release(owner.Invocation.Owner);
        }
        // The prepared characters' controllers must not change before the
        // native step. A change pauses manual dice; it never throws.
        static bool OwnersValid(NativeRollPlan plan)
        {
            if(plan==null||!plan.Networked) return true;
            try {
                foreach(var owner in plan.NetworkOwners)
                    if(MultiplayerRolls.OwnerFor(owner.Key)!=owner.Value) { Pause("A character's controlling player changed while its dice were pending."); return false; }
                return true;
            } catch(Exception error) { Pause("The native dice ownership could not be verified: "+error.Message); return false; }
        }

        static IEnumerator Collect(NativeRollPlan plan,object iterator)
        {
            bool complete=false;
            try
            {
                // Collect actual raw faces, never fabricate a vector from a total.
                for(int i=0;i<plan.Leaves.Count;)
                {
                    var leaf=plan.Leaves[i];
                    var group=plan.Leaves.Skip(i).Take(leaf.GroupCount).ToArray();
                    i+=group.Length;
                    if(SkipGroup(plan,group)) continue;
                    yield return CollectGroup(plan,group);
                    if(plan.NativeFallback||!plan.Networked&&!PlanAllowed(plan)) yield break;
                }
                if(!RunReady(plan)) yield break;
                plan.Ready=true; complete=true;
            }
            finally
            {
                if(!complete) { plan.NativeFallback=true; DisposePlan(plan); plans.Remove(iterator); }
            }
        }

        // A conditional group whose condition does not hold is skipped.
        static bool SkipGroup(NativeRollPlan plan,NativeRollPlan.Leaf[] group)
        {
            var when=group[0].When;
            if(when==null) return false;
            bool ask;
            try { ask=when(); }
            catch(Exception error) { Plugin.Log("A conditional dice group could not be evaluated: "+error.Message); ask=false; }
            if(ask) return false;
            foreach(var leaf in group) { leaf.Skipped=true; leaf.Used=true; }
            return true;
        }
        static bool RunReady(NativeRollPlan plan)
        {
            try { foreach(var action in plan.OnReady) action(); return true; }
            catch(Exception error) { Plugin.Warn("Entered dice could not be applied; using the game's roll: "+error.Message); plan.NativeFallback=true; return false; }
        }
        static IEnumerator CollectGroup(NativeRollPlan plan,NativeRollPlan.Leaf[] leaves)
        {
            var primary=new List<FaceIdentity>();
            foreach(var leaf in leaves)
            {
                int n=leaf.Advantage==RuleDefinitions.AdvantageType.None?1:2;
                for(int i=0;i<n;i++) primary.Add(new FaceIdentity(leaf.Binding,leaf.Ordinal,i));
            }
            int[] accepted=null;
            yield return Ask(plan,leaves[0],primary.ToArray(),leaves[0].Title,HintText(leaves[0]),values=>accepted=values);
            if(accepted==null) { plan.NativeFallback=true; yield break; }
            int offset=0;
            foreach(var leaf in leaves)
            {
                int n=leaf.Advantage==RuleDefinitions.AdvantageType.None?1:2;
                leaf.Identities.AddRange(primary.Skip(offset).Take(n)); leaf.Faces.AddRange(accepted.Skip(offset).Take(n)); leaf.DrawCounts.Add(n);
                offset+=n;
                yield return CollectReplacements(plan,leaf);
                if(plan.NativeFallback) yield break;
            }
        }
        static IEnumerator CollectReplacements(NativeRollPlan plan,NativeRollPlan.Leaf leaf)
        {
            if(leaf.Static||!leaf.CanReroll) yield break;
            int n=leaf.Advantage==RuleDefinitions.AdvantageType.None?1:2;
            int[] simulated=leaf.Faces.ToArray();
            // Mirror only the native decision to request another raw die. The
            // original RulesetActor.RollDie still applies every rule and event.
            foreach(var feature in leaf.Features)
            {
                if((feature.ValidityContext&leaf.Context)==0 ||
                    (feature.ValidSkills.Count>0&&!feature.ValidSkills.Contains(leaf.Skill)) ||
                    (feature.RequireProficiency&&!leaf.Proficient)) continue;
                for(int i=0;i<n;i++) simulated[i]=Clamp(simulated[i],feature.MinRollValue,feature.MaxRollValue>0?feature.MaxRollValue:int.MaxValue);
            }
            for(int f=0;f<leaf.Features.Count;f++)
            {
                var feature=leaf.Features[f];
                if((feature.ValidityContext&leaf.Context)==0||feature.MinRerollValue<=0) continue;
                for(int i=0;i<n;i++)
                {
                    int remaining=feature.RerollCount,attempt=0;
                    while(simulated[i]<=feature.MinRerollValue&&remaining-- >0)
                    {
                        var identity=new FaceIdentity(leaf.Binding,leaf.Ordinal,i,feature,f,++attempt);
                        int[] replacement=null;
                        string name=feature.Name=="DieRollModifierHalfingLucky"?"Lucky reroll":"Reroll";
                        string note=RollText.RerollHint(i,n,simulated[i],feature.Name);
                        yield return Ask(plan,leaf,new[]{identity},name,note,values=>replacement=values);
                        if(replacement==null) { plan.NativeFallback=true; yield break; }
                        leaf.Identities.Add(identity); leaf.Faces.Add(replacement[0]); leaf.DrawCounts.Add(1);
                        simulated[i]=replacement[0];
                        // The shipped native loop applies the triggering clamp
                        // once for every feature in featuresToBrowse.
                        foreach(var ignored in leaf.Features)
                            simulated[i]=Clamp(simulated[i],feature.MinRollValue,feature.MaxRollValue>0?feature.MaxRollValue:int.MaxValue);
                    }
                }
            }
        }
        static int Clamp(int value,int min,int max) { return value<min?min:value>max?max:value; }

        static RollPrompt BuildPrompt(NativeRollPlan.Leaf leaf,FaceIdentity[] identities,string title,string hint)
        {
            var faces=new int[identities.Length]; for(int i=0;i<faces.Length;i++) faces[i]=Max(leaf.Die);
            string name; try { name=leaf.MasterOwned?leaf.ActorLabel??"":leaf.DisplayActor==null?"":leaf.DisplayActor.Name??""; } catch(Exception) { name=""; }
            var info=new PromptInfo();
            info.IsReroll=identities.Length>0&&identities[0].RerollFeature!=null;
            if(!info.IsReroll&&identities.Length==2&&leaf.GroupCount==1&&leaf.Advantage!=RuleDefinitions.AdvantageType.None)
                info.Advantage=leaf.Advantage==RuleDefinitions.AdvantageType.Advantage?1:-1;
            info.Target=leaf.DisplayDC;
            info.TargetLabel=leaf.Context==RuleDefinitions.RollContext.AttackRoll?"AC":"DC";
            info.HasBonus=leaf.HasDisplayBonus; info.Bonus=leaf.DisplayBonus;
            info.Kind=RollKinds.Of(leaf.Static?leaf.RequestCategory:leaf.Context,leaf.Title);
            var factory=PromptFactory;
            return factory!=null?factory(title,name,leaf.Note,hint,faces,info):new RollPrompt(title,name,leaf.Note,hint,faces);
        }
        static IEnumerator Ask(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities,string title,string note,Action<int[]> result)
        {
            if(plan.Networked) {
                yield return AskNetwork(plan,leaf,identities,title,note,result);
                yield break;
            }
            RollTicket ticket; string reason;
            if(!plan.Session.TryRequest(identities,out ticket,out reason)) { Plugin.Log(reason); yield break; }
            RollPrompt prompt=null;
            try
            {
                // One modal at a time; requests retain their own opaque identity.
                while(RollWindow.Visible)
                {
                    if(plan.NativeFallback||!PlanAllowed(plan)) { broker.Cancel(ticket.Id); yield break; }
                    yield return null;
                }
                if(plan.NativeFallback||!PlanAllowed(plan)) { broker.Cancel(ticket.Id); yield break; }
                prompt=BuildPrompt(leaf,identities,title,note);
                plan.DisplayedPrompt=prompt;
                RollWindow.Display(prompt,
                    values=>{ string ignored; if(!broker.TryAccept(ticket.Id,values,out ignored)) Plugin.Log(ignored); },
                    ()=>broker.Cancel(ticket.Id));
                while(ticket.IsPending)
                {
                    if(plan.NativeFallback||!PlanAllowed(plan)) { broker.Cancel(ticket.Id); break; }
                    yield return null;
                }
                int[] accepted; if(ticket.TryGetAccepted(out accepted)) result(accepted);
            }
            finally {
                if(prompt!=null) RollWindow.Close(prompt);
                if(ReferenceEquals(plan.DisplayedPrompt,prompt)) plan.DisplayedPrompt=null;
                if(ticket.IsPending) broker.Cancel(ticket.Id);
            }
        }

        static IEnumerator AskNetwork(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities,string title,string note,Action<int[]> result)
        {
            RollTicket ticket; string reason;
            if(plan.Session==null||!plan.Session.TryRequest(identities,out ticket,out reason)) { Pause("The dice request could not be prepared."); plan.NativeFallback=true; yield break; }
            RollPrompt prompt; string signature; bool supported;
            if(!PreparePrompt(plan,leaf,identities,title,note,out prompt,out signature,out supported))
            { broker.Cancel(ticket.Id); plan.NativeFallback=true; yield break; }
            bool done=false, automatic=true;
            int[] accepted=null;
            try {
                yield return MultiplayerRolls.Rendezvous(plan.NetworkKey,plan.NetworkOrdinal++,signature,leaf.MasterOwned?null:leaf.DisplayActor,prompt,
                    supported,received=>accepted=received,value=>{automatic=value;done=true;});
                if(!done) { Pause("The multiplayer prompt ended without a shared decision."); plan.NativeFallback=true; broker.Cancel(ticket.Id); yield break; }
                if(automatic) { plan.NativeFallback=true; broker.Cancel(ticket.Id); yield break; }
                if(!broker.TryAccept(ticket.Id,accepted,out reason)) { Pause("The agreed dice could not be accepted: "+reason); plan.NativeFallback=true; yield break; }
                int[] values; if(!ticket.TryGetAccepted(out values)) { Pause("The agreed dice were canceled before collection."); plan.NativeFallback=true; yield break; }
                result(values);
            }
            finally { if(ticket.IsPending) broker.Cancel(ticket.Id); }
        }
        static bool PreparePrompt(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities,string title,string note,
            out RollPrompt prompt,out string signature,out bool supported)
        {
            prompt=null; signature=null; supported=false;
            try {
                prompt=BuildPrompt(leaf,identities,title,note);
                signature=NativeNetworkDescriptor.PromptSignature(plan,leaf,identities);
                supported=StateValid(plan);
                return true;
            } catch(Exception error) { Pause("The dice prompt could not be prepared for multiplayer: "+error.Message); return false; }
        }
        // Original arguments are evaluated once again by the stock iterator.
        // Verify those actual values before the original synchronous call.
        public static void ValidateNativeArguments(object iterator,string site,object[] actual)
        {
            NativeRollPlan plan;
            if(!plans.TryGetValue(iterator,out plan)||plan.Invocation.Site!=site) return;
            var expected=plan.Invocation.Args; var parameters=plan.Invocation.Method.GetParameters();
            bool same=actual!=null&&actual.Length==expected.Length;
            if(same) for(int i=0;i<expected.Length;i++) {
                int index=i-(plan.Invocation.Method.IsStatic?0:1);
                if(index>=0&&parameters[index].ParameterType.IsByRef) continue;
                object a=expected[i], b=actual[i];
                if(a==null?b!=null:a.GetType().IsValueType||a is string?!a.Equals(b):!ReferenceEquals(a,b)) {same=false;break;}
            }
            if(same) return;
            if(plan.Networked) Pause("Native multiplayer arguments changed after the agreement.");
            Bypass(plan,"Native arguments changed while waiting.");
        }
        public static void ObserveNativeValues(object iterator,string site,object result,object[] outputs)
        {
            NativeRollPlan plan;
            if(!plans.TryGetValue(iterator,out plan)||plan.Invocation.Site!=site) return;
            var parameters=plan.Invocation.Method.GetParameters().Where(parameter=>parameter.ParameterType.IsByRef).ToArray();
            var method=plan.Invocation.Method as MethodInfo;
            bool valid=method!=null&&outputs!=null&&outputs.Length==parameters.Length&&outputs.Length<=64;
            if(valid) {
                var type=method.ReturnType;
                valid=type==typeof(void)?result==null:result==null?!type.IsValueType:type.IsInstanceOfType(result);
            }
            if(valid) for(int i=0;i<outputs.Length;i++) {
                var type=parameters[i].ParameterType.GetElementType();
                if(outputs[i]==null?type.IsValueType:!type.IsInstanceOfType(outputs[i])) {valid=false;break;}
            }
            if(!valid) {
                if(plan.Networked) Pause("The native result or output vector does not match the registered method.");
                return;
            }
            plan.NativeReturn=result;
            plan.NativeOutputs=(object[])outputs.Clone();
            plan.NativeObserved=true;
        }
        public static bool IsReplicatedRestOwner(RulesetCharacterHero actor)
        {
            var plan=Current;
            return plan!=null&&plan.Invocation.Site=="RestHitDice.Network"&&ReferenceEquals(plan.Invocation.Arg("hero"),actor);
        }
        public static void NetworkRunCompleted(object owner)
        {
            var plan=networkCompleted;
            if(plan==null||!ReferenceEquals(plan.Invocation.Owner,owner)||completionStack.Count!=0) return;
            completionStack.Push(CompleteNetworkStep(plan));
        }
        // Every player finishes the step together and compares the game's
        // result. A different result is the games' own synchronization: it is
        // logged (and noticed once for a roll with entered dice), never fatal.
        static IEnumerator CompleteNetworkStep(NativeRollPlan plan)
        {
            bool done=false, differs=false;
            string signature=null, parts="";
            try {
                string randomState=NativeNetworkScheduler.RandomStamp();
                signature=NativeNetworkDescriptor.CompletedSignature(plan,randomState);
                try { parts=NativeNetworkDescriptor.CompletedParts(plan,randomState); } catch(Exception error) { parts="error:"+error.GetType().Name; }
            }
            catch(Exception error) { signature=null; Plugin.Log("The native result could not be described: "+error.Message); }
            // An undescribable result still finishes the step together.
            if(signature==null) signature=NativeNetworkDescriptor.Fallback(plan.NetworkKey,plan.NetworkOrdinal);
            MultiplayerRolls.LogResult(plan.NetworkKey,signature,parts);
            yield return MultiplayerRolls.Rendezvous(plan.NetworkKey,plan.NetworkOrdinal++,signature,null,null,true,
                values=>{},value=>{differs=value;done=true;},allowAutomaticOnMismatch:true);
            if(!done) Pause("The players' games did not finish this roll together.");
            else if(differs) MultiplayerRolls.ResultsDiffered(plan.NetworkKey,plan.NetworkNativeManual);
            if(ReferenceEquals(networkCompleted,plan)) networkCompleted=null;
            if(ReferenceEquals(networkOwner,plan)) networkOwner=null;
            NativeNetworkScheduler.Release(plan.Invocation.Owner);
        }
        internal static void UpdateNetworkCompletion()
        {
            if(networkCompleted!=null&&completionStack.Count==0&&Plugin.Networked()&&
                (System.Diagnostics.Stopwatch.GetTimestamp()-networkExitStarted)/(double)System.Diagnostics.Stopwatch.Frequency>30)
                MultiplayerRolls.Fail("The native coroutine did not reach its multiplayer completion barrier.");
            try {
                for(int budget=0;budget<64&&completionStack.Count>0;budget++) {
                    var iterator=completionStack.Peek();
                    if(!iterator.MoveNext()) { completionStack.Pop(); var disposable=iterator as IDisposable;if(disposable!=null)disposable.Dispose();continue; }
                    var child=iterator.Current as IEnumerator;
                    if(child!=null) {completionStack.Push(child);continue;}
                    break;
                }
            } catch(Exception error) {
                MultiplayerRolls.Fail("Native multiplayer completion failed: "+error.Message);
                ReleaseNetworkHold();
            }
            PumpNetwork();
            // Paused: nothing waits for a comparison any more.
            if(Plugin.Networked()&&MultiplayerRolls.Failed&&(networkOwner!=null||networkCompleted!=null)&&completionStack.Count==0) ReleaseNetworkHold();
            if(!Plugin.Networked()&&networkOwner!=null) {
                NativeNetworkScheduler.Release(networkOwner.Invocation.Owner);
                networkCompleted=null;networkOwner=null;
                while(completionStack.Count>0) {var disposable=completionStack.Pop() as IDisposable;if(disposable!=null)try{disposable.Dispose();}catch(Exception){} }
            }
        }

        public static void EnterNative(object iterator,string site)
        {
            NativeRollPlan plan;
            if(!plans.TryGetValue(iterator,out plan)||plan.Invocation.Site!=site) plan=null;
            if(plan!=null&&!OwnersValid(plan)) { plan.NativeFallback=true; plan=null; }
            if(plan!=null&&(!plan.Ready||plan.NativeFallback)) plan=null;
            ValidateNativeState(plan);
            if(plan!=null&&plan.Networked) plan.NetworkNativeManual=true;
            if(active==null) active=new Stack<ActiveCall>();
            active.Push(new ActiveCall{Owner=iterator,Site=site,Plan=plan});
        }
        public static void ExitNative(object iterator,string site)
        {
            if(active!=null&&active.Count>0)
            {
                var top=active.Peek();
                if(!ReferenceEquals(top.Owner,iterator)||top.Site!=site)
                    throw new InvalidOperationException("A native roll scope was exited by another owner.");
                active.Pop();
            }
            NativeRollPlan completed;
            if(plans.TryGetValue(iterator,out completed)&&completed.Networked&&completed.NetworkAdmitted) {
                completed.NativeFinished=true;
                if(!completed.NativeObserved) Pause("The native multiplayer call did not record its result and outputs.");
                if(MultiplayerRolls.Failed||!ReferenceEquals(networkOwner,completed)) ReleaseOwner(completed);
                else { networkCompleted=completed; networkExitStarted=System.Diagnostics.Stopwatch.GetTimestamp(); }
            }
            CancelOwner(iterator);
        }
        public static void AbortNative(object iterator)
        {
            if(active!=null)
            {
                while(active.Count>0&&ReferenceEquals(active.Peek().Owner,iterator)) active.Pop();
            }
            CancelOwner(iterator);
        }
        public static object EnterForm(object form)
        { var p=Current; if(p==null) return null; ValidateNativeState(p); var old=p.CurrentForm; p.CurrentForm=form; return old; }
        static void ValidateNativeState(NativeRollPlan plan)
        {
            if(plan==null||plan.NativeFallback) return;
            if(!PlanAllowed(plan)) { Bypass(plan,"Manual settings changed while waiting."); return; }
            if(plan.NativeStateValid==null) return;
            try { if(!plan.NativeStateValid()) Bypass(plan,"The native roll providers changed while waiting."); }
            catch(Exception error) { Bypass(plan,"The native roll providers could not be verified: "+error.Message); }
        }
        static bool PlanAllowed(NativeRollPlan plan)
        {
            try
            {
                if(!Plugin.Enabled||Plugin.UnsupportedRulesMod||plan.ManualStateValid!=null&&!plan.ManualStateValid()) return false;
                foreach(var leaf in plan.Leaves) if(!LeafAllowed(leaf)) return false;
                return true;
            }
            catch(Exception error) { Plugin.Log("Manual settings could not be verified: "+error.Message); return false; }
        }
        // The game starts processing one condition/effect of a composite plan.
        public static object EnterAnchor(object anchor)
        { var p=Current; if(p==null) return null; var old=p.CurrentAnchor; p.CurrentAnchor=anchor; return old; }
        public static void ExitAnchor(object previous) { var p=Current; if(p!=null) p.CurrentAnchor=previous; }
        internal static bool HasAnchoredPlan { get { var p=Current; return p!=null&&!p.NativeFallback&&p.Leaves.Any(leaf=>leaf.Anchor!=null); } }
        public static bool HasPreparedForm(object form)
        { var p=Current; return p!=null&&p.Ready&&!p.NativeFallback&&ReferenceEquals(p.CurrentForm,form); }
        public static void ExitForm(object previous) { var p=Current; if(p!=null) p.CurrentForm=previous; }
        static NativeRollPlan Current { get { return active!=null&&active.Count>0?active.Peek().Plan:null; } }
        public static void CancelOwner(object iterator)
        {
            if(iterator==null) return;
            NativeRollPlan plan; if(plans.TryGetValue(iterator,out plan)) {
                if(plan.Networked&&ReferenceEquals(networkOwner,plan)&&!plan.NativeFinished)
                    MultiplayerRolls.Fail("A replicated native roll was canceled before completion.");
                DisposePlan(plan); plans.Remove(iterator);
            }
        }
        static void DisposePlan(NativeRollPlan plan)
        {
            plan.NativeFallback=true;
            // Native TA.Reset clears its nested enumerators without disposing
            // Ask. Retire the owned modal here as well as its broker ticket.
            // Reference ownership keeps a canceled waiter from closing a
            // different action's current window.
            var prompt=plan.DisplayedPrompt; plan.DisplayedPrompt=null;
            try { if(prompt!=null) RollWindow.Close(prompt); }
            finally { try { if(plan.Session!=null) plan.Session.Dispose(); } finally { alive.Remove(plan); } }
        }
        public static void Reset()
        {
            MultiplayerRolls.Reset();
            NativeNetworkScheduler.Reset();
            if(networkOwner!=null&&Plugin.Networked()) MultiplayerRolls.Fail("A multiplayer native action was reset.");
            networkOwner=null; networkCompleted=null;
            foreach(var wait in networkWaits.ToArray()) DisposeWait(wait);
            networkWaits.Clear();
            while(completionStack.Count>0) {var disposable=completionStack.Pop() as IDisposable;if(disposable!=null)try{disposable.Dispose();}catch(Exception){} }
            foreach(var plan in alive.ToArray())
                try { DisposePlan(plan); } catch(Exception error) { Plugin.Warn("A canceled manual window could not clean up: "+error.Message); }
            broker.Reset(); plans=new ConditionalWeakTable<object,NativeRollPlan>();
            if(active!=null) active.Clear();
            if(staticSums!=null) staticSums.Clear();
            if(actors!=null) { foreach(var frame in actors) if(frame.Lease!=null) frame.Lease.Dispose(); actors.Clear(); }
            try { RestPatches.Reset(); } finally { RollWindow.Close(); }
        }

        static bool PrepareD20(NativeInvocation call,NativeRollPlan plan)
        {
            var actor=call.Receiver as RulesetCharacter;
            string name=call.Method.Name;
            if(name=="ApplyTargetFiltering")
            {
                var action=call.Receiver as CharacterActionMagicEffect;
                var description=call.Arg("effectDescription") as EffectDescription;
                if(action==null||description==null||!Plugin.Settings.SpellPools||
                    description.TargetFilteringMethod!=RuleDefinitions.TargetFilteringMethod.CharacterIncreasingHitPointsFromPool) return true;
                var property=AccessTools.Property(typeof(CharacterActionMagicEffect),"AddDice");
                int added=(int)property.GetValue(action,null);
                plan.RequireManualState(()=>Plugin.Settings.SpellPools);
                plan.AddStaticDice(action.ActingCharacter.RulesetCharacter,description.PoolFilterDieType,description.PoolFilterDiceNumber+added,
                    title:"Hit point pool",note:"Spell effect pool",hint:"Enter each raw die. The game chooses affected creatures from the total.");
                return true;
            }
            if(name=="ProcessContenderInitiative")
            {
                var contender=call.Arg("character") as GameLocationCharacter;
                if(contender==null) return true;
                var manager=call.Receiver as GameLocationBattle;
                var settings=ServiceRepository.GetService<IGameSettingsService>();
                if(settings!=null&&settings.GroupedMonsterInitiatives&&manager!=null)
                {
                    var cached=AccessTools.Field(typeof(GameLocationBattle),"initiativeByName").GetValue(manager) as Dictionary<string,int>;
                    if(cached!=null&&cached.ContainsKey(contender.Name)) return true;
                }
                actor=contender.RulesetCharacter;
                name="RollInitiative";
            }
            if(actor==null) return false;
            RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None;
            if(name=="RollAttackMode"||name=="RollMagicAttack")
            {
                if(call.Arg<int>("predefinedRoll")>0) return true;
                var trends=call.Arg("advantageTrends") as List<RuleDefinitions.TrendInfo>;
                if(trends!=null) advantage=RuleDefinitions.ComputeAdvantage(trends);
                if(call.Arg<bool>("ignoreAdvantage")) advantage=RuleDefinitions.AdvantageType.None;
                plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AttackRoll,1,advantage,title:"Attack roll");
                DescribeAttack(call,plan,name);
                var bonusDice=AttackBonusPlanner;
                if(bonusDice!=null) { try { bonusDice(call,plan,actor); } catch(Exception error) { Plugin.Log("Attack bonus dice keep the game's roll: "+error.Message); } }
                return true;
            }
            if(name=="RollAbilityCheck")
            {
                if(call.Arg<bool>("passive")) return true;
                var trends=call.Arg("advantageTrends") as List<RuleDefinitions.TrendInfo>;
                if(trends!=null) advantage=RuleDefinitions.ComputeAdvantage(trends);
                string skill=call.Arg<string>("proficiencyName","");
                plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,advantage,actor.IsProficient(skill),skill,title:"Ability check",note:RollText.Check(call.Arg<string>("abilityScoreName",""),skill));
                plan.Describe(call.Arg<int>("checkDC"),true,call.Arg<int>("baseBonus")+call.Arg<int>("rollModifier"));
                return true;
            }
            if(name=="ResolveContestCheck")
            {
                string skill=call.Arg<string>("proficiencyName","");
                string opponentSkill=call.Arg<string>("opponentProficiencyName","");
                var opponent=call.Arg("opponent") as RulesetCharacter;
                advantage=RuleDefinitions.ComputeAdvantage(call.Arg("advantageTrends") as List<RuleDefinitions.TrendInfo>);
                plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,advantage,actor.IsProficient(skill),skill,title:"Contest check",note:RollText.Check(call.Arg<string>("abilityScoreName",""),skill));
                plan.Describe(0,true,call.Arg<int>("baseBonus")+call.Arg<int>("rollModifier"));
                if(opponent!=null)
                {
                    advantage=RuleDefinitions.ComputeAdvantage(call.Arg("opponentAdvantageTrends") as List<RuleDefinitions.TrendInfo>);
                    plan.AddActorDice(opponent,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.AbilityCheck,1,advantage,opponent.IsProficient(opponentSkill),opponentSkill,title:"Resist contest",note:RollText.Check(call.Arg<string>("opponentAbilityScoreName",""),opponentSkill));
                    plan.Describe(0,true,call.Arg<int>("opponentBaseBonus")+call.Arg<int>("opponentRollModifier"));
                }
                return true;
            }
            if(name=="RollSavingThrow")
            {
                advantage=RuleDefinitions.ComputeAdvantage(call.Arg("advantageTrends") as List<RuleDefinitions.TrendInfo>);
                plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.SavingThrow,1,advantage,title:"Saving throw",note:RollText.Save(call.Arg<string>("abilityScoreName","")));
                plan.Describe(call.Arg<int>("saveDC"),true,call.Arg<int>("saveBonus")+call.Arg<int>("rollModifier"));
                return true;
            }
            if(name=="RollInitiative")
            {
                if(call.Arg<int>("forcedInitiative")>0) return true;
                int trend=0;
                var features=new List<FeatureDefinition>();
                using(var scope=NativeScratchScope.ForActor(actor))
                {
                    if(actor.IsSubstitute) scope.AddActor(actor.OriginalFormCharacter);
                    actor.EnumerateFeaturesToBrowse<IInitiativeModifier>(features);
                }
                foreach(IInitiativeModifier feature in features) feature.RefreshInitiativeBonus(ref trend);
                advantage=trend>0?RuleDefinitions.AdvantageType.Advantage:trend<0?RuleDefinitions.AdvantageType.Disadvantage:RuleDefinitions.AdvantageType.None;
                plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.InitiativeRoll,1,advantage,title:"Initiative",
                    hint:"Sets the turn order for this battle: the highest total acts first.");
                DescribeAttack(call,plan,"RollInitiative");
                return true;
            }
            if(name=="RollDeathSavingThrow")
            {
                var trends=new List<RuleDefinitions.TrendInfo>(); var features=new List<FeatureDefinition>();
                var origins=new Dictionary<FeatureDefinition,RuleDefinitions.FeatureOrigin>();
                using(var scope=NativeScratchScope.ForActor(actor))
                {
                    if(actor.IsSubstitute) scope.AddActor(actor.OriginalFormCharacter);
                    actor.EnumerateFeaturesToBrowse<IDeathSavingThrowAffinityProvider>(features,origins);
                }
                foreach(IDeathSavingThrowAffinityProvider feature in features) feature.ComputeSavingThrowTrend(trends,origins[(FeatureDefinition)feature]);
                advantage=RuleDefinitions.ComputeAdvantage(trends);
                plan.AddActorDice(actor,RuleDefinitions.DieType.D20,RuleDefinitions.RollContext.DeathSavingThrow,1,advantage,title:"Death saving throw",
                    hint:"10 or more is a success, less is a failure. Three successes stabilize, three failures mean death. A natural 20 regains 1 hit point; a natural 1 counts as two failures.");
                plan.Describe(10,false,0);
                return true;
            }
            if(name=="RollMagicalHitPointsPool")
            {
                if(!Plugin.Settings.SpellPools) return true;
                plan.RequireManualState(()=>Plugin.Settings.SpellPools);
                plan.AddStaticDice(actor,call.Arg<RuleDefinitions.DieType>("dieType"),call.Arg<int>("diceNumber"),title:"Hit point pool",note:"Spell effect pool",hint:"Enter each raw die. The game distributes the resulting pool.");
                return true;
            }
            return false;
        }

        // Optional display context (target armor class and fixed attack bonus).
        internal static Action<NativeInvocation,NativeRollPlan,string> AttackDescriber;
        // Optional: modifier dice the game rolled into the attack before the
        // await (Bless, Bane), entered with the attack roll (AttackBonusDice).
        internal static Action<NativeInvocation,NativeRollPlan,RulesetCharacter> AttackBonusPlanner;
        // Optional: the character an actor rolls for (a spell's effect proxy
        // rolls for its controller). Identity when unset.
        internal static Func<RulesetActor,RulesetActor> ControllerOf;
        static void DescribeAttack(NativeInvocation call,NativeRollPlan plan,string name)
        {
            var describe=AttackDescriber;
            if(describe==null) return;
            try { describe(call,plan,name); } catch(Exception) { }
        }

        static NativeRollPlan.Leaf SelectLeaf(RulesetActor actor,RuleDefinitions.DieType die,RuleDefinitions.RollContext context,
            bool proficient,RuleDefinitions.AdvantageType advantage,bool canReroll,string skill,bool isStatic)
        {
            var plan=Current; if(plan==null||plan.NativeFallback||!Plugin.Enabled||Plugin.UnsupportedRulesMod) return null;
            if(plan.CurrentForm!=null&&!EffectPatches.IsEligibleNativeDraw(actor,context,isStatic)) return null;
            // Skip only different form identities. Within a form, order and all
            // actual arguments must agree; a mismatch revokes the whole batch.
            foreach(var leaf in plan.Leaves)
            {
                if(leaf.Used||!ReferenceEquals(leaf.Form,plan.CurrentForm)) continue;
                // A composite part's die belongs to its own condition/effect.
                if(leaf.Anchor!=null&&!ReferenceEquals(leaf.Anchor,plan.CurrentAnchor)) continue;
                // An outer/deferred site's unrelated die is not the planned roll.
                if(plan.Scoped&&(leaf.Static!=isStatic||!ReferenceEquals(leaf.Actor,actor)||leaf.Die!=die||leaf.Context!=context||leaf.Skill!=(skill??""))) return null;
                if(leaf.Static!=isStatic || !ReferenceEquals(leaf.Actor,actor) || leaf.Die!=die || leaf.Context!=context ||
                    leaf.Proficient!=proficient || leaf.Advantage!=advantage || leaf.CanReroll!=canReroll || leaf.Skill!=(skill??""))
                { Bypass(plan,"Native dice arguments changed; using the game's roll."); return null; }
                leaf.Used=true;
                return leaf;
            }
            return null;
        }
        static void Bypass(NativeRollPlan plan,string reason)
        { if(plan==null) return; if(plan.Networked&&plan.NetworkAdmitted&&plan.Ready) Pause(reason); plan.NativeFallback=true; if(plan.Session!=null) plan.Session.Dispose(); Plugin.Log(reason+" Site: "+plan.Invocation.Site); }

        static void ActorPrefix(RulesetActor __instance,RuleDefinitions.DieType dieType,RuleDefinitions.RollContext rollContext,
            bool isProficient,RuleDefinitions.AdvantageType advantageType,bool canRerollDice,string skill,bool enumerateFeatures,out ActorFrame __state)
        {
            __state=new ActorFrame();
            // Notifications inside a native roll can roll more dice. They must
            // not consume the next die of this outer prepared invocation.
            var leaf=Max(dieType)<=1||actors!=null&&actors.Count>0?null:SelectLeaf(__instance,dieType,rollContext,isProficient,advantageType,canRerollDice,skill,false);
            if(leaf!=null)
            {
                var plan=Current; string reason;
                string observedStamp=null;
                try { observedStamp=FeatureStamp(enumerateFeatures?Features(__instance):__instance.FeaturesToBrowse.Cast<FeatureDefinitionDieRollModifier>().ToList()); }
                catch(Exception) { }
                if(observedStamp==null||leaf.StateStamp!=plan.Invocation.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)+"/"+observedStamp)
                    Bypass(plan,"Dice features changed while the prompt was open.");
                else
                {
                    var binding=new RollBinding(__instance,plan.Invocation.Owner,plan.Invocation,plan.CurrentForm,leaf.Role,Max(dieType),leaf.Ordinal,leaf.StateStamp);
                    if(plan.Session.TryEnterNative(binding,leaf.Identities,out __state.Lease,out reason)) __state.Leaf=leaf;
                    else Bypass(plan,reason);
                }
            }
            if(actors==null) actors=new Stack<ActorFrame>(); actors.Push(__state);
        }
        static Exception ActorFinalizer(Exception __exception,ActorFrame __state)
        {
            if(__state!=null&&__state.Lease!=null) __state.Lease.Dispose();
            if(actors!=null&&actors.Count>0&&ReferenceEquals(actors.Peek(),__state)) actors.Pop();
            return __exception;
        }
        static bool RawPrefix(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,
            ref int firstRoll,ref int secondRoll,ref int __result)
        {
            NativeRollPlan.Leaf leaf=null; NativeRollLease lease=null; bool own=false;
            if(Max(diceType)<=1) return true;
            if(actors!=null&&actors.Count>0) { leaf=actors.Peek().Leaf; lease=actors.Peek().Lease; }
            else
            {
                if(staticSums==null||staticSums.Count==0||!staticSums.Peek()) return true;
                leaf=SelectLeaf(null,diceType,RuleDefinitions.RollContext.None,false,advantageType,false,"",true);
                if(leaf!=null)
                {
                    string reason; var plan=Current;
                    if(!plan.Session.TryEnterNative(leaf.Binding,leaf.Identities,out lease,out reason)) { Bypass(plan,reason); return true; }
                    own=true;
                }
            }
            if(leaf==null||lease==null) return true;
            try
            {
                var current=Current;
                if(current==null||current.NativeFallback||!Plugin.Enabled||Plugin.UnsupportedRulesMod) return true;
                // The native first draw precedes feature enumeration; ActorPrefix
                // validates its snapshot before entering that body. Replacement
                // draws occur after enumeration and after DieRerolled callbacks.
                // Revalidate their live ordered features so a changed threshold or
                // feature list cannot consume another die's replacement stage.
                if(!own&&leaf.NextDraw>0)
                {
                    string observedStamp=null;
                    try { observedStamp=FeatureStamp(leaf.Actor.FeaturesToBrowse.Cast<FeatureDefinitionDieRollModifier>().ToList()); }
                    catch(Exception) { }
                    if(observedStamp==null||leaf.StateStamp!=current.Invocation.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)+"/"+observedStamp)
                    { Bypass(current,"Dice features changed during the native reroll."); return true; }
                }
                int count=advantageType==RuleDefinitions.AdvantageType.None?1:2;
                if(leaf.Die!=diceType||leaf.NextDraw>=leaf.DrawCounts.Count||leaf.DrawCounts[leaf.NextDraw]!=count)
                { Bypass(Current,"An unexpected native die was requested."); return true; }
                int start=0; for(int i=0;i<leaf.NextDraw;i++) start+=leaf.DrawCounts[i];
                if(!own&&!ReferenceEquals(leaf.Identities[start].RerollFeature,actors.Peek().RerollFeature))
                { Bypass(Current,"The native reroll feature changed."); return true; }
                int first; if(!lease.TryConsume(leaf.Identities[start],out first)) { Bypass(Current,"A dice permit expired."); return true; }
                int second=-1;
                if(count==2&&!lease.TryConsume(leaf.Identities[start+1],out second)) { Bypass(Current,"A paired dice permit expired."); return true; }
                leaf.NextDraw++; firstRoll=first; secondRoll=second;
                if(!own&&actors!=null&&actors.Count>0) actors.Peek().Manual=true;
                __result=advantageType==RuleDefinitions.AdvantageType.Advantage?Math.Max(first,second):advantageType==RuleDefinitions.AdvantageType.Disadvantage?Math.Min(first,second):first;
                return false;
            }
            finally { if(own) lease.Dispose(); }
        }
        static void RerollPrefix(FeatureDefinitionDieRollModifier dieRollModifier,out FeatureDefinitionDieRollModifier __state)
        {
            __state=actors!=null&&actors.Count>0?actors.Peek().RerollFeature:null;
            if(actors!=null&&actors.Count>0) actors.Peek().RerollFeature=dieRollModifier;
        }
        static Exception RerollFinalizer(Exception __exception,FeatureDefinitionDieRollModifier __state)
        { if(actors!=null&&actors.Count>0) actors.Peek().RerollFeature=__state; return __exception; }
        static void StaticSumPrefix(int diceNumber,RuleDefinitions.DieType diceType)
        {
            var p=Current; bool allow=false;
            if(p!=null&&p.CurrentForm!=null&&!EffectPatches.IsEligibleNativeDraw(null,RuleDefinitions.RollContext.None,true))
            { if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(false); return; }
            if(NativeSavePlanner.Previewing||NativeAbilityPlanner.Previewing) { if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(false); return; }
            if(ExtraPreviewingActive) { if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(false); return; }
            // The static-bonus rules of the planned call that owns the next die
            // (a composite plan's current part, otherwise the plan's target).
            var gate=p==null?null:GateTarget(p);
            if(gate!=null&&gate.Method.Name=="TryRollSavingThrow"&&!NativeSavePlanner.AllowsStaticBonus)
            { if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(false); return; }
            if(gate!=null&&gate.Method.DeclaringType==typeof(GameLocationCharacter)&&gate.Method.Name=="RollAbilityCheck"&&!NativeAbilityPlanner.AllowsStaticBonus)
            { if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(false); return; }
            if(Max(diceType)<=1||diceNumber<=0) { if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(false); return; }
            if(p!=null&&!p.NativeFallback&&(actors==null||actors.Count==0)&&(staticSums==null||staticSums.Count==0))
            {
                var leaves=Pending(p).ToArray();
                // A static sum is an entire original call, never a partial
                // number of matching dice borrowed from a later invocation.
                if(leaves.Length>0&&leaves[0].Static&&diceNumber>0)
                {
                    allow=leaves[0].GroupCount==diceNumber&&leaves[0].GroupStart==leaves[0].Ordinal&&leaves.Length>=diceNumber;
                    if(allow) for(int i=0;i<diceNumber;i++) if(!leaves[i].Static||leaves[i].Die!=diceType||leaves[i].GroupStart!=leaves[0].GroupStart) allow=false;
                    if(!allow&&!p.Scoped) Bypass(p,"The native static dice batch changed.");
                }
            }
            if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(allow);
        }
        static Exception StaticSumFinalizer(Exception __exception)
        { if(staticSums!=null&&staticSums.Count>0) staticSums.Pop(); return __exception; }
        static void ActorSumPrefix(RulesetActor __instance,RuleDefinitions.DieType diceType,RuleDefinitions.RollContext context,
            int diceNumber,bool canRerollDice,bool maximumDamage,string skill)
        {
            var p=Current; if(p==null||p.NativeFallback||(actors!=null&&actors.Count>0)||maximumDamage) return;
            if(Max(diceType)<=1||diceNumber<=0) return;
            if(p.CurrentForm!=null&&!EffectPatches.IsEligibleNativeDraw(__instance,context,false)) return;
            var leaves=Pending(p).ToArray();
            if(leaves.Length==0||leaves[0].Static) return;
            if(p.Scoped&&(!ReferenceEquals(leaves[0].Actor,__instance)||leaves[0].Context!=context||leaves[0].Die!=diceType)) return;
            bool match=leaves[0].GroupCount==diceNumber&&leaves[0].GroupStart==leaves[0].Ordinal&&leaves.Length>=diceNumber;
            if(match) for(int i=0;i<diceNumber;i++)
            { var l=leaves[i]; if(!ReferenceEquals(l.Actor,__instance)||l.Die!=diceType||l.Context!=context||l.CanReroll!=canRerollDice||l.Skill!=(skill??"")||l.GroupStart!=leaves[0].GroupStart) match=false; }
            if(match) return;
            // A composite part bound to its own form or anchor retires only
            // that group; the game rolls this batch and every other part stays.
            if(p.Scoped&&(leaves[0].Form!=null||leaves[0].Anchor!=null))
            {
                int start=leaves[0].GroupStart;
                foreach(var leaf in p.Leaves) if(leaf.GroupStart==start&&!leaf.Used) leaf.Used=true;
                Plugin.Log("A planned dice group did not match the game's roll; the game rolled it. Site: "+p.Invocation.Site);
                return;
            }
            Bypass(p,"The native actor dice batch changed.");
        }
        static IEnumerable<NativeRollPlan.Leaf> Pending(NativeRollPlan p)
        {
            foreach(var leaf in p.Leaves)
                if(!leaf.Used&&ReferenceEquals(leaf.Form,p.CurrentForm)&&(leaf.Anchor==null||ReferenceEquals(leaf.Anchor,p.CurrentAnchor))) yield return leaf;
        }
        static NativeInvocation GateTarget(NativeRollPlan p)
        {
            foreach(var leaf in Pending(p)) return leaf.PartTarget??p.Target;
            return p.Target;
        }

        // These shims replace only the verified raw-draw call instruction in
        // the original native leaf. Arbitrary RuleDefinitions.RollDie callers
        // (including event handlers) retain the unpatched RNG implementation.
        public static int BaseFace(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        { firstRoll=secondRoll=0; int result=0;
            if(RawPrefix(diceType,advantageType,ref firstRoll,ref secondRoll,ref result))
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            BurnNativeDraw(diceType,advantageType,rollAlterationScore,firstRoll,secondRoll);
            return result; }
        public static int ReplacementFace(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        { return BaseFace(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore); }
        public static int StaticFace(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        {
            firstRoll=secondRoll=0; int result=0;
            if((actors!=null&&actors.Count>0)||staticSums==null||staticSums.Count==0||!staticSums.Peek())
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            if(RawPrefix(diceType,advantageType,ref firstRoll,ref secondRoll,ref result))
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            BurnNativeDraw(diceType,advantageType,rollAlterationScore,firstRoll,secondRoll);
            return result;
        }
        public static int HitDieFace(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        {
            firstRoll=secondRoll=0;
            if(Current==null||(Current.Invocation.Site!="RestHitDice"&&Current.Invocation.Site!="RestHitDice.Network")||!RestPatches.CanConsumeNativeHitDie||actors!=null&&actors.Count>0)
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(true);
            try { int result=0; if(RawPrefix(diceType,advantageType,ref firstRoll,ref secondRoll,ref result))
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            BurnNativeDraw(diceType,advantageType,rollAlterationScore,firstRoll,secondRoll);
            return result; }
            finally { staticSums.Pop(); }
        }

        // A raw die of the effect form the game is applying (a Dispel Magic
        // check): consumes that form's prepared static die, else the game's.
        public static int FormStaticFace(object form,RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,
            out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        {
            firstRoll=secondRoll=0;
            var plan=Current;
            if(plan==null||form==null||!ReferenceEquals(plan.CurrentForm,form)||actors!=null&&actors.Count>0||staticSums!=null&&staticSums.Count>0)
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(true);
            try { return BaseFace(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore); }
            finally { staticSums.Pop(); }
        }
        public static int InvocationStaticFace(MethodBase expectedMethod,RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,
            out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        {
            firstRoll=secondRoll=0;
            if(Current==null||!Equals(Current.Invocation.Method,expectedMethod)||actors!=null&&actors.Count>0||staticSums!=null&&staticSums.Count>0)
                return RuleDefinitions.RollDie(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
            if(staticSums==null) staticSums=new Stack<bool>(); staticSums.Push(true);
            try { return BaseFace(diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore); }
            finally { staticSums.Pop(); }
        }
        static void BurnNativeDraw(RuleDefinitions.DieType die,RuleDefinitions.AdvantageType advantage,float alteration,int first,int second)
        {
            var plan=Current; if(plan==null||!plan.Networked) return;
            int ignoredFirst,ignoredSecond;
            RuleDefinitions.RollDie(die,advantage,out ignoredFirst,out ignoredSecond,alteration);
            Plugin.Log(NetworkLog.Line("native_face","session",MultiplayerRolls.SessionToken,
                "local_actor",MultiplayerRolls.LocalActor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "action",plan.NetworkKey,"die_faces",Max(die).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "advantage",((int)advantage).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "first",first.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "second",second.ToString(System.Globalization.CultureInfo.InvariantCulture),"native_burn","true"));
        }

        static IEnumerable<CodeInstruction> FaceTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase original)
        {
            var codes=instructions.ToList();
            var native=AccessTools.Method(typeof(RuleDefinitions),"RollDie");
            string replacement=original.DeclaringType==typeof(RuleDefinitions)?"StaticFace":original.Name=="RollDie"?"BaseFace":"ReplacementFace";
            int count=0;
            foreach(var code in codes) if(code.opcode==OpCodes.Call&&Equals(code.operand,native))
            { code.operand=AccessTools.Method(typeof(RuntimeRolls),replacement); count++; }
            if(count!=1) throw new InvalidOperationException("The native raw dice leaf changed: "+original);
            return codes;
        }
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RulesetActor),"RollDie"),
                prefix:new HarmonyMethod(typeof(RuntimeRolls),"ActorPrefix"),transpiler:new HarmonyMethod(typeof(RuntimeRolls),"FaceTranspiler"),finalizer:new HarmonyMethod(typeof(RuntimeRolls),"ActorFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetActor),"RerollDieAsNeeded"),
                prefix:new HarmonyMethod(typeof(RuntimeRolls),"RerollPrefix"),transpiler:new HarmonyMethod(typeof(RuntimeRolls),"FaceTranspiler"),finalizer:new HarmonyMethod(typeof(RuntimeRolls),"RerollFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollStaticDiceAndSum"),
                prefix:new HarmonyMethod(typeof(RuntimeRolls),"StaticSumPrefix"),transpiler:new HarmonyMethod(typeof(RuntimeRolls),"FaceTranspiler"),finalizer:new HarmonyMethod(typeof(RuntimeRolls),"StaticSumFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetActor),"RollDiceAndSum"),prefix:new HarmonyMethod(typeof(RuntimeRolls),"ActorSumPrefix"));
            NativeSavePlanner.Install(harmony);
            NativeAbilityPlanner.Install(harmony);
            BardicPatches.Install(harmony);
            ShovePatches.Install(harmony);
        }
    }
}
