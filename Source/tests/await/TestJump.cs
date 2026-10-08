using System;using System.Collections;using System.Collections.Generic;
namespace TA { public struct int3 {public int x,y,z;}public class Coroutine {
protected Stack<IEnumerator> enumerators=new Stack<IEnumerator>();public bool IsFinished{get;private set;}public bool IsSuspended{get;private set;}public Exception LastException{get;private set;}public int Count{get{return enumerators.Count;}}
public void Push(IEnumerator value){enumerators.Push(value);IsFinished=false;}public void Reset(){enumerators.Clear();IsFinished=false;IsSuspended=false;}public void Run(){throw new Exception("actual native Run must be extracted");}protected IEnumerator EnumerableRun(){while(!IsFinished){Run();if(!IsFinished)yield return null;}}
} } public static partial class Trace {public static int Errors;public static void LogException(Exception e){Errors++;}}

public partial class RulesetActor {}
public static partial class RuleDefinitions {public enum AdvantageType{None,Advantage,Disadvantage}public enum RollOutcome{CriticalSuccess,Success,Neutral,Failure,CriticalFailure}}
public partial class ActionModifier {}
public partial class CharacterActionParams {public List<ActionModifier> ActionModifiers{get;set;}=new List<ActionModifier>{new ActionModifier()};}
public partial class GameLocationCharacter {
 public int Calls;public int ReturnResult=17;public Exception ThrowError;public RuleDefinitions.RollOutcome ReturnOutcome=RuleDefinitions.RollOutcome.Success;
 public int RollAbilityCheck(string ability,string skill,int dc,RuleDefinitions.AdvantageType affinity,ActionModifier mod,bool passive,int forced,out RuleDefinitions.RollOutcome outcome,out int delta,bool rollDie=true,bool notify=true,bool display=true){Calls++;outcome=ReturnOutcome;delta=ReturnResult-dc;if(ThrowError!=null)throw ThrowError;return ReturnResult;}
 public void PublishValues(ref int value,out RuleDefinitions.RollOutcome outcome){Calls++;value+=5;outcome=RuleDefinitions.RollOutcome.Failure;}
 public ref int ReadReference(){return ref ReturnResult;}
}
public partial class CharacterAction {public GameLocationCharacter ActingCharacter{get;set;}=new GameLocationCharacter();public CharacterActionParams ActionParams{get;set;}=new CharacterActionParams();public RuleDefinitions.RollOutcome AbilityCheckRollOutcome{get;set;}=RuleDefinitions.RollOutcome.Success;public int AbilityCheckSuccessDelta{get;set;}}
public class CharacterActionMoveStepJump:CharacterAction {
 private TA.int3 jumpPosition,landingPosition;
 public static bool Acrobatics=true,Athletics=true,Penalty=false;
 public static bool NeedsAcrobaticsCheck(TA.int3 p){return Acrobatics;}public static bool NeedsAthleticsCheck(GameLocationCharacter c,TA.int3 a,TA.int3 b){return Athletics;}public static bool AutomaticPenalty(GameLocationCharacter c,TA.int3 a,TA.int3 b){return Penalty;}
 public IEnumerator RollChecksIfNecessary(){throw new Exception("actual native iterator factory must be extracted");}
}
public interface IGameLocationBattleService {IEnumerator HandleFailedAbilityCheck(CharacterAction action,GameLocationCharacter actor,ActionModifier mod);}
public partial class Battle:IGameLocationBattleService {public int FailedChecks;public IEnumerator HandleFailedAbilityCheck(CharacterAction a,GameLocationCharacter b,ActionModifier c){FailedChecks++;yield return null;}}
public static class ServiceRepository {public static Battle CurrentBattle=new Battle();public static T GetService<T>(){return(T)(object)CurrentBattle;}}
public class AwaitEh {
 public GameLocationCharacter Actor=new GameLocationCharacter();public ActionModifier Mod=new ActionModifier();public int NativeFinally,Delta;
 public IEnumerator Fault(){foreach(var value in new List<int>{3}){try{yield return null;}finally{NativeFinally++;}}RuleDefinitions.RollOutcome outcome;int delta;Actor.RollAbilityCheck("Strength","Athletics",12,RuleDefinitions.AdvantageType.None,Mod,false,-1,out outcome,out delta);Delta=delta;yield return null;}
 public IEnumerator UnsafeFinally(){yield return null;try{RuleDefinitions.RollOutcome outcome;int delta;Actor.RollAbilityCheck("Dexterity","Acrobatics",10,RuleDefinitions.AdvantageType.None,Mod,false,-1,out outcome,out delta);}finally{NativeFinally++;}yield return null;}
 public bool ThrowCleanup;
 public IEnumerator ActiveFault(){yield return null;foreach(var value in new List<int>{3}){try{yield return null;RuleDefinitions.RollOutcome outcome;int delta;Actor.RollAbilityCheck("Strength","Athletics",12,RuleDefinitions.AdvantageType.None,Mod,false,-1,out outcome,out delta);Delta=delta;}finally{NativeFinally++;if(ThrowCleanup)throw new InvalidOperationException("owned native cleanup failed");}}}
 public IEnumerator BranchYield(){yield return null;RuleDefinitions.RollOutcome outcome;int delta;Actor.RollAbilityCheck("Strength","Athletics",12,RuleDefinitions.AdvantageType.None,Mod,false,-1,out outcome,out delta);Delta=delta;}
 public IEnumerator BranchUnverified(){yield return null;RuleDefinitions.RollOutcome outcome;int delta;Actor.RollAbilityCheck("Strength","Athletics",12,RuleDefinitions.AdvantageType.None,Mod,false,-1,out outcome,out delta);Delta=delta;}
 public IEnumerator VoidRoll(){int value=8;RuleDefinitions.RollOutcome outcome;Actor.PublishValues(ref value,out outcome);Delta=value;yield return null;}
 public IEnumerator UnsafeOutput(){Delta=Actor.ReadReference();yield return null;}
}
