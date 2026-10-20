using System;
using System.IO;
using Terraria.ModLoader;

namespace TerraStorage
{
    // Mod entry point for Requisition. Handles all incoming network packets
    // by delegating to the centralized <see cref="Systems.NetworkHandler"/>.
    public class Requisition : Mod
    {
        public static string DebugLogPath { get; private set; }
        public static ModKeybind OpenRemoteTerminalHotkey { get; private set; }

        public override void Load()
        {
            Terraria.On_Player.QuickStackAllChests += Systems.QuickStackSystem.OnQuickStackAllChests;
            OpenRemoteTerminalHotkey = KeybindLoader.RegisterKeybind(this, "Open Remote Terminal", "None");

            string logDir = Path.Combine(AppContext.BaseDirectory, "tModLoader-Logs");
            DebugLogPath = Path.Combine(logDir, "requisition_debug.log");

            // The debug log is a convenience, so nothing about it may fail the mod's load. A file
            // locked by another process (Host & Play runs a server from the same install, writing
            // the same log) or an unwritable install directory just means no debug log this session.
            try
            {
                Directory.CreateDirectory(logDir);

                // Rotate: keep last 3 sessions
                // .log → .1.log → .2.log → deleted
                string slot2 = Path.Combine(logDir, "requisition_debug.2.log");
                string slot1 = Path.Combine(logDir, "requisition_debug.1.log");

                if (File.Exists(slot2)) File.Delete(slot2);
                if (File.Exists(slot1)) File.Move(slot1, slot2);
                if (File.Exists(DebugLogPath)) File.Move(DebugLogPath, slot1);

                File.WriteAllText(DebugLogPath, $"=== Requisition Debug Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Warn($"Debug log unavailable this session: {ex.Message}");
                DebugLogPath = null;
            }
        }

        public override void Unload()
        {
            Terraria.On_Player.QuickStackAllChests -= Systems.QuickStackSystem.OnQuickStackAllChests;
            Common.PlainItemCache.Clear();
        }

        public override void HandlePacket(System.IO.BinaryReader reader, int whoAmI)
        {
            Systems.NetworkHandler.HandlePacket(this, reader, whoAmI);
        }
    }
}
