using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using Photon.Realtime;
using ManualDiceRoller;

internal static class TransportTests
{
    static int assertions;
    static void Check(bool condition,string name) { assertions++; if(!condition) throw new Exception(name); }
    sealed class Wire { internal int Sender,Target; internal byte[] Bytes; }
    sealed class Hub
    {
        internal readonly Dictionary<int,Gateway> Gateways=new Dictionary<int,Gateway>();
        internal readonly List<Wire> Queue=new List<Wire>();
        internal readonly Dictionary<int,MultiplayerTransport> Nodes=new Dictionary<int,MultiplayerTransport>();
        internal readonly List<string> Logs=new List<string>();
        internal int[] Members; internal int Master=1; internal double Time; internal bool Holding;
        internal Hub(int count=2)
        {
            Members=Enumerable.Range(1,count).ToArray();
            foreach(int actor in Members)
            {
                var gateway=new Gateway(this,actor); Gateways.Add(actor,gateway);
                Nodes.Add(actor,new MultiplayerTransport(gateway,()=>gateway.Profile,Logs.Add,nativeHolding:()=>Holding));
            }
        }
        internal void Step(bool deliver=true)
        {
            Time+=.05;
            foreach(var pair in Nodes) pair.Value.Update();
            if(deliver)
            {
                var queued=Queue.ToArray(); Queue.Clear();
                foreach(var packet in queued)
                {
                    Gateway target;
                    if(Gateways.TryGetValue(packet.Target,out target)&&target.Receiver!=null&&target.Active)
                        target.Receiver.OnEvent(Event(packet.Sender,packet.Bytes));
                }
            }
        }
        internal void Pump(int rounds=100) { for(int i=0;i<rounds;i++) Step(); }
        internal void Start(ProtocolRollSpec spec)
        {
            foreach(var node in Nodes.Values) Check(node.Protocol.BeginRoll(spec,Time),"all nodes join roll");
            Pump(30);
        }
    }
    sealed class Gateway : IMultiplayerRoomGateway
    {
        internal readonly Hub Hub; internal readonly int Actor;
        internal object Instance=new object(); internal string Profile=ProtocolProfile.Hash("matching-build");
        internal bool Active=true,FailSend; internal int Registers,Unregisters,Leaves;
        internal IOnEventCallback Receiver;
        internal Gateway(Hub hub,int actor) { Hub=hub; Actor=actor; }
        public double Now { get { return Hub.Time; } }
        public bool TryGetRoom(out MultiplayerRoom result)
        {
            result=Active?new MultiplayerRoom { Instance=Instance,Name="fixture room",LocalActor=Actor,MasterActor=Hub.Master,Members=(int[])Hub.Members.Clone() }:null;
            return Active;
        }
        public void Register(IOnEventCallback target) { Registers++; Receiver=target; }
        public void Unregister(IOnEventCallback target) { Unregisters++; if(ReferenceEquals(target,Receiver)) Receiver=null; }
        public bool Send(int target,byte[] bytes)
        {
            if(FailSend) return false;
            Hub.Queue.Add(new Wire { Sender=Actor,Target=target,Bytes=(byte[])bytes.Clone() }); return true;
        }
        public bool LeaveRoom() { Leaves++; Active=false; return true; }
    }
    sealed class Owners : IPlayerControllerService
    { internal int Owner=2; public Dictionary<int,PlayerController> ControllersById {get;private set;}=new Dictionary<int,PlayerController> {
        {1,new PlayerController { Type=PlayerController.ControllerType.Human }},{2,new PlayerController { Type=PlayerController.ControllerType.Human }},
        {4242,new PlayerController { Type=PlayerController.ControllerType.AI }} };
      public int GetPlayerControllingHero(RulesetCharacter character) { return Owner; } }
    static EventData Event(int sender,byte[] payload,byte code=MultiplayerTransport.EventCode)
    {
        var value=new EventData { Code=code };
        value.Parameters[254]=sender; value.Parameters[245]=payload; return value;
    }
    static ProtocolRollSpec Spec(string name="attack",int owner=2,bool supported=true)
    { return new ProtocolRollSpec(name,0,ProtocolProfile.Hash("state"),owner,new[]{20,4},supported); }
    static void BasicTransport()
    {
        var fingerprint=typeof(MultiplayerRolls).GetMethod("ProfileFingerprint",BindingFlags.Static|BindingFlags.NonPublic);
        string enabled=(string)fingerprint.Invoke(null,null);Plugin.Enabled=false;
        Check(enabled!=(string)fingerprint.Invoke(null,null),"enabled and disabled peers cannot advertise an identical manual-roll profile");Plugin.Enabled=true;
        var hub=new Hub(4); hub.Pump();
        Check(hub.Nodes.Values.All(node=>node.Ready),"four native Photon callback interfaces handshake");
        Check(hub.Gateways.Values.All(gateway=>gateway.Registers==1),"registration once across frames");
        Check(hub.Logs.Count(line=>line.Contains("\"event\":\"session_ready\""))==4,"ready diagnostics once each peer");
        hub.Start(Spec());
        Check(hub.Nodes[2].Protocol.NeedsInput,"controlling actor gets input");
        Check(hub.Nodes.Where(pair=>pair.Key!=2).All(pair=>!pair.Value.Protocol.NeedsInput),"other peers wait");
        int[] faces={17,3}; Check(hub.Nodes[2].Protocol.SubmitFaces(faces,hub.Time),"native owner enters faces"); faces[0]=1;
        hub.Pump();
        foreach(var node in hub.Nodes.Values)
        {
            ProtocolOutcome result;
            Check(node.Protocol.TryTakeOutcome(out result),"all peers released identical result");
            Check(!result.Automatic&&result.Faces.SequenceEqual(new[]{17,3}),"input vector copied and unanimous");
            var copy=result.Faces; copy[0]=2; Check(result.Faces[0]==17,"outcome defensive copy");
        }
        foreach(var node in hub.Nodes.Values) { node.Dispose(); node.Dispose(); }
        Check(hub.Gateways.Values.All(gateway=>gateway.Unregisters==1),"callback teardown exactly once");
    }
    static void CallbackValidation()
    {
        var hub=new Hub(); hub.Pump(); var node=hub.Nodes[1];
        node.OnEvent(Event(2,new byte[]{1,2,3,4,5})); node.OnEvent(Event(2,new byte[]{0x4d,0x44,0x52,0x33,1},197));
        hub.Pump(2); Check(!node.Failed,"foreign payload/code ignored");
        node.OnEvent(Event(999,new byte[]{0x4d,0x44,0x52,0x33,1})); hub.Pump(2);
        Check(!node.Failed,"outsider sender filtered before decoder");
        hub.Nodes[1].Protocol.Tick(hub.Time+2);
        var message=hub.Nodes[1].Protocol.DrainOutbound().First(send=>send.TargetActor==2).Payload;
        hub.Nodes[2].OnEvent(Event(1,message)); message[4]=255;
        hub.Nodes[2].Update(); Check(!hub.Nodes[2].Failed,"callback deep copies payload before caller mutation");
        var huge=new byte[MultiplayerProtocol.MaxPacketBytes+1]; huge[0]=0x4d;huge[1]=0x44;huge[2]=0x52;huge[3]=0x33;
        node.OnEvent(Event(2,huge)); Check(hub.Gateways[1].Leaves==0,"callback copies only, no inline native leave");
        node.Update(); Check(node.Failed&&hub.Gateways[1].Leaves==0&&node.FailureReason.Contains("safe size"),"oversized protocol packet pauses manual dice on main update without leaving the room");
        var overflow=new Hub(); overflow.Pump();
        for(int index=0;index<513;index++) overflow.Nodes[1].OnEvent(Event(2,new byte[]{0x4d,0x44,0x52,0x33,1}));
        Check(overflow.Gateways[1].Leaves==0,"queue overflow no inline native work");
        overflow.Nodes[1].Update(); Check(overflow.Nodes[1].Failed&&overflow.Gateways[1].Leaves==0&&overflow.Nodes[1].FailureReason.Contains("queue overflowed"),"bounded queue overflow pauses manual dice");
    }
    static void RecoveryAndFailures()
    {
        var hub=new Hub(3); hub.Pump(); string previous=hub.Nodes[1].Protocol.SessionToken;
        hub.Members=new[]{1,2}; hub.Gateways[3].Active=false; hub.Pump();
        Check(hub.Nodes[1].Ready&&hub.Nodes[2].Ready,"idle departure renegotiates remaining roster");
        Check(hub.Nodes[1].Protocol.SessionToken!=previous,"new roster fresh session nonce");
        var roster=new Hub(); roster.Pump(); roster.Start(Spec()); roster.Members=new[]{1}; roster.Gateways[2].Active=false;
        roster.Nodes[1].Update(); Check(roster.Nodes[1].Failed&&roster.Gateways[1].Leaves==0,"active departure pauses manual dice rather than a unilateral manual roll");
        var settings=new Hub(); settings.Pump(); settings.Start(Spec()); settings.Gateways[1].Profile=ProtocolProfile.Hash("different settings");
        settings.Nodes[1].Update(); Check(settings.Nodes[1].Failed&&settings.Gateways[1].Leaves==0,"active settings changes pause manual dice");
        var holding=new Hub(); holding.Pump(); holding.Holding=true; holding.Gateways[1].Profile=ProtocolProfile.Hash("changed between native barriers");
        holding.Nodes[1].Update(); Check(holding.Nodes[1].Failed&&holding.Gateways[1].Leaves==0,"native action lease prevents idle session replacement between barriers");
        var send=new Hub(); send.Gateways[1].FailSend=true; send.Nodes[1].Update();
        Check(send.Nodes[1].Failed&&send.Gateways[1].Leaves==0,"Photon send refusal pauses manual dice");
        var stopped=new Hub(); stopped.Pump(); string stoppedSession=stopped.Nodes[1].Protocol.SessionToken;
        stopped.Nodes[1].Fail("fixture stop"); stopped.Nodes[1].Update();
        Check(stopped.Nodes[1].Failed&&stopped.Gateways[1].Leaves==0&&stopped.Nodes[1].Protocol.SessionToken==stoppedSession,"a paused session stays paused in the same room");
        stopped.Nodes[1].RequestRearm(); stopped.Holding=true; stopped.Nodes[1].Update();
        Check(stopped.Nodes[1].Failed,"a roll still finishing with the game's dice delays the fresh session");
        stopped.Holding=false; stopped.Nodes[1].Update();
        Check(!stopped.Nodes[1].Failed&&stopped.Nodes[1].Protocol.SessionToken!=stoppedSession&&stopped.Gateways[1].Leaves==0,"reloading together starts a fresh session in the same room");
        stopped.Nodes[2].RequestRearm(); stopped.Pump();
        Check(stopped.Nodes[2].Failed,"re-arming is a no-op for a peer that was never paused");
        stopped.Nodes[2].Fail("fixture stop"); stopped.Nodes[2].RequestRearm(); stopped.Pump();
        Check(stopped.Nodes[1].Ready&&stopped.Nodes[2].Ready,"both re-armed peers negotiate a fresh shared session");
        stoppedSession=stopped.Nodes[1].Protocol.SessionToken;
        stopped.Nodes[1].Fail("fixture stop"); stopped.Members=new[]{1}; stopped.Nodes[1].Update();
        Check(!stopped.Nodes[1].Failed&&stopped.Nodes[1].Protocol.SessionToken!=stoppedSession,"a roster change after a pause starts a fresh session");
        stoppedSession=stopped.Nodes[1].Protocol.SessionToken;
        stopped.Gateways[1].Active=false; stopped.Nodes[1].Update(); stopped.Gateways[1].Active=true; stopped.Gateways[1].Instance=new object(); stopped.Nodes[1].Update();
        Check(!stopped.Nodes[1].Failed&&stopped.Nodes[1].Protocol.SessionToken!=stoppedSession,"fresh observed room after native teardown can negotiate again");
        var slow=new Hub(); slow.Pump(); slow.Start(Spec()); slow.Time+=120; slow.Pump(2);
        Check(!slow.Nodes.Values.Any(node=>node.Failed),"human dice entry may take minutes");
        var disconnect=new Hub(); disconnect.Pump(); disconnect.Start(Spec()); disconnect.Gateways[1].Active=false;
        disconnect.Nodes[1].Update(); Check(disconnect.Nodes[1].Failed&&disconnect.Nodes[1].FailureReason.Contains("disconnected"),"unexpected room loss keeps failure after core teardown");
        disconnect.Holding=true; disconnect.Gateways[1].Active=true; disconnect.Gateways[1].Instance=new object(); disconnect.Nodes[1].Update();
        Check(disconnect.Nodes[1].Failed&&disconnect.Nodes[1].Protocol==null,"an old native action cannot attach to a reconnected room");
    }
    static void InstallFacade(Hub hub,int actor)
    {
        typeof(MultiplayerRolls).GetField("transport",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,hub.Nodes[actor]);
        typeof(MultiplayerRolls).GetField("occupied",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,false);
        RollWindow.Reset(); Plugin.Logs.Clear(); ServiceRepository.Players=new Owners();
    }
    static void OwnerFacade()
    {
        var hub=new Hub(); hub.Pump(); InstallFacade(hub,2);
        var hero=new RulesetCharacterHero { Guid=123 };
        Check(MultiplayerRolls.OwnerFor(hero)==2,"native character controller drives prompt ownership");
        Check(MultiplayerRolls.OwnerFor(new RulesetCharacter { IsSubstitute=true,OriginalFormCharacter=hero })==2,"wild shape keeps original player owner");
        Check(MultiplayerRolls.OwnerFor(new RulesetCharacter())==1,"NPC dice belong to host");
        ((Owners)ServiceRepository.Players).Owner=4242;
        Check(MultiplayerRolls.OwnerFor(hero)==1,"native AI controlled hero dice belong to host");
        ((Owners)ServiceRepository.Players).Owner=99; bool rejected=false;
        try { MultiplayerRolls.OwnerFor(hero); } catch(InvalidOperationException) { rejected=true; }
        Check(rejected,"absent controlling player rejected"); ((Owners)ServiceRepository.Players).Owner=2;
        bool completed=false,automatic=true; int[] accepted=null;
        var coroutine=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),hero,new RollPrompt(20,4),true,
            values=>accepted=values,value=>{ automatic=value; completed=true; });
        Check(hub.Nodes[1].Protocol.BeginRoll(Spec(),hub.Time),"host joins owner facade request");
        for(int index=0;index<100&&!RollWindow.Visible;index++) { Check(coroutine.MoveNext(),"owner request yields until prompt"); hub.Step(); }
        Check(RollWindow.Visible&&RollWindow.Displays==1&&RollWindow.Authorized,"owner prompt explicitly multiplayer authorized");
        Check(!completed,"native continuation held before input");
        RollWindow.Submit(19,4);
        Check(!completed,"input callback never resumes native coroutine inline");
        bool running=true;
        for(int index=0;index<100&&running;index++) { running=coroutine.MoveNext(); hub.Step(); }
        Check(!running&&completed&&!automatic&&accepted.SequenceEqual(new[]{19,4}),"owner facade completes after shared release");
        Check(!RollWindow.Visible&&RollWindow.Closes==1,"owned prompt retired on completion");
        Check(Plugin.Logs.Any(line=>line.Contains("\"event\":\"roll_completed\"")),"facade records committed outcome");
        var barrier=MultiplayerRolls.Rendezvous("postnative",-1,ProtocolProfile.Hash("state"),hero,null,true,values=>{},value=>{});
        Check(barrier.MoveNext(),"zero face barrier waits for peer"); ((IDisposable)barrier).Dispose();
        Check(hub.Nodes[2].Failed&&hub.Gateways[2].Leaves==0,"native cancellation pauses manual dice without leaving");
    }
    static void NonOwnerFacade()
    {
        var hub=new Hub(); hub.Pump(); InstallFacade(hub,1); var hero=new RulesetCharacterHero();
        bool complete=false; int[] accepted=null;
        var coroutine=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),hero,new RollPrompt(20,4),true,values=>accepted=values,value=>complete=true);
        Check(hub.Nodes[2].Protocol.BeginRoll(Spec(),hub.Time),"owner remote node joins");
        for(int index=0;index<100&&!hub.Nodes[2].Protocol.NeedsInput;index++) { Check(coroutine.MoveNext(),"non owner awaits remote input"); hub.Step(); }
        Check(RollWindow.Displays==0&&!complete&&RollWindow.Waiting==1,"non owner sees waiting banner without entry UI");
        hub.Nodes[2].Protocol.SubmitFaces(new[]{7,2},hub.Time);
        bool running=true; for(int index=0;index<100&&running;index++) { running=coroutine.MoveNext(); hub.Step(); }
        Check(complete&&accepted.SequenceEqual(new[]{7,2})&&RollWindow.Displays==0&&RollWindow.WaitingCloses==1,"non owner resumes with remote raw vector and clears owned waiting banner");
    }
    static void MissingPeer()
    {
        var hub=new Hub(); InstallFacade(hub,1); hub.Nodes[1].Update();
        var hero=new RulesetCharacterHero(); ((Owners)ServiceRepository.Players).Owner=1;
        bool complete=false;
        var coroutine=MultiplayerRolls.Rendezvous("attack",-1,ProtocolProfile.Hash("state"),hero,null,false,values=>{},value=>complete=true);
        Check(coroutine.MoveNext(),"missing peer barrier yields");
        hub.Time+=31; Check(!coroutine.MoveNext(),"missing peer has bounded admission wait");
        Check(hub.Nodes[1].Failed&&hub.Gateways[1].Leaves==0&&!complete,"missing build pauses manual dice; no unilateral decision is released");
    }
    static void OwnershipChanges()
    {
        foreach(bool useAuto in new[]{false,true})
        {
            var hub=new Hub();hub.Pump();InstallFacade(hub,2);
            var hero=new RulesetCharacterHero {Guid=777};bool completed=false;
            var roll=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),hero,new RollPrompt(20,4),true,
                values=>{},value=>completed=true);
            hub.Nodes[1].Protocol.BeginRoll(Spec(),hub.Time);
            for(int frame=0;frame<100&&!RollWindow.Visible;frame++){roll.MoveNext();hub.Step();}
            Check(RollWindow.Visible,"owner-change reproduction opens original controller's prompt");
            ((Owners)ServiceRepository.Players).Owner=1;
            if(useAuto)RollWindow.Automatic();else RollWindow.Submit(19,4);
            Check(hub.Nodes[2].Failed&&!completed,"a stale controller cannot submit manual or Auto input after native ownership changes");
            Check(hub.Gateways[2].Leaves==0&&!FacadeOccupied(),"owner transfer retires its own modal and pauses manual dice without leaving");
            ((IDisposable)roll).Dispose();
        }
        var waiting=new Hub();waiting.Pump();InstallFacade(waiting,1);
        var remoteHero=new RulesetCharacterHero();bool finished=false;
        var observing=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),remoteHero,new RollPrompt(20,4),true,
            values=>{},value=>finished=true);
        waiting.Nodes[2].Protocol.BeginRoll(Spec(),waiting.Time);
        for(int frame=0;frame<100&&!waiting.Nodes[2].Protocol.NeedsInput;frame++){observing.MoveNext();waiting.Step();}
        Check(RollWindow.Waiting==1,"observing peer has original owner's waiting banner");
        ((Owners)ServiceRepository.Players).Owner=1;
        Check(!observing.MoveNext()&&waiting.Nodes[1].Failed&&!finished,"observing peer rejects an ownership transfer before accepting a remote result");
        Check(RollWindow.WaitingCloses==1,"owner-transfer failure retires the exact observer banner");
    }
    static bool FacadeOccupied()
    { return (bool)typeof(MultiplayerRolls).GetField("occupied",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null); }
    static void OrphanedChildren()
    {
        var hub=new Hub(); hub.Pump(); InstallFacade(hub,2); var hero=new RulesetCharacterHero();
        var old=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),hero,new RollPrompt(20,4),true,values=>{},value=>{});
        hub.Nodes[1].Protocol.BeginRoll(Spec(),hub.Time);
        for(int index=0;index<100&&!RollWindow.Visible;index++) { old.MoveNext(); hub.Step(); }
        Check(RollWindow.Visible&&FacadeOccupied(),"old native nested facade owns modal and lock");
        // Reproduce TA.Reset: its parent is canceled, but this yielded child
        // is removed from the stack without calling its Dispose method.
        MultiplayerRolls.Fail("Native parent was reset without disposing child");
        Check(!RollWindow.Visible&&!FacadeOccupied()&&RollWindow.Closes==1,"shared stop synchronously retires orphan child modal and lock");
        foreach(var gateway in hub.Gateways.Values) gateway.Active=false;
        foreach(var node in hub.Nodes.Values) node.Update(); hub.Queue.Clear();
        foreach(var gateway in hub.Gateways.Values) { gateway.Active=true; gateway.Instance=new object(); }
        hub.Pump(); Check(hub.Nodes.Values.All(node=>node.Ready),"room can rejoin after orphan cancellation");
        var next=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),hero,new RollPrompt(20,4),true,values=>{},value=>{});
        hub.Nodes[1].Protocol.BeginRoll(Spec(),hub.Time);
        for(int index=0;index<100&&!RollWindow.Visible;index++) { next.MoveNext(); hub.Step(); }
        Check(RollWindow.Visible&&FacadeOccupied(),"new request opens after old orphan cleanup");
        ((IDisposable)old).Dispose();
        Check(RollWindow.Visible&&FacadeOccupied()&&!hub.Nodes[2].Failed,"stale child finally cannot close or unlock new request");
        ((IDisposable)next).Dispose(); Check(!RollWindow.Visible&&!FacadeOccupied(),"current child cancellation retires its own resources");

        var waiting=new Hub(); waiting.Pump(); InstallFacade(waiting,1);
        var orphan=MultiplayerRolls.Rendezvous("attack",0,ProtocolProfile.Hash("state"),hero,new RollPrompt(20,4),true,values=>{},value=>{});
        waiting.Nodes[2].Protocol.BeginRoll(Spec(),waiting.Time);
        for(int index=0;index<50&&RollWindow.Waiting==0;index++) { orphan.MoveNext(); waiting.Step(); }
        Check(RollWindow.Waiting==1&&FacadeOccupied(),"remote request has owned cosmetic waiting banner");
        MultiplayerRolls.Reset();
        Check(RollWindow.WaitingCloses==1&&!FacadeOccupied(),"reset retires orphan waiting banner without child finally");
        ((IDisposable)orphan).Dispose(); Check(RollWindow.WaitingCloses==1,"late orphan disposal does not repeat owned cleanup");
    }
    public static int Main()
    {
        try { string preflightError; Check(MultiplayerRolls.Preflight(out preflightError),"loaded genuine Photon assemblies match pinned hashes");
            BasicTransport(); CallbackValidation(); RecoveryAndFailures(); OwnerFacade(); NonOwnerFacade(); MissingPeer(); OrphanedChildren(); OwnershipChanges();
            Console.WriteLine("Network transport/facade: "+assertions+" assertions passed; genuine Photon EventData/ABI, queued gateway fixture."); return 0; }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
