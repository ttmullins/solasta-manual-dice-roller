using System;
using System.Collections.Generic;
using System.Reflection;

namespace ManualDiceRoller
{
    // The production explanations are compiled against genuine game types.
    // Only the runtime's plain plan/invocation records are host doubles here.
    public sealed class NativeInvocation
    {
        public readonly object Owner; public readonly string Site; public readonly MethodBase Method; public readonly object[] Args; public readonly long Generation;
        public object Receiver { get { return Method.IsStatic ? null : Args[0]; } }
        public NativeInvocation(object owner,string site,MethodBase method,object[] args,long generation)
        {Owner=owner;Site=site;Method=method;Args=(object[])args.Clone();Generation=generation;}
        public object Arg(string name)
        {
            var parameters=Method.GetParameters();
            for(int i=0;i<parameters.Length;i++) if(parameters[i].Name==name) return Args[i+(Method.IsStatic?0:1)];
            return null;
        }
    }
    public sealed class NativeRollPlan
    {
        internal sealed class Leaf
        {
            internal RuleDefinitions.RollContext Context;
            internal bool Static;
            internal string Title,Note,Hint;
        }
        internal readonly List<Leaf> Leaves=new List<Leaf>();
    }
}
