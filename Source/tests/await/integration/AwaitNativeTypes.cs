using System;
using System.Collections;
using System.Collections.Generic;

namespace TA
{
    public struct int3 { public int x,y,z; }
    public class Coroutine
    {
        protected Stack<IEnumerator> enumerators=new Stack<IEnumerator>();
        public bool IsFinished{get;private set;}
        public bool IsSuspended{get;private set;}
        public Exception LastException{get;private set;}
        public int Count {get{return enumerators.Count;}}
        public void Push(IEnumerator routine){enumerators.Push(routine);IsFinished=false;}
        public void Run(){throw new Exception("Extract pristine native TA.Run before executing this fixture");}
        public void Reset(){throw new Exception("Extract pristine native TA.Reset before executing this fixture");}
        protected IEnumerator EnumerableRun(){while(!IsFinished){Run();if(!IsFinished)yield return null;}}
    }
}
public static class Trace {public static void LogException(Exception error){}}
public static partial class RuleDefinitions
{ public enum RollOutcome{CriticalSuccess,Success,Neutral,Failure,CriticalFailure} }
public class ActionModifier { }
public class CharacterActionParams
{public List<ActionModifier> ActionModifiers{get;set;}=new List<ActionModifier>{new ActionModifier()};}
public partial class GameLocationCharacter
{
    public int Calls;
    // The combined cancellation fixture stops before native consumption. This
    // boundary double delegates to genuine extracted actor RollDie when resumed.
    public int RollAbilityCheck(string abilityScoreName,string proficiencyName,int checkDC,
        RuleDefinitions.AdvantageType advantageType,ActionModifier modifier,bool passive,int forcedRoll,
        out RuleDefinitions.RollOutcome outcome,out int delta,bool rollDie=true,bool notify=true,bool display=true)
    {
        Calls++;
        int result=forcedRoll>0?forcedRoll:RulesetCharacter.RollAbilityCheck(abilityScoreName,proficiencyName,new List<RuleDefinitions.TrendInfo>(),passive);
        outcome=result>=checkDC?RuleDefinitions.RollOutcome.Success:RuleDefinitions.RollOutcome.Failure;
        delta=result-checkDC;
        return result;
    }
}
public class CharacterAction
{
    public GameLocationCharacter ActingCharacter{get;set;}=new GameLocationCharacter{RulesetCharacter=new RulesetCharacter()};
    public CharacterActionParams ActionParams{get;set;}=new CharacterActionParams();
    public RuleDefinitions.RollOutcome AbilityCheckRollOutcome{get;set;}=RuleDefinitions.RollOutcome.Success;
    public int AbilityCheckSuccessDelta{get;set;}
}
public class CharacterActionMoveStepJump:CharacterAction
{
    private TA.int3 jumpPosition,landingPosition;
    public static bool Acrobatics=true,Athletics=true,Penalty=false;
    public static bool NeedsAcrobaticsCheck(TA.int3 position){return Acrobatics;}
    public static bool NeedsAthleticsCheck(GameLocationCharacter character,TA.int3 first,TA.int3 second){return Athletics;}
    public static bool AutomaticPenalty(GameLocationCharacter character,TA.int3 first,TA.int3 second){return Penalty;}
    public IEnumerator RollChecksIfNecessary(){throw new Exception("Extract pristine Jump iterator before executing this fixture");}
}
public interface IGameLocationBattleService
{IEnumerator HandleFailedAbilityCheck(CharacterAction action,GameLocationCharacter actor,ActionModifier modifier);}
