using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TA;

namespace ManualDiceRoller
{
    // Preparation is limited to native, deterministic batches whose dice forms
    // are all of one kind (damage, healing or temporary hit points); forms
    // without dice may sit beside them. The original form dispatcher,
    // damage/healing functions and caches still run. Random-selection and
    // filtered batches, and batches mixing dice kinds, have no safe
    // synchronous await boundary between their forms and are left to the game.
    public static class EffectPatches
    {
        static readonly FieldInfo actualForms=AccessTools.Field(typeof(CharacterActionMagicEffect),"actualEffectForms");
        static readonly FieldInfo damageCache=AccessTools.Field(typeof(RulesetImplementationManager),"damageByFormIndex");
        static readonly PropertyInfo addDice=AccessTools.Property(typeof(CharacterActionMagicEffect),"AddDice");
        static readonly PropertyInfo addHP=AccessTools.Property(typeof(CharacterActionMagicEffect),"AddHP");
        static readonly PropertyInfo addTempHP=AccessTools.Property(typeof(CharacterActionMagicEffect),"AddTempHP");
        static readonly PropertyInfo targetItem=AccessTools.Property(typeof(CharacterActionMagicEffect),"TargetItem");
        static readonly FieldInfo activeSpell=AccessTools.Field(typeof(CharacterActionCastSpell),"activeSpell");
        static readonly FieldInfo activePower=AccessTools.Field(typeof(CharacterActionUsePower),"activePower");
        [ThreadStatic] static FormFrame currentForm;
        [ThreadStatic] static DrawFrame currentDraw;
        sealed class FormFrame
        {
            internal EffectForm Form;
            internal RulesetImplementationDefinitions.ApplyFormsParams Parameters;
            internal bool HealingBaseStarted;
            internal int NextHealingBonus;
        }
        sealed class DrawFrame
        { internal RulesetActor Actor; internal RuleDefinitions.RollContext Context; internal bool Static; }
        sealed class FormState { internal object Native; internal FormFrame Previous; internal DrawFrame Draw; }

        // Only an explicit native dice helper activates form-owned permits.
        // Concentration and notification rolls within InflictDamage remain
        // automatic and cannot consume or invalidate another form's dice.
        public static bool IsEligibleNativeDraw(RulesetActor actor,RuleDefinitions.RollContext context,bool isStatic)
        {
            return currentDraw!=null && currentDraw.Static==isStatic &&
                (isStatic||ReferenceEquals(currentDraw.Actor,actor)&&currentDraw.Context==context);
        }

        public static bool TryPrepare(NativeInvocation call,NativeRollPlan plan)
        {
            using(var scratch=new NativeScratchScope()) return TryPrepareScoped(call,plan,scratch);
        }
        static void AddActorScratch(NativeScratchScope scratch,RulesetActor actor)
        {
            scratch.AddActor(actor);
            var character=actor as RulesetCharacter;
            if(character!=null&&character.IsSubstitute) scratch.AddActor(character.OriginalFormCharacter);
        }
        static bool TryPrepareScoped(NativeInvocation call,NativeRollPlan plan,NativeScratchScope scratch)
        {
            List<EffectForm> forms;
            RulesetImplementationDefinitions.ApplyFormsParams parameters;
            RuleDefinitions.EffectApplication application;
            bool proxyOnly=false, selfOnly=false;
            RulesetImplementationManager manager;
            if(call.Method.Name=="ApplyEffectForms")
            {
                forms=call.Arg("effectForms") as List<EffectForm>;
                parameters=call.Arg<RulesetImplementationDefinitions.ApplyFormsParams>("formsParams");
                AddActorScratch(scratch,parameters.sourceCharacter); AddActorScratch(scratch,parameters.targetCharacter);
                application=call.Arg<RuleDefinitions.EffectApplication>("effectApplication");
                proxyOnly=call.Arg<bool>("proxyOnly"); selfOnly=call.Arg<bool>("forceSelfConditionOrLightOnly");
                manager=call.Receiver as RulesetImplementationManager;
            }
            else if(call.Method.Name=="ApplyMagicEffect")
            {
                var action=call.Receiver as CharacterActionMagicEffect;
                if(action!=null)
                {
                    AddActorScratch(scratch,action.ActingCharacter==null?null:action.ActingCharacter.RulesetCharacter);
                    var target=call.Arg("target") as GameLocationCharacter;
                    AddActorScratch(scratch,target==null?null:target.RulesetActor);
                }
                if(action==null||!TryMagicParameters(call,action,out forms,out parameters,out application,out proxyOnly)) return true;
                manager=ServiceRepository.GetService<IRulesetImplementationService>() as RulesetImplementationManager;
            }
            else return false;

            if(forms==null||manager==null||proxyOnly||selfOnly||application!=RuleDefinitions.EffectApplication.All) return true;
            if(parameters.targetCharacter==null) return true;
            // Traps and hazards have no source character: their damage is a
            // static roll (RollStaticDamage) entered by the affected hero's
            // player. Other source-less batches stay native.
            bool trap=parameters.sourceCharacter==null;
            if(trap&&(!Plugin.Settings.Traps||parameters.effectSourceType!=RuleDefinitions.EffectSourceType.Environment)) return true;
            // A batch may lose later forms when an earlier one kills, downs or
            // ends the effect on its target. Reference-bound permits cannot
            // shift to another form; unused accepted dice are retired at the
            // call's exit. Forms without dice of their own (conditions, motion,
            // summons, counters and the like) roll nothing here: any die they
            // cause belongs to another form or site and stays the game's, so
            // they may share a batch with the planned dice. The native loop
            // counts only damage forms for the area damage cache index.
            foreach(var form in forms) if(form==null) return true;
            for(int i=0;i<forms.Count;i++) for(int j=0;j<i;j++) if(ReferenceEquals(forms[i],forms[j])) return true;
            // A Dispel Magic style counter form (no other dice in the batch):
            // the caster's d20 against each targeted spell.
            // A detrimental condition carrying the source's bardic die (Cutting
            // Words style): the bard's die, rolled when the condition lands.
            if(!forms.Any(DiceForm)&&forms.Any(form=>IsDispel(form)||IsBardicCondition(form,parameters)))
            {
                if(trap) return true;
                foreach(var form in forms)
                {
                    if(!WillApply(form,parameters)) continue;
                    if(IsDispel(form)) AddDispelCheck(plan,form,parameters);
                    else if(IsBardicCondition(form,parameters)) AddBardicConditionDie(plan,form,parameters);
                }
                return true;
            }
            // Damage and healing alter one another's eligibility and feature
            // callbacks. Until an exact inter-form await exists, use one kind.
            EffectForm.EffectFormType? kind=null;
            foreach(var form in forms)
            {
                if(!DiceForm(form)) continue;
                if(kind==null) kind=form.FormType;
                else if(form.FormType!=kind.Value) return true;
            }
            if(kind==null) return true;

            if(trap&&kind.Value!=EffectForm.EffectFormType.Damage) return true;
            if(trap) plan.RequireManualState(()=>Plugin.Settings.Traps);
            bool firstDamage=true;
            int index=0;
            foreach(var form in forms)
            {
                if(!DiceForm(form)||!WillApply(form,parameters)) continue;
                if(trap) AddTrapDamage(plan,form,parameters,ref firstDamage);
                else if(form.FormType==EffectForm.EffectFormType.Damage)
                    AddDamage(plan,form,parameters,manager,ref firstDamage,ref index);
                else if(form.FormType==EffectForm.EffectFormType.Healing)
                    AddHealing(plan,form,parameters);
                else AddTemporaryHP(plan,form,parameters);
            }
            return true;
        }

