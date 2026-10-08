using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

class ShimUnityInternalCalls
{
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types){foreach(var type in types){yield return type;foreach(var child in Types(type.NestedTypes))yield return child;}}
    static void Main(string[] args)
    {
        var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
        using(var module=ModuleDefinition.ReadModule(args[0],new ReaderParameters{AssemblyResolver=resolver}))
        {
            int count=0;var constructor=module.ImportReference(typeof(NotSupportedException).GetConstructor(new[]{typeof(string)}));
            foreach(var method in Types(module.Types).SelectMany(type=>type.Methods).Where(method=>method.IsInternalCall))
            {
                method.ImplAttributes=MethodImplAttributes.IL|MethodImplAttributes.Managed;
                var body=new MethodBody(method);body.Instructions.Add(Instruction.Create(OpCodes.Ldstr,"Test-only Unity dependency: native InternalCalls cannot be invoked by this qualification fixture."));body.Instructions.Add(Instruction.Create(OpCodes.Newobj,constructor));body.Instructions.Add(Instruction.Create(OpCodes.Throw));method.Body=body;count++;
            }
            module.Write(args[1]);Console.WriteLine("Test-only Unity dependency: "+count+" native InternalCalls become managed throwing methods; none may be invoked; game assembly unchanged.");
        }
    }
}
