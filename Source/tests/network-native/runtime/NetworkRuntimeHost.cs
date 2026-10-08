using System;
using System.Collections;
using ManualDiceRoller;

namespace ManualDiceRoller
{
    // Only the network transport and room consensus are represented here.
    // Protocol fault handling is independently exercised by tests/network.
    internal static class MultiplayerRolls
    {
        internal static string SessionToken{get{return "fixture";}}internal static int LocalActor{get{return 1;}}internal static bool Ready{get{return true;}}internal static bool Busy{get{return RuntimeRolls.NetworkHolding;}}
        internal static bool Failed;internal static string Status{get{return "Transport boundary fixture";}}
        internal static int Rounds,Frames;internal static bool Automatic,EndAdmission,CompletionDiffers;
        internal static int Controller=1;
        internal static int OwnerFor(RulesetActor actor){return Controller;}
        internal static Action AfterInput;
        internal static void Initialize(){}internal static void Update(){}internal static void Shutdown(){Reset();}
        internal static void Reset(){Failed=false;Rounds=0;Frames=0;Automatic=false;EndAdmission=false;CompletionDiffers=false;AfterInput=null;Controller=1;Differences=0;}
        internal static void Fail(string reason){Failed=true;Plugin.Log(reason);}
        internal static void Rearm(){}
        internal static readonly System.Collections.Generic.List<string> Descriptors=new System.Collections.Generic.List<string>();
        internal static void LogDescriptor(string actionKey,string state,string parts){Descriptors.Add(actionKey+" "+state+" "+parts);}
        internal static readonly System.Collections.Generic.List<string> Results=new System.Collections.Generic.List<string>();
        internal static int Differences;
        internal static void LogResult(string actionKey,string result,string parts){Results.Add(actionKey+" "+result);}
        internal static void ResultsDiffered(string actionKey,bool manualDice){Differences++;}
        internal static IEnumerator Rendezvous(string actionKey,int ordinal,string signature,RulesetActor actor,RollPrompt prompt,bool manualAllowed,Action<int[]> accept,Action<bool> automatic,bool allowAutomaticOnMismatch=false)
        {
            Rounds++;
            // Network replies arrive over later rendered frames.
            for(int frame=0;frame<Frames;frame++)yield return null;
            if(ordinal==-1&&EndAdmission){EndAdmission=false;yield break;}
            bool completion=signature.StartsWith("completed/",StringComparison.Ordinal);
            if(completion&&CompletionDiffers){CompletionDiffers=false;automatic(true);yield break;}
            bool useNative=!completion&&(Automatic||!manualAllowed);
            if(prompt!=null&&!useNative)
            {
                var values=new int[prompt.DieFaces.Length];for(int i=0;i<values.Length;i++)values[i]=RollWindow.Inputs.Count==0?prompt.DieFaces[i]:RollWindow.Inputs.Dequeue();
                RollWindow.Prompts.Add(prompt);accept(values);
                var after=AfterInput;AfterInput=null;if(after!=null)after();
            }
            automatic(useNative);
            yield break;
        }
    }
    internal static class NetworkLog {internal static string Line(string message,params object[] values){return message+"/"+string.Join("/",values);}}
    internal static class NativeNetworkDescriptor
    {
        internal static NativeRollPlan LastCompletedPlan;
        internal static string ActionKey(NativeInvocation invocation,string randomState){return invocation.Site;}
        internal static string Identity(NativeInvocation invocation){return invocation.Site;}
        internal static string Signature(NativeRollPlan plan){return plan.Invocation.Site+"/plan";}
        internal static string CompletedParts(NativeRollPlan plan,string state){return "rng="+state;}
        internal static string Fallback(string identity,int ordinal){return "completed/unavailable";}
        internal static string PromptSignature(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities){return leaf.Role+"/"+identities.Length;}
        internal static string CompletedSignature(NativeRollPlan plan,string state){LastCompletedPlan=plan;return "completed/"+state;}
        internal static string Parts(NativeInvocation invocation,string state){return "rng="+state;}
    }
}
