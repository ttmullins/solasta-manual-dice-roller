using System;
namespace ManualDiceRoller {
public sealed class ModSettings { public bool Attack=true,Ability=true,SavingThrows=true,Initiative=true,Death=true,Damage=true,Healing=true,SpellPools=true,HitDice=true,EnemyRolls,MultiplayerExtras=true,Concentration=true,Downtime=true,Lore=true,RepeatSaves=true,LingeringEffects=true,Features=true,Stealth=true,CharacterCreation=true,DamageSaves,HiddenPerception,LevelUpHitPoints,Traps=true; }
public static class Plugin { public static bool Enabled=true; public static readonly ModSettings Settings=new ModSettings(); public static void Log(string value) {} }
public static class RuntimeRolls { public static bool NetworkHolding; public static int Releases; public static void ReleaseNetworkHold() { Releases++; NetworkHolding=false; } }
public sealed class RollPrompt { readonly int[] faces; public string Actor="Player"; public RollPrompt(params int[] faces) { this.faces=(int[])faces.Clone(); } public int[] CopyDieFaces() { return (int[])faces.Clone(); } }
public static class RollWindow { public static bool Visible; public static void Display(RollPrompt prompt,Action<int[]> accepted,Action automatic,bool networkAuthorized=false) {} public static void Close(RollPrompt prompt) {} public static void DisplayWaiting(RollPrompt prompt,string message) {} public static void CloseWaiting(RollPrompt prompt) {} }
}
