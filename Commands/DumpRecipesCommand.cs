using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using TerraStorage.Common;
using TerraStorage.Content.Tiles;
using TerraStorage.Systems;

namespace TerraStorage.Commands
{
    // Diagnostic command: dumps the full recipe graph, recipe groups, and the nearest Terminal's
    // storage network (stations, conditions, item counts) to a text file. Lets crafting-panel
    // performance be analyzed against a real game's data instead of synthetic fixtures.
    // Usage: type  /tsdump  in chat while standing next to your Terminal.
    public class DumpRecipesCommand : ModCommand
    {
        public override string Command => "tsdump";
        public override CommandType Type => CommandType.Chat;
        public override string Usage => "/tsdump  (run next to your Terminal)";
        public override string Description => "Dump all recipes + current storage to a file for performance analysis.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            var player = Main.LocalPlayer;

            // Resolve the nearest Terminal's network so the dump reflects the player's real storage.
            List<Guid> diskIds = new();
            HashSet<int> stations = new();
            HashSet<CraftingCondition> conditions = new();
            var terminal = FindNearestTerminal(player);
            if (terminal != null)
            {
                diskIds = terminal.GetConnectedDiskIds();
                (stations, conditions) = terminal.GetStationsAndConditions();
            }

            var counts = StorageWorldSystem.Instance?.GetItemCounts(diskIds) ?? new Dictionary<int, int>();

            var sb = new StringBuilder();
            sb.AppendLine($"# numRecipes={Recipe.numRecipes} itemCount={ItemLoader.ItemCount} terminalFound={terminal != null} storedTypes={counts.Count}");
            sb.AppendLine("STATIONS: " + string.Join(" ", stations));
            sb.AppendLine("CONDITIONS: " + string.Join(" ", conditions.Select(c => c.ToString())));

            // "type count" is the format the tests parse; everything after the '#' is for a reader.
            // Item type ids are assigned at load time, so a dump without names cannot be read back
            // against anyone else's mod list.
            sb.AppendLine("STORAGE:");
            foreach (var kvp in counts)
            {
                sb.Append(kvp.Key).Append(' ').Append(kvp.Value)
                  .Append("  # ").Append(Lang.GetItemNameValue(kvp.Key))
                  .AppendLine();
            }

            sb.AppendLine("GROUPS:");
            foreach (var kvp in RecipeGroup.recipeGroups)
                sb.AppendLine($"{kvp.Key} {string.Join(" ", kvp.Value.ValidItems)}");

            sb.AppendLine("RECIPES:");
            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                var r = Main.recipe[i];
                if (r?.createItem == null || r.createItem.type <= ItemID.None)
                    continue;

                var ings = r.requiredItem
                    .Where(it => it != null && it.type > ItemID.None)
                    .Select(it => $"{it.type}:{it.stack}");
                var tiles = r.requiredTile.Where(t => t >= 0);

                var ingredientNames = r.requiredItem
                    .Where(it => it != null && it.type > ItemID.None)
                    .Select(it => $"{it.stack}x {Lang.GetItemNameValue(it.type)}");

                sb.Append(r.createItem.type).Append(':').Append(r.createItem.stack)
                  .Append(" | ").Append(string.Join(",", ings))
                  .Append(" | tiles:").Append(string.Join(",", tiles))
                  .Append(" | groups:").Append(string.Join(",", r.acceptedGroups))
                  .Append("  # ").Append(Lang.GetItemNameValue(r.createItem.type))
                  .Append(" <- ").Append(string.Join(" + ", ingredientNames))
                  .AppendLine();
            }

            string path = Path.Combine(Main.SavePath, "ts_recipe_dump.txt");
            try
            {
                File.WriteAllText(path, sb.ToString());
                caller.Reply($"Dumped {Recipe.numRecipes} recipes, {RecipeGroup.recipeGroups.Count} groups, {counts.Count} stored types.");
                caller.Reply("File: " + path);
                if (terminal == null)
                    caller.Reply("WARNING: no Terminal found nearby — STORAGE is empty. Stand next to your Terminal and rerun.");
            }
            catch (Exception ex)
            {
                caller.Reply("Dump failed: " + ex.Message);
            }
        }

        private static TerminalEntity FindNearestTerminal(Player player)
        {
            TerminalEntity best = null;
            float bestSq = float.MaxValue;
            foreach (var kvp in TileEntity.ByID)
            {
                if (kvp.Value is not TerminalEntity t)
                    continue;
                float dx = player.Center.X - (t.Position.X * 16f + 24f);
                float dy = player.Center.Y - (t.Position.Y * 16f + 24f);
                float d = dx * dx + dy * dy;
                if (d < bestSq) { bestSq = d; best = t; }
            }
            return best;
        }
    }
}
