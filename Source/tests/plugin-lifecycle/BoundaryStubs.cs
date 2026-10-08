using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

[assembly: AssemblyVersion("0.32.4.0")]

namespace UnityModManagerNet
{
    public static class UnityModManager
    {
        public abstract class ModSettings
        {
            public static bool FailLoad, NullLoad;
            public static T Load<T>(ModEntry entry) where T : ModSettings, new()
            { if (FailLoad) throw new InvalidOperationException("injected settings load"); return NullLoad ? null : new T(); }
            public virtual void Save(ModEntry entry) { }
            protected static void Save(ModSettings settings, ModEntry entry) { }
        }
        public sealed class ModInfo { public string Version = "0.8.4.2"; }
        public sealed class ModLogger
        {
            public bool Fail;
            public readonly List<string> Lines = new List<string>();
            public void Log(string value) { Lines.Add(value); if (Fail) throw new InvalidOperationException("injected logger"); }
            public void Warning(string value) { Log(value); }
            public void Error(string value) { Log(value); }
        }
        public sealed class ModEntry
        {
            public ModInfo Info = new ModInfo();
            public ModLogger Logger = new ModLogger();
            public string Path = "";
            public Func<ModEntry, bool, bool> OnToggle;
            public Action<ModEntry> OnGUI, OnSaveGUI;
            public Func<ModEntry, bool> OnUnload;
            public Action<ModEntry, float> OnUpdate;
        }
    }
}
// Test-only networking boundaries avoid executing Photon/Unity internal calls.
public static class ServiceRepository { public static T GetService<T>() where T:class { return null; } }
namespace Photon.Pun { public static class PhotonNetwork { public static bool InRoom, OfflineMode; } }
namespace UnityEngine
{
    public static class Application
    {
        public static bool FailVersion;
        public static string Version = "2019.4.37f1\"test\nvalue";
        public static string unityVersion { get { if (FailVersion) throw new InvalidOperationException("injected Unity version"); return Version; } }
    }
}
namespace ManualDiceRoller
{
    public static class TestBoundary
    {
        public static string FailInstall;
        public static bool FailPreflight, FailReset, FailCreate, FailDestroy, FailClose, FailTransportInitialize, FailTransportShutdown;
        public static int Resets, Creates, Destroys, Closes, TransportInitializations, TransportShutdowns, TransportUpdates, CompletionUpdates;
        public static bool ResetSawDisabled, DestroySawDisabled, TransportSawEnabled;
        public static readonly List<string> Installs = new List<string>();
        public static bool Preflight(out string error) { error = FailPreflight ? "injected preflight" : null; return !FailPreflight; }
        public static void Install(Harmony harmony, string name)
        {
            Installs.Add(name);
            harmony.Patch(typeof(TestTargets).GetMethod(name), prefix: new HarmonyMethod(typeof(TestTargets).GetMethod("Prefix")));
            if (FailInstall == name) throw new InvalidOperationException("injected " + name + " install");
        }
            public static void ResetFaults()
        {
            FailInstall = null; FailPreflight = FailReset = FailCreate = FailDestroy = FailClose = false;
            Resets = Creates = Destroys = Closes = 0; ResetSawDisabled = DestroySawDisabled = false; Installs.Clear();
            ModFeatures.Flushes = ModFeatures.Detaches = 0;
            UnityModManagerNet.UnityModManager.ModSettings.FailLoad = UnityModManagerNet.UnityModManager.ModSettings.NullLoad = false;
            UnityEngine.Application.FailVersion = false;
            FailTransportInitialize=FailTransportShutdown=false;
            TransportInitializations=TransportShutdowns=TransportUpdates=CompletionUpdates=0;
            TransportSawEnabled=false;
            Photon.Pun.PhotonNetwork.InRoom=Photon.Pun.PhotonNetwork.OfflineMode=false;
        }
    }
    public static class TestTargets
    {
        public static void Native() { }
        public static void Runtime() { }
        public static void Effects() { }
        public static void Rest() { }
        public static void Input() { }
        public static void Network() { }
        public static void Compatibility() { }
        public static void Foreign() { }
        public static void Features() { }
        public static void Prefix() { }
    }
    public static class RollWindow
    {
        public static void Create() { TestBoundary.Creates++; if (TestBoundary.FailCreate) throw new InvalidOperationException("injected window create"); }
        public static void Close() { TestBoundary.Closes++; if (TestBoundary.FailClose) throw new InvalidOperationException("injected window close"); }
        public static void DestroyWindow() { TestBoundary.Destroys++; TestBoundary.DestroySawDisabled = !Plugin.Enabled; if (TestBoundary.FailDestroy) throw new InvalidOperationException("injected window destroy"); }
    }
    public static class RuntimeRolls
    {
        public static void Reset() { TestBoundary.Resets++; TestBoundary.ResetSawDisabled = !Plugin.Enabled; if (TestBoundary.FailReset) throw new InvalidOperationException("injected runtime reset"); }
        public static void Install(Harmony harmony) { TestBoundary.Install(harmony, "Runtime"); }
        public static void UpdateNetworkCompletion() { TestBoundary.CompletionUpdates++; }
    }
    public static class NativePatches
    {
        public static bool Preflight(out string error) { return TestBoundary.Preflight(out error); }
        public static void Install(Harmony harmony)
        {
            new Harmony("ManualDiceRoller.FaultCompatibility").Patch(typeof(TestTargets).GetMethod("Compatibility"), prefix: new HarmonyMethod(typeof(TestTargets).GetMethod("Prefix")));
            TestBoundary.Install(harmony, "Native");
        }
    }
    public static class EffectPatches { public static void Install(Harmony harmony) { TestBoundary.Install(harmony, "Effects"); } }
    public static class RestPatches
    {
        public static bool Preflight(out string error) { return TestBoundary.Preflight(out error); }
        public static void Install(Harmony harmony) { TestBoundary.Install(harmony, "Rest"); }
    }
    internal static class NativeNetworkScheduler
    {
        internal static bool Preflight(out string error) { return TestBoundary.Preflight(out error); }
        internal static void Install(Harmony harmony) { TestBoundary.Install(harmony,"Network"); }
    }
    internal static class MultiplayerRolls
    {
        internal static bool Ready { get { return false; } }
        internal static string Status { get { return "Mocked lifecycle transport"; } }
        internal static bool Preflight(out string error) { return TestBoundary.Preflight(out error); }
        internal static void Initialize() { TestBoundary.TransportInitializations++; TestBoundary.TransportSawEnabled=Plugin.Enabled; if(TestBoundary.FailTransportInitialize) throw new InvalidOperationException("injected multiplayer initialize"); }
        internal static void Shutdown() { TestBoundary.TransportShutdowns++; if(TestBoundary.FailTransportShutdown) throw new InvalidOperationException("injected multiplayer shutdown"); }
        internal static void Update() { TestBoundary.TransportUpdates++; }
        internal static void Fail(string reason) { }
        internal static void Rearm() { }
    }
    internal static class ModFeatures
    {
        internal static int Flushes, Detaches;
        internal static bool Preflight(out string error) { return TestBoundary.Preflight(out error); }
        internal static void Install(Harmony harmony) { TestBoundary.Install(harmony, "Features"); }
        internal static void Flush() { Flushes++; }
        internal static void Drop() { }
        internal static void Detach() { Detaches++; }
    }
    internal static class InputGuards
    {
        public static bool Preflight(out string error) { return TestBoundary.Preflight(out error); }
        public static void Install(Harmony harmony) { TestBoundary.Install(harmony, "Input"); }
    }
}
