using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace ManualDiceRoller
{
    // Borrows the game's own typefaces for the roll window. Solasta draws its UI
    // with TextMeshPro; a dynamic TMP font keeps its source Font, which IMGUI can
    // use directly. The most common font across the game's labels becomes the
    // body face and the font used by its largest labels becomes the title face.
    // Nothing is created or modified; on any failure the default font is kept.
    internal static class GameTheme
    {
        static RollTheme cached;
        static int attempts;
        static float nextAttempt;

        internal static RollTheme Resolve()
        {
            if (cached != null && cached.Title != null && cached.Body != null) return cached;
            float now = Time.realtimeSinceStartup;
            if (attempts >= 6 || now < nextAttempt) return cached;
            attempts++; nextAttempt = now + 10;
            try
            {
                var usage = new Dictionary<TMP_FontAsset, int>();
                var size = new Dictionary<TMP_FontAsset, float>();
                foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                {
                    if (text == null) continue;
                    var font = text.font;
                    if (font == null || font.sourceFontFile == null) continue;
                    int count; usage.TryGetValue(font, out count); usage[font] = count + 1;
                    float largest; size.TryGetValue(font, out largest); size[font] = Math.Max(largest, text.fontSize);
                }
                var ranked = usage.Where(pair => pair.Value >= 3).OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList();
                TMP_FontAsset body = ranked.FirstOrDefault();
                TMP_FontAsset title = ranked.Where(font => font != body).OrderByDescending(font => size[font]).FirstOrDefault() ?? body;
                var theme = new RollTheme { Body = body == null ? null : body.sourceFontFile, Title = title == null ? null : title.sourceFontFile };
                if (theme.Body == null && theme.Title == null) theme = FromFonts();
                if (theme != null && (theme.Body != null || theme.Title != null)) cached = theme;
            }
            catch (Exception error) { Plugin.Log("Game fonts are unavailable for the roll window: " + error.Message); attempts = 6; }
            return cached;
        }
        static readonly string[] TitleHints = { "title", "cinzel", "trajan", "serif", "garamond", "philosopher", "display" };
        static readonly string[] Ignored = { "arial", "liberation", "legacyruntime", "consola", "courier", "mono" };
        static RollTheme FromFonts()
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>().Where(font => font != null && !string.IsNullOrEmpty(font.name)
                && !Ignored.Any(word => font.name.ToLowerInvariant().Contains(word))).ToList();
            if (fonts.Count == 0) return null;
            var title = fonts.FirstOrDefault(font => TitleHints.Any(word => font.name.ToLowerInvariant().Contains(word)));
            var body = fonts.FirstOrDefault(font => font != title) ?? title;
            return new RollTheme { Title = title ?? body, Body = body };
        }
        internal static void Reset() { cached = null; attempts = 0; nextAttempt = 0; }
    }
}
