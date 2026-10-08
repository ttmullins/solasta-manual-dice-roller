using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

class AuditPlugin
{
    const string Pristine = "a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee";
    static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
    { yield return type;foreach (var nested in type.NestedTypes)foreach (var value in Walk(nested))yield return value; }
    static int Main(string[] args)
    {
        var errors = new List<string>();int references = 0;int bodies = 0;
        var resolver = new DefaultAssemblyResolver();
        foreach (string directory in args.Skip(1)) resolver.AddSearchDirectory(directory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        using (var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters {AssemblyResolver = resolver}))
        {
            if (module.Assembly.Name.Name != "ManualDiceRoller") errors.Add("Unexpected standalone assembly identity");
            if(module.Kind!=ModuleKind.Dll) errors.Add("Runtime plugin must be a managed DLL");
            if((module.Attributes & ModuleAttributes.ILOnly)==0 || (module.Attributes & ModuleAttributes.Required32Bit)!=0)
                errors.Add("Runtime plugin must be pure IL without a 32-bit process requirement");
            if(module.Architecture!=TargetArchitecture.I386 && module.Architecture!=TargetArchitecture.AMD64)
                errors.Add("Runtime architecture is incompatible with Windows x64 Unity");
            Console.WriteLine("Runtime module profile: "+module.RuntimeVersion+"; "+module.Architecture+"; "+module.Attributes+"; framework references supplied by the pristine game.");
            var umm = module.AssemblyReferences.FirstOrDefault(r => r.Name == "UnityModManager");
            var harmony = module.AssemblyReferences.FirstOrDefault(r => r.Name == "0Harmony");
            var core = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib");
            if (umm == null || umm.Version != new Version(0,32,4,0)) errors.Add("Wrong or absent genuine UMM reference");
            if (harmony == null || harmony.Version != new Version(2,3,6,0)) errors.Add("Wrong or absent genuine Harmony reference");
            if (core == null || core.Version != new Version(4,0,0,0)) errors.Add("Plugin is not built against the game Mono framework");
            foreach (var reference in module.AssemblyReferences)
            {
                if (new[] {"System.Private.CoreLib","System.Runtime","Mono.Cecil","ManualRollFixes","BuildSmoke"}.Contains(reference.Name)) errors.Add("Unwanted runtime reference: " + reference.Name);
                try {
                    var resolved=resolver.Resolve(reference);
                    if(resolved.Name.FullName!=reference.FullName)
                        errors.Add("Runtime assembly identity differs from the actual supplied reference: "+reference.FullName+" -> "+resolved.Name.FullName);
                    Console.WriteLine("Runtime reference identity: "+reference.FullName);
                } catch(Exception e) {errors.Add("Unresolved runtime assembly identity: "+reference.FullName+" ("+e.GetType().Name+")");}
            }
            if (module.Resources.Any(r => r.Name.IndexOf("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) >= 0)) errors.Add("Game assembly embedded as a resource");
            var all = module.Types.SelectMany(Walk).ToArray();
            var plugin = all.FirstOrDefault(t => t.FullName == "ManualDiceRoller.Plugin");
            var entry = plugin == null ? null : plugin.Methods.FirstOrDefault(m => m.Name == "Load" && m.IsPublic && m.IsStatic && m.ReturnType.FullName == "System.Boolean" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "UnityModManagerNet.UnityModManager/ModEntry");
            if (entry == null) errors.Add("UMM Plugin.Load entry point has the wrong signature");
            foreach (var type in all)
            {
                if (type.Name.IndexOf("GoDice",StringComparison.OrdinalIgnoreCase) >= 0 || type.Name == "BuildSmoke" || type.Name == "HarmonyFixtureHost") errors.Add("Excluded development/hardware type shipped: " + type.FullName);
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    bodies++;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var call = instruction.Operand as MethodReference;
                        if (call != null && call.DeclaringType.FullName == "System.IO.File" && new[] {"Copy","Move","WriteAllBytes","Replace"}.Contains(call.Name))
                            errors.Add("Runtime file replacement API is not allowed in this standalone plugin: " + method.FullName + " -> " + call.Name);
                        var text = instruction.Operand as string;
                        if (text != null && (text.IndexOf("GoDice", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("feedback.txt",StringComparison.OrdinalIgnoreCase) >= 0)) errors.Add("Excluded hardware protocol string in " + method.FullName);
                    }
                }
                foreach(var method in type.Methods.Where(m=>m.IsPInvokeImpl)) errors.Add("Unreviewed native P/Invoke dependency shipped: "+method.FullName);
            }
            if (!all.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i => i.Operand is string && (string)i.Operand == Pristine))
                errors.Add("Exact pristine-build hash guard constant absent");
            foreach (var reference in module.GetTypeReferences())
            {
                references++;
                try {if (reference.Resolve() == null) errors.Add("Unresolved type: " + reference.FullName);}
                catch (Exception e) {errors.Add("Unresolved type: " + reference.FullName + " (" + e.GetType().Name + ")");}
            }
            foreach (var reference in module.GetMemberReferences())
            {
                references++;
                try
                {
                    if (reference is MethodReference && ((MethodReference)reference).Resolve() == null) errors.Add("Unresolved method: " + reference.FullName);
                    else if (reference is FieldReference && ((FieldReference)reference).Resolve() == null) errors.Add("Unresolved field: " + reference.FullName);
                }
                catch (Exception e) {errors.Add("Unresolved member: " + reference.FullName + " (" + e.GetType().Name + ")");}
            }
        }
        foreach (var error in errors.Distinct()) Console.Error.WriteLine(error);
        Console.WriteLine("Standalone assembly audit: " + references + " references; " + bodies + " compiled method bodies; " + errors.Distinct().Count() + " errors.");
        return errors.Count == 0 ? 0 : 1;
    }
}
