using System;
using System.Collections;
using System.Linq;
using System.Reflection;
namespace ManualDiceRoller {
internal static class NativeSiteManifest {
internal static bool BadFingerprint,UnsafeFinally,UnsafeBranch,UnsafeOutput;
static void AddFixture(Type type,string id,string nativeName="RollAbilityCheck"){var move=type.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);var parsed=(IEnumerable)typeof(NativePatches).GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{move});int offset=-1;foreach(var item in parsed){var operand=item.GetType().GetField("Operand",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(item) as MethodBase;if(operand!=null&&operand.Name==nativeName)offset=(int)item.GetType().GetField("Offset",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(item);}NativePatches.Register(new AwaitSite(type.FullName,NativePatches.Hash(move.GetMethodBody().GetILAsByteArray()),offset,id));}
static Type Fixture(string prefix){return typeof(AwaitEh).GetNestedTypes(BindingFlags.NonPublic).Single(t=>t.Name.StartsWith(prefix,StringComparison.Ordinal));}
internal static void Register(){var type=typeof(CharacterActionMoveStepJump).GetNestedType("<RollChecksIfNecessary>d__11",BindingFlags.NonPublic);var move=type.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);var hash=NativePatches.Hash(move.GetMethodBody().GetILAsByteArray());NativePatches.Register(new AwaitSite(type.FullName,BadFingerprint?new string('0',64):hash,0x0070,"Jump:Acrobatics"));NativePatches.Register(new AwaitSite(type.FullName,hash,0x012a,"Jump:Athletics"));
AddFixture(Fixture(UnsafeFinally?"<UnsafeFinally>":"<Fault>"),"EH:Roll");
AddFixture(Fixture("<ActiveFault>"),"EH:ActiveRoll");
AddFixture(Fixture(UnsafeBranch?"<BranchUnverified>":"<BranchYield>"),"Branch:Roll");
AddFixture(typeof(CharacterActionCastSpell).GetNestedType("<CounterEffectAction>d__13",BindingFlags.NonPublic),"NativeCounterspell:Roll");
AddFixture(Fixture(UnsafeOutput?"<UnsafeOutput>":"<VoidRoll>"),"Outputs:Void",UnsafeOutput?"ReadReference":"PublishValues");
var shove=typeof(CharacterActionShove).GetNestedType("<ExecuteImpl>d__4",BindingFlags.NonPublic);var shoveMove=shove.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
NativePatches.Register(new AwaitSite(shove.FullName,NativePatches.Hash(shoveMove.GetMethodBody().GetILAsByteArray()),0x00a7,"CharacterActionShove/<ExecuteImpl>d__4:ResolveRolls:00a7",true));
}
}}
