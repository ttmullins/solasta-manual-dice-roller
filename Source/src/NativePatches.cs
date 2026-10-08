using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace ManualDiceRoller
{
    // A supported site is pinned to the supplied pristine MoveNext body. Runtime
    // patches preserve its instructions; only verified await/lease blocks are added.
    internal sealed class AwaitSite
    {
        internal readonly string IteratorType, Fingerprint, Id;
        internal readonly int CallOffset;
        internal readonly bool RestoreIteratorReceiver;
        internal AwaitSite(string iteratorType, string fingerprint, int callOffset, string id,bool restoreIteratorReceiver=false)
        { IteratorType=iteratorType.Replace('/', '+'); Fingerprint=fingerprint; CallOffset=callOffset; Id=id; RestoreIteratorReceiver=restoreIteratorReceiver; }
    }

    internal static class NativePatches
    {
        sealed class Parsed { internal int Offset; internal OpCode Code; internal object Operand; }
        sealed class Bound
        {
            internal AwaitSite Site; internal MethodInfo Move, Dispose; internal MethodBase Native;
            internal FieldInfo State, Current; internal int Anchor, Call, Dispatch, NewState, NativeStateCount;
            internal Type[] Locals; internal List<Parsed> IL; internal int ExtraStack; internal bool BranchDispatch;
        }
        sealed class Suspension
        {
            internal int State; internal object[] Locals; internal IEnumerator Wait; internal Bound Site;
        }
        static readonly List<AwaitSite> sites=new List<AwaitSite>();
        static readonly Dictionary<MethodBase,List<Bound>> bound=new Dictionary<MethodBase,List<Bound>>();
        static readonly Dictionary<string,Bound> byId=new Dictionary<string,Bound>(StringComparer.Ordinal);
        static ConditionalWeakTable<object,Suspension> suspended=new ConditionalWeakTable<object,Suspension>();
        sealed class Guard {internal string Type,Method,Hash;internal int Count;}
        static readonly List<Guard> guards=new List<Guard>();
        internal static void RegisterGuard(string type,string method,int count,string hash) {guards.Add(new Guard{Type=type,Method=method,Count=count,Hash=hash});}
        static readonly List<WeakReference> continuations=new List<WeakReference>();
        static bool registered;
        const string CompatibilityId="ManualDiceRoller.FaultCompatibility";
        internal static void Register(AwaitSite site) { if(bound.Count!=0)throw new InvalidOperationException("Await manifest already bound");sites.Add(site); }
        internal static bool Preflight(out string error)
        {
            error=null;bound.Clear();byId.Clear();
            try {
                var game=typeof(RulesetActor).Assembly;
                if(game.GetType("ManualRoll",false)!=null)throw new InvalidOperationException("Restore the original game assembly before loading this plugin.");
                if(!registered){registered=true;NativeSiteManifest.Register();}
                foreach(var guard in guards){
                    var method=game.GetType(guard.Type,true).GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly).Single(m=>m.Name==guard.Method&&m.GetParameters().Length==guard.Count);
                    if(method.GetMethodBody()==null||Hash(method.GetMethodBody().GetILAsByteArray())!=guard.Hash)throw new InvalidOperationException("Unsupported native dice method: "+guard.Type+"."+guard.Method);
                    var patches=Harmony.GetPatchInfo(method);if(patches!=null&&patches.Transpilers.Count>0)throw new InvalidOperationException("Another mod rewrites native dice: "+guard.Type+"."+guard.Method);
                }
                foreach(var site in sites){
                    if(byId.ContainsKey(site.Id))throw new InvalidOperationException("Duplicate await identity: "+site.Id);
                    var type=game.GetType(site.IteratorType,true);
                    var move=type.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                    if(move==null||move.ReturnType!=typeof(bool))throw new InvalidOperationException("Missing native iterator: "+site.IteratorType);
                    var body=move.GetMethodBody();
                    if(body==null||Hash(body.GetILAsByteArray())!=site.Fingerprint)throw new InvalidOperationException("Unsupported native method version: "+site.IteratorType);
                    var patches=Harmony.GetPatchInfo(move);
                    if(patches!=null&&patches.Transpilers.Any(p=>p.owner!=CompatibilityId||p.PatchMethod!=Own(nameof(NormalizeFault))))throw new InvalidOperationException("Another mod already rewrites "+site.IteratorType);
                    var il=Read(move);int call=il.FindIndex(i=>i.Offset==site.CallOffset);
                    if(call<0||!(il[call].Operand is MethodBase)||!(il[call].Code==OpCodes.Call||il[call].Code==OpCodes.Callvirt))throw new InvalidOperationException("Native call site changed: "+site.Id);
                    var native=(MethodBase)il[call].Operand;
                    var nativeMethod=native as MethodInfo;
                    if(nativeMethod==null||!ObservableNativeType(nativeMethod.ReturnType,true)||native.GetParameters().Where(p=>p.ParameterType.IsByRef).Any(p=>!ObservableNativeType(p.ParameterType.GetElementType(),false)))
                        throw new InvalidOperationException("Unsupported native result or output type: "+site.Id);
                    int need=native.GetParameters().Length+(native.IsStatic?0:1),anchor=call-1;
                    for(;anchor>=0;anchor--){need-=Delta(il[anchor],move);if(need==0)break;if(need<0)throw new InvalidOperationException("Shared native argument stack: "+site.Id);}
                    if(anchor<0)throw new InvalidOperationException("Cannot locate native arguments: "+site.Id);
                    var depths=Depths(il,move);
                    int extra=depths[call]-native.GetParameters().Length-(native.IsStatic?0:1);
                    // The pinned Shove conditional carries only this immutable
                    // iterator receiver under its call arguments. Its exact
                    // branch and common bool setter are verified separately;
                    // every other site still requires an empty capture stack.
                    if(site.RestoreIteratorReceiver){
                        if(!PinnedShoveReceiver(site,move,native,il,anchor,call,depths,extra))throw new InvalidOperationException("Unsupported Shove iterator receiver: "+site.Id);
                    }else while(anchor>0&&depths[anchor]>0)anchor--;
                    if(depths[anchor]!=(site.RestoreIteratorReceiver?1:0)||extra<0)throw new InvalidOperationException("Await stack is not empty: "+site.Id);
                    int dispatch=il.FindIndex(i=>i.Code==OpCodes.Switch);bool branchDispatch=false;int nativeStateCount=0;
                    if(dispatch<0){
                        int stateStore=il.FindIndex(i=>i.Code==OpCodes.Stfld&&i.Operand is FieldInfo&&((FieldInfo)i.Operand).Name=="<>1__state");
                        var entries=il.Take(stateStore).Select((v,i)=>new{v,i}).Where(v=>v.v.Code==OpCodes.Brfalse||v.v.Code==OpCodes.Brfalse_S).ToArray();
                        if(entries.Length!=1||depths[entries[0].i]!=1)throw new InvalidOperationException("Unsupported iterator state dispatcher: "+site.Id);
                        // Reserve slots above every original literal state. The
                        // original branch dispatcher remains intact, including
                        // its failed-check/reaction continuation paths.
                        nativeStateCount=1;
                        foreach(var store in il.Select((v,i)=>new{v,i}).Where(v=>v.v.Code==OpCodes.Stfld&&v.v.Operand is FieldInfo&&((FieldInfo)v.v.Operand).Name=="<>1__state")){
                            int storedState;
                            if(store.i==0||!TryConstant(il[store.i-1],out storedState))throw new InvalidOperationException("Unsupported branch iterator state assignment: "+site.Id);
                            if(storedState>=nativeStateCount)nativeStateCount=checked(storedState+1);
                        }
                        dispatch=entries[0].i;branchDispatch=true;
                    }else nativeStateCount=((int[])il[dispatch].Operand).Length;
                    foreach(var h in body.ExceptionHandlingClauses){
                        if(h.Flags==ExceptionHandlingClauseOptions.Finally&&Within(il[anchor].Offset,h.TryOffset,h.TryLength))throw new InvalidOperationException("Await would execute a synchronous finally: "+site.Id);
                        if(Within(il[anchor].Offset,h.HandlerOffset,h.HandlerLength)||h.Flags==ExceptionHandlingClauseOptions.Filter&&il[anchor].Offset>=h.FilterOffset&&il[anchor].Offset<h.HandlerOffset)throw new InvalidOperationException("Await in a native exception handler: "+site.Id);
                    }
                                        if(!SameTryRegions(body,il[dispatch].Offset,il[anchor].Offset))throw new InvalidOperationException("Await would enter a different native exception region: "+site.Id);
                    for(int i=anchor;i<call;i++)if(!PureArgument(il[i]))throw new InvalidOperationException("Unverified native argument evaluation at "+site.Id+" IL_"+il[i].Offset.ToString("x4")+" "+il[i].Code.Name+" "+il[i].Operand);
                    if(il.Where((v,i)=>i<anchor||i>call).Any(i=>Targets(i).Any(t=>t>il[anchor].Offset&&t<=il[call].Offset)))throw new InvalidOperationException("A branch enters native arguments: "+site.Id);
                    if(il.Skip(anchor).Take(call-anchor).Any(i=>Targets(i).Any(t=>t<il[anchor].Offset||t>il[call].Offset)))throw new InvalidOperationException("Native arguments branch outside the capture block: "+site.Id);
                    foreach(var h in body.ExceptionHandlingClauses.Where(h=>h.Flags==ExceptionHandlingClauseOptions.Fault)){
                        var cleanup=il.Where(i=>Within(i.Offset,h.HandlerOffset,h.HandlerLength)&&i.Code!=OpCodes.Nop).ToArray();
                        if(cleanup.Length!=3||cleanup[0].Code!=OpCodes.Ldarg_0||!(cleanup[1].Operand is MethodBase)||((MethodBase)cleanup[1].Operand).Name!="System.IDisposable.Dispose"||((MethodBase)cleanup[1].Operand).DeclaringType!=type||cleanup[2].Code!=OpCodes.Endfinally)throw new InvalidOperationException("Unsupported native fault cleanup: "+site.Id);
                    }
                    var locals=body.LocalVariables.Select(v=>v.LocalType).ToArray();
                    if(body.LocalVariables.Any(v=>v.IsPinned||v.LocalType.IsByRef||v.LocalType.IsPointer||v.LocalType==typeof(TypedReference)))throw new InvalidOperationException("Native local cannot survive an await: "+site.Id);
                    var state=type.GetField("<>1__state",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                    var current=type.GetField("<>2__current",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                    if(state==null||state.FieldType!=typeof(int)||current==null||current.FieldType!=typeof(object))throw new InvalidOperationException("Unsupported native iterator fields: "+site.Id);
                    var dispose=type.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).SingleOrDefault(m=>m.Name=="System.IDisposable.Dispose");
                    if(dispose==null||!typeof(IDisposable).IsAssignableFrom(type))throw new InvalidOperationException("Native iterator disposal missing: "+site.Id);
                    var b=new Bound{Site=site,Move=move,Dispose=dispose,Native=native,State=state,Current=current,Anchor=anchor,Call=call,Dispatch=dispatch,Locals=locals,IL=il,ExtraStack=extra,BranchDispatch=branchDispatch,NativeStateCount=nativeStateCount};
                    List<Bound> list;if(!bound.TryGetValue(move,out list))bound[move]=list=new List<Bound>();
                    b.NewState=nativeStateCount+list.Count;list.Add(b);byId.Add(site.Id,b);
                }
                return true;
            } catch(Exception e){bound.Clear();byId.Clear();error=e.Message;return false;}
        }
        internal static void Install(Harmony harmony)
        {
            if(bound.Count==0)throw new InvalidOperationException("Native await preflight has not succeeded");
            var compatibility=new Harmony(CompatibilityId);
            foreach(var method in bound.Keys.Where(m=>m.GetMethodBody().ExceptionHandlingClauses.Any(h=>h.Flags==ExceptionHandlingClauseOptions.Fault))){
                var info=Harmony.GetPatchInfo(method);
                if(info==null||!info.Transpilers.Any(p=>p.owner==CompatibilityId&&p.PatchMethod==Own(nameof(NormalizeFault))))
                    compatibility.Patch(method,transpiler:new HarmonyMethod(Own(nameof(NormalizeFault))){priority=Priority.Last});
            }
            var transpiler=new HarmonyMethod(typeof(NativePatches).GetMethod(nameof(Transpile),BindingFlags.Static|BindingFlags.NonPublic));
            var finalizer=new HarmonyMethod(typeof(NativePatches).GetMethod(nameof(MoveFinalizer),BindingFlags.Static|BindingFlags.NonPublic));
            foreach(var pair in bound){
                bool nativeFault=pair.Key.GetMethodBody().ExceptionHandlingClauses.Any(h=>h.Flags==ExceptionHandlingClauseOptions.Fault);
                harmony.Patch(pair.Key,transpiler:transpiler,finalizer:nativeFault?null:finalizer);
            }
            var cleanup=new HarmonyMethod(typeof(NativePatches).GetMethod(nameof(DisposePrefix),BindingFlags.Static|BindingFlags.NonPublic));
            foreach(var dispose in bound.Values.SelectMany(v=>v).Select(b=>b.Dispose).Distinct())harmony.Patch(dispose,prefix:cleanup);
            var reset=typeof(TA.Coroutine).GetMethod("Reset",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
            harmony.Patch(reset,prefix:new HarmonyMethod(typeof(NativePatches).GetMethod(nameof(CoroutineResetPrefix),BindingFlags.NonPublic|BindingFlags.Static)));
        }
        internal static void Reset()
        {
            // An enabled patch must retain the continuation for a live iterator:
            // after ticket cancellation it resumes through our added state and
            // restores its saved native locals before continuing automatically.
            // Only native Dispose/TA.Reset may discard an owned continuation.
            RuntimeRolls.Reset();
            continuations.RemoveAll(r=>!r.IsAlive);
        }
        internal static bool IsRegistered(string id) {return id!=null&&byId.ContainsKey(id);}
        internal static bool HasPending {get{return continuations.Any(r=>{var iterator=r.Target;Suspension s;return iterator!=null&&suspended.TryGetValue(iterator,out s);});}}
        static IEnumerator Prepare(object iterator,string id,object[] args,object[] locals)
        {
            Bound b=byId[id];
            Suspension old;if(suspended.TryGetValue(iterator,out old))throw new InvalidOperationException("Overlapping await in one native iterator");
            var wait=RuntimeRolls.PrepareNativeRoll(iterator,id,b.Native,args);
            if(wait==null)return null;
            suspended.Add(iterator,new Suspension{State=(int)b.State.GetValue(iterator),Locals=locals,Wait=wait,Site=b});continuations.Add(new WeakReference(iterator));
            return wait;
        }
        static object[] Resume(object iterator)
        {
            Suspension frame;if(!suspended.TryGetValue(iterator,out frame))throw new InvalidOperationException("Native await lost its continuation");
            suspended.Remove(iterator);continuations.RemoveAll(r=>!r.IsAlive||ReferenceEquals(r.Target,iterator));frame.Site.State.SetValue(iterator,frame.State);return frame.Locals;
        }
        static void Enter(object iterator,string id) { RuntimeRolls.EnterNative(iterator,id); }
        static void Exit(object iterator,string id) { RuntimeRolls.ExitNative(iterator,id); }
        static void Cancel(object iterator)
        {
            Suspension frame;
            try {
                if(suspended.TryGetValue(iterator,out frame)){
                    suspended.Remove(iterator);continuations.RemoveAll(r=>!r.IsAlive||ReferenceEquals(r.Target,iterator));frame.Site.State.SetValue(iterator,frame.State);
                    var dispose=frame.Wait as IDisposable;if(dispose!=null)dispose.Dispose();
                }
            } finally {
                RuntimeRolls.CancelOwner(iterator);
            }
        }
        static void DisposePrefix(object __instance) { CleanupOwner(__instance); }
        static Exception MoveFinalizer(object __instance,Exception __exception)
        {
            if(__exception!=null)CleanupOwner(__instance);
            return __exception;
        }
        static void CleanupOwner(object iterator)
        {
            // Cancellation must not prevent the native Dispose body from
            // running or replace a native roll exception with a UI error.
            try{Cancel(iterator);}catch(Exception error){Trace.LogException(error);}
            try{RuntimeRolls.AbortNative(iterator);}catch(Exception error){Trace.LogException(error);}
        }
        static void CoroutineResetPrefix(TA.Coroutine __instance)
        { CancelNativeStack(__instance,new HashSet<TA.Coroutine>()); }
        static void CancelNativeStack(TA.Coroutine native,HashSet<TA.Coroutine> visited)
        {
            if(native==null||!visited.Add(native))return;
            var field=typeof(TA.Coroutine).GetField("enumerators",BindingFlags.Instance|BindingFlags.NonPublic);
            var stack=field==null?null:field.GetValue(native) as Stack<IEnumerator>;if(stack==null)return;
            foreach(var iterator in stack.ToArray()){
                Suspension frame;if(suspended.TryGetValue(iterator,out frame)){
                    // Native TA.Reset only clears its stack; it never disposes
                    // frames. Our pending native owner can hold active compiler
                    // finally scopes, so restore its saved state through the
                    // Dispose prefix and execute the original cleanup once.
                    try{((IDisposable)iterator).Dispose();}catch(Exception error){Trace.LogException(error);}
                }
                // TA.Run pushes its own EnumerableRun iterator when a yielded
                // child TA coroutine must be driven. Inspect only that explicit
                // native wrapper, never arbitrary user enumerator state.
                var type=iterator.GetType();
                if(type.DeclaringType==typeof(TA.Coroutine)){
                    var owner=type.GetField("<>4__this",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                    if(owner!=null&&owner.FieldType==typeof(TA.Coroutine))CancelNativeStack(owner.GetValue(iterator) as TA.Coroutine,visited);
                }
            }
        }
        static MethodInfo Own(string name) { return typeof(NativePatches).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static); }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> input,ILGenerator generator,MethodBase __originalMethod)
        {
            List<Bound> list;if(!bound.TryGetValue(__originalMethod,out list))throw new InvalidOperationException("Native await method was not preflighted");
            var original=input.Select(i=>new CodeInstruction(i)).ToList();
            if(original.Count!=list[0].IL.Count)throw new InvalidOperationException("Another transpiler changed native instructions");
            for(int i=0;i<original.Count;i++)if(original[i].opcode!=list[0].IL[i].Code)throw new InvalidOperationException("Native instruction mismatch");
            var extraCases=new List<Label>();var before=new Dictionary<int,List<CodeInstruction>>();var after=new Dictionary<int,List<CodeInstruction>>();
            var returnTrue=generator.DefineLabel();var finish=new CodeInstruction(OpCodes.Ldc_I4_1);finish.labels.Add(returnTrue);
            foreach(var b in list){
                var prepare=new List<CodeInstruction>();var resume=generator.DefineLabel();var continuation=generator.DefineLabel();extraCases.Add(resume);
                var locals=generator.DeclareLocal(typeof(object[]));var awaiter=generator.DeclareLocal(typeof(IEnumerator));
                // Re-evaluate only the hash-verified pure argument loads. Byref
                // outputs are used only inside this call frame and never boxed.
                var localLabels=new Dictionary<Label,Label>();
                for(int i=b.Anchor;i<=b.Call;i++)foreach(var label in original[i].labels)localLabels[label]=generator.DefineLabel();
                for(int i=b.Anchor;i<b.Call;i++){
                    var clone=new CodeInstruction(original[i]);clone.labels=clone.labels.Select(l=>localLabels[l]).ToList();clone.blocks.Clear();
                    if(clone.operand is Label)clone.operand=localLabels[(Label)clone.operand];
                    else if(clone.operand is Label[])clone.operand=((Label[])clone.operand).Select(l=>localLabels[l]).ToArray();
                    prepare.Add(clone);
                }
                var argumentEnd=new CodeInstruction(OpCodes.Nop);argumentEnd.labels.AddRange(original[b.Call].labels.Select(l=>localLabels[l]));prepare.Add(argumentEnd);
                var argTypes=new List<Type>();if(!b.Native.IsStatic)argTypes.Add(b.Native.DeclaringType);argTypes.AddRange(b.Native.GetParameters().Select(p=>p.ParameterType));
                var captured=argTypes.Select(generator.DeclareLocal).ToArray();
                for(int i=captured.Length-1;i>=0;i--)prepare.Add(new CodeInstruction(OpCodes.Stloc,captured[i]));
                for(int i=0;i<b.ExtraStack;i++)prepare.Add(new CodeInstruction(OpCodes.Pop));
                prepare.Add(new CodeInstruction(OpCodes.Ldarg_0));prepare.Add(new CodeInstruction(OpCodes.Ldstr,b.Site.Id));
                prepare.Add(new CodeInstruction(OpCodes.Ldc_I4,argTypes.Count));prepare.Add(new CodeInstruction(OpCodes.Newarr,typeof(object)));
                for(int i=0;i<argTypes.Count;i++){
                    prepare.Add(new CodeInstruction(OpCodes.Dup));prepare.Add(new CodeInstruction(OpCodes.Ldc_I4,i));
                    if(argTypes[i].IsByRef)prepare.Add(new CodeInstruction(OpCodes.Ldnull));
                    else {prepare.Add(new CodeInstruction(OpCodes.Ldloc,captured[i]));if(argTypes[i].IsValueType)prepare.Add(new CodeInstruction(OpCodes.Box,argTypes[i]));}
                    prepare.Add(new CodeInstruction(OpCodes.Stelem_Ref));
                }
                Snapshot(prepare,b.Locals);prepare.Add(new CodeInstruction(OpCodes.Call,Own(nameof(Prepare))));prepare.Add(new CodeInstruction(OpCodes.Stloc,awaiter));
                prepare.Add(new CodeInstruction(OpCodes.Ldloc,awaiter));prepare.Add(new CodeInstruction(OpCodes.Brfalse,continuation));
                prepare.Add(new CodeInstruction(OpCodes.Ldarg_0));prepare.Add(new CodeInstruction(OpCodes.Ldloc,awaiter));prepare.Add(new CodeInstruction(OpCodes.Stfld,b.Current));
                prepare.Add(new CodeInstruction(OpCodes.Ldarg_0));prepare.Add(new CodeInstruction(OpCodes.Ldc_I4,b.NewState));prepare.Add(new CodeInstruction(OpCodes.Stfld,b.State));
                prepare.Add(new CodeInstruction(OpCodes.Leave,returnTrue));
                var resumeStart=new CodeInstruction(OpCodes.Ldarg_0);resumeStart.labels.Add(resume);prepare.Add(resumeStart);
                prepare.Add(new CodeInstruction(OpCodes.Call,Own(nameof(Resume))));prepare.Add(new CodeInstruction(OpCodes.Stloc,locals));
                for(int i=0;i<b.Locals.Length;i++){
                    prepare.Add(new CodeInstruction(OpCodes.Ldloc,locals));prepare.Add(new CodeInstruction(OpCodes.Ldc_I4,i));prepare.Add(new CodeInstruction(OpCodes.Ldelem_Ref));
                    prepare.Add(new CodeInstruction(b.Locals[i].IsValueType?OpCodes.Unbox_Any:OpCodes.Castclass,b.Locals[i]));prepare.Add(new CodeInstruction(OpCodes.Stloc,i));
                }
                var enterStart=new CodeInstruction(OpCodes.Ldarg_0);enterStart.labels.Add(continuation);prepare.Add(enterStart);prepare.Add(new CodeInstruction(OpCodes.Ldstr,b.Site.Id));prepare.Add(new CodeInstruction(OpCodes.Call,Own(nameof(Enter))));
                if(b.Site.RestoreIteratorReceiver)prepare.Add(new CodeInstruction(OpCodes.Ldarg_0));
                // Branches to the original argument anchor must run preparation.
                prepare[0].labels.AddRange(original[b.Anchor].labels);original[b.Anchor].labels.Clear();
                prepare[0].blocks.AddRange(original[b.Anchor].blocks);original[b.Anchor].blocks.Clear();
                before.Add(b.Anchor,prepare);
                // Preparation snapshots the call's input arguments before its
                // asynchronous wait. The original argument block still runs at
                // handoff; verify the values it actually produced before any
                // accepted face can enter a different actor or invocation.
                // Byref outputs remain live only in this synchronous call and
                // are deliberately represented as null in the comparison.
                var verify=new List<CodeInstruction>();
                for(int i=captured.Length-1;i>=0;i--)verify.Add(new CodeInstruction(OpCodes.Stloc,captured[i]));
                verify.Add(new CodeInstruction(OpCodes.Ldarg_0));verify.Add(new CodeInstruction(OpCodes.Ldstr,b.Site.Id));
                verify.Add(new CodeInstruction(OpCodes.Ldc_I4,argTypes.Count));verify.Add(new CodeInstruction(OpCodes.Newarr,typeof(object)));
                for(int i=0;i<argTypes.Count;i++){
                    verify.Add(new CodeInstruction(OpCodes.Dup));verify.Add(new CodeInstruction(OpCodes.Ldc_I4,i));
                    if(argTypes[i].IsByRef)verify.Add(new CodeInstruction(OpCodes.Ldnull));
                    else {verify.Add(new CodeInstruction(OpCodes.Ldloc,captured[i]));if(argTypes[i].IsValueType)verify.Add(new CodeInstruction(OpCodes.Box,argTypes[i]));}
                    verify.Add(new CodeInstruction(OpCodes.Stelem_Ref));
                }
                var validate=AccessTools.Method(typeof(RuntimeRolls),"ValidateNativeArguments");
                if(validate==null)throw new InvalidOperationException("The native argument validation hook is missing.");
                verify.Add(new CodeInstruction(OpCodes.Call,validate));
                for(int i=0;i<captured.Length;i++)verify.Add(new CodeInstruction(OpCodes.Ldloc,captured[i]));
                verify[0].labels.AddRange(original[b.Call].labels);original[b.Call].labels.Clear();
                // Start markers must include validation. End markers still
                // belong after the original native call's protected body.
                var starts=original[b.Call].blocks.Where(block=>block.blockType!=ExceptionBlockType.EndExceptionBlock).ToArray();
                verify[0].blocks.AddRange(starts);original[b.Call].blocks.RemoveAll(block=>block.blockType!=ExceptionBlockType.EndExceptionBlock);
                before.Add(b.Call,verify);
                // Observe only copied values after the unchanged native call.
                // A typed spill preserves its exact return stack; byref locals
                // are read synchronously before any original output consumer.
                var observed=new List<CodeInstruction>();
                var returnType=((MethodInfo)b.Native).ReturnType;
                var nativeResult=returnType==typeof(void)?null:generator.DeclareLocal(returnType);
                if(nativeResult!=null)observed.Add(new CodeInstruction(OpCodes.Stloc,nativeResult));
                observed.Add(new CodeInstruction(OpCodes.Ldarg_0));observed.Add(new CodeInstruction(OpCodes.Ldstr,b.Site.Id));
                if(nativeResult==null)observed.Add(new CodeInstruction(OpCodes.Ldnull));
                else{observed.Add(new CodeInstruction(OpCodes.Ldloc,nativeResult));observed.Add(new CodeInstruction(OpCodes.Box,returnType));}
                var outputs=argTypes.Select((type,index)=>new{type,index}).Where(arg=>arg.type.IsByRef).ToArray();
                observed.Add(new CodeInstruction(OpCodes.Ldc_I4,outputs.Length));observed.Add(new CodeInstruction(OpCodes.Newarr,typeof(object)));
                for(int i=0;i<outputs.Length;i++){
                    var element=outputs[i].type.GetElementType();
                    observed.Add(new CodeInstruction(OpCodes.Dup));observed.Add(new CodeInstruction(OpCodes.Ldc_I4,i));
                    observed.Add(new CodeInstruction(OpCodes.Ldloc,captured[outputs[i].index]));observed.Add(new CodeInstruction(OpCodes.Ldobj,element));observed.Add(new CodeInstruction(OpCodes.Box,element));
                    observed.Add(new CodeInstruction(OpCodes.Stelem_Ref));
                }
                var observe=AccessTools.Method(typeof(RuntimeRolls),"ObserveNativeValues");
                if(observe==null)throw new InvalidOperationException("The native output observation hook is missing.");
                observed.Add(new CodeInstruction(OpCodes.Call,observe));
                if(nativeResult!=null)observed.Add(new CodeInstruction(OpCodes.Ldloc,nativeResult));
                observed.Add(new CodeInstruction(OpCodes.Ldarg_0));observed.Add(new CodeInstruction(OpCodes.Ldstr,b.Site.Id));observed.Add(new CodeInstruction(OpCodes.Call,Own(nameof(Exit))));
                after.Add(b.Call,observed);
            }
            var dispatch=original[list[0].Dispatch];
            if(list[0].BranchDispatch){
                // Keep the original comparison chain for every original state.
                // Only new continuation slots leave it, through trampolines
                // that discard the duplicated dispatcher value before resume.
                var gate=new List<CodeInstruction>();
                for(int i=0;i<extraCases.Count;i++){
                    var next=generator.DefineLabel();var duplicate=new CodeInstruction(OpCodes.Dup);
                    if(i==0){duplicate.labels.AddRange(dispatch.labels);dispatch.labels.Clear();duplicate.blocks.AddRange(dispatch.blocks);dispatch.blocks.Clear();}
                    gate.Add(duplicate);gate.Add(new CodeInstruction(OpCodes.Ldc_I4,list[i].NewState));gate.Add(new CodeInstruction(OpCodes.Bne_Un,next));
                    gate.Add(new CodeInstruction(OpCodes.Pop));gate.Add(new CodeInstruction(OpCodes.Br,extraCases[i]));
                    var native=new CodeInstruction(OpCodes.Nop);native.labels.Add(next);gate.Add(native);
                }
                before.Add(list[0].Dispatch,gate);
            }
            else dispatch.operand=((Label[])dispatch.operand).Concat(extraCases).ToArray();
            var result=new List<CodeInstruction>();for(int i=0;i<original.Count;i++){List<CodeInstruction> block;if(before.TryGetValue(i,out block))result.AddRange(block);result.Add(original[i]);if(after.TryGetValue(i,out block))result.AddRange(block);}
            result.Add(finish);result.Add(new CodeInstruction(OpCodes.Ret));
            foreach(var instruction in result){if(instruction.opcode.OperandType==OperandType.ShortInlineBrTarget){var longName=instruction.opcode.Name.Substring(0,instruction.opcode.Name.Length-2);instruction.opcode=opcodes.Values.First(op=>op.Name==longName);}}
            return result;
        }
        // Kept under a separate identity until process exit. Removing dice
        // patches therefore rebuilds a native-equivalent valid fault body rather
        // than triggering Harmony's invalid fault-handler leave instruction.
        static IEnumerable<CodeInstruction> NormalizeFault(IEnumerable<CodeInstruction> input)
        {
            var result=new List<CodeInstruction>();var regions=new Stack<bool>();
            foreach(var source in input){
                var instruction=new CodeInstruction(source);bool beginsFault=false;
                foreach(var block in instruction.blocks){
                    if(block.blockType==ExceptionBlockType.BeginExceptionBlock)regions.Push(false);
                    if(block.blockType==ExceptionBlockType.BeginFaultBlock){if(regions.Count==0)throw new InvalidOperationException("Fault outside native exception region");regions.Pop();regions.Push(true);beginsFault=true;}
                    if(block.blockType==ExceptionBlockType.BeginCatchBlock||block.blockType==ExceptionBlockType.BeginFinallyBlock){if(regions.Count>0){regions.Pop();regions.Push(false);}}
                }
                if(beginsFault){
                    var pop=new CodeInstruction(OpCodes.Pop);pop.labels.AddRange(instruction.labels);instruction.labels.Clear();
                    foreach(var block in instruction.blocks)pop.blocks.Add(block.blockType==ExceptionBlockType.BeginFaultBlock?new ExceptionBlock(ExceptionBlockType.BeginCatchBlock,typeof(object)):block);
                    instruction.blocks.Clear();result.Add(pop);
                }
                if(instruction.opcode==OpCodes.Endfinally&&regions.Count>0&&regions.Peek())instruction.opcode=OpCodes.Rethrow;
                result.Add(instruction);
                foreach(var block in source.blocks)if(block.blockType==ExceptionBlockType.EndExceptionBlock&&regions.Count>0)regions.Pop();
            }
            return result;
        }
        static void Snapshot(List<CodeInstruction> code,Type[] locals)
        {
            code.Add(new CodeInstruction(OpCodes.Ldc_I4,locals.Length));code.Add(new CodeInstruction(OpCodes.Newarr,typeof(object)));
            for(int i=0;i<locals.Length;i++){
                code.Add(new CodeInstruction(OpCodes.Dup));code.Add(new CodeInstruction(OpCodes.Ldc_I4,i));code.Add(new CodeInstruction(OpCodes.Ldloc,i));
                if(locals[i].IsValueType)code.Add(new CodeInstruction(OpCodes.Box,locals[i]));code.Add(new CodeInstruction(OpCodes.Stelem_Ref));
            }
        }
        static bool Within(int value,int start,int length) { return value>=start&&value<start+length; }
        static bool ObservableNativeType(Type type,bool allowVoid)
        { return type!=null&&(allowVoid&&type==typeof(void)||type.IsValueType&&!type.IsByRef&&!type.IsPointer&&type!=typeof(TypedReference)); }
        static bool PinnedShoveReceiver(AwaitSite site,MethodInfo move,MethodBase native,List<Parsed> il,int anchor,int call,int[] depths,int extra)
        {
            if(site.IteratorType!="CharacterActionShove+<ExecuteImpl>d__4"||site.Id!="CharacterActionShove/<ExecuteImpl>d__4:ResolveRolls:00a7"||site.CallOffset!=0x00a7||
                move.DeclaringType.FullName!=site.IteratorType||!native.IsStatic||native.Name!="ResolveRolls"||native.DeclaringType.FullName!="CharacterActionShove"||((MethodInfo)native).ReturnType!=typeof(bool)||
                anchor<5||call!=anchor+6||il[anchor].Offset!=0x0095||depths[anchor]!=1||extra!=1)return false;
            var parameters=native.GetParameters();
            if(parameters.Length!=3||parameters[0].ParameterType.FullName!="GameLocationCharacter"||parameters[1].ParameterType!=parameters[0].ParameterType||parameters[2].ParameterType.FullName!="ActionDefinitions+Id")return false;
            var expected=new[]{OpCodes.Ldarg_0,OpCodes.Ldloc_2,OpCodes.Ldloc_3,OpCodes.Or,OpCodes.Brtrue_S,OpCodes.Ldloc_1,OpCodes.Call,OpCodes.Ldarg_0,OpCodes.Ldfld,OpCodes.Ldloc_1,OpCodes.Call,OpCodes.Call,OpCodes.Br_S,OpCodes.Ldc_I4_1,OpCodes.Stfld};
            var offsets=new[]{0x008f,0x0090,0x0091,0x0092,0x0093,0x0095,0x0096,0x009b,0x009c,0x00a1,0x00a2,0x00a7,0x00ac,0x00ae,0x00af};
            if(anchor-5+expected.Length>il.Count)return false;
            for(int i=0;i<expected.Length;i++)if(il[anchor-5+i].Offset!=offsets[i]||il[anchor-5+i].Code!=expected[i])return false;
            if(!Equals(il[anchor-1].Operand,0x00ae)||!Equals(il[call+1].Operand,0x00af))return false;
            var target=il[anchor+3].Operand as FieldInfo;var success=il[call+3].Operand as FieldInfo;
            var actorGetter=il[anchor+1].Operand as MethodInfo;var idGetter=il[anchor+5].Operand as MethodInfo;
            return target!=null&&target.DeclaringType==move.DeclaringType&&target.Name=="<target>5__2"&&target.FieldType==parameters[0].ParameterType&&
                success!=null&&success.DeclaringType==move.DeclaringType&&success.Name=="<success>5__4"&&success.FieldType==typeof(bool)&&
                actorGetter!=null&&actorGetter.DeclaringType.FullName=="CharacterAction"&&actorGetter.Name=="get_ActingCharacter"&&actorGetter.ReturnType==parameters[0].ParameterType&&
                idGetter!=null&&idGetter.DeclaringType==actorGetter.DeclaringType&&idGetter.Name=="get_ActionId"&&idGetter.ReturnType==parameters[2].ParameterType;
        }
        static bool TryConstant(Parsed instruction,out int value)
        {
            if(instruction.Code==OpCodes.Ldc_I4||instruction.Code==OpCodes.Ldc_I4_S){value=Convert.ToInt32(instruction.Operand);return true;}
            if(instruction.Code==OpCodes.Ldc_I4_M1){value=-1;return true;}
            var constants=new[]{OpCodes.Ldc_I4_0,OpCodes.Ldc_I4_1,OpCodes.Ldc_I4_2,OpCodes.Ldc_I4_3,OpCodes.Ldc_I4_4,OpCodes.Ldc_I4_5,OpCodes.Ldc_I4_6,OpCodes.Ldc_I4_7,OpCodes.Ldc_I4_8};
            value=Array.IndexOf(constants,instruction.Code);return value>=0;
        }
        static bool SameTryRegions(System.Reflection.MethodBody body,int first,int second)
        { return body.ExceptionHandlingClauses.All(h=>Within(first,h.TryOffset,h.TryLength)==Within(second,h.TryOffset,h.TryLength)); }
        internal static string Hash(byte[] bytes)
        { using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        static bool PureArgument(Parsed i)
        {
            string name=i.Code.Name;
            if((i.Code.FlowControl==FlowControl.Branch||i.Code.FlowControl==FlowControl.Cond_Branch)&&i.Code!=OpCodes.Leave&&i.Code!=OpCodes.Leave_S&&i.Code!=OpCodes.Switch)return true;
            if(name.StartsWith("ldarg",StringComparison.Ordinal)||name.StartsWith("ldloc",StringComparison.Ordinal)||name.StartsWith("ldc",StringComparison.Ordinal))return true;
            if(i.Code==OpCodes.Ldfld||i.Code==OpCodes.Ldflda||i.Code==OpCodes.Ldsfld||i.Code==OpCodes.Ldsflda||i.Code==OpCodes.Ldnull||i.Code==OpCodes.Ldstr||i.Code==OpCodes.Dup||i.Code==OpCodes.Pop||i.Code==OpCodes.Nop||i.Code==OpCodes.Box||i.Code==OpCodes.Unbox_Any||i.Code==OpCodes.Castclass||i.Code==OpCodes.Conv_I4||i.Code==OpCodes.Conv_I8)return true;
            var method=i.Operand as MethodBase;
            return (i.Code==OpCodes.Call||i.Code==OpCodes.Callvirt)&&method!=null&&(method.Name.StartsWith("get_",StringComparison.Ordinal)||method.Name=="GetEffectSource"||method.Name=="GetService"||method.Name=="GetBaseDefinition");
        }
        static IEnumerable<int> Targets(Parsed i)
        { if(i.Operand is int[]){foreach(var t in (int[])i.Operand)yield return t;}else if(i.Code.FlowControl==FlowControl.Branch||i.Code.FlowControl==FlowControl.Cond_Branch){if(i.Operand is int)yield return (int)i.Operand;} }
        static int Delta(Parsed i,MethodBase owner)
        {
            var method=i.Operand as MethodBase;
            if(method!=null&&(i.Code==OpCodes.Call||i.Code==OpCodes.Callvirt||i.Code==OpCodes.Newobj))return (i.Code==OpCodes.Newobj||method is MethodInfo&&((MethodInfo)method).ReturnType!=typeof(void)?1:0)-method.GetParameters().Length-(i.Code!=OpCodes.Newobj&&!method.IsStatic?1:0);
            if(i.Code==OpCodes.Ret)return owner is MethodInfo&&((MethodInfo)owner).ReturnType!=typeof(void)?-1:0;
            Func<StackBehaviour,int> count=b=>b==StackBehaviour.Pop0||b==StackBehaviour.Push0?0:b.ToString().Split('_').Length;
            if(i.Code.StackBehaviourPop==StackBehaviour.Varpop||i.Code.StackBehaviourPush==StackBehaviour.Varpush)throw new InvalidOperationException("Unsupported variable stack instruction");
            return count(i.Code.StackBehaviourPush)-count(i.Code.StackBehaviourPop);
        }
        static int[] Depths(List<Parsed> il,MethodBase method)
        {
            var offsets=il.Select((v,i)=>new{v.Offset,Index=i}).ToDictionary(v=>v.Offset,v=>v.Index);var depth=Enumerable.Repeat(int.MinValue,il.Count).ToArray();var queue=new Queue<int>();
            Action<int,int> visit=(index,value)=>{if(depth[index]==int.MinValue){depth[index]=value;queue.Enqueue(index);}else if(depth[index]!=value)throw new InvalidOperationException("Ambiguous native evaluation stack");};
            visit(0,0);foreach(var h in method.GetMethodBody().ExceptionHandlingClauses){visit(offsets[h.HandlerOffset],h.Flags==ExceptionHandlingClauseOptions.Clause||h.Flags==ExceptionHandlingClauseOptions.Filter?1:0);if(h.Flags==ExceptionHandlingClauseOptions.Filter)visit(offsets[h.FilterOffset],1);}
            while(queue.Count>0){int index=queue.Dequeue();var i=il[index];int next=depth[index]+Delta(i,method);if(next<0)throw new InvalidOperationException("Native stack underflow");if(i.Code==OpCodes.Leave||i.Code==OpCodes.Leave_S)next=0;
                foreach(int target in Targets(i))visit(offsets[target],next);
                if(i.Code.FlowControl!=FlowControl.Branch&&i.Code.FlowControl!=FlowControl.Return&&i.Code.FlowControl!=FlowControl.Throw&&i.Code!=OpCodes.Endfinally&&i.Code!=OpCodes.Endfilter&&index+1<il.Count)visit(index+1,next);
            }
            return depth;
        }
        static readonly Dictionary<short,OpCode> opcodes=typeof(OpCodes).GetFields(BindingFlags.Static|BindingFlags.Public).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(c=>c.Value);
        static List<Parsed> Read(MethodBase method)
        {
            byte[] bytes=method.GetMethodBody().GetILAsByteArray();int at=0;var result=new List<Parsed>();var module=method.Module;
            while(at<bytes.Length){int offset=at;short key=bytes[at++];if(key==0xfe)key=(short)(0xfe00|bytes[at++]);var code=opcodes[key];object value=null;
                switch(code.OperandType){
                    case OperandType.InlineNone:break;
                    case OperandType.ShortInlineI:value=(sbyte)bytes[at++];break;
                    case OperandType.ShortInlineVar:value=(int)bytes[at++];break;
                    case OperandType.InlineVar:value=(int)BitConverter.ToUInt16(bytes,at);at+=2;break;
                    case OperandType.ShortInlineBrTarget:value=at+1+(sbyte)bytes[at++];break;
                    case OperandType.InlineBrTarget:value=at+4+BitConverter.ToInt32(bytes,at);at+=4;break;
                    case OperandType.InlineSwitch:int count=BitConverter.ToInt32(bytes,at);at+=4;int end=at+count*4;var targets=new int[count];for(int i=0;i<count;i++){targets[i]=end+BitConverter.ToInt32(bytes,at);at+=4;}value=targets;break;
                    case OperandType.InlineI:value=BitConverter.ToInt32(bytes,at);at+=4;break;
                    case OperandType.InlineI8:value=BitConverter.ToInt64(bytes,at);at+=8;break;
                    case OperandType.ShortInlineR:value=BitConverter.ToSingle(bytes,at);at+=4;break;
                    case OperandType.InlineR:value=BitConverter.ToDouble(bytes,at);at+=8;break;
                    case OperandType.InlineString:value=module.ResolveString(BitConverter.ToInt32(bytes,at));at+=4;break;
                    case OperandType.InlineMethod:case OperandType.InlineField:case OperandType.InlineType:case OperandType.InlineTok:value=module.ResolveMember(BitConverter.ToInt32(bytes,at));at+=4;break;
                    default:throw new InvalidOperationException("Unsupported native IL operand: "+code.Name);
                }
                result.Add(new Parsed{Offset=offset,Code=code,Operand=value});
            }
            return result;
        }
    }
}
