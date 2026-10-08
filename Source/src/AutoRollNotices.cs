using System;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ManualDiceRoller
{
    // Transparency for the rolls this mod cannot (or was told not to) ask for:
    // when the game rolls a d20 for a party member in an enabled category, a
    // short corner notice shows what was rolled and the session roll history
    // keeps it. It never changes the roll.
    internal static class AutoRollNotices
    {
        const string NoticeTitle = "ROLLED BY THE GAME";
        static string lastText;
        static float lastShown;
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RulesetActor), "RollDie"),
                postfix: new HarmonyMethod(typeof(AutoRollNotices).GetMethod(nameof(RollPostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            // The UI's wiring of the display-only multiplayer hooks: the
            // controlling player's name for the waiting banner, and other
            // players' agreed faces for the roll history.
            MultiplayerRolls.PlayerName = PlayerName;
            MultiplayerRolls.RemoteFacesAgreed = (prompt, player, faces) => RollHistory.AddRemote(prompt, player, faces);
            // Manual dice paused after the players' games disagreed.
            MultiplayerRolls.Paused = reason => {
                if (MultiplayerRolls.Mismatch(reason)) RollWindow.ShowNotice("MANUAL DICE OFF", "Versions or roll settings differ between players", null, 20f);
                else RollWindow.ShowNotice("MANUAL DICE PAUSED", "The game rolls until you reload together", null, 20f);
            };
            // Same dice, but the two games' own results differed (once per session).
            MultiplayerRolls.Diverged = () => RollWindow.ShowNotice("GAMES DIFFER", "Results differed; reload if your screens differ", null, 15f);
        }
        static void RollPostfix(RulesetActor __instance, RuleDefinitions.DieType dieType, RuleDefinitions.RollContext rollContext,
            RuleDefinitions.AdvantageType advantageType, int firstRoll, int secondRoll, string skill, int __result)
        {
            try
            {
                if (dieType != RuleDefinitions.DieType.D20 || Plugin.Settings == null) return;
                if (NativeProbe.Active || NativeAbilityPlanner.Previewing || NativeSavePlanner.Previewing) return;
                if (RuntimeRolls.CurrentDrawWasManual) return;
                if (!RuntimeRolls.IsPartyMember(__instance) || !RuntimeRolls.CanManual(__instance, rollContext)) return;
                string what = Describe(rollContext, skill);
                if (what == null) return;
                bool paired = advantageType != RuleDefinitions.AdvantageType.None && secondRoll > 0;
                string faces = !paired ? firstRoll.ToString()
                    : firstRoll + " and " + secondRoll + (advantageType == RuleDefinitions.AdvantageType.Advantage ? ", kept higher" : ", kept lower");
                string actor = GameNames.ActorName(__instance);
                string text = actor + " · " + what + ": " + faces;
                float now = UnityEngine.Time.realtimeSinceStartup;
                // Collapse an identical notice repeated within two seconds.
                if (text == lastText && now - lastShown < 2f) return;
                lastText = text; lastShown = now;
                string kind = RollKinds.Of(rollContext, what);
                // The history keeps every game d20, even with notices turned off.
                try {
                    RollHistory.AddNotice(what, actor, kind, paired ? new[] { firstRoll, secondRoll } : new[] { firstRoll },
                        !paired ? 0 : advantageType == RuleDefinitions.AdvantageType.Advantage ? 1 : -1);
                } catch (Exception) { }
                if (Plugin.Settings.AutoRollNotices) RollWindow.ShowNotice(NoticeTitle, text, kind);
            }
            catch (Exception) { }
        }
        static string Describe(RuleDefinitions.RollContext context, string skill)
        {
            switch (context)
            {
                case RuleDefinitions.RollContext.AttackRoll: return "Attack roll";
                case RuleDefinitions.RollContext.AbilityCheck: return string.IsNullOrEmpty(skill) ? "Ability check" : RollText.Proficiency(skill) + " check";
                case RuleDefinitions.RollContext.SavingThrow: return "Saving throw";
                case RuleDefinitions.RollContext.ConcentrationCheck: return "Concentration";
                case RuleDefinitions.RollContext.InitiativeRoll: return "Initiative";
                case RuleDefinitions.RollContext.DeathSavingThrow: return "Death save";
                default: return null;
            }
        }
        // The controlling player's display name, read locally from the game's
        // own controller list (or the Photon room). No network traffic.
        static string PlayerName(int actor)
        {
            string name = null;
            try {
                var service = ServiceRepository.GetService<IPlayerControllerService>();
                var controller = service == null ? null : service.GetPlayerController(actor);
                if (controller != null) name = controller.Nickname;
            } catch (Exception) { name = null; }
            if (string.IsNullOrEmpty(name)) {
                try {
                    var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                    var player = room == null ? null : room.GetPlayer(actor);
                    if (player != null) name = player.NickName;
                } catch (Exception) { name = null; }
            }
            return Clean(name);
        }
        // Player names are free text: one short line, no control characters.
        internal static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var text = new StringBuilder(name.Length);
            foreach (char c in name) text.Append(char.IsControl(c) ? ' ' : c);
            string result = text.ToString().Trim();
            if (result.Length > 24) result = result.Substring(0, 23).TrimEnd() + "…";
            return result.Length == 0 ? null : result;
        }
    }
}
