using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Complete original Shove iterator runs against explicit game-effect, movement,
// service and final-contest boundary doubles. ResolveRolls/ResolveContestCheck
// themselves execute pristine IL in the separate shove-native fixture.
public static partial class RuleDefinitions
{
    public enum Side { Ally, Enemy, Neutral }
    public enum ActivationTime { None, AfterShove }
}
public static class ActionDefinitions
{
    public enum Id { Shove, ShoveBonus, Pushed, FreeFall, UsePower }
    public delegate void ShoveActionUsedHandler(GameLocationCharacter actor,GameLocationCharacter target,ActionDefinition definition,bool success);
}
public class ActionDefinition : BaseDefinition { }
public class FeatureDefinitionPower : FeatureDefinition
{ public RuleDefinitions.ActivationTime ActivationTime {get;set;} }
public class RulesetUsablePower
{ public FeatureDefinitionPower PowerDefinition {get;set;}=new FeatureDefinitionPower(); }
public class RulesetEffectPower : RulesetEffect { }
public partial class RulesetActor
{
    public delegate void ShoveAutomaticIncapacitatedHandler(BaseDefinition definition,RulesetActor actor);
    public ShoveAutomaticIncapacitatedHandler ShoveAutomaticIncapacitated {get;set;}
}
public partial class RulesetCharacter
{
    public bool IsIncapacitated {get;set;}
    public List<RulesetUsablePower> UsablePowers {get;set;}=new List<RulesetUsablePower>();
    public bool IsPowerOverriden(RulesetUsablePower power) {return false;}
    public int GetRemainingUsesOfPower(RulesetUsablePower power) {return 0;}
}
public class GameLocationCharacterEventSystem
{
    public enum Event { None }
    public List<int> Events=new List<int>();
    public IEnumerator UpdateMotionsAndWaitForEvent(Event value) {Events.Add((int)value);yield return null;}
    public IEnumerator WaitForEvent(Event value) {Events.Add((int)value);yield return null;}
}
public partial class GameLocationCharacter
{
    public RuleDefinitions.Side Side {get;set;}=RuleDefinitions.Side.Ally;
    public GameLocationCharacterEventSystem EventSystem {get;set;}=new GameLocationCharacterEventSystem();
    public int Turns,ShoveCalls,ProneCalls;
    public bool LastShoveSuccess,ProneResult=true;
    public void TurnTowards(GameLocationCharacter character,bool instant,bool orient) {Turns++;}
    public IEnumerator WaitForHitAnimation() {yield return null;}
    public void Shove(GameLocationCharacter target,bool success) {ShoveCalls++;LastShoveSuccess=success;}
    public bool SetProne(bool value) {ProneCalls++;return ProneResult;}
}
public partial class CharacterActionParams
{
    public List<GameLocationCharacter> TargetCharacters {get;set;}=new List<GameLocationCharacter>();
    public List<TA.int3> Positions {get;set;}=new List<TA.int3>{new TA.int3()};
    public bool BoolParameter {get;set;}=true;
    public bool BoolParameter4 {get;set;}
    public bool CanBeCancelled {get;set;}
    public string StringParameter {get;set;}
    public CharacterActionParams() { }
    public CharacterActionParams(GameLocationCharacter actor,ActionDefinitions.Id id) { }
    public CharacterActionParams(GameLocationCharacter actor,ActionDefinitions.Id id,TA.int3 position) { }
}
public partial class CharacterAction
{
    public enum InterruptionType { None }
    public delegate void ActionExecutedHandler(CharacterAction action);
    public ActionDefinitions.Id ActionId {get;set;}=ActionDefinitions.Id.Shove;
    public ActionDefinition ActionDefinition {get;set;}=new ActionDefinition();
}
public interface IGameLocationActionService
{
    ActionDefinitions.ShoveActionUsedHandler ShoveActionUsed {get;}
    void StopCharacterActions(GameLocationCharacter character,CharacterAction.InterruptionType interruption,bool force);
    void ExecuteAction(CharacterActionParams parameters,CharacterAction.ActionExecutedHandler callback,bool priority);
    bool IsActionInCurrentChains(ActionDefinitions.Id id,bool includePending,bool includeCurrent,GameLocationCharacter actor);
}
public interface IRulesetImplementationService
{ RulesetEffectPower InstantiateEffectPower(RulesetCharacter actor,RulesetUsablePower power,bool delay); }
public partial class Battle : IGameLocationActionService,IRulesetImplementationService
{
    public ActionDefinitions.ShoveActionUsedHandler ShoveActionUsed {get;set;}
    public int StoppedActions,ExecutedActions;
    public void StopCharacterActions(GameLocationCharacter character,CharacterAction.InterruptionType interruption,bool force) {StoppedActions++;}
    public void ExecuteAction(CharacterActionParams parameters,CharacterAction.ActionExecutedHandler callback,bool priority) {ExecutedActions++;}
    public bool IsActionInCurrentChains(ActionDefinitions.Id id,bool includePending,bool includeCurrent,GameLocationCharacter actor) {return false;}
    public RulesetEffectPower InstantiateEffectPower(RulesetCharacter actor,RulesetUsablePower power,bool delay) {return new RulesetEffectPower();}
}
public static partial class Trace {public static void LogError(string message) {Errors++;}}
public class CharacterActionShove : CharacterAction
{
    public static int NativeCalls;
    public static bool NativeResult=true;
    public static GameLocationCharacter LastActor,LastOpponent;
    public static ActionDefinitions.Id LastActionId;
    public static Exception NativeError;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ResolveRolls(GameLocationCharacter actor,GameLocationCharacter opponent,ActionDefinitions.Id actionId)
    {NativeCalls++;LastActor=actor;LastOpponent=opponent;LastActionId=actionId;if(NativeError!=null)throw NativeError;return NativeResult;}
    [MethodImpl(MethodImplOptions.NoInlining)] public IEnumerator ExecuteImpl() {throw new Exception("Original native Shove iterator must be copied");}
}
