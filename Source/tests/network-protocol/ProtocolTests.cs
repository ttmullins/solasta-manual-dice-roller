using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ManualDiceRoller;

static class ProtocolTests
{
    static int assertions, schedules;
    static readonly string Profile = ProtocolProfile.Hash("artifact/game/settings/capabilities/exact");
    static readonly string Signature = ProtocolProfile.Hash("native action and ordered roll feature descriptor");
    static void Check(bool value, string message) { ++assertions; if (!value) throw new Exception(message); }
    static void Throws(Action action, string message) { bool threw = false; try { action(); } catch (ArgumentException) { threw = true; } Check(threw,message); }
    sealed class Envelope
    {
        public int Sender, Target; public byte[] Bytes;
        public int Kind { get { return Bytes[5]; } }
        public Envelope Clone() { return new Envelope {Sender=Sender,Target=Target,Bytes=(byte[])Bytes.Clone()}; }
    }
    sealed class Network
    {
        public readonly Dictionary<int, MultiplayerProtocol> Nodes = new Dictionary<int, MultiplayerProtocol>();
        public readonly List<Envelope> Queue = new List<Envelope>();
        public readonly List<Envelope> History = new List<Envelope>();
        public readonly int[] Members;
        public double Now;
        public readonly Random Random;
        public bool Chaotic;
        public Func<Envelope,bool> Drop;
        public Network(int count, int seed=1, string token="epoch-1", string[] profiles=null)
        {
            Members=Enumerable.Range(1,count).ToArray(); Random=new Random(seed);
            foreach (int actor in Members) Nodes.Add(actor,new MultiplayerProtocol(actor,1,Members,actor==1?token:"",new ProtocolProfile(profiles==null?Profile:profiles[actor-1]),0));
            Drain();
        }
        public void Drain()
        {
            foreach (var pair in Nodes) foreach(var send in pair.Value.DrainOutbound()) {
                var env=new Envelope{Sender=pair.Key,Target=send.TargetActor,Bytes=send.Payload};
                History.Add(env.Clone());
                Check(send.TargetActor!=pair.Key,"self messages must never require transport loopback");
                Check(env.Bytes.Length<=MultiplayerProtocol.MaxPacketBytes,"outgoing packets bounded");
                Queue.Add(env);
            }
        }
        public void Deliver(Envelope env) { Nodes[env.Target].Receive(env.Sender,env.Bytes,Now); Drain(); }
        public void Frame()
        {
            int budget=Queue.Count+8;
            while(Queue.Count>0 && budget-->0) {
                int index=Chaotic?Random.Next(Queue.Count):0;
                var env=Queue[index]; Queue.RemoveAt(index);
                if(Drop!=null&&Drop(env))continue;
                if(Chaotic&&Random.Next(10)==0)continue;
                if(Chaotic&&Random.Next(7)==0)Queue.Add(env.Clone());
                Deliver(env);
            }
            Now+=0.2;
            foreach(var node in Nodes.Values)node.Tick(Now);
            Drain();
        }
        public void Until(Func<bool> condition, string message, int frames=500)
        {
            for(int i=0;i<frames&&!condition();++i)Frame();
            Check(condition(),message+" phases="+String.Join(",",Nodes.Values.Select(x=>x.Phase+":"+x.FailureReason)));
        }
        public void Handshake() { Until(()=>Nodes.Values.All(x=>x.Ready),"complete exact-profile handshake"); }
        public void Begin(Func<int,ProtocolRollSpec> factory)
        { foreach(int actor in Members)Check(Nodes[actor].BeginRoll(factory(actor),Now),"local native request enters rendezvous");Drain(); }
        public void Input(int actor,int[] faces)
        { Until(()=>Nodes[actor].NeedsInput,"only controlling owner gets input request");Check(Nodes[actor].SubmitFaces(faces,Now),"valid owner input accepted");Drain(); }
        public Dictionary<int,ProtocolOutcome> Finish()
        {
            var results=new Dictionary<int,ProtocolOutcome>();
            Until(()=>{
                foreach(var pair in Nodes) {
                    ProtocolOutcome result;
                    if(pair.Value.TryTakeOutcome(out result)) {
                        Check(!results.ContainsKey(pair.Key),"exactly one release per peer");results.Add(pair.Key,result);
                        foreach(var other in Nodes.Values)Check(other.Phase=="committed"||other.Phase=="released"||other.Phase=="ready","release requires every participant already committed");
                    }
                }
                return results.Count==Members.Length;
            },"all peers receive one identical committed outcome");
            Until(()=>Nodes.Values.All(x=>x.CanBeginRoll),"every participant acknowledges release before the next host request");
            return results;
        }
    }
    static ProtocolRollSpec Spec(int owner=2,int ordinal=0,int[] dice=null,bool manual=true,string action="native/action-1",string signature=null,bool fallback=false)
    {return new ProtocolRollSpec(action,ordinal,signature??Signature,owner,dice??new[]{20},manual,fallback);}
    static bool Same(int[] a,int[] b){return a.SequenceEqual(b);}
    static Envelope Find(Network n,int kind,int sender=0,int target=0)
    {return n.History.Last(x=>x.Kind==kind&&(sender==0||x.Sender==sender)&&(target==0||x.Target==target)).Clone();}

