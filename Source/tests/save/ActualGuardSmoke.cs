using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using ManualDiceRoller;

static class ActualGuardSmoke
{
    static void Main()
    {
        var target=(RulesetCharacterHero)RuntimeHelpers.GetUninitializedObject(typeof(RulesetCharacterHero));
        var manager=(RulesetImplementationManager)RuntimeHelpers.GetUninitializedObject(typeof(RulesetImplementationManager));
        var method=typeof(RulesetImplementationManager).GetMethod("TryRollSavingThrow");
        var parameters=method.GetParameters();var args=new object[parameters.Length+1];args[0]=manager;
        for(int i=0;i<parameters.Length;i++)if(parameters[i].Name=="target")args[i+1]=target;
        var call=new NativeInvocation(new object(),"ActualMetadata",method,args,1);
        foreach(var type in new[]{typeof(RulesetActor),typeof(RulesetCharacterHero),typeof(ServiceRepository),typeof(AttributeDefinitions),typeof(RulesetAttribute),typeof(RuleDefinitions),typeof(ActionModifier),typeof(BaseDefinition)})
            if(type.Assembly!=typeof(RulesetActor).Assembly)Console.WriteLine("External trusted native type: "+type.FullName+" in "+type.Assembly.GetName().Name);
        if(!NativeSavePlanner.ForeignPatchGuard(call,target,null)) throw new Exception("Unpatched actual native metadata failed the foreign patch guard.");
        Console.WriteLine("PASS foreign-patch guard accepts the actual pristine game/Unity method metadata without executing native engine calls.");
    }
}