        static bool IsBardicCondition(EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p)
        {
            if(form==null||form.FormType!=EffectForm.EffectFormType.Condition||form.ConditionForm==null||form.ConditionForm.Operation!=ConditionForm.ConditionOperation.Add) return false;
            var definition=form.ConditionForm.ConditionDefinition;
            if(definition==null||definition.ConditionType!=RuleDefinitions.ConditionType.Detrimental||!definition.UsesBardicInspirationDie()) return false;
            // ApplyConditionForm adds nothing after a successful save.
            if(p.rolledSaveThrow&&form.SavingThrowAffinity!=RuleDefinitions.EffectSavingThrowType.None&&p.saveOutcome!=RuleDefinitions.RollOutcome.Failure&&p.saveOutcome!=RuleDefinitions.RollOutcome.CriticalFailure) return false;
            if(form.DcModifier!=0&&p.saveOutcome==RuleDefinitions.RollOutcome.Failure&&p.saveOutcomeDelta>form.DcModifier) return false;
            return p.sourceCharacter!=null&&RuntimeRolls.Max(p.sourceCharacter.GetBardicInspirationDieValue())>1;
        }
        static void AddBardicConditionDie(NativeRollPlan plan,EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p)
        {
            if(!Plugin.Settings.Features) return;
            var definition=form.ConditionForm.ConditionDefinition;
            plan.RequireManualState(()=>Plugin.Settings.Features);
            plan.AddStaticDice(p.sourceCharacter,p.sourceCharacter.GetBardicInspirationDieValue(),1,form,title:"Bardic die",
                note:RollText.Source(definition.Name)+" on "+(p.targetCharacter==null?"the target":p.targetCharacter.Name),
                hint:"Roll your inspiration die; the game applies it as the effect's penalty.",category:Plugin.FeatureDice);
        }
        public static int BardicConditionFace(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        {
            var frame=currentForm;
            bool own=frame!=null&&frame.Form.FormType==EffectForm.EffectFormType.Condition&&IsBardicCondition(frame.Form,frame.Parameters);
            return FormFace(own?frame.Form:null,diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
        }
        static IEnumerable<CodeInstruction> BardicConditionTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes=new List<CodeInstruction>(instructions);
            var native=AccessTools.Method(typeof(RuleDefinitions),"RollDie");
            int count=0;
            foreach(var code in codes) if(code.opcode==System.Reflection.Emit.OpCodes.Call&&Equals(code.operand,native)) { code.operand=AccessTools.Method(typeof(EffectPatches),nameof(BardicConditionFace)); count++; }
            if(count!=1) throw new InvalidOperationException("The native bardic condition die changed.");
            return codes;
        }
        static bool IsDispel(EffectForm form)
        { return form!=null&&form.FormType==EffectForm.EffectFormType.Counter&&form.CounterForm!=null&&(int)form.CounterForm.Type==1; }
        // RulesetImplementationManagerLocation.ApplyCounterForm, dissipate
        // spells: one raw d20 plus the caster's bonus, compared with each spell
        // above the automatic level. Asked only when such a spell is present.
        static void AddDispelCheck(NativeRollPlan plan,EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p)
        {
            if(p.targetCharacter==null||p.sourceCharacter==null||!Plugin.Settings.Features) return;
            var counter=form.CounterForm;
            int bonus=0,extra=0;
            var spell=p.activeEffect as RulesetEffectSpell;
            if(spell!=null)
            {
                var definition=spell.SpellDefinition;
                if(definition.EffectDescription.HasAdditionalSlotAdvancement)
                    extra+=definition.EffectDescription.EffectAdvancement.ComputeAdditionalSpellLevelBySlotDelta(spell.SlotLevel-definition.SpellLevel);
                if(counter.AddSpellCastingAbility&&spell.SpellRepertoire!=null)
                    bonus+=AttributeDefinitions.ComputeAbilityScoreModifier(p.sourceCharacter.GetAttribute(spell.SpellRepertoire.SpellCastingAbility).CurrentValue);
            }
            if(counter.AddProficiencyBonus&&p.sourceCharacter is RulesetCharacterHero) bonus+=p.sourceCharacter.GetAttribute("ProficiencyBonus").CurrentValue;
            if(counter.AddAbilityBonus&&counter.AbilityToAdd!=string.Empty&&p.sourceCharacter is RulesetCharacterHero)
                bonus+=AttributeDefinitions.ComputeAbilityScoreModifier(p.sourceCharacter.GetAttribute(counter.AbilityToAdd).CurrentValue);
            int automatic=counter.AutomaticSpellLevel+extra, highest=0;
            var proxy=p.targetCharacter as RulesetCharacterEffectProxy;
            if(proxy!=null)
            {
                var effect=RulesetEntity.GetEntity<RulesetEffect>(proxy.EffectGuid) as RulesetEffectSpell;
                if(effect!=null) highest=effect.SlotLevel;
            }
            else
            {
                var character=p.targetCharacter as RulesetCharacter;
                List<RulesetCondition> effects;
                if(character!=null&&character.ConditionsByCategory!=null&&character.ConditionsByCategory.TryGetValue("11Effect",out effects)&&effects!=null)
                    foreach(var condition in effects)
                    {
                        SpellDefinition definition;
                        if(condition!=null&&!string.IsNullOrEmpty(condition.EffectDefinitionName)&&DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(condition.EffectDefinitionName,out definition))
                            highest=Math.Max(highest,condition.EffectLevel);
                    }
            }
            if(highest<=automatic) return;
            plan.RequireManualState(()=>Plugin.Settings.Features);
            plan.AddStaticDice(p.sourceCharacter,RuleDefinitions.DieType.D20,1,form,title:"Dispel check",
                note:"Beat "+counter.CheckBaseDC+" + each spell's level (spells of level "+automatic+" or lower end automatically)",
                hint:"A natural 20 always succeeds and a 1 always fails.",category:RuleDefinitions.RollContext.AbilityCheck);
            plan.Describe(0,true,bonus);
        }
        // The form's own raw die is its eligible static draw.
        static int FormFace(EffectForm form,RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore)
        {
            var saved=currentDraw;
            if(form!=null) currentDraw=new DrawFrame {Static=true};
            try { return RuntimeRolls.FormStaticFace(form,diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore); }
            finally { currentDraw=saved; }
        }
        static string Sha256(byte[] bytes)
        { using(var hash=System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        static IEnumerable<CodeInstruction> CounterTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes=new List<CodeInstruction>(instructions);
            var native=AccessTools.Method(typeof(RuleDefinitions),"RollDie");
            int count=0;
            foreach(var code in codes) if(code.opcode==System.Reflection.Emit.OpCodes.Call&&Equals(code.operand,native)) { code.operand=AccessTools.Method(typeof(EffectPatches),nameof(CounterFace)); count++; }
            if(count!=1) throw new InvalidOperationException("The native dispel check changed.");
            return codes;
        }
        public static int CounterFace(RuleDefinitions.DieType diceType,RuleDefinitions.AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0f)
        {
            var frame=currentForm;
            return FormFace(frame==null||!IsDispel(frame.Form)?null:frame.Form,diceType,advantageType,out firstRoll,out secondRoll,rollAlterationScore);
        }
        static bool DiceForm(EffectForm form)
        {
            return form.FormType==EffectForm.EffectFormType.Damage||form.FormType==EffectForm.EffectFormType.Healing||
                form.FormType==EffectForm.EffectFormType.TemporaryHitPoints;
        }
        static int PropertyInt(PropertyInfo property,object owner)
        { if(property==null) throw new InvalidOperationException("Native spell parameter property is unavailable."); return (int)property.GetValue(owner,null); }
        static bool TryMagicParameters(NativeInvocation call,CharacterActionMagicEffect action,
            out List<EffectForm> forms,out RulesetImplementationDefinitions.ApplyFormsParams p,
            out RuleDefinitions.EffectApplication application,out bool proxyOnly)
        {
            forms=null; p=default(RulesetImplementationDefinitions.ApplyFormsParams);
            application=RuleDefinitions.EffectApplication.All; proxyOnly=false;
            // The two supplied stock overrides have different effect-level
            // arguments. Unknown overrides must establish their own boundary.
            RulesetEffect effect=action.GetType()==typeof(CharacterActionCastSpell)?activeSpell.GetValue(action) as RulesetEffect:
                action.GetType()==typeof(CharacterActionUsePower)?activePower.GetValue(action) as RulesetEffect:null;
            int effectLevel;
            RuleDefinitions.EffectSourceType source;
            if(action.GetType()==typeof(CharacterActionCastSpell) && effect is RulesetEffectSpell)
            { effectLevel=((RulesetEffectSpell)effect).SlotLevel; source=RuleDefinitions.EffectSourceType.Spell; }
            else if(action.GetType()==typeof(CharacterActionUsePower) && effect is RulesetEffectPower)
            { effectLevel=0; source=RuleDefinitions.EffectSourceType.Power; }
            else return false;
            var target=call.Arg("target") as GameLocationCharacter;
            if(target==null||action.ActingCharacter==null) return false;
            bool positions=call.Method.GetParameters().Length==2;
            // Position-only calls normally create proxies, topology or motion.
            // Their target-null branch has no supported dice leaf.
            p.FillSourceAndTarget(action.ActingCharacter.RulesetCharacter,positions?target.RulesetCharacter:target.RulesetActor);
            p.FillFromActiveEffect(effect);
            p.FillSpecialParameters(positions?action.RolledSaveThrow:call.Arg<bool>("rolledSaveThrow"),PropertyInt(addDice,action),
                PropertyInt(addHP,action),PropertyInt(addTempHP,action),effectLevel,
                positions?null:call.Arg("actionModifier") as ActionModifier,
                positions?action.SaveOutcome:call.Arg<RuleDefinitions.RollOutcome>("saveOutcome"),
                positions?action.SaveOutcomeDelta:call.Arg<int>("saveOutcomeDelta"),
                !positions&&call.Arg<RuleDefinitions.RollOutcome>("outcome")==RuleDefinitions.RollOutcome.CriticalSuccess,
                positions?0:call.Arg<int>("targetIndex"),positions?0:call.Arg<int>("targetCount"),
                positions?null:targetItem.GetValue(action,null) as RulesetItem);
            p.targetSubstitute=action.ActionParams.TargetSubstitute; p.effectSourceType=source;
            if(!positions&&(effect.EffectDescription.RangeType==RuleDefinitions.RangeType.MeleeHit||effect.EffectDescription.RangeType==RuleDefinitions.RangeType.RangeHit))
                p.attackOutcome=call.Arg<RuleDefinitions.RollOutcome>("outcome");
            if(positions)
            {
                // Stock CastSpell's position override passes Power as well.
                p.effectSourceType=RuleDefinitions.EffectSourceType.Power;
                p.position=call.Arg<int3>("position");
                forms=effect.EffectDescription.EffectForms; proxyOnly=effect.EffectDescription.HasEffectProxy;
            }
            else
            {
                var byTarget=actualForms.GetValue(action) as Dictionary<int,List<EffectForm>>;
                if(byTarget==null||!byTarget.TryGetValue(p.targetIndex,out forms)) return false;
            }
            application=effect.EffectDescription.EffectApplication;
            return true;
        }

        static bool WillApply(EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p)
        {
            if(p.activeEffect!=null&&p.activeEffect.Terminated) return false;
            // These dispatcher branches only permit damage forms.
            bool miss=p.attackOutcome==RuleDefinitions.RollOutcome.Failure||p.attackOutcome==RuleDefinitions.RollOutcome.CriticalFailure;
            bool hitRange=p.activeEffect!=null&&(p.activeEffect.EffectDescription.RangeType==RuleDefinitions.RangeType.MeleeHit||p.activeEffect.EffectDescription.RangeType==RuleDefinitions.RangeType.RangeHit);
            if(hitRange&&miss&&p.activeEffect.EffectDescription.HalfDamageOnAMiss&&form.FormType!=EffectForm.EffectFormType.Damage) return false;
            if(p.activeEffect is RulesetEffectSpell&&form.FormType!=EffectForm.EffectFormType.Damage&&
                p.sourceCharacter.CanForceHalfDamage(((RulesetEffectSpell)p.activeEffect).SpellDefinition)&&
                (hitRange&&miss||p.activeEffect.EffectDescription.HasSavingThrow&&
                    (p.saveOutcome==RuleDefinitions.RollOutcome.Success||p.saveOutcome==RuleDefinitions.RollOutcome.CriticalSuccess))) return false;
            return true;
        }

        static bool Area(RuleDefinitions.TargetType target)
        {
            return target==RuleDefinitions.TargetType.Line||target==RuleDefinitions.TargetType.Cone||target==RuleDefinitions.TargetType.Cube||
                target==RuleDefinitions.TargetType.CubeWithOffset||target==RuleDefinitions.TargetType.Sphere||target==RuleDefinitions.TargetType.Cylinder||
                target==RuleDefinitions.TargetType.CylinderWithDiameter||target==RuleDefinitions.TargetType.WallLine||target==RuleDefinitions.TargetType.WallRing||
                target==RuleDefinitions.TargetType.ArcFromIndividual;
        }
        static int AddedDamageDice(EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p,bool first)
        {
            int extra=(first||(p.activeEffect is RulesetEffectSpell&&!form.DamageForm.IgnoreSpellAdvancementDamageDice))?p.addDice:0;
            if(form.ApplyLevel==EffectForm.LevelApplianceType.MultiplyDice)
            {
                if(form.LevelType==RuleDefinitions.LevelSourceType.CharacterLevel) extra=p.sourceCharacter.GetAttribute("CharacterLevel").CurrentValue;
                else if(form.LevelType==RuleDefinitions.LevelSourceType.ClassLevel) extra=p.classLevel;
                else if(form.LevelType==RuleDefinitions.LevelSourceType.ClassLevelHalfUp) extra=(p.classLevel+1)/2;
                else if(form.LevelType==RuleDefinitions.LevelSourceType.EffectLevel) extra=p.effectLevel;
            }
            else if(form.ApplyLevel==EffectForm.LevelApplianceType.DiceNumberByLevelTable)
                extra=DiceByRank.GetDiceOfRank(p.sourceCharacter.GetAttribute("CharacterLevel").CurrentValue,form.DiceByLevelTable,true);
            return extra;
        }
        static void AddDamage(NativeRollPlan plan,EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p,
            RulesetImplementationManager manager,ref bool first,ref int index)
        {
            var damage=form.DamageForm;
            if(damage==null||p.targetCharacter.IsDead||(!first&&p.targetCharacter.IsDeadOrDyingOrUnconscious)) return;
            float multiplier=p.actionModifier!=null?p.actionModifier.DefenderDamageMultiplier:1f;
            bool forcedHalf=p.activeEffect is RulesetEffectSpell&&p.sourceCharacter.CanForceHalfDamage(((RulesetEffectSpell)p.activeEffect).SpellDefinition);
            // Display only: why the game will deal less than the dice show.
            string reason=null, target=TargetName(p.targetCharacter);
            if(p.activeEffect!=null&&(p.activeEffect.EffectDescription.RangeType==RuleDefinitions.RangeType.MeleeHit||p.activeEffect.EffectDescription.RangeType==RuleDefinitions.RangeType.RangeHit)&&
                (p.attackOutcome==RuleDefinitions.RollOutcome.Failure||p.attackOutcome==RuleDefinitions.RollOutcome.CriticalFailure)&&
                (p.activeEffect.EffectDescription.HalfDamageOnAMiss||forcedHalf)) { multiplier=.5f; reason="The attack missed, but this effect still deals half damage: the game halves the total."; }
            else if(form.SavingThrowAffinity!=RuleDefinitions.EffectSavingThrowType.None&&p.rolledSaveThrow)
            {
                if(p.saveOutcome==RuleDefinitions.RollOutcome.Success||p.saveOutcome==RuleDefinitions.RollOutcome.CriticalSuccess)
                {
                    if(form.SavingThrowAffinity==RuleDefinitions.EffectSavingThrowType.Negates) multiplier=p.negatedModulationFactor;
                    else if(form.SavingThrowAffinity==RuleDefinitions.EffectSavingThrowType.HalfDamage) multiplier=p.targetCharacter.HasSpecialHalfDamage(p.savingThrowAbility)?0f:.5f;
                    if(multiplier>0f&&multiplier<1f) reason=(target.Length>0?target:"The target")+" made the saving throw: the game halves the total.";
                }
                else if(p.targetCharacter.HasSpecialHalfDamage(p.savingThrowAbility)) { multiplier=.5f; reason=(target.Length>0?target:"The target")+" has a feature that halves this damage."; }
            }
            if(multiplier<=0f) return;
            int extra=AddedDamageDice(form,p,first);
            bool reroll=first||p.attackMode==null||p.attackMode.SourceDefinition==null||p.attackMode.SourceDefinition is MonsterAttackDefinition;
            bool versatile=p.useVersatileDamage&&first;
            first=false;
            bool area=Area(p.targetType), cached=false, maximum=false;
            if(area)
            {
                var cache=damageCache.GetValue(manager) as Dictionary<int,int>;
                if(cache==null) throw new InvalidOperationException("Native damage cache is unavailable.");
                cached=cache.ContainsKey(index); index++;
            }
            if(p.activeEffect!=null&&(p.targetCharacter is RulesetCharacterHero||p.targetCharacter is RulesetCharacterMonster))
                foreach(var affinity in p.activeEffect.EffectDescription.DamageAffinitiesByFamily)
                    if(affinity.Family==((RulesetCharacter)p.targetCharacter).CharacterFamily)
                    { maximum=affinity.DamageAffinityType==RuleDefinitions.DamageAffinityType.MaximumDamage; break; }
            // An area's first application still draws its ordinary shared pool
            // even when this particular target receives maximum damage.
            if((area&&cached)||(!area&&maximum)) return;
            RuleDefinitions.DieType die=versatile?damage.VersatileDieType:damage.DieType;
            var hero=p.sourceCharacter as RulesetCharacterHero;
            if(damage.OverrideWithBardicInspirationDie&&hero!=null&&hero.GetBardicInspirationDieValue()!=RuleDefinitions.DieType.D1)
                die=hero.GetBardicInspirationDieValue();
            int count=checked((damage.DiceNumber+extra)*(p.criticalSuccess&&!damage.IgnoreCriticalDoubleDice?2:1));
            plan.AddActorDice(p.sourceCharacter,die,p.attackMode!=null?RuleDefinitions.RollContext.AttackDamageValueRoll:RuleDefinitions.RollContext.MagicDamageValueRoll,
                count,canReroll:reroll,form:form,title:p.attackMode!=null?"Attack damage":"Spell damage",note:DamageNote(form.DamageForm==null?null:form.DamageForm.DamageType,target),
                hint:reason??"Enter each raw die. The game adds damage bonuses and applies resistances.",enumerate:false);
        }
        static string TargetName(RulesetActor actor)
        {
            try { return actor==null?"":(actor.Name??"").Trim(); }
            catch(Exception) { return ""; }
        }
        static string DamageNote(string type,string target)
        {
            string text=RollText.Damage(type);
            if(target.Length==0) return text;
            return (text.Length>0?text:"Damage")+" to "+target;
        }
        // ApplyDamageForm's source-less branch: RollStaticDamage once per call
        // (a triggered trap), or once for the first target of a sphere trap
        // whose result the others share.
        static void AddTrapDamage(NativeRollPlan plan,EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p,ref bool first)
        {
            var damage=form.DamageForm;
            if(damage==null||damage.OverrideWithBardicInspirationDie||damage.DamageType=="DamageAncestry") return;
            if(p.targetCharacter.IsDead||(!first&&p.targetCharacter.IsDeadOrDyingOrUnconscious)) return;
            float multiplier=p.actionModifier!=null?p.actionModifier.DefenderDamageMultiplier:1f;
            if(p.activeEffect!=null&&(p.activeEffect.EffectDescription.RangeType==RuleDefinitions.RangeType.MeleeHit||p.activeEffect.EffectDescription.RangeType==RuleDefinitions.RangeType.RangeHit)&&
                (p.attackOutcome==RuleDefinitions.RollOutcome.Failure||p.attackOutcome==RuleDefinitions.RollOutcome.CriticalFailure)&&p.activeEffect.EffectDescription.HalfDamageOnAMiss) multiplier=.5f;
            else if(form.SavingThrowAffinity!=RuleDefinitions.EffectSavingThrowType.None&&p.rolledSaveThrow)
            {
                if(p.saveOutcome==RuleDefinitions.RollOutcome.Success||p.saveOutcome==RuleDefinitions.RollOutcome.CriticalSuccess)
                {
                    if(form.SavingThrowAffinity==RuleDefinitions.EffectSavingThrowType.Negates) multiplier=p.negatedModulationFactor;
                    else if(form.SavingThrowAffinity==RuleDefinitions.EffectSavingThrowType.HalfDamage) multiplier=p.targetCharacter.HasSpecialHalfDamage(p.savingThrowAbility)?0f:.5f;
                }
                else if(p.targetCharacter.HasSpecialHalfDamage(p.savingThrowAbility)) multiplier=.5f;
            }
            if(multiplier<=0f) return;
            int extra;
            if(form.ApplyLevel==EffectForm.LevelApplianceType.MultiplyDice)
            {
                // Level-scaled dice need a source character; the game itself
                // cannot roll them for a trap.
                if(form.LevelType==RuleDefinitions.LevelSourceType.ClassLevel) extra=p.classLevel;
                else if(form.LevelType==RuleDefinitions.LevelSourceType.ClassLevelHalfUp) extra=(p.classLevel+1)/2;
                else if(form.LevelType==RuleDefinitions.LevelSourceType.EffectLevel) extra=p.effectLevel;
                else return;
            }
            else if(form.ApplyLevel==EffectForm.LevelApplianceType.DiceNumberByLevelTable) return;
            else extra=(first||(p.activeEffect is RulesetEffectSpell&&!damage.IgnoreSpellAdvancementDamageDice))?p.addDice:0;
            bool versatile=p.useVersatileDamage&&first;
            first=false;
            // A sphere trap rolls for its first target and shares the result.
            if(p.trapRangeType==RuleDefinitions.TrapRangeType.Sphere&&p.targetIndex!=0) return;
            var target=p.targetCharacter;
            int count=checked((damage.DiceNumber+extra)*(p.criticalSuccess?2:1));
            string targetName=TargetName(target);
            plan.AddStaticDice(target,versatile?damage.VersatileDieType:damage.DieType,count,form,
                title:p.effectSourceType==RuleDefinitions.EffectSourceType.Environment?"Trap or hazard damage":"Damage",
                note:DamageNote(damage.DamageType,targetName),hint:"Enter each raw die. The game applies the bonus, saving throw and resistances.",
                category:Plugin.TrapDice);
            // Planned before the target's saving throw is known (it is asked
            // first, in the same plan): say what the save does to these dice.
            if(form.SavingThrowAffinity!=RuleDefinitions.EffectSavingThrowType.None&&p.rolledSaveThrow)
                plan.HintAfterSave(form,target,form.SavingThrowAffinity==RuleDefinitions.EffectSavingThrowType.HalfDamage,targetName);
        }
        static List<FeatureDefinition> HealingFeatures(RulesetActor actor)
        {
            var features=new List<FeatureDefinition>();
            using(var scratch=new NativeScratchScope())
            { AddActorScratch(scratch,actor); actor.EnumerateFeaturesToBrowse<IHealingModificationProvider>(features); }
            return features;
        }
        sealed class HealingState
        {
            readonly FeatureDefinition feature;
            readonly RuleDefinitions.DieType die;
            readonly RuleDefinitions.LevelSourceType addLevel;
            readonly int count;
            readonly bool maximum;
            internal HealingState(FeatureDefinition value)
            {
                feature=value; var provider=(IHealingModificationProvider)value;
                die=provider.HealingBonusDiceType; count=provider.HealingBonusDiceNumber;
                addLevel=provider.AddLevel; maximum=provider.MaximizeReceivedHealing;
            }
            internal bool Matches(FeatureDefinition value)
            {
                var provider=value as IHealingModificationProvider;
                return ReferenceEquals(feature,value)&&provider!=null&&die==provider.HealingBonusDiceType&&
                    count==provider.HealingBonusDiceNumber&&addLevel==provider.AddLevel&&maximum==provider.MaximizeReceivedHealing;
            }
        }
        static bool HealingStateMatches(RulesetActor actor,HealingState[] expected)
        {
            var observed=HealingFeatures(actor);
            if(observed.Count!=expected.Length) return false;
            for(int i=0;i<expected.Length;i++) if(!expected[i].Matches(observed[i])) return false;
            return true;
        }
        static void AddHealing(NativeRollPlan plan,EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p)
        {
            if(form.HealingForm==null||form.HealingForm.HealingComputation!=RuleDefinitions.HealingComputation.Dice||!RuntimeRolls.CanManual(p.sourceCharacter,RuleDefinitions.RollContext.HealValueRoll)) return;
            var sourceFeatures=HealingFeatures(p.sourceCharacter); var targetFeatures=HealingFeatures(p.targetCharacter);
            var sourceState=new HealingState[sourceFeatures.Count]; var targetState=new HealingState[targetFeatures.Count];
            for(int i=0;i<sourceState.Length;i++) sourceState[i]=new HealingState(sourceFeatures[i]);
            for(int i=0;i<targetState.Length;i++) targetState[i]=new HealingState(targetFeatures[i]);
            plan.RequireNativeState(()=>HealingStateMatches(p.sourceCharacter,sourceState)&&HealingStateMatches(p.targetCharacter,targetState));
            if(p.effectLevel>0)
                foreach(var feature in sourceFeatures)
                {
                    var bonus=(IHealingModificationProvider)feature;
                    plan.AddStaticDice(p.sourceCharacter,bonus.HealingBonusDiceType,bonus.HealingBonusDiceNumber,form,"Additional healing","Enter each raw bonus die.",
                        category:RuleDefinitions.RollContext.HealValueRoll);
                }
            foreach(var feature in targetFeatures) if(((IHealingModificationProvider)feature).MaximizeReceivedHealing) return;
            plan.AddActorDice(p.sourceCharacter,form.HealingForm.DieType,RuleDefinitions.RollContext.HealValueRoll,checked(form.HealingForm.DiceNumber+p.addDice),form:form,title:"Healing",hint:"Enter each raw die. The game applies healing bonuses and caps.",enumerate:false);
        }
        static void AddTemporaryHP(NativeRollPlan plan,EffectForm form,RulesetImplementationDefinitions.ApplyFormsParams p)
        {
            if(form.TemporaryHitPointsForm==null) return;
            var target=form.TemporaryHitPointsForm.ApplyToSelf?p.sourceCharacter:p.targetCharacter;
            if(!(target is RulesetCharacter)) return;
            plan.AddActorDice(p.sourceCharacter,form.TemporaryHitPointsForm.DieType,RuleDefinitions.RollContext.TemporaryHPValueRoll,
                checked(form.TemporaryHitPointsForm.DiceNumber+p.addDice),form:form,title:"Temporary hit points",hint:"Enter each raw die. The game applies the bonus.",enumerate:false);
        }

        static void FormPrefix(EffectForm effectForm,RulesetImplementationDefinitions.ApplyFormsParams formsParams,out FormState __state)
        {
            __state=new FormState {Native=RuntimeRolls.EnterForm(effectForm),Previous=currentForm,Draw=currentDraw};
            currentForm=new FormFrame {Form=effectForm,Parameters=formsParams}; currentDraw=null;
        }
        static Exception FormFinalizer(FormState __state,Exception __exception)
        {
            if(__state!=null) { RuntimeRolls.ExitForm(__state.Native); currentForm=__state.Previous; currentDraw=__state.Draw; }
            return __exception;
        }
        static void DamagePrefix(RulesetActor __instance,DamageForm damageForm,bool attackModeDamage,out DrawFrame __state)
        {
            __state=currentDraw; currentDraw=null;
            if(currentForm!=null&&currentForm.Form.FormType==EffectForm.EffectFormType.Damage&&
                ReferenceEquals(currentForm.Form.DamageForm,damageForm)&&ReferenceEquals(currentForm.Parameters.sourceCharacter,__instance))
                currentDraw=new DrawFrame {Actor=__instance,Context=attackModeDamage?RuleDefinitions.RollContext.AttackDamageValueRoll:RuleDefinitions.RollContext.MagicDamageValueRoll};
        }
        static void HealPrefix(HealingForm healingForm,RulesetCharacter source,out DrawFrame __state)
        {
            __state=currentDraw; currentDraw=null;
            if(currentForm!=null&&currentForm.Form.FormType==EffectForm.EffectFormType.Healing&&
                ReferenceEquals(currentForm.Form.HealingForm,healingForm)&&ReferenceEquals(currentForm.Parameters.sourceCharacter,source))
            {
                currentForm.HealingBaseStarted=true;
                currentDraw=new DrawFrame {Actor=source,Context=RuleDefinitions.RollContext.HealValueRoll};
            }
        }
        static void TemporaryPrefix(TemporaryHitPointsForm temporaryHitPointsForm,RulesetCharacter source,out DrawFrame __state)
        {
            __state=currentDraw; currentDraw=null;
            if(currentForm!=null&&currentForm.Form.FormType==EffectForm.EffectFormType.TemporaryHitPoints&&
                ReferenceEquals(currentForm.Form.TemporaryHitPointsForm,temporaryHitPointsForm)&&ReferenceEquals(currentForm.Parameters.sourceCharacter,source))
                currentDraw=new DrawFrame {Actor=source,Context=RuleDefinitions.RollContext.TemporaryHPValueRoll};
        }
        // RuleDefinitions.RollStaticDamage of a source-less damage form: its one
        // RollStaticDiceAndSum is that form's static draw.
        [ThreadStatic] static DamageForm staticDamage;
        sealed class StaticDamageState { internal DamageForm Previous; }
        static void StaticDamagePrefix(DamageForm damageForm,out StaticDamageState __state)
        {
            __state=new StaticDamageState {Previous=staticDamage}; staticDamage=null;
            if(currentForm!=null&&currentForm.Form.FormType==EffectForm.EffectFormType.Damage&&currentForm.Parameters.sourceCharacter==null&&
                ReferenceEquals(currentForm.Form.DamageForm,damageForm)) staticDamage=damageForm;
        }
        static Exception StaticDamageFinalizer(StaticDamageState __state,Exception __exception) { if(__state!=null) staticDamage=__state.Previous; return __exception; }
        static void StaticSumPrefix(int diceNumber,RuleDefinitions.DieType diceType,out DrawFrame __state)
        {
            __state=currentDraw; currentDraw=null;
            if(staticDamage!=null&&currentForm!=null&&ReferenceEquals(currentForm.Form.DamageForm,staticDamage))
            {
                staticDamage=null;
                if(RuntimeRolls.HasPreparedForm(currentForm.Form)) currentDraw=new DrawFrame {Static=true};
                return;
            }
            if(currentForm==null||!RuntimeRolls.HasPreparedForm(currentForm.Form)||currentForm.Form.FormType!=EffectForm.EffectFormType.Healing||currentForm.HealingBaseStarted||currentForm.Parameters.effectLevel<=0) return;
            var features=HealingFeatures(currentForm.Parameters.sourceCharacter);
            if(currentForm.NextHealingBonus>=features.Count) return;
            var bonus=(IHealingModificationProvider)features[currentForm.NextHealingBonus];
            if(bonus.HealingBonusDiceNumber!=diceNumber||bonus.HealingBonusDiceType!=diceType) return;
            currentForm.NextHealingBonus++;
            currentDraw=new DrawFrame {Static=true};
        }
        static Exception DrawFinalizer(DrawFrame __state,Exception __exception) { currentDraw=__state; return __exception; }
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RulesetImplementationManager),"ApplyEffectForm"),
                prefix:new HarmonyMethod(typeof(EffectPatches),"FormPrefix"),finalizer:new HarmonyMethod(typeof(EffectPatches),"FormFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetActor),"RollDamage"),prefix:new HarmonyMethod(typeof(EffectPatches),"DamagePrefix"),finalizer:new HarmonyMethod(typeof(EffectPatches),"DrawFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetCharacter),"RollAndHeal"),prefix:new HarmonyMethod(typeof(EffectPatches),"HealPrefix"),finalizer:new HarmonyMethod(typeof(EffectPatches),"DrawFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(RulesetCharacter),"RollForTemporaryHitPoints"),prefix:new HarmonyMethod(typeof(EffectPatches),"TemporaryPrefix"),finalizer:new HarmonyMethod(typeof(EffectPatches),"DrawFinalizer"));
            var counter=typeof(RulesetImplementationManagerLocation).GetMethod("ApplyCounterForm",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly);
            if(counter==null||Sha256(counter.GetMethodBody().GetILAsByteArray())!="2bd46ab750cc220a735af4af31ab122c5f4f22925c45dbf0370ebbd330057022")
                throw new InvalidOperationException("Unsupported native dispel check version.");
            harmony.Patch(counter,transpiler:new HarmonyMethod(typeof(EffectPatches),nameof(CounterTranspiler)));
            var added=typeof(RulesetCharacter).GetMethod("OnConditionAdded",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
            if(added==null||Sha256(added.GetMethodBody().GetILAsByteArray())!="a332c08d8194b9a869410eebad4d75a709acf30aca6dd892fcb7fbc43c19fe79")
                throw new InvalidOperationException("Unsupported native condition die version.");
            harmony.Patch(added,transpiler:new HarmonyMethod(typeof(EffectPatches),nameof(BardicConditionTranspiler)));
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollStaticDamage"),prefix:new HarmonyMethod(typeof(EffectPatches),"StaticDamagePrefix"),finalizer:new HarmonyMethod(typeof(EffectPatches),"StaticDamageFinalizer"));
            var staticPrefix=new HarmonyMethod(typeof(EffectPatches),"StaticSumPrefix") {priority=Priority.First};
            harmony.Patch(AccessTools.Method(typeof(RuleDefinitions),"RollStaticDiceAndSum"),prefix:staticPrefix,finalizer:new HarmonyMethod(typeof(EffectPatches),"DrawFinalizer"));
        }
    }
}
