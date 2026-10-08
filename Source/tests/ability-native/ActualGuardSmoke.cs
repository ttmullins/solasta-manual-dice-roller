using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ManualDiceRoller;

static class ActualGuardSmoke
{
    static void Main()
    {
        var location=(GameLocationCharacter)RuntimeHelpers.GetUninitializedObject(typeof(GameLocationCharacter));
        var hero=(RulesetCharacterHero)RuntimeHelpers.GetUninitializedObject(typeof(RulesetCharacterHero));
        var method=typeof(GameLocationCharacter).GetMethods().Single(value=>value.Name=="RollAbilityCheck"&&value.GetParameters().Length==12);
        var args=new object[method.GetParameters().Length+1];args[0]=location;
        var call=new NativeInvocation(new object(),"ActualAbilityMetadata",method,args,1);
        var guard=typeof(NativeAbilityPlanner).GetMethod("ForeignPatchGuard",BindingFlags.NonPublic|BindingFlags.Static);
        if(!(bool)guard.Invoke(null,new object[]{call,location})) throw new Exception("Unpatched actual location ability metadata failed the foreign patch guard.");
        var basic=typeof(RulesetCharacter).GetMethods().Single(value=>value.Name=="RollBasicAbilityCheck"&&value.GetParameters().Length==6);
        args=new object[basic.GetParameters().Length+1];args[0]=hero;call=new NativeInvocation(new object(),"ActualBasicMetadata",basic,args,2);
        if(!(bool)guard.Invoke(null,new object[]{call,null})) throw new Exception("Unpatched actual Basic ability metadata failed the foreign patch guard.");
        var actorGuard=typeof(NativeAbilityPlanner).GetMethod("ForeignActorGuard",BindingFlags.NonPublic|BindingFlags.Static);
        if(!(bool)actorGuard.Invoke(null,new object[]{hero})) throw new Exception("Unpatched actual hero ability metadata failed the foreign actor guard.");
        Console.WriteLine("PASS: actual pristine game/Unity metadata accepts location, Basic and hero ability guards without executing Unity native calls.");
    }
}
