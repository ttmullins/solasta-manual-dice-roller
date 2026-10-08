using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

// Metadata-only audit of the unmodified game transport. No connection is made.
class AuditNetworkAbi
{
    static int errors;
    static void Require(TypeDefinition type, string name, string result, params string[] arguments)
    {
        var matches = type.Methods.Where(m => m.Name == name && m.ReturnType.FullName == result &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(arguments)).ToArray();
        if(matches.Length != 1) { Console.Error.WriteLine("Missing/ambiguous Photon ABI: " + type.FullName + "." + name); errors++; }
        else Console.WriteLine("ABI " + matches[0].FullName);
    }
    static int Main(string[] args)
    {
        if(args.Length != 1) return 2;
        var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(args[0]);
        var input = new Dictionary<string,AssemblyDefinition>();
        foreach(var name in new[]{"PhotonUnityNetworking.dll", "PhotonRealtime.dll", "Photon3Unity3D.dll"})
        {
            var value = AssemblyDefinition.ReadAssembly(Path.Combine(args[0],name),new ReaderParameters{AssemblyResolver=resolver});
            input.Add(name,value); Console.WriteLine("TRANSPORT " + value.Name.FullName);
        }
        try
        {
            var pun = input["PhotonUnityNetworking.dll"].MainModule.GetType("Photon.Pun.PhotonNetwork");
            var callback = input["PhotonRealtime.dll"].MainModule.GetType("Photon.Realtime.IOnEventCallback");
            var send = input["Photon3Unity3D.dll"].MainModule.GetType("ExitGames.Client.Photon.SendOptions");
            if(pun == null || callback == null || send == null) { Console.Error.WriteLine("Required Photon types absent"); return 1; }
            Require(pun,"AddCallbackTarget","System.Void","System.Object");
            Require(pun,"RemoveCallbackTarget","System.Void","System.Object");
            Require(pun,"RaiseEvent","System.Boolean","System.Byte","System.Object","Photon.Realtime.RaiseEventOptions","ExitGames.Client.Photon.SendOptions");
            Require(callback,"OnEvent","System.Void","ExitGames.Client.Photon.EventData");
            foreach(var name in new[]{"get_InRoom","get_OfflineMode","get_IsMasterClient","get_LocalPlayer","get_MasterClient","get_PlayerList"})
            {
                var methods=pun.Methods.Where(m=>m.Name==name&&m.IsStatic&&m.Parameters.Count==0).ToArray();
                if(methods.Length!=1) { Console.Error.WriteLine("Required Photon getter absent/ambiguous: " + name); errors++; }
                else Console.WriteLine("ABI " + methods[0].FullName);
            }
            Require(send,"get_Reliability","System.Boolean");
            Require(send,"set_Reliability","System.Void","System.Boolean");
        }
        finally { foreach(var value in input.Values) value.Dispose(); }
        Console.WriteLine("Pristine Photon transport ABI audit: " + errors + " errors.");
        return errors==0?0:1;
    }
}
