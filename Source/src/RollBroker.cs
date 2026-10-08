using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ManualDiceRoller
{
    /// <summary>The immutable identity and state snapshot of one native invocation.</summary>
    public sealed class RollBinding : IEquatable<RollBinding>
    {
        public object Actor { get; private set; }
        public object Action { get; private set; }
        public object Effect { get; private set; }
        public object Form { get; private set; }
        public string Role { get; private set; }
        public int DieFaces { get; private set; }
        public int InvocationIndex { get; private set; }
        public string StateStamp { get; private set; }

        public RollBinding(object actor, object action, object effect, object form, string role,
            int dieFaces, int invocationIndex, string stateStamp)
        {
            if (action == null) throw new ArgumentNullException("action");
            if (String.IsNullOrEmpty(role)) throw new ArgumentException("A native role is required.", "role");
            if (dieFaces < 1 || dieFaces > 1000) throw new ArgumentOutOfRangeException("dieFaces");
            if (invocationIndex < 0) throw new ArgumentOutOfRangeException("invocationIndex");
            Actor = actor; Action = action; Effect = effect; Form = form; Role = role;
            DieFaces = dieFaces; InvocationIndex = invocationIndex; StateStamp = stateStamp ?? "";
        }

        public bool Equals(RollBinding other)
        {
            return other != null && ReferenceEquals(Actor, other.Actor) && ReferenceEquals(Action, other.Action)
                && ReferenceEquals(Effect, other.Effect) && ReferenceEquals(Form, other.Form)
                && String.Equals(Role, other.Role, StringComparison.Ordinal) && DieFaces == other.DieFaces
                && InvocationIndex == other.InvocationIndex && String.Equals(StateStamp, other.StateStamp, StringComparison.Ordinal);
        }
        public override bool Equals(object other) { return Equals(other as RollBinding); }
        internal static int ReferenceHash(object value) { return value == null ? 0 : RuntimeHelpers.GetHashCode(value); }
        public override int GetHashCode()
        {
            unchecked {
                int hash = ReferenceHash(Actor); hash = hash * 31 + ReferenceHash(Action);
                hash = hash * 31 + ReferenceHash(Effect); hash = hash * 31 + ReferenceHash(Form);
                hash = hash * 31 + Role.GetHashCode(); hash = hash * 31 + DieFaces;
                hash = hash * 31 + InvocationIndex; return hash * 31 + StateStamp.GetHashCode();
            }
        }
    }

    /// <summary>One original die face or one particular feature replacement.</summary>
    public sealed class FaceIdentity : IEquatable<FaceIdentity>
    {
        public RollBinding Binding { get; private set; }
        public int DieIndex { get; private set; }
        public int FaceIndex { get; private set; }
        public object RerollFeature { get; private set; }
        public int FeatureIndex { get; private set; }
        public int RerollAttempt { get; private set; }

        public FaceIdentity(RollBinding binding, int dieIndex, int faceIndex,
            object rerollFeature = null, int featureIndex = -1, int rerollAttempt = 0)
        {
            if (binding == null) throw new ArgumentNullException("binding");
            if (dieIndex < 0) throw new ArgumentOutOfRangeException("dieIndex");
            if (faceIndex < 0 || faceIndex > 1) throw new ArgumentOutOfRangeException("faceIndex");
            if (rerollFeature == null && (featureIndex != -1 || rerollAttempt != 0))
                throw new ArgumentException("Original faces cannot identify a replacement stage.");
            if (rerollFeature != null && (featureIndex < 0 || rerollAttempt < 1))
                throw new ArgumentException("Replacement faces require a feature occurrence and positive attempt.");
            Binding = binding; DieIndex = dieIndex; FaceIndex = faceIndex;
            RerollFeature = rerollFeature; FeatureIndex = featureIndex; RerollAttempt = rerollAttempt;
        }
        public bool Equals(FaceIdentity other)
        {
            return other != null && Binding.Equals(other.Binding) && DieIndex == other.DieIndex && FaceIndex == other.FaceIndex
                && ReferenceEquals(RerollFeature, other.RerollFeature) && FeatureIndex == other.FeatureIndex && RerollAttempt == other.RerollAttempt;
        }
        public override bool Equals(object other) { return Equals(other as FaceIdentity); }
        public override int GetHashCode()
        {
            unchecked {
                int hash = Binding.GetHashCode(); hash = hash * 31 + DieIndex; hash = hash * 31 + FaceIndex;
                hash = hash * 31 + RollBinding.ReferenceHash(RerollFeature); hash = hash * 31 + FeatureIndex;
                return hash * 31 + RerollAttempt;
            }
        }
    }

    public enum RollTicketState { Pending, Accepted, Complete, Canceled }

    public sealed class RollTicket
    {
        internal readonly RollBroker Broker;
        internal readonly RollSession Session;
        internal readonly FaceIdentity[] Identities;
        internal int[] Values;
        internal bool Canceled;
        public long Id { get; private set; }
        public int Count { get { return Identities.Length; } }
        public RollTicketState State { get { return Broker.Query(this); } }
        public bool IsPending { get { return State == RollTicketState.Pending; } }
        public bool IsAccepted { get { var state = State; return state == RollTicketState.Accepted || state == RollTicketState.Complete; } }
        public bool IsCanceled { get { return State == RollTicketState.Canceled; } }
        public bool IsComplete { get { return State == RollTicketState.Complete; } }
        internal RollTicket(RollBroker broker, RollSession session, long id, FaceIdentity[] identities)
        { Broker = broker; Session = session; Id = id; Identities = identities; }
        public FaceIdentity[] CopyIdentities() { return (FaceIdentity[])Identities.Clone(); }
        public bool TryGetAccepted(out int[] faces) { return Broker.TryGetAccepted(this, out faces); }
    }

    public sealed class RollSession : IDisposable
    {
        internal readonly RollBroker Broker;
        internal readonly object Action;
        internal readonly Dictionary<FaceIdentity, RollBroker.Slot> Slots = new Dictionary<FaceIdentity, RollBroker.Slot>();
        internal readonly List<RollTicket> Tickets = new List<RollTicket>();
        internal bool Closed;
        public bool IsCanceled { get { return Broker.SessionClosed(this); } }
        internal RollSession(RollBroker broker, object action) { Broker = broker; Action = action; }
        public bool TryRequest(IEnumerable<FaceIdentity> identities, out RollTicket ticket, out string reason)
        { return Broker.TryRequest(this, identities, out ticket, out reason); }
        public bool TryEnterNative(RollBinding observed, IEnumerable<FaceIdentity> exactOrder, out NativeRollLease lease, out string reason)
        { return Broker.TryEnterNative(this, observed, exactOrder, out lease, out reason); }
        public void Cancel() { Broker.Close(this); }
        public void Dispose() { Cancel(); }
    }

    /// <summary>Exact accepted inputs for one synchronous native call. This class never waits.</summary>
    public sealed class NativeRollLease : IDisposable
    {
        internal readonly RollBroker Broker;
        internal readonly RollSession Session;
        internal readonly RollBroker.Slot[] Slots;
        internal int Next;
        internal bool Closed;
        internal NativeRollLease(RollBroker broker, RollSession session, RollBroker.Slot[] slots)
        { Broker = broker; Session = session; Slots = slots; }
        public bool TryConsume(FaceIdentity observed, out int face) { return Broker.TryConsume(this, observed, out face); }
        public int Remaining { get { return Broker.Remaining(this); } }
        public void Dispose() { Broker.Close(this); }
    }

    /// <summary>
    /// Pure ownership engine. Coroutine adapters collect exact faces; native RNG hooks
    /// read a currently active, explicitly bound lease. There is no total substitution,
    /// trait implementation, loose queue search, UI, or synchronous wait in this class.
    /// </summary>
    public sealed class RollBroker
    {
        public const int MaxFacesPerRequest = 512;
        public const int MaxFacesPerSession = 4096;
        public const int MaxExactLeaseFaces = MaxFacesPerSession;
        internal enum SlotState { Pending, Available, Reserved, Consumed, Canceled }
        internal sealed class Slot
        {
            internal readonly FaceIdentity Identity;
            internal readonly RollTicket Ticket;
            internal readonly int Index;
            internal SlotState State;
            internal NativeRollLease Lease;
            internal Slot(FaceIdentity identity, RollTicket ticket, int index)
            { Identity = identity; Ticket = ticket; Index = index; State = SlotState.Pending; }
        }
        readonly object gate = new object();
        readonly HashSet<RollSession> sessions = new HashSet<RollSession>();
        readonly List<NativeRollLease> native = new List<NativeRollLease>();
        RollTicket pending;
        long nextId;

        public bool IsPromptBusy { get { lock (gate) return pending != null; } }
        public RollTicket PendingTicket { get { lock (gate) return pending; } }
        public RollSession BeginSession(object action)
        {
            if (action == null) throw new ArgumentNullException("action");
            lock (gate) { var session = new RollSession(this, action); sessions.Add(session); return session; }
        }
        static FaceIdentity[] Copy(IEnumerable<FaceIdentity> identities, int maximum, out bool oversized)
        {
            oversized = false;
            if (identities == null) return null;
            var result = new List<FaceIdentity>();
            // Inspect at most maximum + 1 elements. Even an infinite producer
            // must be rejected before it can allocate an unbounded face plan.
            foreach (var identity in identities) {
                if (result.Count == maximum) { oversized = true; return null; }
                result.Add(identity);
            }
            return result.ToArray();
        }

        internal bool TryRequest(RollSession session, IEnumerable<FaceIdentity> identities, out RollTicket ticket, out string reason)
        {
            ticket = null; reason = "";
            bool oversized; FaceIdentity[] requested;
            try { requested = Copy(identities, MaxFacesPerRequest, out oversized); }
            catch (Exception) { reason = "Could not read the requested face plan."; return false; }
            lock (gate) {
                if (session.Closed || !sessions.Contains(session)) { reason = "Session canceled."; return false; }
                if (pending != null) { reason = "Another request owns the prompt."; return false; }
                if (oversized || requested == null || requested.Length == 0) { reason = "Invalid face count."; return false; }
                if (requested.Length > MaxFacesPerSession - session.Slots.Count) { reason = "The roll session exceeds its supported face budget."; return false; }
                var unique = new HashSet<FaceIdentity>();
                foreach (var identity in requested) {
                    if (identity == null || !ReferenceEquals(identity.Binding.Action, session.Action)) { reason = "Request action does not match its owner."; return false; }
                    if (!unique.Add(identity) || session.Slots.ContainsKey(identity)) { reason = "Face identity already used."; return false; }
                }
                if (nextId == Int64.MaxValue) { reason = "Request identity exhausted."; return false; }
                ticket = new RollTicket(this, session, ++nextId, requested);
                for (int i = 0; i < requested.Length; i++) session.Slots.Add(requested[i], new Slot(requested[i], ticket, i));
                session.Tickets.Add(ticket); pending = ticket; return true;
            }
        }
        public bool TryAccept(long id, int[] exactFaces, out string reason)
        {
            reason = "";
            // Copy before validation and storage; callers never own broker values.
            if (exactFaces != null && exactFaces.Length > MaxFacesPerRequest) { reason = "Enter every requested die face."; return false; }
            var faces = exactFaces == null ? null : (int[])exactFaces.Clone();
            lock (gate) {
                var ticket = pending;
                if (ticket == null || ticket.Id != id || ticket.Session.Closed || ticket.Canceled) { reason = "Stale or canceled request."; return false; }
                if (faces == null || faces.Length != ticket.Count) { reason = "Enter every requested die face."; return false; }
                for (int i = 0; i < faces.Length; i++)
                    if (faces[i] < 1 || faces[i] > ticket.Identities[i].Binding.DieFaces) { reason = "A face is outside its die range."; return false; }
                ticket.Values = faces;
                foreach (var identity in ticket.Identities) ticket.Session.Slots[identity].State = SlotState.Available;
                pending = null; return true;
            }
        }
        public bool Cancel(long id)
        {
            lock (gate) {
                foreach (var session in sessions) foreach (var ticket in session.Tickets)
                    if (ticket.Id == id && !ticket.Canceled) { CancelTicket(ticket); return true; }
                return false;
            }
        }
        void CancelTicket(RollTicket ticket)
        {
            ticket.Canceled = true;
            if (ReferenceEquals(pending, ticket)) pending = null;
            foreach (var identity in ticket.Identities) {
                var slot = ticket.Session.Slots[identity];
                if (slot.State != SlotState.Consumed) slot.State = SlotState.Canceled;
            }
        }
        internal RollTicketState Query(RollTicket ticket)
        {
            lock (gate) {
                if (ticket.Canceled || ticket.Session.Closed) return RollTicketState.Canceled;
                if (ticket.Values == null) return RollTicketState.Pending;
                foreach (var identity in ticket.Identities)
                    if (ticket.Session.Slots[identity].State != SlotState.Consumed) return RollTicketState.Accepted;
                return RollTicketState.Complete;
            }
        }
        internal bool TryGetAccepted(RollTicket ticket, out int[] faces)
        {
            lock (gate) {
                faces = null;
                if (ticket.Canceled || ticket.Session.Closed || ticket.Values == null) return false;
                faces = (int[])ticket.Values.Clone(); return true;
            }
        }
        internal bool SessionClosed(RollSession session) { lock (gate) return session.Closed; }

        internal bool TryEnterNative(RollSession session, RollBinding observed, IEnumerable<FaceIdentity> exactOrder,
            out NativeRollLease lease, out string reason)
        {
            lease = null; reason = ""; bool oversized; FaceIdentity[] identities;
            try { identities = Copy(exactOrder, MaxExactLeaseFaces, out oversized); }
            catch (Exception) { reason = "Could not read the native face plan."; return false; }
            lock (gate) {
                if (session.Closed || !sessions.Contains(session)) { reason = "Session canceled."; return false; }
                if (observed == null || !ReferenceEquals(observed.Action, session.Action)) { reason = "Native action mismatch."; return false; }
                if (pending != null && ReferenceEquals(pending.Session, session)) { reason = "Faces are still pending."; return false; }
                if (oversized || identities == null || identities.Length == 0) { reason = "No supported accepted native face plan."; return false; }
                var selected = new Slot[identities.Length]; var unique = new HashSet<FaceIdentity>();
                for (int i = 0; i < identities.Length; i++) {
                    Slot slot; var identity = identities[i];
                    if (identity == null || !identity.Binding.Equals(observed) || !unique.Add(identity)
                        || !session.Slots.TryGetValue(identity, out slot) || slot.State != SlotState.Available || slot.Ticket.Canceled) {
                        reason = "Native identity, state snapshot, or accepted face mismatch."; return false;
                    }
                    selected[i] = slot;
                }
                lease = new NativeRollLease(this, session, selected);
                foreach (var slot in selected) { slot.State = SlotState.Reserved; slot.Lease = lease; }
                native.Add(lease); return true;
            }
        }
        /// <summary>Mask an outer lease while an unprepared nested native roll runs.</summary>
        public NativeRollLease SuspendNative()
        {
            lock (gate) { var lease = new NativeRollLease(this, null, new Slot[0]); native.Add(lease); return lease; }
        }
        internal bool TryConsume(NativeRollLease lease, FaceIdentity observed, out int face)
        {
            lock (gate) {
                face = 0;
                if (lease.Closed || lease.Session == null || lease.Session.Closed || native.Count == 0
                    || !ReferenceEquals(native[native.Count - 1], lease) || lease.Next >= lease.Slots.Length) return false;
                var slot = lease.Slots[lease.Next];
                if (observed == null || !slot.Identity.Equals(observed) || slot.State != SlotState.Reserved
                    || !ReferenceEquals(slot.Lease, lease) || slot.Ticket.Canceled) return false;
                face = slot.Ticket.Values[slot.Index]; slot.State = SlotState.Consumed; lease.Next++; return true;
            }
        }
        internal int Remaining(NativeRollLease lease) { lock (gate) return lease.Closed ? 0 : lease.Slots.Length - lease.Next; }
        internal void Close(NativeRollLease lease)
        {
            lock (gate) {
                if (lease.Closed) return;
                lease.Closed = true; native.Remove(lease);
                foreach (var slot in lease.Slots) if (slot.State == SlotState.Reserved && ReferenceEquals(slot.Lease, lease)) {
                    slot.State = SlotState.Canceled; slot.Ticket.Canceled = true;
                }
            }
        }
        internal void Close(RollSession session)
        {
            lock (gate) {
                if (session.Closed) return;
                session.Closed = true;
                foreach (var ticket in session.Tickets) CancelTicket(ticket);
                for (int i = native.Count - 1; i >= 0; i--) if (ReferenceEquals(native[i].Session, session)) Close(native[i]);
                sessions.Remove(session);
            }
        }
        public void Reset()
        {
            lock (gate) {
                foreach (var session in new List<RollSession>(sessions)) Close(session);
                foreach (var lease in new List<NativeRollLease>(native)) Close(lease);
                pending = null;
                // nextId intentionally survives reset: a late UI callback cannot accept a new request.
            }
        }
    }
}
