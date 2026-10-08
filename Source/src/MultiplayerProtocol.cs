using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ManualDiceRoller
{
    /// <summary>Only canonical, game-independent wire data enters this state machine.</summary>
    public sealed class ProtocolProfile
    {
        public string Fingerprint { get; private set; }
        public ProtocolProfile(string fingerprint)
        {
            if (!MultiplayerProtocol.IsDigest(fingerprint)) throw new ArgumentException("A canonical SHA256 profile is required.", "fingerprint");
            Fingerprint = fingerprint;
        }
        public static string Hash(string canonicalValue)
        {
            if (canonicalValue == null) throw new ArgumentNullException("canonicalValue");
            using (var hash = SHA256.Create()) {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(canonicalValue));
                var result = new StringBuilder(64);
                foreach (var value in bytes) result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }
    }

    public sealed class ProtocolRollSpec
    {
        readonly int[] dice;
        public string ActionKey { get; private set; }
        public int Ordinal { get; private set; }
        public string Signature { get; private set; }
        public int OwnerActor { get; private set; }
        public bool ManualSupported { get; private set; }
        public bool AllowAutomaticOnMismatch { get; private set; }
        public int[] DieFaces { get { return (int[])dice.Clone(); } }
        internal int[] Dice { get { return dice; } }
        public ProtocolRollSpec(string actionKey, int ordinal, string signature, int ownerActor, int[] dieFaces, bool manualSupported, bool allowAutomaticOnMismatch = false)
        {
            if (String.IsNullOrEmpty(actionKey) || Encoding.UTF8.GetByteCount(actionKey) > 256) throw new ArgumentException("A bounded action identity is required.", "actionKey");
            if (ordinal < -1 || ownerActor < 1 || !MultiplayerProtocol.IsDigest(signature)) throw new ArgumentException("Invalid roll identity.");
            if (dieFaces == null || dieFaces.Length > MultiplayerProtocol.MaxFacesPerRequest || (allowAutomaticOnMismatch && dieFaces.Length != 0)) throw new ArgumentException("Invalid die count.", "dieFaces");
            dice = (int[])dieFaces.Clone();
            foreach (var faces in dice) if (faces < 1 || faces > 1000) throw new ArgumentException("Invalid die size.", "dieFaces");
            ActionKey = actionKey; Ordinal = ordinal; Signature = signature; OwnerActor = ownerActor; ManualSupported = manualSupported;
            AllowAutomaticOnMismatch = allowAutomaticOnMismatch;
        }
        internal bool Matches(ProtocolRollSpec other)
        {
            if (other == null || ActionKey != other.ActionKey || Ordinal != other.Ordinal || Signature != other.Signature || OwnerActor != other.OwnerActor || dice.Length != other.dice.Length || AllowAutomaticOnMismatch != other.AllowAutomaticOnMismatch) return false;
            for (int i = 0; i < dice.Length; ++i) if (dice[i] != other.dice[i]) return false;
            return true;
        }
        internal bool SafeAutomaticMismatch(ProtocolRollSpec other)
        { return other != null && AllowAutomaticOnMismatch && other.AllowAutomaticOnMismatch && dice.Length == 0 && other.dice.Length == 0 && ActionKey == other.ActionKey && Ordinal == other.Ordinal && OwnerActor == other.OwnerActor; }
    }

    public sealed class ProtocolSend
    {
        readonly byte[] bytes;
        public int TargetActor { get; private set; }
        public byte[] Payload { get { return (byte[])bytes.Clone(); } }
        internal ProtocolSend(int target, byte[] payload) { TargetActor = target; bytes = (byte[])payload.Clone(); }
    }

    public sealed class ProtocolOutcome
    {
        readonly int[] faces;
        public long Sequence { get; private set; }
        public string Signature { get; private set; }
        public bool Automatic { get; private set; }
        public int[] Faces { get { return (int[])faces.Clone(); } }
        internal ProtocolOutcome(long sequence, string signature, bool automatic, int[] values)
        { Sequence = sequence; Signature = signature; Automatic = automatic; faces = (int[])values.Clone(); }
    }

    /// <summary>
    /// Exact-roster consensus for one ordered native dice request. An outcome is released
    /// only after every participant has acknowledged the identical committed decision.
    /// Transport supplies authenticated sender IDs and reliable ordered delivery; retries
    /// additionally tolerate loss, duplication, and delayed packets. Membership changes
    /// while a request is active require a new native room/session, never local fallback.
    /// </summary>
    public sealed class MultiplayerProtocol
    {
        public const int WireVersion = 2;
        public const int MaxPacketBytes = 16384;
        public const int MaxFacesPerRequest = 512;
        public const int MaxFacesPerAction = 4096;
        public const int MaxMembers = 16;
        public const double RetrySeconds = 1.0;
        public const double TimeoutSeconds = 30.0;
        const uint Magic = 0x3352444d; // MDR3, little endian
        enum Kind : byte { Hello=1, HandshakeReady, Welcome, WelcomeAck, Start, Ready, Offer, Input, Proposal, ProposalAck, Commit, CommitAck, Release, Released, Halt }
        sealed class Packet
        {
            public Kind Type; public string Session = ""; public long Sequence;
            public string Text = ""; public string Extra = ""; public string Nonce = ""; public ProtocolRollSpec Spec;
            public bool Automatic; public int[] Faces = new int[0];
        }
        sealed class Step
        {
            public long Sequence; public ProtocolRollSpec Spec; public double Started;
            public readonly Dictionary<int, ProtocolRollSpec> Ready = new Dictionary<int, ProtocolRollSpec>();
            public readonly HashSet<int> ProposalAcks = new HashSet<int>();
            public readonly HashSet<int> CommitAcks = new HashSet<int>();
            public Packet Offer, Input, Proposal, Commit;
            public string Digest; public bool Committed;
        }
        readonly int local, master;
        readonly int[] members;
        readonly HashSet<int> memberSet;
        readonly string profile, roster;
        readonly string localNonce=Guid.NewGuid().ToString("N");
        readonly Dictionary<int,string> peerNonces=new Dictionary<int,string>();
        readonly List<ProtocolSend> outbound = new List<ProtocolSend>();
        readonly HashSet<int> hellos = new HashSet<int>(), handshakeReady = new HashSet<int>(), welcomeAcks = new HashSet<int>();
        readonly Dictionary<int, Packet> earlyReady = new Dictionary<int, Packet>();
        readonly HashSet<int> released = new HashSet<int>();
        string session, failure = "", failureDetail = "", previousDigest;
        bool welcomed, ready, halted;
        long nextSequence = 1, previousSequence;
        double lastRetry = Double.NegativeInfinity, clock;
        Step step;
        Packet earlyProposal, earlyCommit, earlyRelease;
        ProtocolOutcome outcome;
        string countedAction;
        int countedFaces;

        public bool Ready { get { return ready && !halted; } }
        public bool HasSession { get { return !String.IsNullOrEmpty(session); } }
        public string SessionToken { get { return session; } }
        public bool Busy { get { return step != null || outcome != null; } }
        public bool CanBeginRoll { get { return !halted && HasSession && !Busy && (local != master || previousSequence == 0 || released.Count == members.Length); } }
        public bool Halted { get { return halted; } }
        public string FailureReason { get { return failure; } }
        // Diagnostics only: both descriptions when players disagreed.
        public string FailureDetail { get { return failureDetail; } }
        static string Describe(ProtocolRollSpec spec)
        {
            if (spec == null) return "none";
            return spec.ActionKey + " ordinal " + spec.Ordinal + " signature " + spec.Signature + " owner " + spec.OwnerActor + " dice " + spec.Dice.Length + (spec.ManualSupported ? " supported" : " unsupported");
        }
        void Disagree(ProtocolRollSpec mine, int player, ProtocolRollSpec theirs)
        {
            if (!halted) failureDetail = "this computer: " + Describe(mine) + "; player " + player + ": " + Describe(theirs);
            HaltLocal("Native roll descriptors disagree between players.", true);
        }
        public long NextSequence { get { return nextSequence; } }
        public bool NeedsInput { get { return !halted && step != null && step.Offer != null && step.Proposal == null && step.Input == null && step.Spec.OwnerActor == local; } }
        public string Phase { get { return halted ? "halted" : step != null ? step.Committed ? "committed" : step.Proposal != null ? "proposed" : step.Offer != null ? "input" : "consensus" : outcome != null ? "released" : ready ? "ready" : "handshake"; } }

        public MultiplayerProtocol(int localActor, int masterActor, int[] roomMembers, string sessionToken, ProtocolProfile protocolProfile, double now)
        {
            if (protocolProfile == null) throw new ArgumentNullException("protocolProfile");
            if (roomMembers == null || roomMembers.Length < 1 || roomMembers.Length > MaxMembers) throw new ArgumentException("Invalid room roster.", "roomMembers");
            members = (int[])roomMembers.Clone(); Array.Sort(members); memberSet = new HashSet<int>();
            foreach (var actor in members) if (actor < 1 || !memberSet.Add(actor)) throw new ArgumentException("Duplicate or invalid room member.", "roomMembers");
            if (!memberSet.Contains(localActor) || !memberSet.Contains(masterActor)) throw new ArgumentException("Local and master actors must belong to the roster.");
            if (sessionToken == null) sessionToken = "";
            if (Encoding.UTF8.GetByteCount(sessionToken) > 64 || (localActor == masterActor && String.IsNullOrEmpty(sessionToken))) throw new ArgumentException("Master must provide a fresh bounded session token.", "sessionToken");
            local = localActor; master = masterActor; session = sessionToken; profile = protocolProfile.Fingerprint;
            roster = ProtocolProfile.Hash(String.Join(",", Array.ConvertAll(members, x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            clock = FiniteTime(now); hellos.Add(local);peerNonces.Add(local,localNonce);
            Tick(now);
        }

        public static bool IsDigest(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        public static bool IsProtocolPacket(byte[] payload)
        { return payload != null && payload.Length >= 5 && payload[0] == 0x4d && payload[1] == 0x44 && payload[2] == 0x52 && payload[3] == 0x33; }
        static bool IsNonce(string value)
        {
            if(value==null||value.Length!=32)return false;
            foreach(char c in value)if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f'))return false;
            return true;
        }

        static double FiniteTime(double value)
        { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException("now"); return value; }
        void AdvanceClock(double now) { now = FiniteTime(now); if (now > clock) clock = now; }
        string HandshakeDigest()
        {
            if(peerNonces.Count!=members.Length) return "";
            var value=new StringBuilder("MDR handshake/2/").Append(session).Append('/').Append(profile).Append('/').Append(roster);
            foreach(int actor in members)value.Append('/').Append(actor.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(peerNonces[actor]);
            return ProtocolProfile.Hash(value.ToString());
        }
        Packet HandshakePacket(Kind kind)
        {var p=New(kind);p.Text=HandshakeDigest();return p;}
        void BindProvisionalSession(string token)
        {
            // A newly joined participant can receive queued events from the
            // previous room before its first current master heartbeat. No old
            // transcript can acknowledge this process's fresh nonce.
            session=token;lastRetry=Double.NegativeInfinity;outbound.Clear();
            hellos.Clear();hellos.Add(local);peerNonces.Clear();peerNonces.Add(local,localNonce);
            handshakeReady.Clear();welcomeAcks.Clear();welcomed=false;ready=false;
            earlyReady.Clear();earlyProposal=earlyCommit=earlyRelease=null;
        }
        Packet New(Kind kind, long sequence = 0) { return new Packet { Type = kind, Session = session, Sequence = sequence }; }
        void Send(int target, Packet packet)
        {
            if (target == local) { Process(local, packet); return; }
            // Outstanding actions are bounded independently of a blocked transport.
            if (outbound.Count >= 256) { HaltLocal("The multiplayer transport was not drained.", false); return; }
            outbound.Add(new ProtocolSend(target, Encode(packet)));
        }
        void Broadcast(Packet packet) { foreach (var actor in members) if (actor != local) Send(actor, packet); }
        public ProtocolSend[] DrainOutbound() { var result = outbound.ToArray(); outbound.Clear(); return result; }

        public void Tick(double now)
        {
            AdvanceClock(now);
            if (halted) return;
            bool waitingForHuman = step != null && step.Offer != null && step.Proposal == null && step.Input == null;
            if (step != null && !waitingForHuman && clock - step.Started >= TimeoutSeconds) { HaltLocal("Timed out waiting for the complete multiplayer roll barrier.", true); return; }
            if (clock - lastRetry < RetrySeconds) return;
            lastRetry = clock;
            if (!HasSession) return;
            if (!ready) {
                var hello = New(Kind.Hello); hello.Text = profile; hello.Extra = roster;hello.Nonce=localNonce; Broadcast(hello);
                if (!ready && hellos.Count == members.Length) Send(master, HandshakePacket(Kind.HandshakeReady));
                if (local == master && handshakeReady.Count == members.Length) Broadcast(HandshakePacket(Kind.Welcome));
                if (welcomed) Send(master, HandshakePacket(Kind.WelcomeAck));
            } else if (local == master) Broadcast(HandshakePacket(Kind.Start));
            if (local == master && previousSequence > 0 && released.Count != members.Length) {
                var release = New(Kind.Release, previousSequence); release.Text = previousDigest; Broadcast(release);
            }
            if (step == null || !ready) return;
            if (local != master || step.Ready.Count != members.Length) Send(master, ReadyPacket(step));
            if (step == null) return;
            if (step.Offer != null && local == master && step.Proposal == null) Broadcast(step.Offer);
            if (step.Input != null && step.Proposal == null) Send(master, step.Input);
            if (step == null) return;
            if (step.Proposal != null) {
                if (local == master && step.Commit == null) Broadcast(step.Proposal);
                var ack = New(Kind.ProposalAck, step.Sequence); ack.Text = step.Digest; Send(master, ack);
            }
            if (step != null && step.Commit != null && local == master) Broadcast(step.Commit);
            if (step != null && step.Committed) { var ack = New(Kind.CommitAck, step.Sequence); ack.Text = step.Digest; Send(master, ack); }
        }

        public bool BeginRoll(ProtocolRollSpec spec, double now)
        {
            AdvanceClock(now);
            if (spec == null) throw new ArgumentNullException("spec");
            if (!CanBeginRoll || !memberSet.Contains(spec.OwnerActor)) return false;
            // The explicit zero-face admission starts a new native invocation,
            // including a repeated invocation at the same call site. A call-site
            // key alone is not an action lifetime and must not accumulate faces
            // across the entire campaign.
            if (countedAction != spec.ActionKey || spec.Ordinal == -1 && spec.Dice.Length == 0) { countedAction = spec.ActionKey; countedFaces = 0; }
            if (countedFaces > MaxFacesPerAction - spec.Dice.Length) { HaltLocal("The action exceeds the bounded multiplayer dice budget.", true); return false; }
            step = new Step { Sequence = nextSequence, Spec = spec, Started = clock };
            if (local == master) {
                foreach (var pair in earlyReady) step.Ready[pair.Key] = pair.Value.Spec;
                earlyReady.Clear();
                foreach (var other in step.Ready) if (!spec.Matches(other.Value) && !spec.SafeAutomaticMismatch(other.Value)) { Disagree(spec, other.Key, other.Value); return false; }
            }
            if (ready) Send(master, ReadyPacket(step));
            var p = earlyProposal; earlyProposal = null; if (p != null) Process(master, p);
            p = earlyCommit; earlyCommit = null; if (p != null) Process(master, p);
            p = earlyRelease; earlyRelease = null; if (p != null) Process(master, p);
            return !halted;
        }
        Packet ReadyPacket(Step value) { var p = New(Kind.Ready, value.Sequence); p.Spec = value.Spec; return p; }
        public bool SubmitFaces(int[] faces, double now)
        {
            AdvanceClock(now);
            if (!NeedsInput || !ValidFaces(step.Spec, faces)) return false;
            var packet = New(Kind.Input, step.Sequence); packet.Spec = step.Spec; packet.Faces = (int[])faces.Clone();
            step.Input = packet; step.Started = clock; Send(master, packet); return !halted;
        }
        public bool RequestAutomatic(double now)
        {
            AdvanceClock(now); if (!NeedsInput) return false;
            var packet = New(Kind.Input, step.Sequence); packet.Spec = step.Spec; packet.Automatic = true;
            step.Input = packet; step.Started = clock; Send(master, packet); return !halted;
        }
        public bool TryTakeOutcome(out ProtocolOutcome result) { result = outcome; if (result == null) return false; outcome = null; return true; }
        public void MembershipChanged(int newMaster, int[] newMembers)
        {
            bool same = newMaster == master && newMembers != null && newMembers.Length == members.Length;
            if (same) { var copy = (int[])newMembers.Clone(); Array.Sort(copy); for (int i = 0; i < copy.Length; ++i) if (copy[i] != members[i]) same = false; }
            if (!same) HaltLocal("The multiplayer roster or master changed; a fresh synchronized session is required.", true);
        }
        public void Abort(string reason) { HaltLocal(String.IsNullOrEmpty(reason) ? "The local native roll could not remain synchronized." : reason, true); }
        public void Halt(string reason, double now) { AdvanceClock(now); Abort(reason); }

        // Profiles combine the exact mod build, game, loader and the shared
        // roll settings: a mismatch is a different version or roll settings.
        public const string ProfileMismatch = "Manual dice profiles or room rosters do not match.";
        public const string RemoteHalt = "A room member stopped the synchronized multiplayer roll.";
        public const string RemoteHaltPrefix = "Another player's game stopped manual dice: ";
        // Every fixed reason a peer of this or an earlier build broadcasts.
        static readonly string[] KnownHaltReasons = {
            ProfileMismatch,
            "The players' games do not list the same room members.",
            "A participant restarted its multiplayer handshake; reload together before continuing.",
            "The multiplayer roster or master changed; a fresh synchronized session is required.",
            "A room member sent a malformed multiplayer protocol packet.",
            "Native roll descriptors disagree between players.",
            "The master proposed a different native roll.",
            "The master proposed invalid dice faces.",
            "The master changed a previously prepared dice decision.",
            "The master changed an early roll decision.",
            "The roll owner changed an accepted dice decision.",
            "A peer changed its pending roll descriptor.",
            "A peer changed its roll capability after voting.",
            "The committed roll digest does not match the prepared decision.",
            "The released roll digest does not match the committed decision.",
            "A roll release arrived without the matching committed decision.",
            "Timed out waiting for the complete multiplayer roll barrier.",
            "The action exceeds the bounded multiplayer dice budget.",
            "The multiplayer transport was not drained.",
            "The multiplayer roster, host or roll settings changed during a roll.",
            "The multiplayer room disconnected during a native roll.",
            "A multiplayer packet had an invalid recipient.",
            "Photon could not send a multiplayer roll packet.",
            "A character's controlling player changed while its dice were pending.",
            "The character's controlling player changed while its dice were pending.",
            "A multiplayer action was canceled before the native handoff.",
            "A multiplayer native action was reset.",
            "A replicated native roll was canceled before completion.",
            "A native multiplayer roll was canceled before its shared outcome.",
            "A multiplayer roll was interrupted.",
            "A multiplayer roll completed another request.",
            "Another game routine rolled while a multiplayer roll was waiting.",
            "Entered dice could not be applied to the native step.",
            "Every player must install this exact mod build before multiplayer dice can continue.",
            "Multiplayer admission ended without a shared decision.",
            "Native multiplayer arguments changed after the agreement.",
            "The agreed dice were canceled before collection.",
            "The dice request could not be prepared.",
            "The multiplayer prompt ended without a shared decision.",
            "The multiplayer roll session disappeared.",
            "The native coroutine did not reach its multiplayer completion barrier.",
            "The native multiplayer call did not record its result and outputs.",
            "The native multiplayer handoff ended without consensus.",
            "The native result or output vector does not match the registered method.",
            "The players' games did not finish this roll together.",
            "Unfinished Business was loaded during a standalone multiplayer session.",
            "The local native roll could not remain synchronized.",
        };
        static Dictionary<string, string> knownHaltDigests;
        public static string KnownHaltReason(string digest)
        {
            if (String.IsNullOrEmpty(digest)) return null;
            var table = knownHaltDigests;
            if (table == null) {
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var reason in KnownHaltReasons) table[ProtocolProfile.Hash(reason)] = reason;
                knownHaltDigests = table;
            }
            string known; return table.TryGetValue(digest, out known) ? known : null;
        }
        public static bool IsProfileMismatch(string reason)
        { return reason != null && (reason == ProfileMismatch || reason == RemoteHaltPrefix + ProfileMismatch); }
        void HaltLocal(string reason, bool broadcast)
        {
            if (halted) return;
            halted = true; ready = false; failure = reason;
            outcome = null;
            if (broadcast && HasSession) { var p = New(Kind.Halt, step == null ? 0 : step.Sequence); p.Text = ProtocolProfile.Hash(reason); Broadcast(p); }
        }
        public void Receive(int actualSender, byte[] payload, double now)
        {
            AdvanceClock(now);
            if (halted || actualSender == local || !memberSet.Contains(actualSender) || !IsProtocolPacket(payload)) return;
            Packet p;
            try { p = Decode(payload); }
            catch (Exception e) {
                if (!(e is IOException) && !(e is InvalidDataException) && !(e is ArgumentException) && !(e is OverflowException)) throw;
                HaltLocal("A room member sent a malformed multiplayer protocol packet.", true); return;
            }
            if (!HasSession) {
                if (p.Type != Kind.Hello || actualSender != master || String.IsNullOrEmpty(p.Session)) return;
                BindProvisionalSession(p.Session);
            }
            if (!String.Equals(session, p.Session, StringComparison.Ordinal)) {
                if(local!=master&&!welcomed&&!ready&&previousSequence==0&&p.Type==Kind.Hello&&actualSender==master)
                    BindProvisionalSession(p.Session);
                else return;
            }
            Process(actualSender, p);
        }

        void Process(int sender, Packet p)
        {
            if (halted) return;
            if (p.Type == Kind.Halt) {
                // A halt carries the digest of its reason; a known reason (sent
                // by any version) is reported by name on every computer.
                string known = KnownHaltReason(p.Text);
                HaltLocal(known == null ? RemoteHalt : RemoteHaltPrefix + known, false); return;
            }
            if (p.Type == Kind.Hello) {
                if (p.Text != profile) {
                    if (!halted) failureDetail = "this computer's profile " + profile + "; " + (sender == master ? "the host's" : "player " + sender.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'s") + " profile " + p.Text;
                    HaltLocal(ProfileMismatch, true); return;
                }
                if (p.Extra != roster) { HaltLocal("The players' games do not list the same room members.", true); return; }
                string previousNonce;
                if(peerNonces.TryGetValue(sender,out previousNonce)&&previousNonce!=p.Nonce) {
                    if(ready||welcomed) {HaltLocal("A participant restarted its multiplayer handshake; reload together before continuing.",true);return;}
                    handshakeReady.Clear();welcomeAcks.Clear();
                }
                peerNonces[sender]=p.Nonce;
                hellos.Add(sender);
                if (!ready && hellos.Count == members.Length) Send(master, HandshakePacket(Kind.HandshakeReady));
                return;
            }
            if (p.Type == Kind.HandshakeReady) {
                if (local != master || hellos.Count != members.Length||p.Text!=HandshakeDigest()) return;
                if (ready) { if (sender != local) Send(sender, HandshakePacket(Kind.Start)); return; }
                handshakeReady.Add(sender);
                if (handshakeReady.Count == members.Length) { welcomed = true; welcomeAcks.Add(local); Broadcast(HandshakePacket(Kind.Welcome)); MaybeStart(); }
                return;
            }
            if (p.Type == Kind.Welcome) {
                if (sender != master || hellos.Count != members.Length||p.Text!=HandshakeDigest()) return;
                welcomed = true; Send(master, HandshakePacket(Kind.WelcomeAck)); return;
            }
            if (p.Type == Kind.WelcomeAck) {
                if (local == master && welcomed&&p.Text==HandshakeDigest()) {
                    if (ready) { if (sender != local) Send(sender, HandshakePacket(Kind.Start)); return; }
                    welcomeAcks.Add(sender); MaybeStart();
                }
                return;
            }
            if (p.Type == Kind.Start) {
                if (!ready && sender == master && welcomed && hellos.Count == members.Length&&p.Text==HandshakeDigest()) { ready = true; if (step != null) Send(master, ReadyPacket(step)); }
                return;
            }
            if (!ready) return;
            if (p.Type == Kind.Released) {
                if (local == master && p.Sequence == previousSequence && p.Text == previousDigest) released.Add(sender);
                return;
            }
            if (p.Sequence == previousSequence && previousSequence > 0) {
                if (p.Type == Kind.Release && sender == master && p.Text == previousDigest) { var ack = New(Kind.Released, p.Sequence); ack.Text = previousDigest; Send(master, ack); }
                else if (local == master && (p.Type == Kind.CommitAck || p.Type == Kind.ProposalAck) && p.Text == previousDigest) { var release = New(Kind.Release, previousSequence); release.Text = previousDigest; Send(sender, release); }
                return;
            }
            if (p.Sequence != nextSequence) return;
            if (p.Type == Kind.Ready) {
                if (local != master || !memberSet.Contains(p.Spec.OwnerActor)) return;
                if (step == null) {
                    Packet previous;
                    if (earlyReady.TryGetValue(sender, out previous) && (!previous.Spec.Matches(p.Spec) || previous.Spec.ManualSupported != p.Spec.ManualSupported)) { HaltLocal("A peer changed its pending roll descriptor.", true); return; }
                    earlyReady[sender] = p; return;
                }
                if (!step.Spec.Matches(p.Spec) && !step.Spec.SafeAutomaticMismatch(p.Spec)) { Disagree(step.Spec, sender, p.Spec); return; }
                ProtocolRollSpec existing;
                if (step.Ready.TryGetValue(sender, out existing) && existing.ManualSupported != p.Spec.ManualSupported) { HaltLocal("A peer changed its roll capability after voting.", true); return; }
                step.Ready[sender] = p.Spec; MaybeOffer(); return;
            }
            if (p.Type == Kind.Offer || p.Type == Kind.Proposal) {
                if (sender != master) return;
                if (step == null) {
                    if (p.Type == Kind.Proposal) {
                        if (earlyProposal != null && DecisionDigest(earlyProposal) != DecisionDigest(p)) { HaltLocal("The master changed an early roll decision.", true); return; }
                        earlyProposal = p;
                    }
                    return;
                }
                if (!step.Spec.Matches(p.Spec) && !(p.Automatic && step.Spec.SafeAutomaticMismatch(p.Spec))) { if (!halted) failureDetail = "this computer: " + Describe(step.Spec) + "; host: " + Describe(p.Spec); HaltLocal("The master proposed a different native roll.", true); return; }
                if (p.Type == Kind.Offer) { if (step.Proposal == null) { if (step.Offer == null) step.Started = clock; step.Offer = p; } return; }
                if (p.Automatic ? p.Faces.Length != 0 : !ValidFaces(step.Spec, p.Faces)) { HaltLocal("The master proposed invalid dice faces.", true); return; }
                string digest = DecisionDigest(p);
                if (step.Proposal != null && step.Digest != digest) { HaltLocal("The master changed a previously prepared dice decision.", true); return; }
                if (step.Proposal == null) step.Started = clock;
                step.Proposal = p; step.Digest = digest;
                var ack = New(Kind.ProposalAck, p.Sequence); ack.Text = digest; Send(master, ack);
                if (earlyCommit != null) { var cached = earlyCommit; earlyCommit = null; Process(master, cached); }
                if (earlyRelease != null) { var cached = earlyRelease; earlyRelease = null; Process(master, cached); }
                return;
            }
            if (p.Type == Kind.Input) {
                if (local != master || step == null || step.Offer == null || sender != step.Spec.OwnerActor || !step.Spec.Matches(p.Spec)) return;
                if (p.Automatic ? p.Faces.Length != 0 : !ValidFaces(step.Spec, p.Faces)) return;
                if (step.Proposal == null) Propose(p.Automatic, p.Faces);
                else if (step.Proposal.Automatic != p.Automatic || !EqualFaces(step.Proposal.Faces, p.Faces)) HaltLocal("The roll owner changed an accepted dice decision.", true);
                return;
            }
            if (p.Type == Kind.ProposalAck) {
                if (local != master || step == null || step.Proposal == null || p.Text != step.Digest) return;
                step.ProposalAcks.Add(sender);
                if (step.ProposalAcks.Count == members.Length && step.Commit == null) {
                    var commit = New(Kind.Commit, step.Sequence); commit.Text = step.Digest; step.Commit = commit;
                    Broadcast(commit); Process(local, commit);
                }
                return;
            }
            if (p.Type == Kind.Commit) {
                if (sender != master) return;
                if (step == null || step.Proposal == null) { earlyCommit = p; return; }
                if (p.Text != step.Digest) { HaltLocal("The committed roll digest does not match the prepared decision.", true); return; }
                if (!step.Committed) step.Started = clock;
                step.Committed = true;
                var ack = New(Kind.CommitAck, p.Sequence); ack.Text = step.Digest; Send(master, ack);
                if (earlyRelease != null) { var cached = earlyRelease; earlyRelease = null; Process(master, cached); }
                return;
            }
            if (p.Type == Kind.CommitAck) {
                if (local != master || step == null || !step.Committed || p.Text != step.Digest) return;
                step.CommitAcks.Add(sender);
                if (step.CommitAcks.Count == members.Length) {
                    var release = New(Kind.Release, step.Sequence); release.Text = step.Digest;
                    previousSequence = step.Sequence; previousDigest = step.Digest; released.Clear();
                    Broadcast(release); FinishRelease(release);
                }
                return;
            }
            if (p.Type == Kind.Release) {
                if (sender != master) return;
                if (step == null || !step.Committed) { earlyRelease = p; return; }
                if (p.Text != step.Digest) { HaltLocal("The released roll digest does not match the committed decision.", true); return; }
                FinishRelease(p);
            }
        }
        void MaybeStart()
        {
            if (ready || local != master || welcomeAcks.Count != members.Length) return;
            ready = true; Broadcast(HandshakePacket(Kind.Start)); if (step != null) Send(master, ReadyPacket(step));
        }
        void MaybeOffer()
        {
            if (step == null || step.Ready.Count != members.Length || step.Offer != null || step.Proposal != null) return;
            foreach (var spec in step.Ready.Values) if (!spec.ManualSupported || !step.Spec.Matches(spec)) { Propose(true, new int[0]); return; }
            if (step.Spec.Dice.Length == 0) { Propose(false, new int[0]); return; }
            var offer = New(Kind.Offer, step.Sequence); offer.Spec = step.Spec; step.Offer = offer; step.Started = clock; Broadcast(offer);
        }
        void Propose(bool automatic, int[] faces)
        {
            var p = New(Kind.Proposal, step.Sequence); p.Spec = step.Spec; p.Automatic = automatic; p.Faces = (int[])faces.Clone();
            Broadcast(p); Process(local, p);
        }
        void FinishRelease(Packet release)
        {
            if (step == null || step.Proposal == null || !step.Committed || release.Text != step.Digest) { HaltLocal("A roll release arrived without the matching committed decision.", true); return; }
            outcome = new ProtocolOutcome(step.Sequence, step.Spec.Signature, step.Proposal.Automatic, step.Proposal.Faces);
            countedFaces += step.Spec.Dice.Length;
            previousSequence = step.Sequence; previousDigest = step.Digest; nextSequence = checked(step.Sequence + 1);
            step = null; earlyProposal = null; earlyCommit = null; earlyRelease = null;
            var ack = New(Kind.Released, previousSequence); ack.Text = previousDigest; Send(master, ack);
        }
        static bool ValidFaces(ProtocolRollSpec spec, int[] faces)
        { if (faces == null || faces.Length != spec.Dice.Length) return false; for (int i = 0; i < faces.Length; ++i) if (faces[i] < 1 || faces[i] > spec.Dice[i]) return false; return true; }
        static bool EqualFaces(int[] a, int[] b)
        { if (a.Length != b.Length) return false; for (int i=0; i<a.Length; ++i) if (a[i]!=b[i]) return false; return true; }
        static string DecisionDigest(Packet p) { return ProtocolProfile.Hash(Convert.ToBase64String(Encode(p))); }

        static void WriteString(BinaryWriter writer, string value, int limit)
        { var bytes = Encoding.UTF8.GetBytes(value ?? ""); if (bytes.Length > limit) throw new InvalidDataException("Oversized protocol string."); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        static string ReadString(BinaryReader reader, int limit)
        { int length = reader.ReadUInt16(); if (length > limit) throw new InvalidDataException("Oversized protocol string."); var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return new UTF8Encoding(false, true).GetString(bytes); }
        static void WriteSpec(BinaryWriter writer, ProtocolRollSpec spec)
        { WriteString(writer, spec.ActionKey, 256); writer.Write(spec.Ordinal); WriteString(writer, spec.Signature, 64); writer.Write(spec.OwnerActor); writer.Write(spec.ManualSupported); writer.Write(spec.AllowAutomaticOnMismatch); writer.Write((ushort)spec.Dice.Length); foreach (var faces in spec.Dice) writer.Write((ushort)faces); }
        static ProtocolRollSpec ReadSpec(BinaryReader reader)
        { var action = ReadString(reader,256); int ordinal = reader.ReadInt32(); var signature = ReadString(reader,64); int owner = reader.ReadInt32(); bool manual = ReadBool(reader); bool fallback=ReadBool(reader); int count = reader.ReadUInt16(); if (count > MaxFacesPerRequest) throw new InvalidDataException("Invalid dice count."); var dice = new int[count]; for (int i=0;i<count;++i) dice[i]=reader.ReadUInt16(); return new ProtocolRollSpec(action,ordinal,signature,owner,dice,manual,fallback); }
        static bool ReadBool(BinaryReader reader) { byte value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid Boolean."); return value != 0; }
        static byte[] Encode(Packet p)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(Magic); writer.Write((byte)WireVersion); writer.Write((byte)p.Type); WriteString(writer,p.Session,64); writer.Write(p.Sequence);
                if (p.Type == Kind.Hello) { WriteString(writer,p.Text,64); WriteString(writer,p.Extra,64); WriteString(writer,p.Nonce,32); }
                else if(p.Type==Kind.HandshakeReady||p.Type==Kind.Welcome||p.Type==Kind.WelcomeAck||p.Type==Kind.Start)WriteString(writer,p.Text,64);
                else if (p.Type == Kind.Ready || p.Type == Kind.Offer || p.Type == Kind.Input || p.Type == Kind.Proposal) {
                    WriteSpec(writer,p.Spec);
                    if (p.Type == Kind.Input || p.Type == Kind.Proposal) { writer.Write(p.Automatic); writer.Write((ushort)p.Faces.Length); foreach (var face in p.Faces) writer.Write((ushort)face); }
                } else if (p.Type == Kind.ProposalAck || p.Type == Kind.Commit || p.Type == Kind.CommitAck || p.Type == Kind.Release || p.Type == Kind.Released || p.Type == Kind.Halt) WriteString(writer,p.Text,64);
                writer.Flush(); var bytes = stream.ToArray(); if (bytes.Length > MaxPacketBytes) throw new InvalidDataException("Oversized protocol packet."); return bytes;
            }
        }
        static Packet Decode(byte[] bytes)
        {
            if (bytes.Length > MaxPacketBytes) throw new InvalidDataException("Oversized protocol packet.");
            using (var stream = new MemoryStream(bytes,false)) using (var reader = new BinaryReader(stream)) {
                if (reader.ReadUInt32() != Magic || reader.ReadByte() != WireVersion) throw new InvalidDataException("Unsupported protocol version.");
                byte kind = reader.ReadByte(); if (kind < (byte)Kind.Hello || kind > (byte)Kind.Halt) throw new InvalidDataException("Invalid packet kind.");
                var p = new Packet { Type=(Kind)kind, Session=ReadString(reader,64), Sequence=reader.ReadInt64() };
                if (String.IsNullOrEmpty(p.Session) || p.Sequence < 0) throw new InvalidDataException("Invalid session or sequence.");
                if (p.Type == Kind.Hello) { p.Text=ReadString(reader,64); p.Extra=ReadString(reader,64);p.Nonce=ReadString(reader,32); if(!IsDigest(p.Text)||!IsDigest(p.Extra)||!IsNonce(p.Nonce))throw new InvalidDataException("Invalid profile digest or participant nonce."); }
                else if(p.Type==Kind.HandshakeReady||p.Type==Kind.Welcome||p.Type==Kind.WelcomeAck||p.Type==Kind.Start){p.Text=ReadString(reader,64);if(!IsDigest(p.Text))throw new InvalidDataException("Invalid handshake digest.");}
                else if (p.Type == Kind.Ready || p.Type == Kind.Offer || p.Type == Kind.Input || p.Type == Kind.Proposal) {
                    if(p.Sequence<1)throw new InvalidDataException("Invalid request sequence."); p.Spec=ReadSpec(reader);
                    if (p.Type == Kind.Input || p.Type == Kind.Proposal) { p.Automatic=ReadBool(reader); int count=reader.ReadUInt16(); if(count>MaxFacesPerRequest)throw new InvalidDataException("Invalid faces count."); p.Faces=new int[count];for(int i=0;i<count;++i)p.Faces[i]=reader.ReadUInt16(); }
                } else if (p.Type == Kind.ProposalAck || p.Type == Kind.Commit || p.Type == Kind.CommitAck || p.Type == Kind.Release || p.Type == Kind.Released || p.Type == Kind.Halt) { p.Text=ReadString(reader,64); if(!IsDigest(p.Text))throw new InvalidDataException("Invalid decision digest."); }
                if(stream.Position!=stream.Length)throw new InvalidDataException("Trailing protocol data."); return p;
            }
        }
    }
}
