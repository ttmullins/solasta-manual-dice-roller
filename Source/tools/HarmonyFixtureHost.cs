using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// Fixture-host adaptation only. This sandbox exposes /proc/self/maps but
// Process.Modules returns no modules, which prevents MonoMod finding clrjit.
// Forward every real platform operation; replace only filename enumeration.
// Never compile this into the UMM plugin or package it as a runtime dependency.
public static class HarmonyFixtureHost
{
    public static IEnumerable<string> MappedFiles()
    {
        return File.ReadAllLines("/proc/self/maps")
            .Where(line => line.IndexOf('/') >= 0)
            .Select(line => line.Substring(line.IndexOf('/')).Replace(" (deleted)", ""))
            .Distinct().ToArray();
    }

    public static bool Initialize(Assembly harmony)
    {
        if (Process.GetCurrentProcess().Modules.Count != 0 || !File.Exists("/proc/self/maps")) return false;
        var tripleType = harmony.GetType("MonoMod.Core.Platforms.PlatformTriple", true);
        var systemInterface = harmony.GetType("MonoMod.Core.Platforms.ISystem", true);
        var system = tripleType.GetMethod("CreateCurrentSystem").Invoke(null, null);
        var realType = system.GetType();
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ManualDiceRoller.FixtureHostShim"), AssemblyBuilderAccess.Run);
        dynamicAssembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor(new[] {typeof(string)}), new object[] {harmony.GetName().Name}));
        var type = dynamicAssembly.DefineDynamicModule("Shim").DefineType("MappedModuleSystem", TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(systemInterface);
        var real = type.DefineField("real", typeof(object), FieldAttributes.Private);
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] {typeof(object)});
        var writer = constructor.GetILGenerator();
        writer.Emit(OpCodes.Ldarg_0);writer.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
        writer.Emit(OpCodes.Ldarg_0);writer.Emit(OpCodes.Ldarg_1);writer.Emit(OpCodes.Stfld, real);writer.Emit(OpCodes.Ret);
        var map = realType.GetInterfaceMap(systemInterface);
        for (int n = 0; n < map.InterfaceMethods.Length; n++)
        {
            var method = map.InterfaceMethods[n];
            var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var body = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig, method.ReturnType, parameters);
            type.DefineMethodOverride(body, method);
            writer = body.GetILGenerator();
            if (method.Name == "EnumerateLoadedModuleFiles")
                writer.Emit(OpCodes.Call, typeof(HarmonyFixtureHost).GetMethod("MappedFiles"));
            else
            {
                writer.Emit(OpCodes.Ldarg_0);writer.Emit(OpCodes.Ldfld, real);writer.Emit(OpCodes.Castclass, realType);
                for (int p = 0; p < parameters.Length; p++) writer.Emit(OpCodes.Ldarg, p + 1);
                writer.Emit(OpCodes.Callvirt, map.TargetMethods[n]);
            }
            writer.Emit(OpCodes.Ret);
        }
        var adapted = Activator.CreateInstance(type.CreateType(), new[] {system});
        var architecture = tripleType.GetMethod("CreateCurrentArchitecture").Invoke(null, new[] {adapted});
        // Linux's real optional initialization is preserved even though the
        // forwarding wrapper implements only ISystem.
        foreach (var initialization in realType.GetInterfaces().Where(i => i.IsGenericType && i.Name.StartsWith("IInitialize") && i.GetGenericArguments()[0].IsInstanceOfType(architecture)))
            initialization.GetMethod("Initialize").Invoke(system, new[] {architecture});
        var runtime = tripleType.GetMethod("CreateCurrentRuntime").Invoke(null, new[] {adapted, architecture});
        var triple = Activator.CreateInstance(tripleType, new[] {architecture, adapted, runtime});
        tripleType.GetField("lazyCurrent", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, triple);
        Console.WriteLine("Fixture host: actual MonoMod platform with module filenames read from /proc/self/maps because Process.Modules is empty.");
        return true;
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public string AssemblyName { get; private set; }
        public IgnoresAccessChecksToAttribute(string name) { AssemblyName = name; }
    }
}
