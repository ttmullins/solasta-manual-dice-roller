using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
class Inventory {
 static IEnumerable<Mono.Cecil.TypeDefinition> Walk(Mono.Cecil.TypeDefinition t) { yield return t; foreach(var n in t.NestedTypes) foreach(var x in Walk(n)) yield return x; }
 static string Hash(byte[] bytes) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
 static bool Roll(MethodReference r) {return r.DeclaringType.FullName=="RulesetActor" && new[]{"RollDie","RollDiceAndSum","RollDamage","RollSavingThrow"}.Contains(r.Name)||r.DeclaringType.FullName=="RuleDefinitions" && new[]{"RollDie","RollStaticDiceAndSum","RollStaticDamage","RollDiceAndSumBest"}.Contains(r.Name)||r.DeclaringType.FullName=="TA.DeterministicRandom"||r.DeclaringType.FullName=="UnityEngine.Random";}
 static void Main(string[] args) {
  using(var stream=File.OpenRead(args[0])) using(var pe=new PEReader(stream)) using(var module=Mono.Cecil.ModuleDefinition.ReadModule(args[0])) {
  var methods=module.Types.SelectMany(Walk).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
  var defs=methods.ToDictionary(m=>m.FullName,m=>m);
  var dice=new HashSet<string>(methods.Where(m=>m.Body.Instructions.Any(i=>i.Operand is MethodReference r && Roll(r))).Select(m=>m.FullName));
  var closure=new HashSet<string>(dice);bool changed;
  do {changed=false;foreach(var m in methods)if(!closure.Contains(m.FullName)&&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&closure.Contains(r.FullName)))changed|=closure.Add(m.FullName);}while(changed);
  var coroutines=methods.Where(m=>m.Name=="MoveNext"&&m.DeclaringType.Fields.Any(f=>f.Name=="<>1__state"));
  Console.WriteLine("# Direct dice/RNG methods: "+dice.Count+"; transitive native callers: "+closure.Count);
  foreach(var method in coroutines) {
   var calls=method.Body.Instructions.Where(i=>i.Operand is MethodReference r&&(closure.Contains(r.FullName)||new[]{"TryRollSavingThrow","ApplyEffectForms","ApplyMagicEffect","ResolveRolls","ApplyTargetFiltering","ProcessContenderInitiative","StartState","ExecuteSaveOnCharacter","TriggerBattleStart","SustainFallDamage","SpendMagicEffectUses"}.Contains(r.Name))).ToArray();
   if(calls.Length==0)continue;
   var handle=System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID);
   var md=pe.GetMetadataReader().GetMethodDefinition(handle);var raw=pe.GetMethodBody(md.RelativeVirtualAddress).GetILBytes();
   Console.WriteLine("OWNER\t"+method.DeclaringType.FullName+"\t"+Hash(raw));
   foreach(var i in calls) Console.WriteLine("CALL\tIL_"+i.Offset.ToString("x4")+"\t"+((MethodReference)i.Operand).FullName);
  }
  foreach(var method in methods.Where(m=>(m.Name=="RollBardicInspirationDie"||m.Name=="ResolveContestCheck") && m.DeclaringType.FullName=="RulesetCharacter" || m.Name=="ResolveRolls" && m.DeclaringType.FullName=="CharacterActionShove")) { var handle=System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID);var md=pe.GetMetadataReader().GetMethodDefinition(handle);Console.WriteLine("HASH\t"+method.FullName+"\t"+Hash(pe.GetMethodBody(md.RelativeVirtualAddress).GetILBytes())); }
  Console.WriteLine("# All direct/indirect rules-related callers");
  foreach(var method in methods.Where(m=>closure.Contains(m.FullName)))foreach(var i in method.Body.Instructions)
   if(i.Operand is MethodReference r&&(Roll(r)||closure.Contains(r.FullName))) Console.WriteLine("EDGE\t"+method.FullName+"\tIL_"+i.Offset.ToString("x4")+"\t"+r.FullName);
  if(args.Length>1)foreach(var name in args.Skip(1))foreach(var m in methods.Where(m=>m.DeclaringType.FullName==name||m.DeclaringType.FullName.StartsWith(name+"/"))) {
   Console.WriteLine("BODY\t"+m.FullName);foreach(var i in m.Body.Instructions)Console.WriteLine(i);foreach(var e in m.Body.ExceptionHandlers)Console.WriteLine("EH "+e.HandlerType+" "+e.TryStart+".."+e.TryEnd+" / "+e.HandlerStart+".."+e.HandlerEnd);
  }
 } }
}
