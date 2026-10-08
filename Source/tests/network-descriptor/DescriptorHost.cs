using System;
using System.Collections.Generic;
using System.Reflection;

namespace ManualDiceRoller
{
    // The production descriptor is compiled against genuine game/Unity types.
    // Only its containing runtime's plain data records are host doubles here.
    public sealed class NativeInvocation
    {
        public readonly object Owner; public readonly string Site; public readonly MethodBase Method; public readonly object[] Args; public readonly long Generation;
        public NativeInvocation(object owner,string site,MethodBase method,object[] args,long generation)
        {Owner=owner;Site=site;Method=method;Args=(object[])args.Clone();Generation=generation;}
    }
    public sealed class NativeRollPlan
    {
        internal sealed class Leaf
        {
            internal RulesetActor DisplayActor;
            internal RuleDefinitions.DieType Die;
            internal RuleDefinitions.RollContext Context,RequestCategory;
            internal RuleDefinitions.AdvantageType Advantage;
            internal bool Proficient,CanReroll,Static,Enumerate,Used;
            internal string Skill="",Role="";
            internal int Ordinal,NextDraw,GroupStart,GroupCount;
            internal object Form;
            internal readonly List<FeatureDefinitionDieRollModifier> Features=new List<FeatureDefinitionDieRollModifier>();
            internal readonly List<int> Faces=new List<int>();
        }
        internal readonly NativeInvocation Invocation;
        internal readonly List<Leaf> Leaves=new List<Leaf>();
        internal string NetworkKey,NetworkSignature;
        internal bool NetworkNativeManual;
        internal bool NativeObserved;
        internal object NativeReturn;
        internal object[] NativeOutputs;
        public NativeRollPlan(NativeInvocation invocation){Invocation=invocation;}
    }
}
