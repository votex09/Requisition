using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.Localization;
using TerraStorage.Common;

namespace TerraStorage.Systems
{
    // A refused storage operation has to say why - in singleplayer, where the crafting panel knows
    // the reason first-hand, and on a multiplayer client, where it arrives as a byte in the
    // server's response. Both speak through here so there is one vocabulary rather than two, and
    // so the network layer never has to reach into a UI element class.
    public static class StorageOperationReporter
    {
        private static readonly StorageOperationFailureThrottle Throttle = new();

        // One click, one refusal, one line. The panel decides locally, so nothing here needs
        // rate limiting - and throttling it would swallow the second of two deliberate clicks,
        // which is the silence this whole vocabulary exists to end.
        public static void ReportFailure(StorageOperationFailure failure)
        {
            Report(failure);
        }

        // One click can become forty packets: deposit-all sends one per inventory slot, and a full
        // network denies every one. Only the answers coming back off the wire are throttled.
        public static void ReportServerDenial(StorageOperationFailure failure)
        {
            if (!Throttle.ShouldReport(failure, Main.GameUpdateCount))
                return;

            Report(failure);
        }

        private static void Report(StorageOperationFailure failure)
        {
            string prefix = Language.GetTextValue(GetPrefixLocalizationKey());
            string reason = Language.GetTextValue(StorageOperationFailures.GetLocalizationKey(failure));

            var (red, green, blue) = GetDenialTextColor();
            Main.NewText(prefix + reason, red, green, blue);

            // Not MenuTick: the multiplayer craft path already ticks when the request is sent, and
            // the same sound for "sent" and "refused" carries no information.
            SoundEngine.PlaySound(SoundID.MenuClose);
        }

        private static string GetPrefixLocalizationKey()
            => "Mods.TerraStorage.UI.OperationFailed.Prefix";

        private static (byte red, byte green, byte blue) GetDenialTextColor() => (255, 100, 100);
    }
}
