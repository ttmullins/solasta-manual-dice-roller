using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;

namespace ManualDiceRoller
{
    // Photon callbacks never advance native actions. They only copy bounded
    // protocol data; the normal game update owns validation and dispatch.
    internal sealed class MultiplayerRoom
    {
        internal object Instance;
        internal string Name;
        internal int LocalActor, MasterActor;
        internal int[] Members;
    }

    internal interface IMultiplayerRoomGateway
    {
        double Now { get; }
        bool TryGetRoom(out MultiplayerRoom room);
        void Register(IOnEventCallback receiver);
        void Unregister(IOnEventCallback receiver);
        bool Send(int targetActor, byte[] packet);
        bool LeaveRoom();
    }

    internal sealed class PhotonRoomGateway : IMultiplayerRoomGateway
    {
        public double Now { get { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; } }
        public bool TryGetRoom(out MultiplayerRoom result)
        {
            result = null;
            if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) return false;
            var room = PhotonNetwork.CurrentRoom;
            var local = PhotonNetwork.LocalPlayer;
            if (room == null || local == null) return false;
            result = new MultiplayerRoom { Instance=room, Name=room.Name ?? "", LocalActor=local.ActorNumber,
                MasterActor=room.MasterClientId, Members=room.Players.Keys.OrderBy(id=>id).ToArray() };
            return true;
        }
        public void Register(IOnEventCallback receiver) { PhotonNetwork.AddCallbackTarget(receiver); }
        public void Unregister(IOnEventCallback receiver) { PhotonNetwork.RemoveCallbackTarget(receiver); }
        public bool Send(int targetActor, byte[] packet)
        {
            return PhotonNetwork.RaiseEvent(MultiplayerTransport.EventCode, packet,
                new RaiseEventOptions { TargetActors=new[]{targetActor}, CachingOption=EventCaching.DoNotCache, InterestGroup=0 },
                new SendOptions { Reliability=true, Channel=0, Encrypt=false });
        }
        public bool LeaveRoom()
        {
            var service=ServiceRepository.GetService<INetworkingService>();
            if (service!=null && service.LeaveRoom()) return true;
            return PhotonNetwork.InRoom && PhotonNetwork.LeaveRoom(false);
        }
    }

    internal sealed class MultiplayerTransport : IOnEventCallback, IDisposable
    {
        // The pinned game uses 10. PUN's internal range starts at 200. Other
        // mods sharing this application event are isolated by the wire magic.
        internal const byte EventCode=198;
        const int MaxQueuedPackets=512;
        sealed class Incoming { internal int Sender; internal byte[] Bytes; }
        readonly IMultiplayerRoomGateway gateway;
        readonly Func<string> profile;
        readonly Action<string> log;
        readonly string pluginHash,gameHash;
        readonly Func<bool> nativeHolding;
        readonly Queue<Incoming> incoming=new Queue<Incoming>();
        readonly object queueLock=new object();
        MultiplayerRoom room;
        MultiplayerProtocol protocol;
        string profileFingerprint, callbackFailure, loggedSession, lastFailure;
        // Paused: manual dice stopped for this session after a disagreement.
        // Everyone stays in the room and the game rolls; a new session starts
        // when the room changes or the players reload together (Rearm).
        bool registered, disposed, paused, rearm, failureReported, loggedReady;

        internal MultiplayerTransport(IMultiplayerRoomGateway gateway, Func<string> profile, Action<string> log,string pluginHash="",string gameHash="",Func<bool> nativeHolding=null)
        {
            if(gateway==null||profile==null||log==null) throw new ArgumentNullException("Multiplayer transport needs a gateway, profile and logger.");
            this.gateway=gateway; this.profile=profile; this.log=log; this.pluginHash=pluginHash; this.gameHash=gameHash;
            this.nativeHolding=nativeHolding??(()=>false);
        }
        internal MultiplayerProtocol Protocol { get { return protocol; } }
        internal bool InRoom { get { return room!=null; } }
        internal bool Ready { get { return protocol!=null&&protocol.Ready&&!paused; } }
        internal bool Failed { get { return paused || protocol!=null&&protocol.Halted; } }
        internal string FailureReason { get { return protocol!=null&&protocol.Halted?protocol.FailureReason:lastFailure??""; } }
        internal int LocalActor { get { return room==null?0:room.LocalActor; } }
        internal int MasterActor { get { return room==null?0:room.MasterActor; } }
        internal int[] Members { get { return room==null?new int[0]:(int[])room.Members.Clone(); } }
        internal double Now { get { return gateway.Now; } }

        public void OnEvent(EventData photonEvent)
        {
            if(disposed || photonEvent==null || photonEvent.Code!=EventCode) return;
            try
            {
                var bytes=photonEvent.CustomData as byte[];
                if(bytes==null||!MultiplayerProtocol.IsProtocolPacket(bytes)) return;
                lock(queueLock)
                {
                    if(bytes.Length>MultiplayerProtocol.MaxPacketBytes)
                    { callbackFailure="A multiplayer roll packet exceeded its safe size."; return; }
                    if(incoming.Count>=MaxQueuedPackets)
                    { callbackFailure="The multiplayer roll receive queue overflowed."; return; }
                    incoming.Enqueue(new Incoming { Sender=photonEvent.Sender, Bytes=(byte[])bytes.Clone() });
                }
            }
            catch(Exception error) { lock(queueLock) callbackFailure="A multiplayer roll callback could not be copied: "+error.GetType().Name; }
        }

        internal void Update()
        {
            if(disposed) return;
            try
            {
                if(!registered) { gateway.Register(this); registered=true; }
                MultiplayerRoom current;
                if(!gateway.TryGetRoom(out current))
                {
                    if(room!=null&&!paused&&(protocol!=null&&protocol.Busy||nativeHolding()))
                        Fail("The multiplayer room disconnected during a native roll.");
                    if(room!=null) log(NetworkLog.Line("room_left","local_actor",room.LocalActor.ToString(CultureInfo.InvariantCulture)));
                    room=null; protocol=null; profileFingerprint=null;
                    ClearIncoming();
                    return;
                }
                ValidateRoom(current);
                current.Members=(int[])current.Members.Clone(); Array.Sort(current.Members);
                string fingerprint=profile();
                bool changed=room==null || !ReferenceEquals(room.Instance,current.Instance)
                    ||room.LocalActor!=current.LocalActor||room.MasterActor!=current.MasterActor
                    ||!room.Members.SequenceEqual(current.Members)||profileFingerprint!=fingerprint||rearm&&paused;
                if(changed)
                {
                    // A roll still finishing with the game's dice completes
                    // before a new session starts.
                    if(nativeHolding()||!paused&&protocol!=null&&protocol.Busy)
                    {
                        if(!paused) { Fail("The multiplayer roster, host or roll settings changed during a roll."); DrainOutbound(); }
                        return;
                    }
                    if(paused) log(NetworkLog.Line("session_rearmed","local_actor",current.LocalActor.ToString(CultureInfo.InvariantCulture)));
                    room=current; profileFingerprint=fingerprint; paused=false; rearm=false; failureReported=false; loggedSession=null; loggedReady=false; lastFailure=null;
                    protocol=new MultiplayerProtocol(current.LocalActor,current.MasterActor,current.Members,
                        current.LocalActor==current.MasterActor?Guid.NewGuid().ToString("N"):"", new ProtocolProfile(fingerprint),gateway.Now);
                    ClearIncoming();
                    log(NetworkLog.Line("session_started","local_actor",current.LocalActor.ToString(CultureInfo.InvariantCulture),
                        "master_actor",current.MasterActor.ToString(CultureInfo.InvariantCulture),"members",string.Join(",",current.Members.Select(id=>id.ToString(CultureInfo.InvariantCulture)).ToArray()),
                        "profile",fingerprint,"room_sha256",ProtocolProfile.Hash(current.Name??"")));
                }
                string callbackError;
                Incoming[] packets;
                lock(queueLock) { callbackError=callbackFailure; callbackFailure=null; packets=incoming.ToArray(); incoming.Clear(); }
                if(callbackError!=null) Fail(callbackError);
                if(protocol==null) return;
                foreach(var packet in packets)
                {
                    if(Array.IndexOf(room.Members,packet.Sender)<0) continue;
                    protocol.Receive(packet.Sender,packet.Bytes,gateway.Now);
                }
                protocol.Tick(gateway.Now);
                if(protocol.HasSession&&loggedSession!=protocol.SessionToken)
                {
                    loggedSession=protocol.SessionToken;
                    log(NetworkLog.Line("session_joined","session",loggedSession,"local_actor",LocalActor.ToString(CultureInfo.InvariantCulture),
                        "master_actor",MasterActor.ToString(CultureInfo.InvariantCulture),"profile",profileFingerprint));
                }
                if(protocol.Ready&&!loggedReady)
                {
                    loggedReady=true;
                    log(NetworkLog.Line("session_ready","session",protocol.SessionToken,"local_actor",LocalActor.ToString(CultureInfo.InvariantCulture),
                        "master_actor",MasterActor.ToString(CultureInfo.InvariantCulture),"members",string.Join(",",room.Members.Select(id=>id.ToString(CultureInfo.InvariantCulture)).ToArray()),
                        "profile",profileFingerprint,"room_sha256",ProtocolProfile.Hash(room.Name??""),"plugin_sha256",pluginHash,"game_sha256",gameHash));
                }
                DrainOutbound();
                if(protocol.Halted) Fail(protocol.FailureReason);
            }
            catch(Exception error) { Fail("The multiplayer roll transport failed: "+error.GetType().Name); }
        }

        static void ValidateRoom(MultiplayerRoom value)
        {
            if(value==null||value.Instance==null||value.Members==null||value.Members.Length<1||value.Members.Length>16
                ||value.LocalActor<=0||value.MasterActor<=0||value.Members.Any(id=>id<=0)
                ||value.Members.Distinct().Count()!=value.Members.Length||Array.IndexOf(value.Members,value.LocalActor)<0
                ||Array.IndexOf(value.Members,value.MasterActor)<0)
                throw new InvalidOperationException("Invalid multiplayer room membership.");
        }
        internal void Flush() { if(protocol!=null) { DrainOutbound(); if(protocol.Halted) Fail(protocol.FailureReason); } }
        void DrainOutbound()
        {
            if(protocol==null||room==null) return;
            // The pure protocol processes its own messages. Never loop them
            // back through Photon or turn a receipt into an inline callback.
            foreach(var send in protocol.DrainOutbound())
            {
                if(send.TargetActor==room.LocalActor||Array.IndexOf(room.Members,send.TargetActor)<0)
                { Fail("A multiplayer packet had an invalid recipient."); break; }
                if(!gateway.Send(send.TargetActor,send.Payload))
                { Fail("Photon could not send a multiplayer roll packet."); break; }
            }
        }
        internal void Fail(string reason)
        {
            if(disposed||paused) return;
            lastFailure=reason;
            if(protocol!=null&&!protocol.Halted) protocol.Halt(reason,gateway.Now);
            paused=true; rearm=false;
            if(!failureReported) {
                failureReported=true;
                log(NetworkLog.Line("halted","reason",reason,"detail",protocol==null?"":protocol.FailureDetail??"","outcome","manual dice paused; the game rolls",
                    "session",protocol==null?"":protocol.SessionToken,"local_actor",LocalActor.ToString(CultureInfo.InvariantCulture)));
            }
            // Tell the other players while still connected. Nobody leaves the
            // room. A send failure may call Fail again; paused prevents recursion.
            try { DrainOutbound(); } catch(Exception error) { log(NetworkLog.Line("halt_send_failed","type",error.GetType().Name)); }
        }
        // The players reloaded together (a location load): start a new
        // session once nothing is pending. A no-op unless paused.
        internal void RequestRearm() { if(paused) rearm=true; }
        void ClearIncoming() { lock(queueLock) { incoming.Clear(); callbackFailure=null; } }
        public void Dispose()
        {
            if(disposed) return;
            disposed=true;
            if(registered) { try { gateway.Unregister(this); } finally { registered=false; } }
            ClearIncoming(); protocol=null; room=null;
        }
    }

    internal static class NetworkLog
    {
        internal static string Line(string kind, params string[] fields)
        {
            if(fields.Length%2!=0) throw new ArgumentException("Network log fields must be key/value pairs.");
            var value=new System.Text.StringBuilder("MDR_NETWORK {\"utc\":").Append(Quote(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)))
                .Append(",\"event\":").Append(Quote(kind)).Append(",\"protocol\":").Append(MultiplayerProtocol.WireVersion.ToString(CultureInfo.InvariantCulture));
            for(int index=0;index<fields.Length;index+=2) value.Append(',').Append(Quote(fields[index])).Append(':').Append(Quote(fields[index+1]));
            return value.Append('}').ToString();
        }
        static string Quote(string input)
        {
            var value=new System.Text.StringBuilder("\"");
            foreach(char c in input??"")
                if(c=='"'||c=='\\') value.Append('\\').Append(c);
                else if(c<32) value.Append("\\u").Append(((int)c).ToString("x4",CultureInfo.InvariantCulture));
                else value.Append(c);
            return value.Append('"').ToString();
        }
    }
}
