using System;
using System.Collections.Generic;
using System.Reflection;

namespace ManualDiceRoller
{
    public static class RuntimeRolls
    {
        public static bool Enabled=true;
        public static bool CanManual(RulesetActor actor,RuleDefinitions.RollContext context){return Enabled&&actor!=null;}
        public static int Max(RuleDefinitions.DieType die){int i=(int)die;return i>=0&&i<RuleDefinitions.DiceMaxValue.Length?RuleDefinitions.DiceMaxValue[i]:0;}
    }
    public sealed class NativeRollPlan
    {
        public sealed class Group {public RuleDefinitions.DieType Die;public int Count;public RuleDefinitions.AdvantageType Advantage;public bool Static;public string Note,Hint;public int DC;public bool HasBonus;public int Bonus;}
        public readonly List<Group> Groups=new List<Group>();
        public Func<bool> NativeStateValid;
        public void RequireNativeState(Func<bool> validator){var old=NativeStateValid;NativeStateValid=old==null?validator:(Func<bool>)(()=>old()&&validator());}
        public void AddStaticDice(RulesetActor actor,RuleDefinitions.DieType die,int count,object form=null,string title=null,string note=null,RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None,string role=null,RuleDefinitions.RollContext category=RuleDefinitions.RollContext.None,string hint=null){Groups.Add(new Group{Die=die,Count=count,Advantage=advantage,Static=true,Note=note,Hint=hint});}
        public void AddActorDice(RulesetActor actor,RuleDefinitions.DieType die,RuleDefinitions.RollContext context,int count,RuleDefinitions.AdvantageType advantage=RuleDefinitions.AdvantageType.None,bool proficient=false,string skill="",bool canReroll=true,object form=null,string title=null,string note=null,bool enumerate=true,string hint=null){Groups.Add(new Group{Die=die,Count=count,Advantage=advantage,Static=false,Note=note,Hint=hint});}
        public void Describe(int dc,bool hasBonus,int bonus){if(Groups.Count==0)return;var g=Groups[Groups.Count-1];g.DC=dc;g.HasBonus=hasBonus;g.Bonus=bonus;}
    }
}
