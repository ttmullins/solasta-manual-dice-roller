using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ManualDiceRoller;

static class ActualGuardSmoke
{
    static void Main()
    {
        var hero=(RulesetCharacterHero)RuntimeHelpers.GetUninitializedObject(typeof(RulesetCharacterHero));
        var location=(GameLocationCharacter)RuntimeHelpers.GetUninitializedObject(typeof(GameLocationCharacter));
        var native=AccessTools.Method(typeof(CharacterActionShove),"ResolveRolls");
        var call=new NativeInvocation(new object(),"ActualShoveMetadata",native,new object[]{location,location,ActionDefinitions.Id.Shove},1);
        if(!NativeAbilityPlanner.ForeignPatchGuard(call,location)||!NativeAbilityPlanner.ForeignActorGuard(hero))
            throw new Exception("Unpatched actual Shove and actor metadata failed the native preview guards.");
        foreach(var method in new[]{native,AccessTools.Method(typeof(RulesetCharacter),"ResolveContestCheck"),AccessTools.Method(typeof(RulesetInventory),"IsMatchingEquipementCondition"),AccessTools.PropertyGetter(typeof(ActionModifier),"AbilityCheckAdvantageTrend")})
            if(!NativeSavePlanner.NativeMethod(method)) throw new Exception("Actual Shove dependency failed its native method guard: "+method);
        var feature=(FeatureDefinitionActionAffinity)RuntimeHelpers.GetUninitializedObject(typeof(FeatureDefinitionActionAffinity));
        if(!NativeSavePlanner.NativeInterfaceMethods(new[]{feature},typeof(IActionPerformanceProvider)))
            throw new Exception("Unpatched native action-performance provider failed its interface method guard.");
        foreach(var method in NativeSavePlanner.Named(typeof(RulesetCharacterHero),"EnumerateFeaturesToBrowse"))
            if(method.IsGenericMethodDefinition&&!NativeSavePlanner.NativeMethod(method.MakeGenericMethod(typeof(IActionPerformanceProvider))))
                throw new Exception("Actual closed native action-performance enumeration failed its guard.");
        Console.WriteLine("PASS actual pristine game/Unity metadata: Shove, contest, equipment, advantage, hero and action-performance guards; no Unity native calls executed.");
    }
}