    static void ConstructorsAndIsolation()
    {
        Throws(()=>new ProtocolProfile("bad"),"reject noncanonical profile");
        Throws(()=>new ProtocolProfile(Profile.ToUpperInvariant()),"reject uppercase digest spelling");
        Throws(()=>new MultiplayerProtocol(1,1,new[]{1,1},"epoch",new ProtocolProfile(Profile),0),"duplicate roster rejected");
        Throws(()=>new MultiplayerProtocol(3,1,new[]{1,2},"epoch",new ProtocolProfile(Profile),0),"outsider local actor rejected");
        Throws(()=>new MultiplayerProtocol(1,1,new[]{1},"",new ProtocolProfile(Profile),0),"empty master epoch rejected");
        Throws(()=>new MultiplayerProtocol(1,1,new[]{1},"epoch",new ProtocolProfile(Profile),Double.NaN),"invalid clock rejected");
        Throws(()=>Spec(dice:new int[513]),"unbounded request rejected");
        Throws(()=>Spec(dice:new[]{0}),"zero-sided dice rejected");
        Throws(()=>Spec(dice:new[]{1001}),"unsupported die maximum rejected");
        Throws(()=>Spec(action:new string('x',257)),"oversized action identity rejected");
        Throws(()=>Spec(action:new string('\u00e9',129)),"action identity bounded in UTF8 bytes");
        Throws(()=>Spec(fallback:true),"fallback descriptor relaxation forbidden on real dice");
        var dice=new[]{20,4};var spec=Spec(dice:dice);dice[0]=1;var copy=spec.DieFaces;copy[1]=1;
        Check(Same(spec.DieFaces,new[]{20,4}),"request die faces are immutable");
        var roster=new[]{1,2};var node=new MultiplayerProtocol(1,1,roster,"epoch",new ProtocolProfile(Profile),0);roster[1]=99;
        var sends=node.DrainOutbound();Check(sends.All(x=>x.TargetActor==2),"roster immutable");
        var payload=sends[0].Payload;payload[0]=0;Check(MultiplayerProtocol.IsProtocolPacket(sends[0].Payload),"outgoing packets are immutable");
    }
    static void BasicManualAndAutomatic()
    {
        var n=new Network(4);n.Handshake();n.Begin(a=>Spec(owner:3,dice:new[]{20,20,4}));
        n.Until(()=>n.Nodes[3].NeedsInput,"owner offered input");
        foreach(var pair in n.Nodes)Check(pair.Value.NeedsInput==(pair.Key==3),"single authorized input owner");
        Check(!n.Nodes[2].SubmitFaces(new[]{1,20,4},n.Now),"nonowner cannot submit");
        Check(!n.Nodes[3].SubmitFaces(new[]{0,20,4},n.Now),"face below die range rejected");
        Check(!n.Nodes[3].SubmitFaces(new[]{1,21,4},n.Now),"face above die range rejected");
        Check(!n.Nodes[3].SubmitFaces(new[]{20},n.Now),"wrong face count rejected");
        int[] submitted={1,20,4};Check(n.Nodes[3].SubmitFaces(submitted,n.Now),"owner submits ordered original faces");submitted[0]=19;n.Drain();
        Check(!n.Nodes[3].SubmitFaces(new[]{19,20,4},n.Now),"owner cannot revise submitted input");
        var result=n.Finish();
        foreach(var value in result.Values){Check(!value.Automatic&&Same(value.Faces,new[]{1,20,4}),"all peers release identical original/advantage/bonus faces");var f=value.Faces;f[0]=2;Check(value.Faces[0]==1,"outcome faces immutable");}
        n.Begin(a=>Spec(owner:3,ordinal:1,dice:new[]{20},signature:ProtocolProfile.Hash("Lucky feature replacement/first face")));
        n.Input(3,new[]{17});result=n.Finish();Check(result.Values.All(x=>Same(x.Faces,new[]{17})&&x.Sequence==2),"Lucky replacement is a distinct ordered ticket");
        n.Begin(a=>Spec(owner:3,ordinal:2,dice:new[]{8},manual:a!=4));result=n.Finish();Check(result.Values.All(x=>x.Automatic&&x.Faces.Length==0),"any unsupported peer coordinates automatic before input");
        n.Begin(a=>Spec(owner:2,ordinal:3,dice:new[]{6}));n.Until(()=>n.Nodes[2].NeedsInput,"owner offered fourth roll");Check(n.Nodes[2].RequestAutomatic(n.Now),"owner may request shared automatic");n.Drain();result=n.Finish();Check(result.Values.All(x=>x.Automatic),"automatic button is a shared committed decision");
    }
    static void HandshakeAndEarlyRoll()
    {
        var n=new Network(3);
        Check(n.Nodes[1].BeginRoll(Spec(owner:1,dice:new int[0],fallback:true),n.Now),"master stages roll before handshake completes");n.Drain();
        n.Until(()=>n.Nodes[2].HasSession&&n.Nodes[3].HasSession,"followers learn epoch only from master");
        Check(n.Nodes[2].BeginRoll(Spec(owner:1,dice:new int[0],fallback:true),n.Now),"follower stages before local readiness");
        Check(n.Nodes[3].BeginRoll(Spec(owner:1,dice:new int[0],fallback:true),n.Now),"last follower stages admission");n.Drain();
        var result=n.Finish();Check(result.Values.All(x=>!x.Automatic&&x.Faces.Length==0),"zero-face all-ready barrier succeeds without input UI");
        var mismatch=new Network(3,profiles:new[]{Profile,ProtocolProfile.Hash("different game/settings/build"),Profile});
        mismatch.Until(()=>mismatch.Nodes.Values.All(x=>x.Halted),"profile mismatch blocks entire manual session");
        Check(mismatch.Nodes.Values.All(x=>!x.Ready),"mismatched profile never becomes ready");
        // 0.8.4.1 (fourth live session): a client whose mod differs halts on
        // the host's first hello, before the host ever reads the client's. The
        // halt carries its reason's digest: every computer now names it.
        Check(mismatch.Nodes.Values.All(x=>MultiplayerProtocol.IsProfileMismatch(x.FailureReason)),"every peer reports a version/settings mismatch: "+String.Join(" | ",mismatch.Nodes.Values.Select(x=>x.FailureReason)));
        Check(mismatch.Nodes.Values.Any(x=>x.FailureDetail.Contains("profile "+Profile)&&x.FailureDetail.Contains(ProtocolProfile.Hash("different game/settings/build"))),"the detecting peer logs both profiles");
        var pair=new Network(2,profiles:new[]{Profile,ProtocolProfile.Hash("client on another version")});
        pair.Until(()=>pair.Nodes.Values.All(x=>x.Halted),"a two-player version mismatch halts both");
        Check(pair.Nodes[1].FailureReason==MultiplayerProtocol.RemoteHaltPrefix+MultiplayerProtocol.ProfileMismatch&&pair.Nodes[2].FailureReason==MultiplayerProtocol.ProfileMismatch,
            "the host learns why the client stopped manual dice: "+pair.Nodes[1].FailureReason);
        var custom=new Network(2);custom.Handshake();custom.Nodes[2].Abort("A reason only this build knows: "+Guid.NewGuid());
        custom.Until(()=>custom.Nodes[1].Halted,"an unknown halt reason still stops the other peer");
        Check(custom.Nodes[1].FailureReason==MultiplayerProtocol.RemoteHalt,"an unrecognized reason keeps the generic report");
        Check(MultiplayerProtocol.KnownHaltReason(ProtocolProfile.Hash("Timed out waiting for the complete multiplayer roll barrier."))=="Timed out waiting for the complete multiplayer roll barrier."&&MultiplayerProtocol.KnownHaltReason("")==null,"known reasons map from their digest only");
        var missing=new Network(2);missing.Queue.Clear();missing.Now=100;foreach(var node in missing.Nodes.Values)node.Tick(missing.Now);
        Check(missing.Nodes.Values.All(x=>!x.Halted&&!x.Ready),"idle missing peer has no destructive timeout");
        Check(missing.Nodes[1].BeginRoll(Spec(owner:1),missing.Now),"strict actual roll enters bounded barrier even before readiness");missing.Now+=31;missing.Nodes[1].Tick(missing.Now);Check(missing.Nodes[1].Halted,"active strict missing-peer roll halts safely");
    }
    static void AdmissionAndDivergence()
    {
        var n=new Network(3);n.Handshake();n.Begin(a=>Spec(owner:1,ordinal:-1,dice:new int[0],signature:a==3?ProtocolProfile.Hash("different prepared plan"):Signature,fallback:true));
        var result=n.Finish();Check(result.Values.All(x=>x.Automatic&&x.Faces.Length==0),"different prepared plans vote shared automatic only in zero-face admission");
        n.Begin(a=>Spec(owner:1,ordinal:0,dice:new int[0],manual:a!=2,fallback:true));result=n.Finish();Check(result.Values.All(x=>x.Automatic),"invalidated final native handoff discards manual plan on every peer");
        var bad=new Network(3);bad.Handshake();bad.Begin(a=>Spec(signature:a==3?ProtocolProfile.Hash("changed real roll"):Signature));bad.Until(()=>bad.Nodes.Values.All(x=>x.Halted),"real roll descriptor divergence never consumes mismatched faces");
        foreach(var node in bad.Nodes.Values){ProtocolOutcome o;Check(!node.TryTakeOutcome(out o),"divergent descriptor releases no native continuation");}
        var wrongAction=new Network(2);wrongAction.Handshake();wrongAction.Begin(a=>Spec(owner:1,dice:new int[0],action:a==1?"action A":"action B",fallback:true));wrongAction.Until(()=>wrongAction.Nodes.Values.All(x=>x.Halted),"different native actions cannot use relaxed automatic admission");
    }
    static void HumanWaitAndBarriers()
    {
        var n=new Network(3);n.Handshake();n.Begin(a=>Spec(owner:2));n.Until(()=>n.Nodes[2].NeedsInput,"owner input pending");
        n.Now+=600;foreach(var node in n.Nodes.Values)node.Tick(n.Now);n.Drain();Check(n.Nodes.Values.All(x=>!x.Halted),"human physical dice input has no thirty-second timeout");
        Check(n.Nodes[2].SubmitFaces(new[]{7},n.Now),"input accepted after ten minutes");n.Drain();
        n.Drop=e=>e.Kind==12&&e.Sender==3; // CommitAck from one participant.
        n.Until(()=>n.Nodes.Values.All(x=>x.Phase=="committed"),"all peers prepare identical committed decision");
        for(int i=0;i<20;++i)n.Frame();
        foreach(var node in n.Nodes.Values){ProtocolOutcome o;Check(!node.TryTakeOutcome(out o),"no native continuation before last commit acknowledgment");}
        n.Drop=null;var results=n.Finish();Check(results.Values.All(x=>x.Faces[0]==7),"lost commit acknowledgment retransmits and releases identical dice");
        var withheld=new Network(3);withheld.Handshake();withheld.Begin(a=>Spec(owner:2));withheld.Input(2,new[]{9});withheld.Drop=e=>e.Kind==13&&e.Target==3;
        withheld.Until(()=>withheld.Nodes[1].Phase=="released"&&withheld.Nodes[2].Phase=="released","host and first follower receive release");
        Check(withheld.Nodes[3].Phase=="committed","late release leaves participant committed with same immutable decision");
        ProtocolOutcome first;Check(withheld.Nodes[1].TryTakeOutcome(out first),"host consumes first release");
        Check(!withheld.Nodes[1].BeginRoll(Spec(ordinal:1),withheld.Now),"master cannot begin next ticket until every release acknowledged");
        withheld.Drop=null;withheld.Until(()=>withheld.Nodes[3].Phase=="released","lost release retransmits");
        ProtocolOutcome second;Check(!withheld.Nodes[1].TryTakeOutcome(out second),"release retransmission does not duplicate host outcome");
    }
    static void SenderValidationAndMalformed()
    {
        var n=new Network(3);n.Handshake();n.Begin(a=>Spec(owner:2));n.Until(()=>n.Nodes[2].NeedsInput,"authorized owner waits");Check(n.Nodes[2].SubmitFaces(new[]{8},n.Now),"capture legitimate owner packet");n.Drain();
        var input=Find(n,8,2,1);n.Queue.RemoveAll(e=>e.Kind==8);input.Sender=3;n.Deliver(input);
        Check(n.Nodes[1].Phase=="input","unauthorized peer cannot submit another hero's dice");
        var offer=Find(n,7,1,3);offer.Sender=2;n.Deliver(offer);Check(n.Nodes[3].Phase=="input","nonmaster cannot impersonate coordinator");
        var junk=new byte[]{0x4d,0x44,0x52,0x33,1};n.Nodes[1].Receive(99,junk,n.Now);Check(!n.Nodes[1].Halted,"outsider malformed packet ignored");
        n.Nodes[1].Receive(3,new byte[]{1,2,3},n.Now);Check(!n.Nodes[1].Halted,"event-code collision foreign packet ignored");
        n.Nodes[1].Receive(3,junk,n.Now);Check(n.Nodes[1].Halted,"authenticated malformed protocol packet blocks unsafe continuation");n.Drain();n.Until(()=>n.Nodes.Values.All(x=>x.Halted),"malformed protocol fault is coordinated");
        var fresh=new Network(3);var hello=Find(fresh,1,1,2);hello.Sender=3;fresh.Deliver(hello);Check(!fresh.Nodes[2].HasSession,"follower cannot bind session from nonmaster hello");
        fresh.Handshake();var oversized=new byte[MultiplayerProtocol.MaxPacketBytes+1];Array.Copy(junk,oversized,junk.Length);fresh.Nodes[2].Receive(1,oversized,fresh.Now);Check(fresh.Nodes[2].Halted,"oversized authenticated packet bounded before decode allocation");
        var duplicates=new Network(4);duplicates.Handshake();var welcomeAck=Find(duplicates,4,2,1);
        for(int i=0;i<100;++i)duplicates.Nodes[1].Receive(2,welcomeAck.Bytes,duplicates.Now);
        var responses=duplicates.Nodes[1].DrainOutbound();
        Check(!duplicates.Nodes[1].Halted&&responses.Length==100&&responses.All(x=>x.TargetActor==2),"repeated completed handshake acknowledgments never fan out to the entire room");
        var trailing=new Network(2);trailing.Handshake();var valid=Find(trailing,5,1,2);valid.Bytes=valid.Bytes.Concat(new byte[]{0}).ToArray();trailing.Deliver(valid);Check(trailing.Nodes[2].Halted,"trailing protocol bytes rejected");
        var wrongVersion=new Network(2);wrongVersion.Handshake();valid=Find(wrongVersion,5,1,2);valid.Bytes[4]=99;wrongVersion.Deliver(valid);Check(wrongVersion.Nodes[2].Halted,"unknown protocol version rejected");
    }
    static void StaleMembershipAndTimeout()
    {
        var n=new Network(2);n.Handshake();n.Begin(a=>Spec());n.Input(2,new[]{12});n.Finish();var oldInput=Find(n,8,2,1);var oldRelease=Find(n,13,1,2);
        n.Begin(a=>Spec(ordinal:1));n.Deliver(oldInput);n.Deliver(oldRelease);Check(n.Nodes.Values.All(x=>!x.Halted),"stale prior ticket packets ignored/idempotently acknowledged");n.Input(2,new[]{13});var result=n.Finish();Check(result.Values.All(x=>x.Faces[0]==13),"stale input cannot change new ticket");
        var newRoom=new Network(2,token:"fresh-room-epoch");newRoom.Handshake();newRoom.Deliver(oldInput);newRoom.Deliver(oldRelease);Check(newRoom.Nodes.Values.All(x=>!x.Busy&&!x.Halted),"old-room packets cannot revive old faces");
        newRoom.Begin(a=>Spec());newRoom.Nodes[1].MembershipChanged(2,new[]{1,2});newRoom.Drain();newRoom.Until(()=>newRoom.Nodes.Values.All(x=>x.Halted),"master migration during uncommitted request stops entire pending operation");
        var idle=new Network(3);idle.Handshake();idle.Nodes[1].MembershipChanged(1,new[]{3,2,1});Check(!idle.Nodes[1].Halted,"roster ordering alone does not alter epoch");idle.Nodes[1].MembershipChanged(1,new[]{1,2});Check(idle.Nodes[1].Halted,"disconnect invalidates exact session roster");
        var timeout=new Network(3);timeout.Handshake();timeout.Begin(a=>Spec());timeout.Input(2,new[]{10});timeout.Drop=e=>e.Kind==10&&e.Sender==3;timeout.Until(()=>timeout.Nodes.Values.All(x=>x.Halted),"missing proposal acknowledgment causes coordinated bounded halt");
        foreach(var node in timeout.Nodes.Values){ProtocolOutcome o;Check(!node.TryTakeOutcome(out o),"timeout has no unilateral automatic/native continuation");}
    }
    static void BudgetsAndSinglePeer()
    {
        var n=new Network(2);n.Handshake();var dice=Enumerable.Repeat(1,512).ToArray();
        for(int i=0;i<8;++i){int ordinal=i;n.Begin(a=>Spec(owner:2,ordinal:ordinal,dice:dice));n.Input(2,dice);n.Finish();}
        Check(!n.Nodes[1].BeginRoll(Spec(owner:2,ordinal:8,dice:new[]{1}),n.Now),"4097th face rejected before input/native draw");n.Drain();n.Until(()=>n.Nodes.Values.All(x=>x.Halted),"total action budget rejection coordinated");
        var repeated=new Network(2);repeated.Handshake();
        for(int action=0;action<2;++action){
            repeated.Begin(a=>Spec(owner:1,ordinal:-1,dice:new int[0],action:"repeated-same-site",fallback:true));repeated.Finish();
            for(int i=0;i<8;++i){int ordinal=i;repeated.Begin(a=>Spec(owner:2,ordinal:ordinal,dice:dice,action:"repeated-same-site"));repeated.Input(2,dice);repeated.Finish();}
        }
        Check(repeated.Nodes.Values.All(x=>!x.Halted),"separate4096-face native actions at the same site have independent budgets");
        var one=new Network(1);one.Handshake();one.Begin(a=>Spec(owner:1,dice:new int[0],fallback:true));var result=one.Finish();Check(result[1].Faces.Length==0&&!result[1].Automatic,"one-peer zero-face barrier handles synchronous self transition");
        one.Begin(a=>Spec(owner:1,ordinal:1,dice:new[]{6}));one.Input(1,new[]{6});result=one.Finish();Check(result[1].Faces[0]==6,"one-peer manual path preserves owner and commit barriers");
    }
    static void DelayedHandshakeFromOldRoom()
    {
        var old=new Network(3,token:"old-bootstrap");old.Handshake();
        var fresh=new Network(3,token:"new-bootstrap");
        foreach(var packet in old.History.Where(e=>e.Target==2&&(e.Kind==1||e.Kind==3||e.Kind==5)).ToArray())
            fresh.Nodes[2].Receive(packet.Sender,packet.Bytes,fresh.Now);
        Check(!fresh.Nodes[2].Ready,"a fresh participant must not accept an old Hello/Welcome/Start transcript before its current handshake");
        Check(fresh.Nodes[2].BeginRoll(Spec(owner:2),fresh.Now),"native admission may wait while a provisional bootstrap is replaced");
        fresh.Drain();fresh.Handshake();
        Check(fresh.Nodes.Values.All(n=>n.SessionToken=="new-bootstrap"),"current master heartbeat replaces an obsolete provisional session");
        Check(fresh.Nodes[1].BeginRoll(Spec(owner:2),fresh.Now)&&fresh.Nodes[3].BeginRoll(Spec(owner:2),fresh.Now),"remaining peers enter the fresh matching action");
        fresh.Drain();fresh.Input(2,new[]{18});var result=fresh.Finish();
        Check(result.Values.All(x=>x.Faces[0]==18&&!x.Automatic),"stale bootstrap cannot strand or answer the fresh native admission");
        foreach(var packet in old.History.Where(e=>e.Target==2&&(e.Kind==1||e.Kind==3||e.Kind==5)).ToArray())
            fresh.Nodes[2].Receive(packet.Sender,packet.Bytes,fresh.Now);
        Check(fresh.Nodes[2].Ready&&fresh.Nodes[2].SessionToken=="new-bootstrap"&&!fresh.Nodes[2].Halted,"an established fresh session cannot be rebound by delayed old handshake packets");
    }
    static void AdversarialSchedules()
    {
        for(int seed=1;seed<=80;++seed){
            var n=new Network(seed%2==0?4:2,seed);n.Chaotic=true;n.Handshake();
            for(int round=0;round<6;++round){
                int owner=1+round%n.Members.Length;int ordinal=round;var dice=new[]{20,4,8};var faces=new[]{1+(seed+round)%20,1+round%4,1+seed%8};
                n.Begin(a=>Spec(owner:owner,ordinal:ordinal,dice:dice,action:"scheduled/action-"+(round/3)));
                n.Input(owner,faces);var result=n.Finish();
                Check(result.Values.All(x=>!x.Automatic&&Same(x.Faces,faces)&&x.Sequence==round+1),"reordering/loss/duplication cannot change owner faces or order");
                Check(n.Nodes.Values.All(x=>!x.Halted),"bounded adverse delivery recovers without unsafe fallback");
                ++schedules;
            }
        }
    }
    static int Main()
    {
        ConstructorsAndIsolation();BasicManualAndAutomatic();HandshakeAndEarlyRoll();AdmissionAndDivergence();HumanWaitAndBarriers();SenderValidationAndMalformed();StaleMembershipAndTimeout();BudgetsAndSinglePeer();AdversarialSchedules();DelayedHandshakeFromOldRoom();
        Console.WriteLine("PASS multiplayer protocol: "+assertions+" assertions; "+schedules+" multiparty randomized request schedules.");
        Console.WriteLine("Scope: production core on .NET 8 with simulated authenticated transport; no live Unity/Mono/Photon or Windows session qualification.");return 0;
    }
}
