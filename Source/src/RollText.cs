using System;
using System.Collections.Generic;
using System.Text;

namespace ManualDiceRoller
{
    // Display-only text for roll prompts. This file has no game dependencies;
    // the plugin installs a resolver that returns the game's own localized
    // names (abilities, skills, tools, damage types, features), so prompts read
    // like the rest of the UI in every language. Unknown names fall back to
    // readable words ("SleightOfHand" -> "Sleight Of Hand").
    internal static class RollText
    {
        // (kind, internal name) -> localized title, or null when unknown.
        internal static Func<string, string, string> Resolver;
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static string Check(string ability, string skill)
        {
            string a = Ability(ability), s = Proficiency(skill);
            if (string.IsNullOrEmpty(s) || s == a) return a;
            if (string.IsNullOrEmpty(a)) return s;
            return a + " (" + s + ")";
        }
        internal static string Save(string ability)
        {
            string a = Ability(ability);
            return string.IsNullOrEmpty(a) ? "" : a + " save";
        }
        internal static string Ability(string name) { return Lookup("ability", name); }
        // A condition, spell or feature that grants a bonus (e.g. Blessed).
        internal static string Source(string name)
        {
            string text = Lookup("source", name);
            foreach (var prefix in new[] { "Condition ", "Feature ", "Spell " })
                if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length) return text.Substring(prefix.Length);
            return text;
        }
        internal static string Proficiency(string name) { return Lookup("proficiency", name); }
        internal static string Damage(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string text = Lookup("damage", name);
            if (text.StartsWith("Damage ", StringComparison.Ordinal)) text = text.Substring(7);
            return text.Length == 0 ? "" : text + " damage";
        }
        internal static string RerollHint(int die, int count, int rolled, string featureName)
        {
            string source = featureName == "DieRollModifierHalfingLucky" ? "Lucky" : Lookup("feature", featureName);
            if (string.IsNullOrEmpty(source)) source = "A reroll feature";
            string which = count > 1 ? "Die " + (die + 1) + " rolled " : "You rolled ";
            return which + rolled + ". " + source + " lets you reroll it - enter the new raw face.";
        }
        static string Lookup(string kind, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string key = kind + ":" + name, value;
            if (cache.TryGetValue(key, out value)) return value;
            value = null;
            var resolver = Resolver;
            if (resolver != null)
            {
                try { value = resolver(kind, name); }
                catch (Exception) { value = null; }
            }
            if (string.IsNullOrEmpty(value)) value = Words(name);
            if (cache.Count > 512) cache.Clear();
            cache[key] = value;
            return value;
        }
        internal static string Words(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]) && value[i - 1] != ' ') text.Append(' ');
                text.Append(c == '_' ? ' ' : c);
            }
            return text.ToString();
        }
        internal static void Reset() { cache.Clear(); }
    }
}
