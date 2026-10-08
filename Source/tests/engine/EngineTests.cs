using System;
using System.Collections.Generic;
using ManualDiceRoller;

static class EngineTests
{
    static int checks;
    sealed class Named
    {
        public string Name;
        public override bool Equals(object other) { return other is Named && ((Named)other).Name == Name; }
        public override int GetHashCode() { return Name == null ? 0 : Name.GetHashCode(); }
    }
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception("Check failed: " + message); }
    static RollBinding Binding(object actor, object action, object effect = null, object form = null,
        string role = "AbilityCheck", int dieFaces = 20, int invocationIndex = 0, string state = "native snapshot")
    { return new RollBinding(actor, action, effect, form, role, dieFaces, invocationIndex, state); }
    static RollTicket Request(RollSession session, params FaceIdentity[] faces)
    { RollTicket ticket; string reason; Check(session.TryRequest(faces, out ticket, out reason), "request: " + reason); return ticket; }
    static void Accept(RollBroker broker, RollTicket ticket, params int[] values)
    { string reason; Check(broker.TryAccept(ticket.Id, values, out reason), "accept: " + reason); }
    static NativeRollLease Lease(RollSession session, RollBinding binding, params FaceIdentity[] faces)
    { NativeRollLease lease; string reason; Check(session.TryEnterNative(binding, faces, out lease, out reason), "enter native: " + reason); return lease; }
    static void NoLease(RollSession session, RollBinding binding, FaceIdentity[] faces, string message)
    { NativeRollLease lease; string reason; Check(!session.TryEnterNative(binding, faces, out lease, out reason) && lease == null && reason.Length > 0, message); }

    static void ExactOriginalAndReroll()
    {
        var broker = new RollBroker(); var actor = new object(); var action = new object();
        var feature = new Named { Name = "HalflingLucky" }; var binding = Binding(actor, action);
        var original = new FaceIdentity(binding, 0, 0);
        var replacement = new FaceIdentity(binding, 0, 0, feature, 0, 1);
        using (var session = broker.BeginSession(action)) {
            var first = Request(session, original);
            Check(first.IsPending && !first.IsAccepted && !first.IsCanceled, "awaiting original face");
            NoLease(session, binding, new[] { original }, "synchronous native entry cannot wait for pending input");
            Accept(broker, first, 1);
            Check(first.IsAccepted && !first.IsPending && !first.IsComplete, "accepted original ready for native");
            var lucky = Request(session, replacement); Accept(broker, lucky, 13);
            int[] preview; Check(lucky.TryGetAccepted(out preview) && preview[0] == 13, "planner can read exact replacement");
            using (var lease = Lease(session, binding, original, replacement)) {
                int face;
                Check(!lease.TryConsume(replacement, out face) && face == 0, "replacement cannot steal original");
                Check(lease.TryConsume(original, out face) && face == 1, "native original is exact entered 1");
                Check(first.IsComplete && lease.Remaining == 1, "original consumed once");
                var sameNameOtherFeature = new FaceIdentity(binding, 0, 0, new Named { Name = "HalflingLucky" }, 0, 1);
                Check(!lease.TryConsume(sameNameOtherFeature, out face), "same-named different feature cannot take replacement");
                Check(!lease.TryConsume(new FaceIdentity(binding, 0, 0, feature, 1, 1), out face), "feature occurrence matches");
                Check(!lease.TryConsume(new FaceIdentity(binding, 0, 0, feature, 0, 2), out face), "reroll attempt matches");
                Check(lease.TryConsume(replacement, out face) && face == 13, "native Lucky replacement uses exact entered face");
                Check(!lease.TryConsume(replacement, out face) && face == 0 && lucky.IsComplete, "replacement cannot repeat");
            }
            NoLease(session, binding, new[] { original }, "consumed original cannot be leased again");
        }
    }

    static void VectorValidationAndCopies()
    {
        var broker = new RollBroker(); var action = new object(); var binding = Binding(new object(), action, dieFaces: 4, role: "MagicDamage");
        var faces = new[] { new FaceIdentity(binding, 0, 0), new FaceIdentity(binding, 1, 0), new FaceIdentity(binding, 2, 0) };
        using (var session = broker.BeginSession(action)) {
            var mutable = new List<FaceIdentity>(faces); RollTicket ticket; string reason;
            Check(session.TryRequest(mutable, out ticket, out reason), "vector request"); mutable.Clear();
            var copy = ticket.CopyIdentities(); copy[0] = null;
            Check(ticket.Count == 3 && ticket.CopyIdentities()[0] != null, "request owns a copied identity vector");
            int[] accepted;
            Check(!ticket.TryGetAccepted(out accepted) && accepted == null, "pending preview unavailable");
            Check(!broker.TryAccept(ticket.Id, null, out reason), "null input rejected");
            Check(!broker.TryAccept(ticket.Id, new[] { 4 }, out reason), "aggregate total cannot replace faces");
            Check(!broker.TryAccept(ticket.Id, new[] { 1, 0, 2 }, out reason), "zero face rejected");
            Check(!broker.TryAccept(ticket.Id, new[] { 1, 5, 2 }, out reason), "out of range face rejected atomically");
            Check(ticket.IsPending && !ticket.TryGetAccepted(out accepted), "invalid input changes no slots");
            var typed = new[] { 1, 4, 2 }; Accept(broker, ticket, typed); typed[0] = 4;
            Check(ticket.TryGetAccepted(out accepted) && accepted[0] == 1, "input vector copied"); accepted[1] = 1;
            Check(ticket.TryGetAccepted(out accepted) && accepted[1] == 4, "preview vector copied");
            Check(!broker.TryAccept(ticket.Id, new[] { 2, 2, 2 }, out reason), "accepted ticket cannot be edited by late UI");
            using (var lease = Lease(session, binding, faces)) {
                int face;
                Check(!lease.TryConsume(faces[1], out face), "later die cannot shift into skipped original");
                Check(lease.TryConsume(faces[0], out face) && face == 1, "first exact face");
                Check(lease.TryConsume(faces[1], out face) && face == 4, "second exact face");
                Check(lease.TryConsume(faces[2], out face) && face == 2, "third exact face");
            }
        }
    }

    static void StrictBindingAndState()
    {
        var broker = new RollBroker(); var actor = new Named { Name = "Ellie" }; var action = new object(); var effect = new Named { Name = "Spell" }; var form = new Named { Name = "Damage" };
        var binding = Binding(actor, action, effect, form); var identity = new FaceIdentity(binding, 0, 0);
        using (var session = broker.BeginSession(action)) {
            var ticket = Request(session, identity); Accept(broker, ticket, 7);
            NoLease(session, Binding(new Named { Name = "Ellie" }, action, effect, form), new[] { identity }, "same-name actor is not owner");
            NoLease(session, Binding(actor, new object(), effect, form), new[] { identity }, "different action is not owner");
            NoLease(session, Binding(actor, action, new Named { Name = "Spell" }, form), new[] { identity }, "same-name effect is not effect");
            NoLease(session, Binding(actor, action, effect, new Named { Name = "Damage" }), new[] { identity }, "same-name form is not form");
            NoLease(session, Binding(actor, action, effect, form, "SavingThrow"), new[] { identity }, "native role cannot change");
            NoLease(session, Binding(actor, action, effect, form, dieFaces: 12), new[] { identity }, "native die cannot change");
            NoLease(session, Binding(actor, action, effect, form, invocationIndex: 1), new[] { identity }, "later invocation cannot repeat earlier face");
            NoLease(session, Binding(actor, action, effect, form, state: "changed features or form"), new[] { identity }, "mutated native inputs invalidate accepted state");
            actor.Name = "Changed actor"; effect.Name = "Changed spell"; form.Name = "Changed form";
            using (var lease = Lease(session, Binding(actor, action, effect, form), identity)) {
                int face; Check(lease.TryConsume(identity, out face) && face == 7, "reference hash remains stable after object name mutation");
            }
            RollTicket duplicate; string reason;
            Check(!session.TryRequest(new[] { identity }, out duplicate, out reason), "same invocation slot cannot be reissued");
            Check(!session.TryRequest(new[] { new FaceIdentity(Binding(actor, new object()), 0, 0) }, out duplicate, out reason), "request action matches session");
        }
        Check(typeof(RollBinding).GetProperty("Role").GetSetMethod() == null && typeof(FaceIdentity).GetProperty("DieIndex").GetSetMethod() == null, "identity properties expose no public setters");
    }

    static void NestedScopesAndSuppression()
    {
        var broker = new RollBroker(); var action = new object(); var actor = new object();
        var parentBinding = Binding(actor, action, invocationIndex: 0); var childBinding = Binding(actor, action, invocationIndex: 1);
        var parent = new FaceIdentity(parentBinding, 0, 0); var child = new FaceIdentity(childBinding, 0, 0);
        using (var session = broker.BeginSession(action)) {
            Accept(broker, Request(session, parent), 8); Accept(broker, Request(session, child), 12);
            using (var outer = Lease(session, parentBinding, parent)) {
                using (var inner = Lease(session, childBinding, child)) {
                    int face; Check(!outer.TryConsume(parent, out face), "nested scope blocks parent ownership");
                    Check(!inner.TryConsume(parent, out face), "child cannot consume parent face");
                    Check(inner.TryConsume(child, out face) && face == 12, "child consumes its own face");
                }
                using (var suppressed = broker.SuspendNative()) {
                    int face; Check(!outer.TryConsume(parent, out face), "automatic nested call masks parent");
                    Check(!suppressed.TryConsume(parent, out face), "automatic nested call receives native fallback");
                }
                int result; Check(outer.TryConsume(parent, out result) && result == 8, "parent resumes after nested scope");
            }
        }
    }

    static void CancellationAndDisposal()
    {
        var broker = new RollBroker(); var action = new object(); var otherAction = new object(); var actor = new object();
        var binding = Binding(actor, action); var face = new FaceIdentity(binding, 0, 0);
        using (var session = broker.BeginSession(action)) using (var sibling = broker.BeginSession(otherAction)) {
            var ticket = Request(session, face); RollTicket other; string reason;
            var siblingFace = new FaceIdentity(Binding(actor, otherAction), 0, 0);
            Check(!sibling.TryRequest(new[] { siblingFace }, out other, out reason) && broker.IsPromptBusy, "one owning prompt at a time");
            Check(broker.Cancel(ticket.Id) && ticket.IsCanceled && !broker.IsPromptBusy, "cancel releases prompt");
            Check(!broker.TryAccept(ticket.Id, new[] { 17 }, out reason), "late acceptance cannot revive cancellation");
            var next = Request(sibling, siblingFace); Accept(broker, next, 9);
            using (var lease = Lease(sibling, siblingFace.Binding, siblingFace)) {
                sibling.Cancel(); int ignored;
                Check(!lease.TryConsume(siblingFace, out ignored) && next.IsCanceled, "cancel revokes active native lease");
            }
            Check(sibling.IsCanceled, "session cancellation observable");
        }
        using (var session = broker.BeginSession(action)) {
            var first = new FaceIdentity(Binding(actor, action, invocationIndex: 2), 0, 0); var second = new FaceIdentity(first.Binding, 0, 1);
            var ticket = Request(session, first, second); Accept(broker, ticket, 4, 18);
            using (var lease = Lease(session, first.Binding, first, second)) {
                int value; Check(lease.TryConsume(first, out value) && value == 4, "partial native playback");
            }
            Check(ticket.IsCanceled, "disposing partial playback revokes unused faces");
            NoLease(session, first.Binding, new[] { second }, "unused face cannot shift into a later call");
        }
        var disposed = broker.BeginSession(action); var pending = Request(disposed, new FaceIdentity(binding, 0, 0)); disposed.Dispose(); disposed.Dispose();
        Check(pending.IsCanceled && !broker.IsPromptBusy, "session disposal is idempotent and releases pending wait");
    }

    static void ResetAndInvalidRequests()
    {
        var broker = new RollBroker(); var action = new object(); var binding = Binding(null, action, role: "StaticDamage", dieFaces: 6);
        var face = new FaceIdentity(binding, 0, 0); var session = broker.BeginSession(action); var first = Request(session, face);
        broker.Reset(); string reason;
        Check(first.IsCanceled && session.IsCanceled && !broker.IsPromptBusy, "reset cancels all sessions and pending tickets");
        using (var replacementSession = broker.BeginSession(action)) {
            var replacement = Request(replacementSession, face);
            Check(replacement.Id > first.Id, "reset preserves monotonic correlation IDs");
            Check(!broker.TryAccept(first.Id, new[] { 6 }, out reason) && replacement.IsPending, "old callback cannot accept new prompt");
            Accept(broker, replacement, 5);
            using (var lease = Lease(replacementSession, binding, face)) {
                broker.Reset(); int value; Check(!lease.TryConsume(face, out value), "reset revokes active native injection");
            }
        }
        using (var valid = broker.BeginSession(action)) {
            RollTicket ticket;
            Check(!valid.TryRequest(null, out ticket, out reason), "null plan rejected");
            Check(!valid.TryRequest(new FaceIdentity[0], out ticket, out reason), "empty plan rejected");
            Check(!valid.TryRequest(new[] { face, face }, out ticket, out reason), "duplicate exact identities rejected");
            var many = new FaceIdentity[513]; for (int i = 0; i < many.Length; i++) many[i] = new FaceIdentity(binding, i, 0);
            Check(!valid.TryRequest(many, out ticket, out reason), "oversized request rejected");
            Check(!valid.TryRequest(new FaceIdentity[] { null }, out ticket, out reason), "null face identity rejected");
            var paired = new FaceIdentity[512]; var exact = new int[512];
            for (int i = 0; i < paired.Length; i++) { paired[i] = new FaceIdentity(binding, i / 2, i % 2); exact[i] = i % 6 + 1; }
            var maximum = Request(valid, paired); Accept(broker, maximum, exact);
            using (var lease = Lease(valid, binding, paired)) {
                bool all = true; for (int i = 0; i < paired.Length; i++) { int value; all &= lease.TryConsume(paired[i], out value) && value == exact[i]; }
                Check(all && maximum.IsComplete && lease.Remaining == 0, "256 paired dice collect and consume every one of 512 exact faces");
            }
        }
        bool badBinding = false; try { Binding(null, action, dieFaces: 0); } catch (ArgumentOutOfRangeException) { badBinding = true; }
        Check(badBinding, "invalid die cannot construct binding");
        bool badStage = false; try { new FaceIdentity(binding, 0, 0, new object()); } catch (ArgumentException) { badStage = true; }
        Check(badStage, "replacement requires immutable feature stage");
    }

    static IEnumerable<FaceIdentity> CountedFaces(RollBinding binding, Action read, Action disposed, bool throws = false)
    {
        try {
            for (int i = 0; ; i++) {
                read();
                if (throws && i == 2) throw new InvalidOperationException("Malformed plan producer");
                yield return new FaceIdentity(binding, i, 0);
            }
        } finally { disposed(); }
    }
    static void BoundedPlansAndSessionBudget()
    {
        var broker = new RollBroker(); var action = new object(); var binding = Binding(new object(), action); string reason;
        using (var session = broker.BeginSession(action)) {
            int reads = 0; bool disposed = false; RollTicket ticket;
            Check(!session.TryRequest(CountedFaces(binding, () => reads++, () => disposed = true), out ticket, out reason), "Infinite request producer is rejected without materializing it");
            Check(reads == RollBroker.MaxFacesPerRequest + 1 && disposed && !broker.IsPromptBusy, "Oversized request reads only its sentinel and disposes producer");
            reads = 0; disposed = false;
            Check(!session.TryRequest(CountedFaces(binding, () => reads++, () => disposed = true, true), out ticket, out reason) && reads == 3 && disposed && !broker.IsPromptBusy, "Throwing request producer fails atomically and disposes");
            var face = new FaceIdentity(binding, 0, 0); var accepted = Request(session, face); Accept(broker, accepted, 13);
            Check(broker.Cancel(accepted.Id) && accepted.IsCanceled, "Accepted ticket may be canceled before native entry");
            NoLease(session, binding, new[] { face }, "Cancel after accept revokes unused native faces");
            Check(!broker.TryAccept(accepted.Id, new[] { 18 }, out reason), "Post-accept canceled callback cannot revive input");
        }
        using (var session = broker.BeginSession(action)) {
            var all = new FaceIdentity[RollBroker.MaxFacesPerSession];
            var tickets = new List<RollTicket>();
            for (int i = 0; i < all.Length; i++) all[i] = new FaceIdentity(binding, i, 0);
            for (int offset = 0; offset < all.Length; offset += RollBroker.MaxFacesPerRequest) {
                int count = Math.Min(RollBroker.MaxFacesPerRequest, all.Length - offset); var group = new FaceIdentity[count]; var values = new int[count];
                Array.Copy(all, offset, group, 0, count); for (int i = 0; i < count; i++) values[i] = 17;
                var ticket = Request(session, group); Accept(broker, ticket, values); tickets.Add(ticket);
            }
            RollTicket extra;
            Check(!session.TryRequest(new[] { new FaceIdentity(binding, all.Length, 0) }, out extra, out reason) && !broker.IsPromptBusy && tickets[0].IsAccepted, "Session budget rejects the first excess slot without changing prior tickets");
            int reads = 0; bool disposed = false; NativeRollLease oversized;
            Check(!session.TryEnterNative(binding, CountedFaces(binding, () => reads++, () => disposed = true), out oversized, out reason), "Infinite native lease producer is bounded");
            Check(reads == RollBroker.MaxExactLeaseFaces + 1 && disposed && oversized == null && tickets[0].IsAccepted, "Oversized native lease is disposed and reserves no slots");
            reads = 0; disposed = false;
            Check(!session.TryEnterNative(binding, CountedFaces(binding, () => reads++, () => disposed = true, true), out oversized, out reason) && reads == 3 && disposed && tickets[0].IsAccepted, "Throwing native lease fails without reserving accepted slots");
            using (var lease = Lease(session, binding, all)) {
                bool exact = true; foreach (var face in all) { int value; exact &= lease.TryConsume(face, out value) && value == 17; }
                Check(exact && lease.Remaining == 0 && tickets.TrueForAll(ticket => ticket.IsComplete), "Full 4096-face session and native lease consume exactly once");
            }
            Check(!session.TryRequest(new[] { new FaceIdentity(binding, all.Length + 1, 0) }, out extra, out reason), "Consumed slots still count toward session's retained identity budget");
        }
    }

    static void Main()
    {
        ExactOriginalAndReroll(); VectorValidationAndCopies(); StrictBindingAndState();
        NestedScopesAndSuppression(); CancellationAndDisposal(); ResetAndInvalidRequests(); BoundedPlansAndSessionBudget();
        Console.WriteLine("PASS: " + checks + " pure broker ownership, exact-face, state, cancellation and stale-input checks.");
    }
}
