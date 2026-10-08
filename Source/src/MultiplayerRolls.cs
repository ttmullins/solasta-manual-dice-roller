using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ManualDiceRoller
{
    // A multiplayer native call always rendezvous, including a call which
    // cannot be rolled manually. Local readiness never decides native results.
    internal static class MultiplayerRolls
    {
        static MultiplayerTransport transport;
        static string artifactProfile;
        static bool occupied;
        static object requestOwner;
        static RollPrompt ownedPrompt,ownedWaiting;
        internal static bool Ready { get { return transport!=null&&transport.Ready; } }
        internal static bool Busy { get { return occupied||transport!=null&&transport.Protocol!=null&&transport.Protocol.Busy; } }
        internal static bool Failed { get { return transport!=null&&transport.Failed; } }
        internal static int LocalActor { get { return transport==null?0:transport.LocalActor; } }
        internal static string SessionToken { get { return transport==null||transport.Protocol==null?"":transport.Protocol.SessionToken; } }
        internal static string Status
        {
            get
            {
                if(transport==null||!transport.InRoom) return "Multiplayer manual rolls require this build on every player’s computer.";
                if(transport.Failed&&Mismatch(transport.FailureReason)) return MismatchText;
                if(transport.Failed) return "Manual dice are paused for this multiplayer session: "+transport.FailureReason+" The game rolls for now. Save, then reload the save together to turn manual dice back on.";
                return transport.Ready ? "Multiplayer ready. Each character’s controlling player enters their dice." : "Waiting for matching Manual Dice Roller builds on every player’s computer.";
            }
        }
        // A different mod version, the mod switched off, or different roll
        // types on one computer: reloading does not help until they match.
        internal static bool Mismatch(string reason) { return MultiplayerProtocol.IsProfileMismatch(reason); }
        internal const string MismatchText="Manual dice are off: the players' Manual Dice Roller versions or roll settings differ. Every player needs the same version, the mod turned on and the same roll types (leave the room to change them). The game rolls until then.";
        internal static void Initialize()
        {
            if(transport!=null) return;
            string plugin=AssemblyHash(typeof(Plugin).Assembly), game=AssemblyHash(typeof(RulesetActor).Assembly),
                harmony=AssemblyHash(typeof(HarmonyLib.Harmony).Assembly), umm=AssemblyHash(typeof(UnityModManagerNet.UnityModManager).Assembly);
            artifactProfile=plugin+"/"+game+"/"+harmony+"/"+umm;
            Plugin.Log(NetworkLog.Line("profile","plugin_sha256",plugin,"game_sha256",game,"harmony_sha256",harmony,"umm_sha256",umm));
            transport=new MultiplayerTransport(new PhotonRoomGateway(),ProfileFingerprint,Plugin.Log,plugin,game,()=>RuntimeRolls.NetworkHolding);
            transport.Update();
        }
        static bool pauseShown;
        // The same reason is noticed once, not at every reload of a room
        // whose players still cannot agree (a version or settings mismatch).
        static string noticedReason; static DateTime noticedAt;
        // Display only: the UI's notice when manual dice pause (set by the
        // plugin's UI; fixtures leave it unset).
        internal static Action<string> Paused;
        internal static void Update()
        {
            if(transport==null) return;
            transport.Update();
            if(transport.Failed||!transport.InRoom) RetireRequest(null);
            if(transport.Failed&&transport.InRoom)
            {
                // Nothing may stay held while paused: the game rolls.
                RuntimeRolls.ReleaseNetworkHold();
                if(!pauseShown)
                {
                    pauseShown=true;
                    string reason=transport.FailureReason;
                    var now=DateTime.UtcNow;
                    if(reason!=noticedReason||(now-noticedAt).TotalMinutes>=10)
                    {
                        noticedReason=reason; noticedAt=now;
                        var hook=Paused;
                        try { if(hook!=null) hook(reason); }
                        catch(Exception error) { Plugin.Log("The paused-dice notice could not be shown: "+error.GetType().Name); }
                    }
                }
            }
            else pauseShown=false;
        }
        // The players loaded a location together: a paused session starts
        // over (a fresh handshake) once nothing is pending.
        internal static void Rearm() { if(transport!=null) transport.RequestRearm(); }
        // Diagnostics: each game's own description of a shared roll (its
        // state and the parts of it), to compare the players' logs.
        internal static void LogDescriptor(string actionKey,string state,string parts)
        {
            if(transport==null) return;
            Plugin.Log(NetworkLog.Line("roll_descriptor","session",SessionToken,"local_actor",LocalActor.ToString(CultureInfo.InvariantCulture),
                "action",actionKey??"","state",state??"","parts",parts??""));
        }
        internal static void LogResult(string actionKey,string result,string parts)
        {
            if(transport==null) return;
            Plugin.Log(NetworkLog.Line("roll_result","session",SessionToken,"local_actor",LocalActor.ToString(CultureInfo.InvariantCulture),
                "action",actionKey??"","result",result??"","parts",parts??""));
        }
        // The games' results for a roll differ. That is the game's own
        // multiplayer synchronization, not the entered dice: it is logged,
        // and a roll with entered dice shows one notice per session.
        static string differNoticeSession;
        internal static Action Diverged;
        internal static void ResultsDiffered(string actionKey,bool manualDice)
        {
            if(transport==null) return;
            Plugin.Log(NetworkLog.Line("results_differ","session",SessionToken,"local_actor",LocalActor.ToString(CultureInfo.InvariantCulture),
                "action",actionKey??"","entered_dice",manualDice?"true":"false"));
            if(!manualDice||differNoticeSession==SessionToken) return;
            differNoticeSession=SessionToken;
            var hook=Diverged;
            try { if(hook!=null) hook(); }
            catch(Exception error) { Plugin.Log("The different-results notice could not be shown: "+error.GetType().Name); }
        }
        internal static void Fail(string reason)
        {
            try { if(transport!=null) transport.Fail(reason); }
            finally { RetireRequest(null); }
        }
        static void RetireRequest(object expected)
        {
            if(expected!=null&&!ReferenceEquals(requestOwner,expected)) return;
            requestOwner=null; occupied=false;
            var prompt=ownedPrompt; ownedPrompt=null;
            var waiting=ownedWaiting; ownedWaiting=null;
            // TA.Reset clears nested iterator frames without disposing them.
            // Retire their modal and request lock independently of finally.
            try { if(prompt!=null) RollWindow.Close(prompt); }
            catch(Exception error) { Plugin.Log("A stopped multiplayer prompt could not close: "+error.GetType().Name); }
            try { if(waiting!=null) RollWindow.CloseWaiting(waiting); }
            catch(Exception error) { Plugin.Log("A stopped multiplayer waiting message could not close: "+error.GetType().Name); }
        }
        internal static bool Preflight(out string error)
        {
            error=null;
            try
            {
                var assemblies=new[]{typeof(ExitGames.Client.Photon.EventData).Assembly,typeof(Photon.Realtime.Player).Assembly,typeof(Photon.Pun.PhotonNetwork).Assembly};
                var names=new[]{"Photon3Unity3D","PhotonRealtime","PhotonUnityNetworking"};
                var hashes=new[]{"5619e0aec897562522ce9adc87b5b80e3e5e9b1b137cbae866f8812e874e7fbe",
                    "1cb825bb0f955fef65a29b2981797214d08fa2137b3738c4924b709b7d28a29d",
                    "f1efa0cebf758bd79d43e202757d39b00e27384c65084ed14a9697af8bc3e32b"};
                for(int index=0;index<assemblies.Length;index++)
                    if(assemblies[index].GetName().Name!=names[index]||AssemblyHash(assemblies[index])!=hashes[index])
                    { error="This multiplayer build requires the original supplied "+names[index]+" game library. Restore Solasta through Steam before using this mod."; return false; }
                return true;
            }
            catch(Exception exception) { error="The multiplayer game libraries could not be verified: "+exception.Message; return false; }
        }
        static string AssemblyHash(System.Reflection.Assembly assembly)
        {
            using(var stream=File.OpenRead(assembly.Location))
            using(var hash=SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant(); }
        static string ProfileFingerprint()
        {
            var settings=Plugin.Settings;
            string flags=string.Concat(new[]{settings.Attack,settings.Ability,settings.SavingThrows,settings.Initiative,
                settings.Death,settings.Damage,settings.Healing,settings.SpellPools,settings.HitDice,settings.EnemyRolls,
                settings.MultiplayerExtras,settings.Concentration,settings.Downtime,settings.Lore,settings.RepeatSaves,
                settings.LingeringEffects,settings.Features,settings.Stealth,settings.CharacterCreation,settings.DamageSaves,settings.HiddenPerception,settings.LevelUpHitPoints,settings.Traps}.Select(value=>value?"1":"0").ToArray());
            using(var hash=SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes("MDR multiplayer/2/"+artifactProfile+"/"+(Plugin.Enabled?"enabled":"disabled")+"/"+flags)));
        }
        // Set by the plugin: maps an effect proxy to its controller.
        internal static Func<RulesetActor,RulesetActor> OwnerActor;
        internal static int OwnerFor(RulesetActor actor)
        {
            if(transport==null||!transport.InRoom) throw new InvalidOperationException("A multiplayer roll has no active room.");
            // A spell's effect proxy belongs to the character controlling it.
            var map=OwnerActor;
            if(map!=null&&actor!=null) actor=map(actor)??actor;
            var character=actor as RulesetCharacter;
            for(int count=0;character!=null&&character.IsSubstitute;count++)
            {
                if(count>=8||character.OriginalFormCharacter==null||ReferenceEquals(character,character.OriginalFormCharacter))
                    throw new InvalidOperationException("A substituted character has no stable owner.");
                character=character.OriginalFormCharacter;
            }
            if(character is RulesetCharacterHero)
            {
                var service=ServiceRepository.GetService<IPlayerControllerService>();
                if(service==null) throw new InvalidOperationException("The native character ownership service is unavailable.");
                int controller=service.GetPlayerControllingHero(character);
                PlayerController nativeController;
                if(service.ControllersById==null||!service.ControllersById.TryGetValue(controller,out nativeController)||nativeController==null)
                    throw new InvalidOperationException("The character’s native controller could not be verified.");
                if(nativeController.Type==PlayerController.ControllerType.AI) return transport.MasterActor;
                if(nativeController.Type!=PlayerController.ControllerType.Human)
                    throw new InvalidOperationException("The character has an unsupported native controller.");
                if(Array.IndexOf(transport.Members,controller)<0)
                    throw new InvalidOperationException("The character’s controlling player is not in this room.");
                return controller;
            }
            return transport.MasterActor;
        }

        internal static IEnumerator Rendezvous(string actionKey,int ordinal,string signature,RulesetActor actor,
            RollPrompt prompt,bool supported,Action<int[]> accepted,Action<bool> completed,bool allowAutomaticOnMismatch=false)
        {
            if(accepted==null||completed==null) throw new ArgumentNullException("A multiplayer roll needs both outcome callbacks.");
            Initialize();
            MultiplayerTransport current=transport;
            MultiplayerProtocol arrivedNode=current.Protocol;
            bool owns=false, done=false;
            object claim=null;
            RollPrompt shown=null;
            try
            {
                // Several native actions may be suspended at once. The single
                // deterministic command stream serializes their rendezvous.
                while(occupied)
                {
                    current.Update();
                    if(current.Failed||!current.InRoom||!ReferenceEquals(arrivedNode,current.Protocol)) yield break;
                    yield return null;
                }
                if(current.Failed||!current.InRoom||!ReferenceEquals(arrivedNode,current.Protocol)) yield break;
                claim=new object(); requestOwner=claim; occupied=true; owns=true;
                int owner; int[] maxima; ProtocolRollSpec spec; string problem;
                // Never throw into the game's coroutine: a roll that cannot be
                // described pauses manual dice and the game rolls it.
                if(!Describe(actor,prompt,actionKey,ordinal,signature,supported,allowAutomaticOnMismatch,out owner,out maxima,out spec,out problem))
                { current.Fail(problem); yield break; }
                var node=current.Protocol;
                if(node==null) { current.Fail("The multiplayer roll session disappeared."); yield break; }
                long sequence=node.NextSequence;
                double started=current.Now;
                while(!node.BeginRoll(spec,current.Now))
                {
                    current.Update();
                    if(current.Failed||!current.InRoom||!ReferenceEquals(node,current.Protocol)) yield break;
                    if(current.Now-started>=MultiplayerProtocol.TimeoutSeconds)
                    { current.Fail("Every player must install this exact mod build before multiplayer dice can continue."); yield break; }
                    yield return null;
                }
                current.Flush();
                if(prompt!=null&&owner!=current.LocalActor)
                {
                    RollWindow.DisplayWaiting(prompt,WaitingText(prompt,owner));
                    ownedWaiting=prompt;
                }
                Plugin.Log(NetworkLog.Line("roll_started","session",node.SessionToken,"local_actor",current.LocalActor.ToString(CultureInfo.InvariantCulture),
                    "sequence",sequence.ToString(CultureInfo.InvariantCulture),"action",actionKey,"ordinal",ordinal.ToString(CultureInfo.InvariantCulture),
                    "signature",signature,"owner",owner.ToString(CultureInfo.InvariantCulture),"supported",supported?"true":"false"));
                while(!done)
                {
                    current.Update();
                    if(current.Failed||!current.InRoom||!ReferenceEquals(node,current.Protocol)) yield break;
                    if(!VerifyOwner(actor,owner)) yield break;
                    if(node.NeedsInput&&owner==current.LocalActor)
                    {
                        if(maxima.Length==0)
                        { node.SubmitFaces(new int[0],current.Now); current.Flush(); }
                        else if(shown==null&&!RollWindow.Visible)
                        {
                            shown=prompt;
                            ownedPrompt=shown;
                            RollWindow.Display(shown,
                                values=>{ if(!current.Failed&&ReferenceEquals(node,current.Protocol)&&VerifyOwner(actor,owner)) { node.SubmitFaces(values,current.Now); current.Flush(); } },
                                ()=>{ if(!current.Failed&&ReferenceEquals(node,current.Protocol)&&VerifyOwner(actor,owner)) { node.RequestAutomatic(current.Now); current.Flush(); } },networkAuthorized:true);
                        }
                    }
                    ProtocolOutcome outcome;
                    if(node.TryTakeOutcome(out outcome))
                    {
                        if(outcome.Sequence!=sequence) { current.Fail("A multiplayer roll completed another request."); yield break; }
                        // The protocol has received the full commit/release
                        // barrier. Only now may a native continuation resume.
                        if(!outcome.Automatic) accepted(outcome.Faces);
                        completed(outcome.Automatic);
                        done=true;
                        if(!outcome.Automatic&&prompt!=null&&owner!=current.LocalActor) ShowRemoteFaces(prompt,owner,outcome.Faces);
                        Plugin.Log(NetworkLog.Line("roll_completed","session",node.SessionToken,"local_actor",current.LocalActor.ToString(CultureInfo.InvariantCulture),
                            "sequence",sequence.ToString(CultureInfo.InvariantCulture),"signature",signature,"automatic",outcome.Automatic?"true":"false",
                            "faces",string.Join(",",outcome.Faces.Select(face=>face.ToString(CultureInfo.InvariantCulture)).ToArray()),
                            "action",actionKey,"ordinal",ordinal.ToString(CultureInfo.InvariantCulture),"owner",owner.ToString(CultureInfo.InvariantCulture)));
                        yield break;
                    }
                    yield return null;
                }
            }
            finally
            {
                if(owns)
                {
                    bool stillOwned=ReferenceEquals(requestOwner,claim);
                    RetireRequest(claim);
                    if(stillOwned&&!done&&current.InRoom&&!current.Failed)
                        current.Fail("A native multiplayer roll was canceled before its shared outcome.");
                }
            }
        }
        static bool Describe(RulesetActor actor,RollPrompt prompt,string actionKey,int ordinal,string signature,bool supported,bool allowAutomaticOnMismatch,
            out int owner,out int[] maxima,out ProtocolRollSpec spec,out string problem)
        {
            owner=0; maxima=null; spec=null; problem=null;
            try {
                owner=OwnerFor(actor);
                maxima=prompt==null?new int[0]:prompt.CopyDieFaces();
                spec=new ProtocolRollSpec(actionKey,ordinal,signature,owner,maxima,supported,allowAutomaticOnMismatch);
                return true;
            } catch(Exception error) { problem="The roll's owner or description could not be shared: "+error.Message; return false; }
        }
        // ---- display only: waiting banner text and other players' history ----
        // Never part of the shared protocol, profile or ownership decisions.
        // The plugin's UI sets both hooks (AutoRollNotices.Install); fixtures
        // leave them unset. A failing hook never affects the roll.
        internal static Func<int,string> PlayerName;
        internal static Action<RollPrompt,string,int[]> RemoteFacesAgreed;
        static string PlayerLabel(int actor)
        {
            try { var lookup=PlayerName; string name=lookup==null?null:lookup(actor); if(!string.IsNullOrEmpty(name)) return name; }
            catch(Exception error) { Plugin.Log("A player name could not be read: "+error.GetType().Name); }
            return "Player "+actor.ToString(CultureInfo.InvariantCulture);
        }
        static string WaitingText(RollPrompt prompt,int owner)
        {
            string character=prompt.Actor;
            return PlayerLabel(owner)+" is rolling"+(string.IsNullOrEmpty(character)?"":" for "+character);
        }
        static void ShowRemoteFaces(RollPrompt prompt,int owner,int[] faces)
        {
            var hook=RemoteFacesAgreed;
            if(hook==null||faces==null) return;
            try { hook(prompt,PlayerLabel(owner),(int[])faces.Clone()); }
            catch(Exception error) { Plugin.Log("Another player's roll could not be added to the history: "+error.GetType().Name); }
        }
        static bool VerifyOwner(RulesetActor actor,int expected)
        {
            try { if(OwnerFor(actor)==expected) return true; }
            catch(Exception error) { Plugin.Log("The pending dice controller could not be verified: "+error.Message); }
            Fail("The character's controlling player changed while its dice were pending.");
            return false;
        }
        internal static void Reset()
        {
            try { if(transport!=null&&Busy&&transport.InRoom) transport.Fail("A multiplayer roll was interrupted."); }
            finally { RetireRequest(null); }
        }
        internal static void Shutdown()
        {
            Reset();
            var old=transport; transport=null; occupied=false; requestOwner=null; artifactProfile=null;
            if(old!=null) old.Dispose();
        }
    }
}
