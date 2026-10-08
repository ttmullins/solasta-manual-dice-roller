using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class ExtractNativeShove
{
    static ModuleDefinition target;
    static MethodDefinition currentMethod;
    static string Signature(TypeReference type)
    {
        if (type is GenericParameter) { var p = (GenericParameter)type; return (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position; }
        if (type is ByReferenceType) return Signature(((ByReferenceType)type).ElementType) + "&";
        if (type is GenericInstanceType) { var g = (GenericInstanceType)type; return Signature(g.ElementType) + "<" + String.Join(",", g.GenericArguments.Select(Signature)) + ">"; }
        return type.FullName;
    }
    static TypeReference Type(TypeReference type)
    {
        if (type is GenericParameter) { var parameter = (GenericParameter)type; return parameter.Type == GenericParameterType.Method ? currentMethod.GenericParameters[parameter.Position] : currentMethod.DeclaringType.GenericParameters[parameter.Position]; }
        if (type is GenericInstanceType) { var g = (GenericInstanceType)type; var mapped = new GenericInstanceType(Type(g.ElementType)); foreach (var a in g.GenericArguments) mapped.GenericArguments.Add(Type(a)); return mapped; }
        if (type is ByReferenceType) return new ByReferenceType(Type(((ByReferenceType)type).ElementType));
        if (type is ArrayType) { var a = (ArrayType)type; return new ArrayType(Type(a.ElementType), a.Rank); }
        return target.GetType(type.FullName) ?? target.ImportReference(type);
    }
    static object Member(object value)
    {
        if (value is GenericInstanceMethod) { var g = (GenericInstanceMethod)value; var mapped = new GenericInstanceMethod((MethodReference)Member(g.ElementMethod)); foreach (var a in g.GenericArguments) mapped.GenericArguments.Add(Type(a)); return mapped; }
        if (value is TypeReference) return Type((TypeReference)value);
        if (value is FieldReference) { var f = (FieldReference)value; var owner = target.GetType(f.DeclaringType.FullName); return owner == null ? target.ImportReference(f) : owner.Fields.Single(x => x.Name == f.Name); }
        if (value is MethodReference) {
            var m = (MethodReference)value; var owner = target.GetType(m.DeclaringType.FullName);
            if (owner == null) return target.ImportReference(m);
            var matches = owner.Methods.Where(x => x.Name == m.Name && x.GenericParameters.Count == m.GenericParameters.Count && Signature(x.ReturnType) == Signature(m.ReturnType) && String.Join(",", x.Parameters.Select(p => Signature(p.ParameterType))) == String.Join(",", m.Parameters.Select(p => Signature(p.ParameterType)))).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Missing fixture member: " + m.FullName);
            return matches[0];
        }
        return value;
    }
    static void Copy(MethodDefinition source, MethodDefinition destination)
    {
        currentMethod = destination;
        for (int i = 0; i < source.Parameters.Count; i++) destination.Parameters[i].Name = source.Parameters[i].Name;
        var body = new MethodBody(destination) { InitLocals = source.Body.InitLocals };
        var instructions = source.Body.Instructions.ToDictionary(i => i, i => Instruction.Create(OpCodes.Nop));
        var locals = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in source.Body.Variables) { var local = new VariableDefinition(Type(v.VariableType)); body.Variables.Add(local); locals[v] = local; }
        foreach (var instruction in source.Body.Instructions) {
            var mapped = instructions[instruction]; mapped.OpCode = instruction.OpCode; object operand = instruction.Operand;
            if (operand is Instruction) operand = instructions[(Instruction)operand];
            else if (operand is Instruction[]) operand = ((Instruction[])operand).Select(i => instructions[i]).ToArray();
            else if (operand is VariableDefinition) operand = locals[(VariableDefinition)operand];
            else if (operand is ParameterDefinition) operand = destination.Parameters[((ParameterDefinition)operand).Index];
            else operand = Member(operand);
            mapped.Operand = operand; body.Instructions.Add(mapped);
        }
        foreach (var h in source.Body.ExceptionHandlers) body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType) { TryStart = instructions[h.TryStart], TryEnd = h.TryEnd == null ? null : instructions[h.TryEnd], HandlerStart = instructions[h.HandlerStart], HandlerEnd = h.HandlerEnd == null ? null : instructions[h.HandlerEnd], CatchType = h.CatchType == null ? null : Type(h.CatchType), FilterStart = h.FilterStart == null ? null : instructions[h.FilterStart] });
        destination.Body = body;
        Console.WriteLine("Extracted pristine IL: " + source.FullName + " (" + source.Body.Instructions.Count + " instructions)");
    }
    static void Main(string[] args)
    {
        using (var source = ModuleDefinition.ReadModule(args[0])) using (target = ModuleDefinition.ReadModule(args[1])) {
            foreach(var t in source.GetTypes().Where(t=>t.FullName=="ActionDefinitions/ActionExecutionModifier"||t.FullName=="RulesetCharacter/AdditionalAbilityCheckDieRolledHandler"||t.FullName=="RulesetCharacter/ContestCheckRolledHandler"))
                Console.WriteLine("Native fixture ABI: "+t.FullName+" valueType="+t.IsValueType+" "+String.Join("; ",t.Methods.Select(m=>m.FullName)));
            foreach (var type in target.GetTypes().Where(type => type.IsEnum)) {
                var native = source.GetType(type.FullName); if (native == null) continue;
                foreach (var field in type.Fields.Where(field => field.HasConstant)) { var original = native.Fields.SingleOrDefault(value => value.Name == field.Name); if (original != null) field.Constant = original.Constant; }
            }
            foreach (var name in new[] { "RollDie", "RerollDieAsNeeded", "RollDiceAndSum" }) Copy(source.GetType("RulesetActor").Methods.Single(m => m.Name == name), target.GetType("RulesetActor").Methods.Single(m => m.Name == name));
            foreach (var name in new[] { "RollDie", "RollStaticDiceAndSum", "ComputeAdvantage" }) Copy(source.GetType("RuleDefinitions").Methods.Single(m => m.Name == name), target.GetType("RuleDefinitions").Methods.Single(m => m.Name == name));
            foreach (var name in new[] { "get_IsSubstitute", "get_OriginalFormCharacter", "EnumerateFeaturesToBrowse" }) Copy(source.GetType("RulesetCharacterMonster").Methods.Single(m => m.Name == name), target.GetType("RulesetCharacterMonster").Methods.Single(m => m.Name == name));
            foreach (var entry in new[] {
                new[] { "GameLocationCharacter", "ComputeAbilityCheckActionModifier" }, new[] { "GameLocationCharacter", "PrepareActionModifier" }, new[] { "GameLocationCharacter", "RollAbilityCheck" },
                new[] { "RulesetCharacter", "ComputeBaseAbilityCheckBonus" }, new[] { "RulesetCharacter", "RollAbilityCheck" }, new[] { "RulesetCharacter", "RollBasicAbilityCheck" },
                new[] { "RulesetCharacterHero", "ComputeBaseAbilityCheckBonus" }, new[] { "RulesetCharacterHero", "IsProficient" },
                new[] { "FeatureDefinitionAbilityCheckAffinity", "ComputeAbilityCheckModifier" }, new[] { "FeatureDefinitionAbilityCheckAffinity", "ComputeAbilityCheckAdvantageTrendAndModifier" }
            }) {
                foreach (var native in source.GetType(entry[0]).Methods.Where(method => method.Name == entry[1])) {
                    var destination = target.GetType(entry[0]).Methods.Single(method => method.Name == native.Name && method.Parameters.Count == native.Parameters.Count);
                    Copy(native, destination);
                }
            }
            Copy(source.GetType("CharacterActionShove").Methods.Single(m=>m.Name=="ResolveRolls"),target.GetType("CharacterActionShove").Methods.Single(m=>m.Name=="ResolveRolls"));
            Copy(source.GetType("RulesetCharacter").Methods.Single(m=>m.Name=="ResolveContestCheck"),target.GetType("RulesetCharacter").Methods.Single(m=>m.Name=="ResolveContestCheck"));
            target.Write(args[1] + ".native");
        }
    }
}
