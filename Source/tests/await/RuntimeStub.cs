using System;using System.Collections;using System.Collections.Generic;using System.Reflection;
namespace ManualDiceRoller {internal static class RuntimeRolls {
internal static bool Ready,Automatic,ThrowWaitDispose;internal static int Prepared,Entered,Exited,Cancelled,Disposed;internal static List<object[]> Args=new List<object[]>();
internal static IEnumerator PrepareNativeRoll(object i,string s,MethodBase m,object[] a){if(Automatic)return null;Prepared++;Args.Add(a);Ready=false;return Wait();}
static IEnumerator Wait(){try{while(!Ready)yield return null;}finally{Disposed++;if(ThrowWaitDispose){ThrowWaitDispose=false;throw new InvalidOperationException("owned waiter cleanup failed");}}}
internal static void EnterNative(object i,string s){Entered++;}internal static void ExitNative(object i,string s){Exited++;}internal static void AbortNative(object i){}internal static void CancelOwner(object i){Cancelled++;}internal static void Reset(){Automatic=true;Ready=true;}
internal static int Validated;internal static bool RejectArgumentMutation;internal static object[] ActualArguments;
internal static List<object> NativeReturns=new List<object>();internal static List<object[]> NativeOutputs=new List<object[]>();internal static List<string> ObservedSites=new List<string>();internal static List<object> ObservedOwners=new List<object>();internal static bool ObserveBeforeExit=true;
internal static void ObserveNativeValues(object i,string s,object result,object[] outputs){ObserveBeforeExit&=Entered>Exited;NativeReturns.Add(result);NativeOutputs.Add((object[])outputs.Clone());ObservedSites.Add(s);ObservedOwners.Add(i);}
internal static void ValidateNativeArguments(object i,string s,object[] a){Validated++;ActualArguments=a;if(!RejectArgumentMutation)return;var expected=Args[Args.Count-1];for(int index=0;index<expected.Length;index++){var left=expected[index];var right=a[index];if(left==null&&right==null)continue;if(left!=null&&(left is string||left.GetType().IsValueType)?left.Equals(right):ReferenceEquals(left,right))continue;throw new InvalidOperationException("Native invocation arguments changed");}}
}}
