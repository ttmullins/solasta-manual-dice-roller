using System;
using System.Collections.Generic;
public class RulesetActor { public ulong Guid; }
public class RulesetCharacter : RulesetActor { public bool IsSubstitute; public RulesetCharacter OriginalFormCharacter; }
public class RulesetCharacterHero : RulesetCharacter { }
public interface INetworkingService { bool LeaveRoom(); }
public sealed class PlayerController { public enum ControllerType { Human,AI }; public ControllerType Type; }
public interface IPlayerControllerService { int GetPlayerControllingHero(RulesetCharacter character); Dictionary<int,PlayerController> ControllersById {get;} }
public static class ServiceRepository {
public static IPlayerControllerService Players;
public static T GetService<T>() { return typeof(T)==typeof(IPlayerControllerService)?(T)(object)Players:default(T); }
}
namespace UnityModManagerNet { public static class UnityModManager {} }
namespace HarmonyLib { public sealed class Harmony {} }
namespace ManualDiceRoller {
public sealed class ModSettings { public bool Attack=true,Ability=true,SavingThrows=true,Initiative=true,Death=true,Damage=true,Healing=true,SpellPools=true,HitDice=true,EnemyRolls,MultiplayerExtras=true,Concentration=true,Downtime=true,Lore=true,RepeatSaves=true,LingeringEffects=true,Features=true,Stealth=true,CharacterCreation=true,DamageSaves,HiddenPerception,LevelUpHitPoints,Traps=true; }
public static class Plugin { public static bool Enabled=true; public static readonly ModSettings Settings=new ModSettings(); public static readonly List<string> Logs=new List<string>(); public static void Log(string value) { Logs.Add(value); } }
public static class RuntimeRolls { public static bool NetworkHolding; public static int Releases; public static void ReleaseNetworkHold() { Releases++; NetworkHolding=false; } }
public sealed class RollPrompt { readonly int[] faces; public string Actor="Player"; public RollPrompt(params int[] faces) { this.faces=(int[])faces.Clone(); } public int[] CopyDieFaces() { return (int[])faces.Clone(); } }
public static class RollWindow {
public static bool Visible; public static int Displays,Closes,Waiting,WaitingCloses; public static Action<int[]> Accepted; public static Action Automatic; public static bool Authorized;
public static void Display(RollPrompt prompt,Action<int[]> accepted,Action automatic,bool networkAuthorized=false) { if(Visible) throw new InvalidOperationException(); Visible=true; Displays++; Accepted=accepted; Automatic=automatic; Authorized=networkAuthorized; }
public static void Close(RollPrompt prompt) { Visible=false; Closes++; }
public static void DisplayWaiting(RollPrompt prompt,string message) { Waiting++; }
public static void CloseWaiting(RollPrompt prompt) { WaitingCloses++; }
public static void Submit(params int[] values) { var callback=Accepted; Visible=false; callback(values); }
public static void Reset() { Visible=false; Displays=Closes=Waiting=WaitingCloses=0; Accepted=null; Automatic=null; Authorized=false; }
}
}
