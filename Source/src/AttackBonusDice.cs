using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ManualDiceRoller
{
    internal static class AttackBonusDice
    {
        // ---- Attack modifier dice (Bless, Bane and similar) ----------------
        // The game rolls a combat affinity's modifier dice while it evaluates
        // the attack, before the registered await, and stores the sum in the
        // action's ActionModifier (AttackRollModifier plus a to-hit trend).
        // The player enters those dice with the attack roll; once every face
        // is known the stored sum is replaced by the entered one, before the
        // original call re-reads its arguments. The d20 itself is unaffected.
        //
        // In multiplayer a player may roll with the attack modifier another
        // player's game sent; the game's network copy of a trend keeps its
        // value, source type and source name but not its die flag, die type
        // or feature. The die is therefore found from those kept fields and
        // the attacker's own combat affinities, the same on every computer: a
        // trend whose source matches exactly one die-valued affinity of the
        // attacker, and no other to-hit trend from that source.
        internal static void Plan(NativeInvocation call,NativeRollPlan plan,RulesetCharacter attacker)
        {
            try
            {
                var trends=call.Arg("toHitTrends") as List<RuleDefinitions.TrendInfo>;
                if(trends==null||trends.Count==0||plan.Leaves.Count==0||attacker==null) return;
                var features=new List<FeatureDefinition>();
                var origins=new Dictionary<FeatureDefinition,RuleDefinitions.FeatureOrigin>();
                using(var scope=NativeScratchScope.ForActor(attacker)) attacker.EnumerateFeaturesToBrowse<ICombatAffinityProvider>(features,origins);
                var dice=new List<KeyValuePair<RuleDefinitions.FeatureOrigin,FeatureDefinitionCombatAffinity>>();
                foreach(var feature in features)
                {
                    var affinity=feature as FeatureDefinitionCombatAffinity;
                    RuleDefinitions.FeatureOrigin origin;
                    if(affinity==null||affinity.MyAttackModifierValueDetermination!=RuleDefinitions.CombatAffinityValueDetermination.Die) continue;
                    if(affinity.MyAttackModifierSign!=RuleDefinitions.AttackModifierSign.Add&&affinity.MyAttackModifierSign!=RuleDefinitions.AttackModifierSign.Substract) continue;
                    if(!origins.TryGetValue(feature,out origin)) continue;
                    dice.Add(new KeyValuePair<RuleDefinitions.FeatureOrigin,FeatureDefinitionCombatAffinity>(origin,affinity));
                }
                if(dice.Count==0) return;
                var modifier=FindModifier(call.Owner,trends);
                int rollIndex=ArgIndex(call,"rollModifier");
                if(modifier==null||rollIndex<0||!(call.Args[rollIndex] is int)||(int)call.Args[rollIndex]!=modifier.AttackRollModifier) return;
                var d20=plan.Leaves[plan.Leaves.Count-1];
                var notes=new List<string>();
                int removed=0;
                for(int t=0;t<trends.Count;t++)
                {
                    var trend=trends[t];
                    var matches=dice.Where(entry=>entry.Key.sourceType==trend.sourceType&&entry.Key.sourceName==trend.sourceName).Select(entry=>entry.Value).ToList();
                    if(matches.Count==0) continue;
                    var affinity=matches[0];
                    bool add=affinity.MyAttackModifierSign==RuleDefinitions.AttackModifierSign.Add;
                    var die=affinity.MyAttackModifierDieType;
                    int count=affinity.MyAttackModifierDiceNumber;
                    // Ambiguous sources keep the game's roll.
                    if(matches.Any(other=>other.MyAttackModifierSign!=affinity.MyAttackModifierSign||other.MyAttackModifierDieType!=die||other.MyAttackModifierDiceNumber!=count)) continue;
                    if(trends.Count(other=>other.sourceType==trend.sourceType&&other.sourceName==trend.sourceName)!=1) continue;
                    int magnitude=Math.Abs(trend.value);
                    if((add?trend.value<=0:trend.value>=0)||count<=0||count>8||magnitude<count||magnitude>count*RuntimeRolls.Max(die)) continue;
                    string source=RollText.Source(trend.sourceName);
                    int start=plan.Leaves.Count;
                    plan.AddStaticDice(attacker,die,count,title:string.IsNullOrEmpty(source)?"Attack bonus die":source,
                        note:(add?"Added to":"Subtracted from")+" the attack roll",
                        hint:"Enter the "+count+"d"+RuntimeRolls.Max(die)+" for "+(string.IsNullOrEmpty(source)?"this effect":source)+".",
                        category:RuleDefinitions.RollContext.AttackRoll);
                    if(plan.Leaves.Count==start) continue;
                    removed+=trend.value;
                    notes.Add((add?"+":"−")+count+"d"+RuntimeRolls.Max(die)+(string.IsNullOrEmpty(source)?"":" "+source));
                    int index=t, original=trend.value, group=start, entered=count, sign=add?1:-1;
                    plan.OnReady.Add(delegate {
                        var faces=plan.GroupFaces(group);
                        if(faces.Length!=entered) throw new InvalidOperationException("The attack modifier dice are incomplete.");
                        int total=faces.Sum()*sign;
                        if(index>=trends.Count||trends[index].value!=original)
                            throw new InvalidOperationException("The attack modifier changed while waiting.");
                        var item=trends[index]; item.value=total; trends[index]=item;
                        modifier.AttackRollModifier+=total-original;
                        plan.Invocation.Args[rollIndex]=(int)plan.Invocation.Args[rollIndex]+(total-original);
                        for(int i=group;i<group+entered&&i<plan.Leaves.Count;i++) plan.Leaves[i].Used=true;
                    });
                }
                if(notes.Count==0) return;
                // The d20's displayed bonus excludes the dice the player enters.
                for(int i=d20.GroupStart;i<d20.GroupStart+d20.GroupCount&&i<plan.Leaves.Count;i++)
                {
                    if(plan.Leaves[i].HasDisplayBonus) plan.Leaves[i].DisplayBonus-=removed;
                    plan.Leaves[i].Note=(string.IsNullOrEmpty(plan.Leaves[i].Note)?"":plan.Leaves[i].Note+" · ")+string.Join(", ",notes.ToArray());
                }
            }
            catch(Exception error) { Plugin.Log("Attack bonus dice keep the game's roll: "+error.Message); }
        }
        static int ArgIndex(NativeInvocation call,string name)
        {
            var parameters=call.Method.GetParameters();
            for(int i=0;i<parameters.Length;i++) if(parameters[i].Name==name) return i+(call.Method.IsStatic?0:1);
            return -1;
        }
        // The attack's ActionModifier is a hoisted local of the native iterator.
        static ActionModifier FindModifier(object owner,List<RuleDefinitions.TrendInfo> trends)
        {
            if(owner==null) return null;
            foreach(var field in owner.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                if(field.FieldType!=typeof(ActionModifier)) continue;
                var value=field.GetValue(owner) as ActionModifier;
                if(value!=null&&ReferenceEquals(value.AttacktoHitTrends,trends)) return value;
            }
            return null;
        }
        // A spell's effect proxy (Flaming Sphere, a summoned weapon, a moving
        // Moonbeam) rolls for the character controlling it.
        internal static RulesetActor Controller(RulesetActor actor)
        {
            var proxy=actor as RulesetCharacterEffectProxy;
            if(proxy==null) return actor;
            RulesetCharacter controller;
            if(RulesetEntity.TryGetEntity<RulesetCharacter>(proxy.ControllerGuid,out controller)&&controller!=null&&!(controller is RulesetCharacterEffectProxy)) return controller;
            return actor;
        }
    }
}
