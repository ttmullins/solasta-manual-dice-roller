using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ManualDiceRoller;

// 0.8.4.0 (third live session): players could not tell why they were asked
// for a Constitution save "against Beak", or why they still took damage after
// succeeding. The window now names what is rolled against and what a success
// changes, read from the game's own records.
static class ReasonsTests
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
    static T Definition<T>(string name) where T:BaseDefinition {var d=Empty<T>();Set(d,"cachedName",name);Set(d,"guid","random-"+name);return d;}
    static RulesetCharacterHero Hero(string name)
    {var h=Empty<RulesetCharacterHero>();Set(h,"name",name);Set(h,"conditionsByCategory",new Dictionary<string,List<RulesetCondition>>());return h;}
    static EffectForm Damage(string type,RuleDefinitions.EffectSavingThrowType affinity)
    {
        var form=Empty<EffectForm>();var damage=Empty<DamageForm>();Set(damage,"damageType",type);
        Set(form,"formType",EffectForm.EffectFormType.Damage);Set(form,"damageForm",damage);Set(form,"savingThrowAffinity",affinity);return form;
    }
    static EffectForm Condition(ConditionDefinition definition,RuleDefinitions.EffectSavingThrowType affinity)
    {
        var form=Empty<EffectForm>();var condition=Empty<ConditionForm>();Set(condition,"conditionDefinition",definition);Set(condition,"operation",ConditionForm.ConditionOperation.Add);
        Set(form,"formType",EffectForm.EffectFormType.Condition);Set(form,"conditionForm",condition);Set(form,"savingThrowAffinity",affinity);return form;
    }
    static NativeInvocation Call(MethodInfo method,object receiver,params object[] named)
    {
        var parameters=method.GetParameters();var args=new object[parameters.Length+(method.IsStatic?0:1)];
        if(!method.IsStatic)args[0]=receiver;
        for(int i=0;i<named.Length;i+=2){
            int index=Array.FindIndex(parameters,p=>p.Name==(string)named[i]);
            if(index<0)throw new Exception("Actual native parameter missing: "+method.Name+"."+named[i]);
            args[index+(method.IsStatic?0:1)]=named[i+1];
        }
        return new NativeInvocation(new object(),"site",method,args,1);
    }
    static NativeRollPlan Plan(RuleDefinitions.RollContext context,string note,string hint=null)
    {var plan=new NativeRollPlan();plan.Leaves.Add(new NativeRollPlan.Leaf{Context=context,Note=note,Hint=hint});return plan;}
    static void Main()
    {
        RollReasons.Titler=null;
        var gimli=Hero("Gimli");var crow=Hero("Giant Crow");
        var blinded=Definition<ConditionDefinition>("ConditionBlinded");
        // A monster attack's save: the crow's beak hits for piercing damage and
        // the save only avoids the condition.
        var beak=Empty<RulesetAttackMode>();Set(beak,"sourceDefinition",Definition<MonsterAttackDefinition>("Attack_GiantCrow_Beak"));
        var modeSave=typeof(RulesetAttackMode).GetMethod("TryRollSavingThrow");
        var plan=Plan(RuleDefinitions.RollContext.SavingThrow,"Constitution save");
        var forms=new List<EffectForm>{Damage("DamagePiercing",RuleDefinitions.EffectSavingThrowType.None),Condition(blinded,RuleDefinitions.EffectSavingThrowType.Negates)};
        RollReasons.Explain(Call(modeSave,beak,"attacker",crow,"target",gimli,"actualEffectForms",forms),plan,0);
        Check(plan.Leaves[0].Note=="Constitution save against Giant Crow's Beak","a monster attack's save names the attacker and the attack: "+plan.Leaves[0].Note);
        Check(plan.Leaves[0].Hint=="On a success: Blinded is avoided. Piercing damage applies either way.","a save that only avoids a condition says the damage still applies: "+plan.Leaves[0].Hint);
        // A spell or trap that halves damage on a success (service call).
        var serviceSave=typeof(RulesetImplementationManager).GetMethod("TryRollSavingThrow");
        plan=Plan(RuleDefinitions.RollContext.SavingThrow,"Dexterity save");
        RollReasons.Explain(Call(serviceSave,null,"caster",null,"target",gimli,"effectForms",new List<EffectForm>{Damage("DamagePiercing",RuleDefinitions.EffectSavingThrowType.HalfDamage)},
            "sourceName","TrapSpikes","sourceDefinition",Definition<SpellDefinition>("TrapSpikes")),plan,0);
        Check(plan.Leaves[0].Note=="Dexterity save against Trap Spikes","a source with no caster is named alone: "+plan.Leaves[0].Note);
        Check(plan.Leaves[0].Hint=="On a success: half damage.","a halving save says so: "+plan.Leaves[0].Hint);
        // Negated damage and a condition the target already has (a repeat save).
        var held=Definition<ConditionDefinition>("ConditionParalyzed");
        var condition=Empty<RulesetCondition>();Set(condition,"conditionDefinition",held);
        ((Dictionary<string,List<RulesetCondition>>)typeof(RulesetActor).GetField("conditionsByCategory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(gimli)).Add("effect",new List<RulesetCondition>{condition});
        plan=Plan(RuleDefinitions.RollContext.SavingThrow,"Wisdom save");
        RollReasons.Explain(Call(serviceSave,null,"caster",crow,"target",gimli,"effectForms",new List<EffectForm>{Condition(held,RuleDefinitions.EffectSavingThrowType.Negates),Damage("DamageFire",RuleDefinitions.EffectSavingThrowType.Negates)},
            "sourceName","PowerCrowCurse"),plan,0);
        Check(plan.Leaves[0].Hint=="On a success: Paralyzed ends; no fire damage.","a held condition ends and negated damage is named: "+plan.Leaves[0].Hint);
        Check(plan.Leaves[0].Note=="Wisdom save against Giant Crow's Crow Curse","a source name without a definition is cleaned: "+plan.Leaves[0].Note);
        // The game's localized titles are used when the plugin provides them.
        RollReasons.Titler=definition=>definition.Name=="Attack_GiantCrow_Beak"?"Beak (localized)":null;
        plan=Plan(RuleDefinitions.RollContext.SavingThrow,"Constitution save");
        RollReasons.Explain(Call(modeSave,beak,"attacker",crow,"target",gimli,"actualEffectForms",forms),plan,0);
        Check(plan.Leaves[0].Note.EndsWith("Beak (localized)"),"the game's own title is preferred");
        RollReasons.Titler=null;
        // Bonus dice and other leaves keep their own text; unreadable forms keep the hint.
        plan=Plan(RuleDefinitions.RollContext.SavingThrow,"Constitution save","keep");plan.Leaves[0].Static=true;
        RollReasons.Explain(Call(modeSave,beak,"attacker",crow,"target",gimli,"actualEffectForms",forms),plan,0);
        Check(plan.Leaves[0].Note=="Constitution save"&&plan.Leaves[0].Hint=="keep","a save's bonus die is not relabelled");
        plan=Plan(RuleDefinitions.RollContext.SavingThrow,"Constitution save","keep");
        RollReasons.Explain(Call(modeSave,beak,"attacker",crow,"target",gimli,"actualEffectForms",null),plan,0);
        Check(plan.Leaves[0].Hint=="keep","without effect forms the hint is unchanged");
        // Attacks: the weapon and the target; an attack bonus note is kept.
        var axe=Empty<RulesetAttackMode>();Set(axe,"sourceDefinition",Definition<ItemDefinition>("Battleaxe"));
        var attack=typeof(RulesetCharacter).GetMethod("RollAttackMode");
        plan=Plan(RuleDefinitions.RollContext.AttackRoll,"+1d4 Bless");
        RollReasons.Explain(Call(attack,gimli,"attackMode",axe,"target",crow),plan,0);
        Check(plan.Leaves[0].Note=="Battleaxe against Giant Crow · +1d4 Bless","an attack names its weapon and target: "+plan.Leaves[0].Note);
        Check(plan.Leaves[0].Hint.Contains("armor class")&&plan.Leaves[0].Hint.Contains("natural 20"),"an attack explains what hits");
        // Text helpers.
        Check(RollReasons.Owned("Jess","Fire Bolt")=="Jess' Fire Bolt"&&RollReasons.Owned("","Fire Bolt")=="Fire Bolt"&&RollReasons.Owned("Gimli","")=="Gimli","possessives");
        Check(RollReasons.Title(Definition<MonsterAttackDefinition>("Attack_EagleMatriarch_Talons"))=="Talons","a monster attack's internal name reads as the attack");
        Console.WriteLine("PASS "+checks+" roll explanation checks over genuine pristine game records (display text only).");
    }
}
