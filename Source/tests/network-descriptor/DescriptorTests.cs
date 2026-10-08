using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ManualDiceRoller;

static class DescriptorTests
{
    static int checks;
    static void Check(bool valid,string message){++checks;if(!valid)throw new Exception(message);}
    static void Set(object instance,string name,object value)
    {
        for(var type=instance.GetType();type!=null;type=type.BaseType){
            var field=type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
            if(field!=null){field.SetValue(instance,value);return;}
        }
        throw new MissingFieldException(instance.GetType().FullName,name);
    }
    static T Empty<T>(){return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));}
    static RulesetAttribute Attribute(int value)
    {var a=Empty<RulesetAttribute>();Set(a,"currentValue",value);Set(a,"baseValue",value);Set(a,"upToDate",true);return a;}
    static RulesetCharacterHero Hero(ulong guid,int ability=16,bool reverse=false)
    {
        var h=Empty<RulesetCharacterHero>();Set(h,"guid",guid);Set(h,"currentHitPoints",12);Set(h,"temporaryHitPoints",3);
        var attrs=new Dictionary<string,RulesetAttribute>();
        if(reverse){attrs.Add("Strength",Attribute(ability));attrs.Add("ArmorClass",Attribute(14));}
        else{attrs.Add("ArmorClass",Attribute(14));attrs.Add("Strength",Attribute(ability));}
        Set(h,"attributes",attrs);Set(h,"conditionsByCategory",new Dictionary<string,List<RulesetCondition>>());
        Set(h,"skillProficiencies",new List<string>(reverse?new[]{"Stealth","Athletics"}:new[]{"Athletics","Stealth"}));
        Set(h,"toolTypeProficiencies",new List<string>());Set(h,"expertiseProficiencies",new List<string>());
        return h;
    }
    static ConditionDefinition Definition(string name,string guid)
    {var definition=Empty<ConditionDefinition>();Set(definition,"cachedName",name);Set(definition,"guid",guid);return definition;}
    static RulesetCondition Condition(ulong guid,ulong target,int amount=2)
    {
        var c=Empty<RulesetCondition>();Set(c,"guid",guid);Set(c,"targetGuid",target);Set(c,"sourceGuid",9UL);
        Set(c,"conditionDefinition",Definition("ConditionBless","definition-bless"));Set(c,"remainingRounds",4);Set(c,"amount",amount);
        return c;
    }
    static NativeRollPlan Plan(RulesetCharacterHero actor,long generation=1)
    {
        var method=typeof(RulesetCharacterHero).GetMethod("ComputeBaseAbilityCheckBonus",new[]{typeof(string),typeof(List<RuleDefinitions.TrendInfo>),typeof(string),typeof(bool),typeof(bool),typeof(bool)});
        if(method==null)throw new Exception("Actual native method ABI missing.");
        var call=new NativeInvocation(new object(),"AbilityCheck.Jump",method,new object[]{actor,"Strength",null,"Athletics",false,true,true},generation);
        var plan=new NativeRollPlan(call){NativeObserved=true,NativeReturn=3,NativeOutputs=new object[0]};plan.NetworkKey=NativeNetworkDescriptor.ActionKey(call,"rng-A");
        plan.NetworkSignature=NativeNetworkDescriptor.Signature(plan);return plan;
    }
    static string Key(NativeRollPlan p,string rng="rng-A"){return NativeNetworkDescriptor.ActionKey(p.Invocation,rng);}
    static string Before(NativeRollPlan p){return NativeNetworkDescriptor.Signature(p);}
    static string After(NativeRollPlan p){return NativeNetworkDescriptor.CompletedSignature(p,"rng-A");}
    public static void NativeScalar(float value){}
    // Live 0.8.0.0 failure: a wolf's attack halted a two-player room. When the
    // game cannot confirm an attack locally it rolls with the attack modifier
    // the acting player's game sent, whose trends lose their feature source,
    // die flag and die type on the wire. Both rolls are the same native roll.
    static RulesetAttackMode AttackMode(int toHit,string tag)
    {
        var mode=Empty<RulesetAttackMode>();Set(mode,"toHitBonus",toHit);Set(mode,"slotName","MainHand");Set(mode,"abilityScore","Strength");
        Set(mode,"attackTags",new List<string>{tag});Set(mode,"toHitBonusTrends",new List<RuleDefinitions.TrendInfo>());
        Set(mode,"accountedProviders",new List<ISavingThrowAffinityProvider>());Set(mode,"description",tag);Set(mode,"formsDescription",tag);
        return mode;
    }
    static NativeInvocation AttackCall(RulesetCharacterHero attacker,RulesetCharacterHero target,RulesetAttackMode mode,List<RuleDefinitions.TrendInfo> toHit,List<RuleDefinitions.TrendInfo> advantage,int modifier)
    {
        var method=typeof(RulesetCharacter).GetMethod("RollAttackMode");
        if(method==null)throw new Exception("Actual native RollAttackMode ABI missing.");
        return new NativeInvocation(new object(),"CharacterActionAttack/<ExecuteImpl>d__7:RollAttackMode:034b",method,
            new object[]{attacker,mode,false,target,null,toHit,false,advantage,false,modifier,RuleDefinitions.RollOutcome.Neutral,0,-1,true},1);
    }
    static string AttackKey(RulesetCharacterHero attacker,RulesetCharacterHero target,RulesetAttackMode mode,List<RuleDefinitions.TrendInfo> toHit,List<RuleDefinitions.TrendInfo> advantage,int modifier)
    { return NativeNetworkDescriptor.ActionKey(AttackCall(attacker,target,mode,toHit,advantage,modifier),"rng-A"); }
    // Diagnostics: the logged parts of two descriptions differ exactly where
    // the roll differs, so two players' logs pinpoint a disagreement.
    static string[] Different(string a,string b)
    {
        var left=a.Split(' ');var right=b.Split(' ');
        if(left.Length!=right.Length)return new[]{"shape"};
        return left.Where((part,index)=>part!=right[index]).Select(part=>part.Split('=')[0]).ToArray();
    }
    static void NetworkCopies()
    {
        var attacker=Hero(7);var target=Hero(8);
        var pack=Empty<FeatureDefinitionCombatAffinity>();Set(pack,"cachedName","PackTactics");Set(pack,"guid","pack-guid");
        var bless=new RuleDefinitions.TrendInfo(3,RuleDefinitions.FeatureSourceType.Condition,"ConditionBlessed",null){dieFlag=RuleDefinitions.TrendInfoDieFlag.AddDie,dieType=RuleDefinitions.DieType.D4};
        var local=new List<RuleDefinitions.TrendInfo>{bless,new RuleDefinitions.TrendInfo(2,RuleDefinitions.FeatureSourceType.Proficiency,"Proficiency",null)};
        var localAdvantage=new List<RuleDefinitions.TrendInfo>{new RuleDefinitions.TrendInfo(1,RuleDefinitions.FeatureSourceType.CharacterFeature,"PackTactics",pack),
            new RuleDefinitions.TrendInfo(-1,RuleDefinitions.FeatureSourceType.Proximity,"ProximityLongRange",attacker),
            new RuleDefinitions.TrendInfo(1,RuleDefinitions.FeatureSourceType.Condition,"ConditionProne",Condition(300,8))};
        // The receiving game's copy, as TrendInfo's network serializer leaves it.
        Func<RuleDefinitions.TrendInfo,RuleDefinitions.TrendInfo> wire=t=>{var copy=new RuleDefinitions.TrendInfo(t.value,t.sourceType,t.sourceName,t.source is RulesetCharacter?t.source:null);
            copy.additive=t.additive;copy.additionalDetails=t.additionalDetails??"";copy.dieFlag=default(RuleDefinitions.TrendInfoDieFlag);copy.dieType=default(RuleDefinitions.DieType);return copy;};
        var received=local.Select(wire).ToList();var receivedAdvantage=localAdvantage.Select(wire).ToList();
        string sent=AttackKey(attacker,target,AttackMode(4,"Melee"),local,localAdvantage,5);
        Check(sent==AttackKey(attacker,target,AttackMode(4,"Melee"),received,receivedAdvantage,5),"an attack rolled with the network copy of the modifier agrees with the locally computed one");
        Check(sent==AttackKey(attacker,target,AttackMode(4,"Reach"),local,localAdvantage,5),"attack mode fields outside the game's network identity (tags, text, caches) are ignored");
        Check(sent!=AttackKey(attacker,target,AttackMode(5,"Melee"),local,localAdvantage,5),"a different to-hit bonus in the attack mode's network identity still differs");
        var changed=received.ToList();var b=changed[0];b.value=4;changed[0]=b;
        Check(sent!=AttackKey(attacker,target,AttackMode(4,"Melee"),changed,receivedAdvantage,5),"a different trend value still differs");
        string parts=NativeNetworkDescriptor.Parts(AttackCall(attacker,target,AttackMode(4,"Melee"),local,localAdvantage,5),"rng-A");
        Check(Different(parts,NativeNetworkDescriptor.Parts(AttackCall(attacker,target,AttackMode(4,"Melee"),changed,receivedAdvantage,5),"rng-A")).SequenceEqual(new[]{"toHitTrends"}),"logged parts name the differing trend list");
        Check(Different(parts,NativeNetworkDescriptor.Parts(AttackCall(attacker,target,AttackMode(4,"Melee"),local,localAdvantage,5),"rng-B")).SequenceEqual(new[]{"rng"}),"logged parts name a random-state difference");
        var hurt=Hero(8);Set(hurt,"currentHitPoints",5);
        Check(Different(parts,NativeNetworkDescriptor.Parts(AttackCall(attacker,hurt,AttackMode(4,"Melee"),local,localAdvantage,5),"rng-A")).SequenceEqual(new[]{"target.hp"}),"logged parts name the differing actor section");
        var renamed=receivedAdvantage.ToList();var r=renamed[0];r.sourceName="Other";renamed[0]=r;
        Check(sent!=AttackKey(attacker,target,AttackMode(4,"Melee"),received,renamed,5),"a different advantage source name still differs");
        Check(sent!=AttackKey(attacker,target,AttackMode(4,"Melee"),received,receivedAdvantage,6),"a different roll modifier still differs");
        var otherCondition=localAdvantage.ToList();var c=otherCondition[2];c.source=Condition(301,8);otherCondition[2]=c;
        Check(AttackKey(attacker,target,AttackMode(4,"Melee"),local,otherCondition,5)==sent,"a condition or feature source is not sent, so it is not compared");
        var characterSource=new List<RuleDefinitions.TrendInfo>{new RuleDefinitions.TrendInfo(1,RuleDefinitions.FeatureSourceType.CharacterFeature,"Help",attacker)};
        var otherHelper=new List<RuleDefinitions.TrendInfo>{new RuleDefinitions.TrendInfo(1,RuleDefinitions.FeatureSourceType.CharacterFeature,"Help",target)};
        Check(AttackKey(attacker,target,AttackMode(4,"Melee"),received,characterSource,5)!=AttackKey(attacker,target,AttackMode(4,"Melee"),received,otherHelper,5),"a ruleset character source is sent and compared by its GUID");
        // Action parameters: local hover position, float parameter, staging flag and failure text are not sent.
        var parameters=Empty<CharacterActionParams>();Set(parameters,"targetCharacters",new List<GameLocationCharacter>());Set(parameters,"subTargets",new List<GameLocationCharacter>());
        Set(parameters,"positions",new List<TA.int3>());Set(parameters,"actionModifiers",new List<ActionModifier>{new ActionModifier()});Set(parameters,"stringParameter","");Set(parameters,"stringParameter2","");
        Set(parameters,"intParameter",3);
        var paramsMethod=typeof(DescriptorTests).GetMethod("NativeParams");
        Func<string> paramsKey=()=>NativeNetworkDescriptor.ActionKey(new NativeInvocation(new object(),"params",paramsMethod,new object[]{parameters},1),"rng-A");
        string before=paramsKey();
        Set(parameters,"cursorHoveredPosition",new UnityEngine.Vector3(1.1f,2.2f,3.3f));Set(parameters,"floatParameter",0.30000001f);Set(parameters,"stagedForExecution",true);
        parameters.ActionModifiers[0].FailureFlags.Add("Failure/&FailureFlagNoPerceptionOfTargetDescription");
        Check(paramsKey()==before,"local-only action parameter state and failure text do not split the descriptor");
        Set(parameters,"intParameter",4);Check(paramsKey()!=before,"a serialized action parameter still differs");
        Set(parameters,"intParameter",3);parameters.ActionModifiers[0].AttackRollModifier=2;Check(paramsKey()!=before,"a sent attack modifier value still differs");
    }
    public static void NativeParams(CharacterActionParams parameters){}
    public static void NativeForms(List<EffectForm> actualEffectForms){}
    // 0.8.4.2 (fifth live session): the same Giant Crow talon attack and a
    // spell's save differed only in their effect forms. The game copies
    // pooled damage forms without their tooltip bonus breakdown, so a reused
    // form keeps an earlier local breakdown. It never changes a result.
    static EffectForm DamageEffect(int bonus,params RuleDefinitions.TrendInfo[] breakdown)
    {
        var damage=Empty<DamageForm>();Set(damage,"damageType","DamagePiercing");Set(damage,"diceNumber",2);Set(damage,"dieType",RuleDefinitions.DieType.D6);Set(damage,"bonusDamage",bonus);
        Set(damage,"damageBonusTrends",new List<RuleDefinitions.TrendInfo>(breakdown));
        var form=Empty<EffectForm>();Set(form,"formType",EffectForm.EffectFormType.Damage);Set(form,"damageForm",damage);Set(form,"diceByLevelTable",new List<DiceByRank>());
        return form;
    }
    static void PooledForms()
    {
        var method=typeof(DescriptorTests).GetMethod("NativeForms");
        Func<EffectForm,string> key=form=>NativeNetworkDescriptor.ActionKey(new NativeInvocation(new object(),"forms",method,new object[]{new List<EffectForm>{form}},1),"rng-A");
        Func<EffectForm,string> parts=form=>NativeNetworkDescriptor.Parts(new NativeInvocation(new object(),"forms",method,new object[]{new List<EffectForm>{form}},1),"rng-A");
        var host=DamageEffect(3);var client=DamageEffect(3,new RuleDefinitions.TrendInfo(2,RuleDefinitions.FeatureSourceType.Equipment,"Stale tooltip",null));
        Check(key(host)==key(client)&&parts(host)==parts(client),"a pooled damage form's leftover tooltip breakdown is not compared");
        Check(Different(parts(host),parts(DamageEffect(4))).SequenceEqual(new[]{"actualEffectForms"}),"a different damage bonus still differs and is named");
    }
    // 0.8.2.0 (second live session): the two games held different attack
    // modes for the same monster attack (attackMode and attackMethod parts
    // differed; random state, characters and modifiers were identical).
    // Players now agree on the roll's identity and dice only; each game
    // rolls with its own state, and results are compared afterwards.
    static void Agreement()
    {
        PooledForms();
        var attacker=Hero(7);var target=Hero(8);
        var trends=new List<RuleDefinitions.TrendInfo>();var advantage=new List<RuleDefinitions.TrendInfo>();
        var bite=AttackMode(4,"Melee");var claw=AttackMode(6,"Melee");Set(claw,"slotName","OffHand");
        var hostCall=AttackCall(attacker,target,bite,trends,advantage,5);var clientCall=AttackCall(attacker,target,claw,trends,advantage,5);
        Check(NativeNetworkDescriptor.ActionKey(hostCall,"rng-A")!=NativeNetworkDescriptor.ActionKey(clientCall,"rng-A"),"the logged state still shows the different attack modes");
        Check(NativeNetworkDescriptor.Identity(hostCall)==NativeNetworkDescriptor.Identity(clientCall),"the same attacker and target make the same shared roll identity, whatever each game's attack mode");
        // 0.8.4.0 (third live session): both games used "Attack_Wolf_Bite",
        // but a custom campaign creates its monsters' definitions with a new
        // random GUID on each computer. Definitions are compared by name.
        var hostDefinition=Empty<MonsterAttackDefinition>();Set(hostDefinition,"cachedName","Attack_Wolf_Bite");Set(hostDefinition,"guid","host-random-guid");
        var clientDefinition=Empty<MonsterAttackDefinition>();Set(clientDefinition,"cachedName","Attack_Wolf_Bite");Set(clientDefinition,"guid","client-random-guid");
        var hostBite=AttackMode(4,"Melee");Set(hostBite,"sourceDefinition",hostDefinition);
        var clientBite=AttackMode(4,"Melee");Set(clientBite,"sourceDefinition",clientDefinition);
        var hostWolf=AttackCall(attacker,target,hostBite,trends,advantage,5);var clientWolf=AttackCall(attacker,target,clientBite,trends,advantage,5);
        Check(NativeNetworkDescriptor.ActionKey(hostWolf,"rng-A")==NativeNetworkDescriptor.ActionKey(clientWolf,"rng-A"),"the same named attack with per-computer GUIDs is the same roll");
        Check(NativeNetworkDescriptor.Parts(hostWolf,"rng-A")==NativeNetworkDescriptor.Parts(clientWolf,"rng-A"),"no logged part differs for a per-computer GUID");
        Set(clientDefinition,"cachedName","Attack_Wolf_Claw");
        Check(Different(NativeNetworkDescriptor.Parts(hostWolf,"rng-A"),NativeNetworkDescriptor.Parts(clientWolf,"rng-A")).SequenceEqual(new[]{"attackMode"}),"a differently named attack still differs and is named in the log");
        Check(NativeNetworkDescriptor.Identity(hostCall)==NativeNetworkDescriptor.Identity(AttackCall(Hero(7,ability:18),Hero(8,reverse:true),claw,trends,advantage,9)),"each game's own state (attributes, modifiers) is not part of the identity");
        Check(NativeNetworkDescriptor.Identity(hostCall)!=NativeNetworkDescriptor.Identity(AttackCall(attacker,Hero(9),bite,trends,advantage,5)),"a different target is a different roll");
        Check(NativeNetworkDescriptor.Identity(hostCall)!=NativeNetworkDescriptor.Identity(AttackCall(Hero(10),target,bite,trends,advantage,5)),"a different attacker is a different roll");
        Check(NativeNetworkDescriptor.Identity(hostCall).StartsWith("CharacterActionAttack/<ExecuteImpl>d__7:RollAttackMode:034b/",StringComparison.Ordinal),"the identity names its site");
        var hostPlan=new NativeRollPlan(hostCall);var clientPlan=new NativeRollPlan(clientCall);
        hostPlan.Leaves.Add(new NativeRollPlan.Leaf{DisplayActor=attacker,Die=RuleDefinitions.DieType.D20,Context=RuleDefinitions.RollContext.AttackRoll,GroupCount=1});
        clientPlan.Leaves.Add(new NativeRollPlan.Leaf{DisplayActor=attacker,Die=RuleDefinitions.DieType.D20,Context=RuleDefinitions.RollContext.AttackRoll,GroupCount=1});
        Check(NativeNetworkDescriptor.Signature(hostPlan)==NativeNetworkDescriptor.Signature(clientPlan),"the same dice for the same roll agree, whatever each game's state");
        clientPlan.Leaves[0].Advantage=RuleDefinitions.AdvantageType.Advantage;
        Check(NativeNetworkDescriptor.Signature(hostPlan)!=NativeNetworkDescriptor.Signature(clientPlan),"different dice (advantage on one computer) do not agree: the game rolls");
        clientPlan.Leaves[0].Advantage=RuleDefinitions.AdvantageType.None;clientPlan.Leaves[0].DisplayActor=target;
        Check(NativeNetworkDescriptor.Signature(hostPlan)!=NativeNetworkDescriptor.Signature(clientPlan),"a die for another character does not agree");
        hostPlan.NativeObserved=clientPlan.NativeObserved=true;hostPlan.NativeOutputs=clientPlan.NativeOutputs=new object[0];hostPlan.NativeReturn=12;clientPlan.NativeReturn=15;
        hostPlan.NetworkKey=clientPlan.NetworkKey=NativeNetworkDescriptor.Identity(hostCall);
        Check(NativeNetworkDescriptor.CompletedSignature(hostPlan,"rng-A")!=NativeNetworkDescriptor.CompletedSignature(clientPlan,"rng-A"),"different results are still detected afterwards (reported, not fatal)");
        Check(NativeNetworkDescriptor.CompletedParts(hostPlan,"rng-A").Contains("return=")&&NativeNetworkDescriptor.Fallback("x",1)!=NativeNetworkDescriptor.Fallback("x",2),"result parts and stand-ins are described");
    }
    static void Main()
    {
        var first=Hero(42);var replica=Hero(42,reverse:true);var one=Plan(first);var two=Plan(replica,99999);
        Check(Key(one)==Key(two),"same native records ignore CLR identity, local generation and dictionary/proficiency insertion order");
        Check(Before(one)==Before(two),"equivalent cached native actor state yields identical canonical plan");
        Check(Key(one)!=Key(two,"rng-B"),"shared RNG divergence changes the logged state key");
        Check(Key(one)!=Key(Plan(Hero(43))),"a different native actor changes the logged state key");
        Check(Key(one)!=Key(Plan(Hero(42,ability:18))),"a different native ability bonus changes the logged state key");
        var returned=After(one);one.NativeReturn=4;Check(After(one)!=returned,"different actual native returns detected with identical actor and RNG state");one.NativeReturn=3;
        one.NativeOutputs=new object[]{RuleDefinitions.RollOutcome.Success,2};var output=After(one);
        one.NativeOutputs=new object[]{RuleDefinitions.RollOutcome.Success,3};Check(After(one)!=output,"different actual native success delta detected despite identical actor and RNG state");
        one.NativeOutputs=new object[]{RuleDefinitions.RollOutcome.Failure,2};Check(After(one)!=output,"different actual native roll outcome detected despite identical actor and RNG state");one.NativeOutputs=new object[0];
        one.NativeObserved=false;bool unobserved=false;try{After(one);}catch(InvalidOperationException){unobserved=true;}Check(unobserved,"native completion cannot certify an unobserved return/output path");one.NativeObserved=true;
        var before=After(one);Set(first,"currentHitPoints",11);Check(After(one)!=before,"post-native health differences are detected even for automatic rolls");Set(first,"currentHitPoints",12);
        before=After(one);Set(first,"temporaryHitPoints",4);Check(After(one)!=before,"post-native temporary health differences detected");Set(first,"temporaryHitPoints",3);
        before=After(one);Set(first,"deathSaveFailures",1);Check(After(one)!=before,"post-native death saving throw counts detected");Set(first,"deathSaveFailures",0);
        before=After(one);Set(first,"usedBardicInspiration",1);Check(After(one)!=before,"post-native Bardic inspiration usage detected");Set(first,"usedBardicInspiration",0);
        before=After(one);Set(first.Attributes["Strength"],"currentValue",17);Check(After(one)!=before,"post-native cached ability differences detected");Set(first.Attributes["Strength"],"currentValue",16);
        first.ConditionsByCategory.Add("effect",new List<RulesetCondition>{Condition(100,42)});
        replica.ConditionsByCategory.Add("effect",new List<RulesetCondition>{Condition(100,42)});
        Check(Key(one)==Key(two),"same native conditions with different CLR objects agree");
        before=After(one);Set(first.ConditionsByCategory["effect"][0],"amount",3);Check(After(one)!=before,"native condition amount changes detected");Set(first.ConditionsByCategory["effect"][0],"amount",2);
        before=After(one);Set(first.ConditionsByCategory["effect"][0],"bardicInspirationRoll",4);Check(After(one)!=before,"stored negative Bardic die differs");Set(first.ConditionsByCategory["effect"][0],"bardicInspirationRoll",0);
        // 0.8.4.0 (third live session): a custom campaign gives its own monsters,
        // items and conditions a new random GUID on every computer; the game
        // identifies definitions between players by name, and so does the mod.
        before=After(one);Set(first.ConditionsByCategory["effect"][0].ConditionDefinition,"guid","different-definition-guid");Check(After(one)==before,"a definition's per-computer GUID is not compared");Set(first.ConditionsByCategory["effect"][0].ConditionDefinition,"guid","definition-bless");
        var attrsEnumerator=first.Attributes.GetEnumerator();attrsEnumerator.MoveNext();var conditionsEnumerator=first.ConditionsByCategory["effect"].GetEnumerator();conditionsEnumerator.MoveNext();
        var condition=first.ConditionsByCategory["effect"][0];var conditionEvent=condition.DurationChanged;
        Key(one);Before(one);After(one);
        Check(attrsEnumerator.MoveNext(),"wire snapshots do not invalidate live native attribute enumerators");
        Check(!conditionsEnumerator.MoveNext(),"wire snapshots do not invalidate native condition list enumerators");
        Check(condition.DurationChanged==conditionEvent,"wire snapshots never mutate native notifications");
        var leaf=new NativeRollPlan.Leaf{DisplayActor=first,Die=RuleDefinitions.DieType.D20,GroupCount=1};one.Leaves.Add(leaf);
        Check(Key(one)==Key(two),"prepared leaves do not change the logged state key");
        Check(Before(one)!=Before(two),"prepared manual leaf mismatch remains a separate capability signature");
        one.NetworkKey=Key(one);two.NetworkKey=Key(two);
        Check(After(one)==After(two),"automatic completion ignores unsupported local prepared leaf shape");
        one.NetworkNativeManual=true;two.NetworkNativeManual=true;leaf.Faces.Add(8);leaf.Used=true;leaf.NextDraw=1;
        Check(After(one)!=After(two),"manual completion detects different consumed leaf/faces");
        first.SkillProficiencies.Add("Arcana");Check(Key(one)!=Key(two),"native skill proficiency differences change the logged state key");first.SkillProficiencies.Remove("Arcana");
        var oversized=Hero(42);for(int i=0;i<513;i++)oversized.Attributes["x"+i]=Attribute(i);
        bool rejected=false;try{Key(Plan(oversized));}catch(InvalidOperationException){rejected=true;}Check(rejected,"native actor descriptor allocation and traversal are bounded");
        var scalar=typeof(DescriptorTests).GetMethod("NativeScalar");
        var scalar1=new NativeInvocation(new object(),"scalar",scalar,new object[]{1.0000001f},1);
        var scalar2=new NativeInvocation(new object(),"scalar",scalar,new object[]{1.0000002f},2);
        Check(NativeNetworkDescriptor.ActionKey(scalar1,"rng-A")!=NativeNetworkDescriptor.ActionKey(scalar2,"rng-A"),"adjacent native floats use exact bits instead of Mono rounded text");
        object nested=new object[0];for(int i=0;i<13;++i)nested=new[]{nested};
        var nestedCall=new NativeInvocation(new object(),"bounded",scalar,new[]{nested},1);
        rejected=false;try{NativeNetworkDescriptor.ActionKey(nestedCall,"rng-A");}catch(InvalidOperationException){rejected=true;}Check(rejected,"excessive nesting is rejected rather than silently erased from consensus");
        NetworkCopies();
        Agreement();
        Console.WriteLine("PASS "+checks+" production native network descriptor checks over genuine pristine game actor/attribute/condition getters.");
        Console.WriteLine("Scope: original cached native records on CoreCLR; no gameplay execution, Unity renderer, native RNG, or live Photon qualification.");
    }
}
