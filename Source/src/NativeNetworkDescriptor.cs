using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ManualDiceRoller
{
    // Wire identities deliberately exclude CLR references, local callback IDs,
    // display/localization strings and the broker's process-local generations.
    internal static class NativeNetworkDescriptor
    {
        static readonly BindingFlags Fields=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
        // Since 0.8.2.0 the players agree on the roll's identity and on the
        // dice to enter, not on each game's state. Live sessions showed the
        // two games can hold different native data for the same roll (for
        // example which of a monster's attacks the action carries); that is
        // the game's own synchronization, and each game rolls with its own
        // state. The full state (ActionKey, CompletedSignature) is still
        // described for diagnostics and for comparing results afterwards.
        //
        // Identity: the call site, the native method and the characters
        // taking part (receiver and character arguments), by GUID.
        internal static string Identity(NativeInvocation invocation)
        {
            var value=new StringBuilder("MDR native identity/1;");
            Put(value,invocation.Site); Put(value,invocation.Method.DeclaringType.FullName); Put(value,invocation.Method.Name);
            var parameters=invocation.Method.GetParameters();
            for(int i=0;i<invocation.Args.Length;i++) {
                int pi=i-(invocation.Method.IsStatic?0:1);
                if(pi>=0&&parameters[pi].ParameterType.IsByRef) continue;
                string id=Participant(invocation.Args[i],0);
                if(id==null) continue;
                Put(value,pi<0?"receiver":parameters[pi].Name); Put(value,id);
            }
            return invocation.Site+"/"+Hash(value);
        }
        static string Participant(object item,int depth)
        {
            if(item==null||depth>2) return null;
            var actor=item as RulesetActor;
            if(actor!=null) return Actor(actor);
            var type=item.GetType();
            if(Inherits(type,"GameLocationCharacter")) return Participant(ReadProperty(item,"RulesetActor")??ReadProperty(item,"RulesetCharacter"),depth+1);
            if(Inherits(type,"CharacterAction")) return Participant(ReadProperty(item,"ActingCharacter"),depth+1);
            return null;
        }
        // The full native state of the call and the shared random state:
        // diagnostics only (logged with its parts at every shared roll).
        internal static string ActionKey(NativeInvocation invocation,string randomState)
        {
            var value=new StringBuilder("MDR native invocation/2;");
            Put(value,randomState); WriteInvocation(value,invocation);
            return invocation.Site+"/"+Hash(value);
        }
        // What every player must agree on before anyone enters a die: the
        // roll's identity and the exact dice (owner, die, count, advantage,
        // order, groups and reroll features). Any difference: the game rolls.
        internal static string Signature(NativeRollPlan plan)
        {
            var value=new StringBuilder("MDR native plan/2;");
            Put(value,Identity(plan.Invocation));
            foreach(var leaf in plan.Leaves) {
                Put(value,leaf.Ordinal); Put(value,Actor(leaf.DisplayActor)); Put(value,(int)leaf.Die); Put(value,(int)leaf.Context);
                Put(value,(int)leaf.RequestCategory); Put(value,(int)leaf.Advantage); Put(value,leaf.Proficient); Put(value,leaf.CanReroll);
                Put(value,leaf.Static); Put(value,leaf.Enumerate); Put(value,leaf.Skill); Put(value,leaf.Role);
                Put(value,leaf.GroupStart); Put(value,leaf.GroupCount);
                Put(value,leaf.Form==null?"":leaf.Form.GetType().FullName);
                foreach(var feature in leaf.Features) {
                    Put(value,feature.Name); Put(value,(int)feature.ValidityContext); Put(value,feature.RequireProficiency);
                    Put(value,feature.MinRollValue); Put(value,feature.MaxRollValue); Put(value,feature.MinRerollValue);
                    Put(value,feature.RerollCount); Put(value,(int)feature.ForcedDamageOnDice);
                    foreach(var skill in feature.ValidSkills) Put(value,skill);
                    Put(value,"end feature");
                }
                Put(value,"end leaf");
            }
            return Hash(value);
        }
        internal static string PromptSignature(NativeRollPlan plan,NativeRollPlan.Leaf leaf,FaceIdentity[] identities)
        {
            var value=new StringBuilder("MDR prompt/1;"); Put(value,plan.NetworkSignature);
            foreach(var face in identities) {
                Put(value,face.DieIndex); Put(value,face.FaceIndex); Put(value,face.FeatureIndex); Put(value,face.RerollAttempt);
            }
            Put(value,leaf.Ordinal); Put(value,Actor(leaf.DisplayActor)); Put(value,(int)leaf.Die);
            return Hash(value);
        }
        // The game's result after the roll (return value, outputs, the
        // characters' state, the random state and the entered dice used).
        // Players compare it; a difference is reported, never fatal.
        internal static string CompletedSignature(NativeRollPlan plan,string randomState)
        {
            var value=new StringBuilder("MDR completed/2;"); Put(value,plan.NetworkKey); Put(value,plan.NetworkNativeManual); Put(value,randomState);
            // Inspect original native references after execution even when the
            // planner chose automatic. Prepared leaves may legitimately differ
            // in that case; the actual replicated actors and effects may not.
            WriteInvocation(value,plan.Invocation);
            if(!plan.NativeObserved)throw new InvalidOperationException("The replicated native return and output values were not observed.");
            Put(value,"native return");
            Write(value,plan.NativeReturn,0,new HashSet<object>(ReferenceComparer.Instance));
            Put(value,"native outputs");
            Write(value,plan.NativeOutputs,0,new HashSet<object>(ReferenceComparer.Instance));
            if(!plan.NetworkNativeManual) return Hash(value);
            Put(value,plan.NetworkSignature);
            foreach(var leaf in plan.Leaves) {
                Put(value,leaf.Ordinal); Put(value,leaf.Used); Put(value,leaf.NextDraw);
                foreach(int face in leaf.Faces) Put(value,face);
            }
            return Hash(value);
        }
        static void WriteInvocation(StringBuilder value,NativeInvocation invocation)
        {
            Put(value,invocation.Site); Put(value,invocation.Method.DeclaringType.FullName); Put(value,invocation.Method.Name);
            var parameters=invocation.Method.GetParameters();
            for(int i=0;i<invocation.Args.Length;i++) {
                int pi=i-(invocation.Method.IsStatic?0:1);
                if(pi>=0&&parameters[pi].ParameterType.IsByRef) continue;
                if(pi>=0&&typeof(Delegate).IsAssignableFrom(parameters[pi].ParameterType)) continue;
                Put(value,pi<0?"receiver":parameters[pi].Name);
                Write(value,invocation.Args[i],0,new HashSet<object>(ReferenceComparer.Instance));
            }
        }
        static string Actor(RulesetActor actor)
        {
            if(actor==null) return "none";
            object guid=ReadProperty(actor,"Guid");
            if(guid==null) throw new InvalidOperationException("A networked actor has no native GUID.");
            return actor.GetType().FullName+"/"+Convert.ToString(guid,CultureInfo.InvariantCulture);
        }
        static object ReadProperty(object value,string name)
        {
            var property=value.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            return property==null||property.GetIndexParameters().Length!=0?null:property.GetValue(value,null);
        }
        static bool Inherits(Type type,string name)
        { for(var current=type;current!=null;current=current.BaseType) if(current.Name==name) return true; return false; }
        static void Write(StringBuilder value,object item,int depth,HashSet<object> path)
        {
            if(value.Length>65536) throw new InvalidOperationException("The native multiplayer descriptor exceeds its safe bound.");
            if(item==null) {Put(value,"null");return;}
            Type type=item.GetType(); Put(value,type.FullName);
            // Mono's default float formatting can round adjacent values to the
            // same text. Native parameter equality requires their exact bits.
            if(item is float) {Put(value,BitConverter.ToString(BitConverter.GetBytes((float)item)));return;}
            if(item is double) {Put(value,BitConverter.ToString(BitConverter.GetBytes((double)item)));return;}
            if(type.IsPrimitive||type.IsEnum||item is string||item is decimal) {Put(value,Convert.ToString(item,CultureInfo.InvariantCulture));return;}
            if(item is Delegate) {Put(value,"callback");return;}
            // The game sends a trend over the network without its feature
            // source or die flags; a peer may roll with that received copy.
            if(item is RuleDefinitions.TrendInfo) {WriteTrend(value,(RuleDefinitions.TrendInfo)item);return;}
            var actor=item as RulesetActor;
            if(actor!=null) {WriteActor(value,actor);return;}
            if(Inherits(type,"GameLocationCharacter")) {Write(value,ReadProperty(item,"RulesetActor")??ReadProperty(item,"RulesetCharacter"),depth+1,path);return;}
            if(Inherits(type,"RulesetEntity")) {Put(value,ReadProperty(item,"Guid"));return;}
            // A definition is identified by its name, as the game sends it
            // between players. A custom campaign's own monsters, items and
            // features get a new random GUID on every computer at load time.
            if(Inherits(type,"BaseDefinition")) {Put(value,ReadProperty(item,"Name"));return;}
            if(Inherits(type,"CharacterAction")) {
                Write(value,ReadProperty(item,"ActingCharacter"),depth+1,path);
                Write(value,ReadProperty(item,"ActionDefinition"),depth+1,path);
                Write(value,ReadProperty(item,"ActionParams"),depth+1,path); return;
            }
            if(depth>=12)throw new InvalidOperationException("The native multiplayer descriptor exceeds its safe nesting bound.");
            if(!type.IsValueType&&!path.Add(item)) {Put(value,"cycle");return;}
            try {
                var list=item as IList;
                if(list!=null) {
                    if(list.Count>1024) throw new InvalidOperationException("A native multiplayer list is too large.");
                    Put(value,list.Count); foreach(var element in list) Write(value,element,depth+1,path);return;
                }
                // Records the game sends between players are described by the
                // fields it actually sends: the other player's copy is rebuilt
                // from those (an attack mode by its network identity, action
                // parameters by their serialized fields). Local-only state
                // (failure text, hover position, staging flags) is excluded.
                string[] selected;
                if(NetworkFields.TryGetValue(type.Name,out selected)) {
                    foreach(var name in selected) {
                        var field=FindField(type,name);
                        Put(value,name);Write(value,field.GetValue(item),depth+1,path);
                    }
                    return;
                }
                // Only stock value/roll parameter records are inspected. Native
                // service receivers are identified by type, not their caches.
                if(!type.IsValueType && type.Name!="ActionModifier" && type.Name!="EffectDescription" && type.Name!="EffectForm" &&
                   type.Name!="DamageForm"&&type.Name!="HealingForm"&&type.Name!="TemporaryHitPointsForm") return;
                var fields=new List<FieldInfo>();
                for(var current=type;current!=null&&current!=typeof(object);current=current.BaseType) fields.AddRange(current.GetFields(Fields));
                foreach(var field in fields.OrderBy(f=>f.DeclaringType.FullName+"/"+f.Name,StringComparer.Ordinal)) {
                    if(typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                    if(type.Name=="ActionModifier"&&field.Name=="failureFlags") continue;
                    // A tooltip breakdown only (GuiEffectsFormater). Pooled
                    // damage forms are copied without it, so a reused form keeps
                    // whatever an earlier local use left there (0.8.4.2).
                    if(field.DeclaringType.Name=="DamageForm"&&field.Name=="damageBonusTrends") continue;
                    Put(value,field.DeclaringType.FullName+"/"+field.Name);Write(value,field.GetValue(item),depth+1,path);
                }
            } finally {if(!type.IsValueType)path.Remove(item);}
        }
        // RulesetAttackMode.IsComparableForNetwork: the identity by which the
        // game finds the attack mode in the receiving player's own list.
        // CharacterActionParams: the fields its network serializer writes,
        // except the hover position and float parameter (rounded on the wire).
        static readonly Dictionary<string,string[]> NetworkFields=new Dictionary<string,string[]>(StringComparer.Ordinal) {
            {"RulesetAttackMode",new[]{"actionType","sourceDefinition","sourceObject","slotName","ranged","thrown","reach","reachRange","closeRange","maxRange",
                "toHitBonus","attacksNumber","useVersatileDamage","freeOffHand","automaticHit","afterChargeOnly"}},
            {"CharacterActionParams",new[]{"actionDefinition","actingCharacter","canBeCancelled","canBeAborted","skipAnimationsAndVFX","targetItem","targetGadget",
                "targetExecutionFlow","isReactionEffect","orientation","readyActionType","breakFreeMode","boolParameter","boolParameter2","boolParameter3",
                "boolParameter4","boolParameter5","intParameter","intParameter2","hasMagneticTargeting","reactionValidated","actionTriggersBattle",
                "attackMode","spellRepertoire","usablePower","targetCharacters","subTargets","positions","activeEffect","stringParameter","stringParameter2",
                "actionModifiers","targetSubstitute"}},
        };
        static readonly Dictionary<string,FieldInfo> fieldCache=new Dictionary<string,FieldInfo>(StringComparer.Ordinal);
        static FieldInfo FindField(Type type,string name)
        {
            string key=type.FullName+"/"+name; FieldInfo field;
            if(fieldCache.TryGetValue(key,out field)) return field;
            for(var current=type;current!=null&&field==null;current=current.BaseType) field=current.GetField(name,Fields);
            if(field==null) throw new InvalidOperationException("The native "+type.Name+" record has no field "+name+".");
            fieldCache[key]=field; return field;
        }
        // TrendInfo's network serializer keeps the value, source type,
        // additive flag, source name, details and a character source only.
        static void WriteTrend(StringBuilder value,RuleDefinitions.TrendInfo trend)
        {
            Put(value,trend.value); Put(value,(int)trend.sourceType); Put(value,trend.additive);
            var character=trend.source as RulesetCharacter;
            Put(value,character==null?"0":Convert.ToString(character.Guid,CultureInfo.InvariantCulture));
            Put(value,trend.sourceName??""); Put(value,trend.additionalDetails??"");
        }
        static void WriteActor(StringBuilder value,RulesetActor actor)
        {
            WriteActorHealth(value,actor); WriteActorAttributes(value,actor); WriteActorConditions(value,actor); WriteActorProficiencies(value,actor);
        }
        static void WriteActorHealth(StringBuilder value,RulesetActor actor)
        {
            Put(value,Actor(actor)); Put(value,actor.CurrentHitPoints);
            var character=actor as RulesetCharacter;
            if(character!=null) {
                Put(value,character.TemporaryHitPoints); Put(value,character.DeathSaveSuccesses); Put(value,character.DeathSaveFailures);
                Put(value,character.UsedBardicInspiration);
            }
        }
        static void WriteActorAttributes(StringBuilder value,RulesetActor actor)
        {
            var attributes=actor.Attributes;
            if(attributes==null||attributes.Count>512)throw new InvalidOperationException("A networked actor has invalid or oversized attributes.");
            foreach(var pair in attributes.OrderBy(p=>p.Key,StringComparer.Ordinal)) {
                if(pair.Value==null)throw new InvalidOperationException("A networked actor has a missing attribute.");
                Put(value,pair.Key); Put(value,pair.Value.CurrentValue); Put(value,pair.Value.BaseValue);
            }
            Put(value,"end attributes");
        }
        static void WriteActorConditions(StringBuilder value,RulesetActor actor)
        {
            // Direct category lists avoid GetAllConditions/feature enumeration,
            // which mutate native scratch buffers and invalidate enumerators.
            var categories=actor.ConditionsByCategory;
            if(categories==null||categories.Count>128)throw new InvalidOperationException("A networked actor has invalid or oversized conditions.");
            int count=0;
            foreach(var pair in categories.OrderBy(p=>p.Key,StringComparer.Ordinal)) {
                if(pair.Value==null||pair.Value.Count>1024-count)throw new InvalidOperationException("A networked actor has invalid or oversized condition entries.");
                count+=pair.Value.Count; Put(value,pair.Key); Put(value,pair.Value.Count);
                foreach(var condition in pair.Value) {
                    if(condition==null||condition.ConditionDefinition==null)throw new InvalidOperationException("A networked actor has an invalid condition.");
                    Put(value,condition.Guid); Put(value,condition.ConditionDefinition.Name);
                    Put(value,condition.TargetGuid); Put(value,condition.SourceGuid); Put(value,condition.DurationType); Put(value,condition.DurationParameter);
                    Put(value,condition.EndOccurence); Put(value,condition.RemainingRounds); Put(value,condition.Amount);
                    Put(value,condition.SourceAbilityBonus); Put(value,condition.SourceProficiencyBonus); Put(value,condition.EffectLevel);
                    Put(value,condition.EffectDefinitionName); Put(value,condition.BardicInspirationDie); Put(value,condition.BardicInspirationRoll);
                    Put(value,condition.HasSaveOverride); Put(value,condition.SaveOverrideAbilityScoreName); Put(value,condition.SaveOverrideDC);
                }
            }
            Put(value,"end conditions");
        }
        static void WriteActorProficiencies(StringBuilder value,RulesetActor actor)
        {
            var hero=actor as RulesetCharacterHero;
            if(hero!=null) {
                WriteStringSet(value,hero.SkillProficiencies); WriteStringSet(value,hero.ToolTypeProficiencies); WriteStringSet(value,hero.ExpertiseProficiencies);
            }
        }
        // Diagnostics: short hashes of each part of an action key, so two
        // players' logs show which part of a roll's description differed.
        internal static string Parts(NativeInvocation invocation,string randomState)
        {
            var parts=new List<string>{"rng="+Short(randomState??"")};
            var parameters=invocation.Method.GetParameters();
            for(int i=0;i<invocation.Args.Length;i++) {
                int pi=i-(invocation.Method.IsStatic?0:1);
                if(pi>=0&&parameters[pi].ParameterType.IsByRef) continue;
                if(pi>=0&&typeof(Delegate).IsAssignableFrom(parameters[pi].ParameterType)) continue;
                string name=pi<0?"receiver":parameters[pi].Name;
                object item=invocation.Args[i];
                if(item!=null&&Inherits(item.GetType(),"GameLocationCharacter")) item=ReadProperty(item,"RulesetActor")??ReadProperty(item,"RulesetCharacter");
                var actor=item as RulesetActor;
                try {
                    if(actor!=null) {
                        parts.Add(name+".hp="+Section(v=>WriteActorHealth(v,actor)));
                        parts.Add(name+".attributes="+Section(v=>WriteActorAttributes(v,actor)));
                        parts.Add(name+".conditions="+Section(v=>WriteActorConditions(v,actor)));
                        parts.Add(name+".proficiencies="+Section(v=>WriteActorProficiencies(v,actor)));
                    }
                    else parts.Add(name+"="+Section(v=>Write(v,item,0,new HashSet<object>(ReferenceComparer.Instance)))+Label(item));
                } catch(Exception error) { parts.Add(name+"=error:"+error.GetType().Name); }
            }
            return string.Join(" ",parts.ToArray());
        }
        // A stand-in result when the game's result cannot be described.
        internal static string Fallback(string identity,int ordinal)
        { var value=new StringBuilder("MDR result unavailable/1;"); Put(value,identity??""); Put(value,ordinal); return Hash(value); }
        // Diagnostics after the roll: the call's parts, then its result.
        internal static string CompletedParts(NativeRollPlan plan,string randomState)
        {
            var parts=new List<string>{Parts(plan.Invocation,randomState)};
            try { parts.Add("return="+Section(v=>Write(v,plan.NativeReturn,0,new HashSet<object>(ReferenceComparer.Instance)))); } catch(Exception error) { parts.Add("return=error:"+error.GetType().Name); }
            try { parts.Add("outputs="+Section(v=>Write(v,plan.NativeOutputs,0,new HashSet<object>(ReferenceComparer.Instance)))); } catch(Exception error) { parts.Add("outputs=error:"+error.GetType().Name); }
            parts.Add("dice="+Section(v=>{ Put(v,plan.NetworkNativeManual); foreach(var leaf in plan.Leaves) { Put(v,leaf.Ordinal); Put(v,leaf.Used); Put(v,leaf.NextDraw); foreach(int face in leaf.Faces) Put(v,face); } }));
            return string.Join(" ",parts.ToArray());
        }
        // A readable name next to a definition's or an attack mode's hash
        // (which of a monster's attacks each game carries, for example).
        static string Label(object item)
        {
            try {
                if(item==null) return "";
                var type=item.GetType();
                if(Inherits(type,"BaseDefinition")) return "~"+Clean(Convert.ToString(ReadProperty(item,"Name"),CultureInfo.InvariantCulture));
                if(type.Name=="RulesetAttackMode") {
                    var source=ReadProperty(item,"SourceDefinition");
                    return "~"+Clean(source==null?"none":Convert.ToString(ReadProperty(source,"Name"),CultureInfo.InvariantCulture))+"/"+Clean(Convert.ToString(ReadProperty(item,"ActionType"),CultureInfo.InvariantCulture))
                        +"/x"+Clean(Convert.ToString(ReadProperty(item,"AttacksNumber"),CultureInfo.InvariantCulture));
                }
            } catch(Exception) { return "~?"; }
            return "";
        }
        static string Clean(string text)
        {
            if(string.IsNullOrEmpty(text)) return "none";
            var value=new StringBuilder(Math.Min(text.Length,48));
            foreach(char c in text) { if(value.Length>=48) break; value.Append(char.IsLetterOrDigit(c)||c=='_'||c=='-'||c=='.'?c:'_'); }
            return value.ToString();
        }
        static string Section(Action<StringBuilder> write) { var value=new StringBuilder(); write(value); return Hash(value).Substring(0,8); }
        static string Short(string text) { using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").Substring(0,8).ToLowerInvariant(); }
        static void WriteStringSet(StringBuilder value,IEnumerable<string> names)
        {
            if(names==null)throw new InvalidOperationException("A networked hero has missing proficiency data.");
            var bounded=new List<string>();
            foreach(var name in names) {if(bounded.Count>=1024)throw new InvalidOperationException("A networked hero has oversized proficiency data.");bounded.Add(name);}
            bounded.Sort(StringComparer.Ordinal); foreach(var name in bounded)Put(value,name); Put(value,"end proficiencies");
        }
        static void Put(StringBuilder target,object value)
        {
            string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
            if(text.Length>65536||target.Length>65536-text.Length-16)throw new InvalidOperationException("The native multiplayer descriptor exceeds its safe bound.");
            target.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
        }
        static string Hash(StringBuilder value)
        {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()))).Replace("-","").ToLowerInvariant();}
        sealed class ReferenceComparer:IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance=new ReferenceComparer();
            public new bool Equals(object a,object b){return ReferenceEquals(a,b);}
            public int GetHashCode(object value){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);}
        }
    }
}
