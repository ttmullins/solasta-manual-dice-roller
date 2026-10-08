using System;
using System.Linq;
using Mono.Cecil;

class InspectDependencies
{
    static int Main(string[] args)
    {
        foreach (string path in args)
        {
            using (var module = ModuleDefinition.ReadModule(path))
            {
                Console.WriteLine(path + "\t" + module.Assembly.Name.FullName);
                foreach (var attribute in module.Assembly.CustomAttributes.Where(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute"))
                    Console.WriteLine("  framework=" + attribute.ConstructorArguments[0].Value);
                foreach (var reference in module.AssemblyReferences)
                    Console.WriteLine("  reference=" + reference.FullName);
            }
        }
        return 0;
    }
}
