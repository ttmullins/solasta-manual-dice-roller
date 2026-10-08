using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

// Development-only ABI smoke probe. Build.py never packages this assembly.
public static class BuildSmoke
{
    public static bool Load(UnityModManager.ModEntry entry)
    {
        entry.OnToggle = (e, active) => true;
        entry.OnGUI = e => {};
        entry.OnSaveGUI = e => {};
        var target = typeof(BuildSmoke).GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
        var harmony = new Harmony("ManualDiceRoller.BuildSmoke");
        var instructions = PatchProcessor.GetCurrentInstructions(target);
        return instructions.Count > 0 && harmony.Id.Length > 0;
    }

    // Compile-only ABI witness; it is never called by a release or suite.
    public static bool NetworkAbi(byte code, byte[] payload, IOnEventCallback target)
    {
        PhotonNetwork.AddCallbackTarget(target);
        PhotonNetwork.RemoveCallbackTarget(target);
        return PhotonNetwork.RaiseEvent(code,payload,new RaiseEventOptions { Receivers=ReceiverGroup.All },
            new SendOptions { Reliability=true });
    }
}
