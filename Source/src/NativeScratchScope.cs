using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Native preparation reuses actor/manager/effect scratch collections. A
    // preview must not even change the original collection's enumerator version.
    // Temporarily substitute private clones, then restore the original objects.
    internal sealed class NativeScratchScope : IDisposable
    {
        sealed class Saved { internal object Owner, Value; internal FieldInfo Field; }
        readonly List<Saved> saved = new List<Saved>();
        bool disposed;
        internal static NativeScratchScope ForActor(RulesetActor actor)
        {
            var scope = new NativeScratchScope();
            try { scope.AddActor(actor); return scope; }
            catch { scope.Dispose(); throw; }
        }
        internal void AddActor(RulesetActor actor)
        {
            if (actor == null) return;
            AddClone(actor, AccessTools.Field(typeof(RulesetActor), "featuresToBrowse"));
            AddClone(actor, AccessTools.Field(typeof(RulesetActor), "featuresOrigin"));
            AddClone(actor, AccessTools.Field(typeof(RulesetActor), "allConditionsForEnumeration"));
        }
        internal void AddClone(object owner, FieldInfo field)
        {
            if (disposed) throw new ObjectDisposedException("NativeScratchScope");
            if (owner == null || field == null || field.IsStatic || !field.DeclaringType.IsInstanceOfType(owner))
                throw new InvalidOperationException("The verified native scratch field is unavailable.");
            foreach (var item in saved) if (ReferenceEquals(item.Owner, owner) && item.Field == field) return;
            try {
                object original = field.GetValue(owner);
                Type type = field.FieldType;
                if (original == null || original.GetType() != type || !type.IsGenericType)
                    throw new InvalidOperationException("An unsupported native scratch collection was encountered.");
                Type definition = type.GetGenericTypeDefinition();
                Type[] arguments = type.GetGenericArguments();
                object clone;
                if (definition == typeof(List<>)) {
                    var enumerable = typeof(IEnumerable<>).MakeGenericType(arguments);
                    clone = type.GetConstructor(new[] { enumerable }).Invoke(new[] { original });
                } else if (definition == typeof(Dictionary<,>)) {
                    var dictionary = typeof(IDictionary<,>).MakeGenericType(arguments);
                    var comparer = typeof(IEqualityComparer<>).MakeGenericType(arguments[0]);
                    object originalComparer = type.GetProperty("Comparer").GetValue(original, null);
                    clone = type.GetConstructor(new[] { dictionary, comparer }).Invoke(new[] { original, originalComparer });
                } else throw new InvalidOperationException("An unsupported native scratch collection was encountered.");
                saved.Add(new Saved { Owner = owner, Field = field, Value = original });
                field.SetValue(owner, clone);
            } catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Exception failed = null;
            for (int index = saved.Count - 1; index >= 0; index--) {
                var item = saved[index];
                try { item.Field.SetValue(item.Owner, item.Value); }
                catch (Exception error) { if (failed == null) failed = error; }
            }
            saved.Clear();
            if (failed != null) throw new InvalidOperationException("The native scratch collections could not be restored.", failed);
        }
    }
}
