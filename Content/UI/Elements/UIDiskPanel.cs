using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Localization;
using Terraria.UI;
using TerraStorage.Common;
using TerraStorage.Content.Items;
using TerraStorage.Content.Tiles;
using TerraStorage.Helpers;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.Elements
{
    // Disks tab panel for the Terminal UI. Shows all disks in connected Drive Bays,
    // their contents, and an in-place upgrade UI that consumes materials from storage.
    public class UIDiskPanel : UIElement
    {
        private const int ListPaneWidth = 220;
        private const int DiskRowHeight = 52;
        private const int ItemCellSize = 40;
        // Height reserved at the bottom of the detail pane for the upgrade section.
        // Sized for up to 3 ingredient rows + button + header.
        private const int UpgradeSectionHeight = 130;
        private const int MaxTierUpgradeSectionHeight = 30;
        private const int DefragButtonHeight = 28;
        private const float UsageBarOffset = 40f;
        private const float ContentsLabelOffset = 14f;
        private const float ContentsGridGap = 18f;
        private const float ContentsGridBottomPadding = 10f;
        private const float DiskListBottomPadding = 6f;

        private static readonly RasterizerState ScissorRasterizer = new() { ScissorTestEnable = true };

        // ---- Upgrade cost tables ----------------------------------------
        // Each DiskTier maps to one or more upgrade options; multiple options
        // exist when the game has alternate materials (e.g., Crimtane/Demonite).

        private static (int itemType, int count)[][] GetUpgradeOptions(DiskTier tier)
            => StorageDiskBase.GetUpgradeOptions(tier);

        private float GetContentsGridTopOffset()
            => UsageBarOffset + ContentsLabelOffset + ContentsGridGap;

        private static bool IsMaxTier(DiskTier tier) => !StorageDiskBase.HasUpgradePath(tier);

        private float GetUpgradeSectionHeight(DiskTier tier)
        {
            bool isMaxTier = IsMaxTier(tier);

            return isMaxTier ? MaxTierUpgradeSectionHeight : UpgradeSectionHeight;
        }

        private float GetContentsGridHeight(float panelHeight, DiskTier tier)
            => panelHeight - GetContentsGridTopOffset() - GetUpgradeSectionHeight(tier)
               - ContentsGridBottomPadding;

        private int GetContentsGridColumns(float paneWidth)
            => Math.Max(1, (int)(paneWidth / ItemCellSize));

        private float GetDiskListHeight(float panelHeight)
            => panelHeight - DefragButtonHeight - DiskListBottomPadding;

        private static int GetDiskItemType(DiskTier tier)
            => StorageDiskBase.GetItemTypeForTier(tier);

        // ---- State ---------------------------------------------------------

        private record struct DiskEntry(DriveBayEntity Bay, int Slot, Item Item, StorageDiskBase Disk);

        private TerminalEntity         _terminal;
        private List<Guid>             _diskIds    = new();
        private HashSet<int>           _stations   = new();
        private HashSet<CraftingCondition> _conditions = new();
        private List<DiskEntry> _disks   = new();
        private int    _selectedIndex    = -1;
        private float  _listScrollPixels = 0f;
        private float  _listScrollTarget = 0f;
        private float  _contentsScroll   = 0f;

        // Consolidated-items cache — rebuilt only when disk or storage version changes
        private List<ConsolidatedItem> _detailItemCache  = new();
        private Guid                   _detailCacheId    = Guid.Empty;
        private long                   _detailCacheVer   = -1;

        // Upgrade button rect in screen space; updated each DrawSelf.
        private Rectangle _upgradeButtonRect = Rectangle.Empty;
        private Rectangle _defragButtonRect  = Rectangle.Empty;

        // Ingredient cache — rebuilt only when selection or storage version changes.
        private struct IngredientState
        {
            public int ItemType, Need, Have;
            //True if the deficit can be crafted from materials in storage.
            public bool Craftable;
        }
        private readonly List<IngredientState> _ingredientStates = new();
        private int  _ingCacheSelectedIdx = -1;
        private long _ingCacheStorageVer  = -1;
        private int  _ingCacheOptionIdx   = -1;   // which option is being shown
        private bool _ingCacheCanAfford;

        // ---- Public API ----------------------------------------------------

        public void SetTerminal(TerminalEntity terminal) => _terminal = terminal;

        public void SetDiskIds(List<Guid> diskIds)
        {
            _diskIds = diskIds ?? new List<Guid>();
            _ingCacheStorageVer = -1; // invalidate so crafting feasibility is re-evaluated
        }

        public void SetStations(HashSet<int> stations, HashSet<CraftingCondition> conditions)
        {
            _stations   = stations   ?? new HashSet<int>();
            _conditions = conditions ?? new HashSet<CraftingCondition>();
        }

        //Re-enumerates all disks from connected Drive Bays.
        public void Refresh()
        {
            _disks.Clear();
            if (_terminal == null) return;

            foreach (var bay in StorageNetwork.FindConnectedDriveBays(_terminal.Position))
            {
                for (int i = 0; i < DriveBayEntity.DiskSlotCount; i++)
                {
                    var item = bay.DiskSlots[i];
                    if (!item.IsAir && item.ModItem is StorageDiskBase disk)
                        _disks.Add(new DiskEntry(bay, i, item, disk));
                }
            }

            // Clamp selection so it stays valid after a refresh.
            if (_selectedIndex >= _disks.Count)
            {
                _selectedIndex = _disks.Count > 0 ? 0 : -1;
                _contentsScroll = 0f;
            }

            _detailCacheVer = -1; // invalidate contents cache
        }

        // ---- Update --------------------------------------------------------

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            var dims = GetInnerDimensions();

            float listHeight    = GetDiskListHeight(dims.Height);
            int visibleDiskRows = Math.Max(1, (int)(listHeight / DiskRowHeight));
            int hiddenDiskRows  = Math.Max(0, _disks.Count - visibleDiskRows);
            float maxListScroll = hiddenDiskRows * DiskRowHeight;

            _listScrollTarget = Math.Clamp(_listScrollTarget, 0, maxListScroll);

            _listScrollPixels += (_listScrollTarget - _listScrollPixels) * 0.15f;
            if (Math.Abs(_listScrollPixels - _listScrollTarget) < 0.5f)
                _listScrollPixels = _listScrollTarget;
            _listScrollPixels = Math.Clamp(_listScrollPixels, 0, maxListScroll);

            int maxContentsScroll = GetMaximumContentsScroll(dims);
            _contentsScroll = Math.Clamp(_contentsScroll, 0, maxContentsScroll);
        }

        private int GetMaximumContentsScroll(CalculatedStyle dims)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _disks.Count)
                return 0;

            var disk        = _disks[_selectedIndex].Disk;
            var items       = GetDetailItems(disk.DiskId);
            float rightW    = dims.Width - ListPaneWidth - 8;
            int columns     = GetContentsGridColumns(rightW);
            float gridH     = GetContentsGridHeight(dims.Height, disk.Tier);
            int visibleRows = Math.Max(1, (int)(gridH / ItemCellSize));
            int totalRows   = (items.Count + columns - 1) / columns;

            return Math.Max(0, totalRows - visibleRows);
        }

        // ---- Helpers -------------------------------------------------------

        private List<ConsolidatedItem> GetDetailItems(Guid diskId)
        {
            long ver = StorageWorldSystem.Instance.StorageVersion;
            if (diskId == _detailCacheId && ver == _detailCacheVer)
                return _detailItemCache;
            _detailCacheId  = diskId;
            _detailCacheVer = ver;
            _detailItemCache = StorageWorldSystem.Instance.GetConsolidatedItems(new[] { diskId });
            return _detailItemCache;
        }

        // ---- Input ---------------------------------------------------------

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);

            var pos = evt.MousePosition;

            // Upgrade button (screen-space rect set during DrawSelf).
            if (!_upgradeButtonRect.IsEmpty && _upgradeButtonRect.Contains((int)pos.X, (int)pos.Y))
            {
                TryUpgrade();
                return;
            }

            // Defragment button.
            if (!_defragButtonRect.IsEmpty && _defragButtonRect.Contains((int)pos.X, (int)pos.Y))
            {
                TryDefragment();
                return;
            }

            // Disk list (left pane).
            var dims = GetInnerDimensions();
            float listHeight = GetDiskListHeight(dims.Height);
            if (pos.X >= dims.X && pos.X < dims.X + ListPaneWidth &&
                pos.Y >= dims.Y && pos.Y < dims.Y + listHeight)
            {
                int row = (int)((pos.Y - dims.Y + _listScrollPixels) / DiskRowHeight);
                if (row >= 0 && row < _disks.Count && row != _selectedIndex)
                {
                    _selectedIndex    = row;
                    _contentsScroll   = 0f;
                    _upgradeButtonRect = Rectangle.Empty;
                }
            }
        }

        public override void ScrollWheel(UIScrollWheelEvent evt)
        {
            base.ScrollWheel(evt);

            var dims    = GetInnerDimensions();
            float notches = -evt.ScrollWheelValue / 120f;

            if (Main.MouseScreen.X < dims.X + ListPaneWidth)
            {
                _listScrollTarget += notches * DiskRowHeight;
            }
            else if (_selectedIndex >= 0 && _selectedIndex < _disks.Count)
            {
                int gridScrollRows = RequisitionClientConfig.GetGridScrollRows();
                _contentsScroll += notches * gridScrollRows;
            }
        }

        // ---- Upgrade logic -------------------------------------------------

        // Rebuilds the ingredient cache if the selection or storage contents have changed.
        // Chooses the first option whose ingredients are fully achievable (directly or via
        // recursive crafting). Falls back to option 0 when nothing is achievable.
        private void EnsureIngredientCache()
        {
            long ver = StorageWorldSystem.Instance.StorageVersion;
            if (_selectedIndex == _ingCacheSelectedIdx && ver == _ingCacheStorageVer) return;

            _ingredientStates.Clear();
            _ingCacheSelectedIdx = _selectedIndex;
            _ingCacheStorageVer  = ver;
            _ingCacheOptionIdx   = -1;
            _ingCacheCanAfford   = false;

            if (_selectedIndex < 0 || _selectedIndex >= _disks.Count) return;
            var opts = GetUpgradeOptions(_disks[_selectedIndex].Disk.Tier);
            if (opts == null) return;

            // Try each option; pick the first one that's fully achievable.
            for (int oi = 0; oi < opts.Length; oi++)
            {
                var states   = BuildStatesForOption(opts[oi]);
                bool feasible = true;
                foreach (var s in states)
                    if (s.Have < s.Need && !s.Craftable) { feasible = false; break; }

                if (feasible || oi == opts.Length - 1)
                {
                    _ingredientStates.AddRange(states);
                    _ingCacheOptionIdx = oi;
                    _ingCacheCanAfford = feasible;
                    return;
                }
            }
        }

        private List<IngredientState> BuildStatesForOption((int itemType, int count)[] option)
        {
            var result = new List<IngredientState>();
            foreach (var (itemType, need) in option)
            {
                int  have      = StorageWorldSystem.Instance.CountItem(_diskIds, itemType);
                bool craftable = false;
                if (have < need)
                {
                    // Resolve the FULL need: asking for `need - have` lets the resolver count the
                    // stock we just subtracted a second time and call it a free direct extract.
                    var plan = RecipeResolver.Resolve(itemType, need, _diskIds, _stations, _conditions);
                    craftable = plan is { IsFeasible: true } && plan.Steps.Count > 0;
                }
                result.Add(new IngredientState { ItemType = itemType, Need = need, Have = have, Craftable = craftable });
            }
            return result;
        }

        private void TryUpgrade()
        {
            EnsureIngredientCache();
            if (!_ingCacheCanAfford || _ingCacheOptionIdx < 0) return;
            if (_selectedIndex < 0 || _selectedIndex >= _disks.Count) return;

            var entry    = _disks[_selectedIndex];
            var guid     = entry.Disk.DiskId;
            var nextTier = (DiskTier)((int)entry.Disk.Tier + 1);

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                var mod = ModLoader.GetMod("TerraStorage");
                NetworkHandler.SendUpgradeDiskRequest(mod, _terminal.ID, entry.Bay.ID, entry.Slot,
                    guid, _ingCacheOptionIdx);
                Terraria.Audio.SoundEngine.PlaySound(SoundID.Grab);
                return;
            }

            // All-or-nothing: the upgrade is only installed if every material was actually consumed.
            var materials = _ingredientStates.Select(s => (s.ItemType, s.Need));
            if (!RecipeResolver.TryConsumeMaterials(_diskIds, materials, _stations, _conditions))
            {
                Refresh();
                return;
            }

            // Build the upgraded disk item and carry the existing GUID across.
            var newItem = new Item();
            newItem.SetDefaults(GetDiskItemType(nextTier));
            if (newItem.ModItem is StorageDiskBase newDisk)
            {
                newDisk.AssignDiskId(guid);
                StorageWorldSystem.Instance.UpgradeDisk(guid, newDisk.Tier);
            }

            // Replace the disk in its Drive Bay slot, keeping it in the same position.
            entry.Bay.RemoveDisk(entry.Slot);
            entry.Bay.DiskSlots[entry.Slot] = newItem.Clone();

            Terraria.Audio.SoundEngine.PlaySound(SoundID.Grab);
            Refresh();
        }

        private void TryDefragment()
        {
            if (_disks.Count < 2) return;

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                var mod = ModLoader.GetMod("TerraStorage");
                NetworkHandler.SendDefragRequest(mod, _terminal.ID);
                return;
            }

            var modified = StorageWorldSystem.Instance.Defragment(_diskIds);
            if (modified.Count > 0)
                Refresh();
        }

        // ---- Drawing -------------------------------------------------------

        protected override void DrawSelf(SpriteBatch sb)
        {
            var dims      = GetInnerDimensions();
            float rightX  = dims.X + ListPaneWidth + 8;
            float rightW  = dims.Width - ListPaneWidth - 8;

            const float defragBtnH = DefragButtonHeight;

            DrawDiskList(sb, dims.X, dims.Y, GetDiskListHeight(dims.Height));

            // Defragment button at the bottom of the left pane.
            bool canDefrag = _disks.Count > 1;
            bool defragHover = canDefrag
                && Main.MouseScreen.X >= dims.X && Main.MouseScreen.X < dims.X + ListPaneWidth
                && Main.MouseScreen.Y >= dims.Y + dims.Height - defragBtnH && Main.MouseScreen.Y < dims.Y + dims.Height;
            _defragButtonRect = new Rectangle((int)dims.X, (int)(dims.Y + dims.Height - defragBtnH), ListPaneWidth, (int)defragBtnH);
            var defragColor = !canDefrag    ? new Color(35, 35, 50) * 0.85f
                            : defragHover   ? new Color(90, 115, 205) * 0.95f
                                            : new Color(63, 82, 151) * 0.85f;
            Utils.DrawInvBG(sb, _defragButtonRect, defragColor);
            string defragLabel = Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.Defragment");
            var defragTextSize = FontAssets.MouseText.Value.MeasureString(defragLabel) * 0.75f;
            Utils.DrawBorderString(sb, defragLabel,
                new Vector2(_defragButtonRect.X + (_defragButtonRect.Width - defragTextSize.X) / 2f,
                            _defragButtonRect.Y + (_defragButtonRect.Height - defragTextSize.Y) / 2f),
                canDefrag ? Color.White : Color.Gray, 0.75f);

            if (defragHover)
            {
                Main.instance.MouseText("Consolidates fragmented items across all disks,\nmaximizing free slots. Requires 2+ disks.");
                Main.LocalPlayer.mouseInterface = true;
            }

            if (_selectedIndex >= 0 && _selectedIndex < _disks.Count)
                DrawDiskDetail(sb, rightX, dims.Y, rightW, dims.Height, _disks[_selectedIndex]);
            else
                Utils.DrawBorderString(sb, Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.SelectDisk"), new Vector2(rightX + 8, dims.Y + 16), Color.Gray, 0.85f);
        }

        private void DrawDiskList(SpriteBatch sb, float x, float y, float height)
        {
            if (_disks.Count == 0)
            {
                Utils.DrawBorderString(sb, Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.NoDisksConnected"), new Vector2(x + 6, y + 8), Color.Gray, 0.7f);
                return;
            }

            // Scissor to list area so rows never bleed into the defrag button.
            var scissor = new Rectangle(
                (int)(x * Main.UIScale),
                (int)(y * Main.UIScale),
                (int)(ListPaneWidth * Main.UIScale),
                (int)(Math.Max(0, height) * Main.UIScale));
            var savedScissor = sb.GraphicsDevice.ScissorRectangle;
            sb.End();
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.AnisotropicClamp,
                     DepthStencilState.None, ScissorRasterizer, null, Main.UIScaleMatrix);
            sb.GraphicsDevice.ScissorRectangle = scissor;

            int startIdx   = (int)(_listScrollPixels / DiskRowHeight);
            float yOffset  = _listScrollPixels % DiskRowHeight;
            int maxVisible = (int)(height / DiskRowHeight) + 2;

            for (int i = startIdx; i < Math.Min(_disks.Count, startIdx + maxVisible); i++)
            {
                float rowY = y + (i - startIdx) * DiskRowHeight - yOffset;
                if (rowY + DiskRowHeight < y || rowY > y + height) continue;

                var entry   = _disks[i];
                bool sel    = i == _selectedIndex;
                bool hover  = !sel
                    && Main.MouseScreen.X >= x && Main.MouseScreen.X < x + ListPaneWidth
                    && Main.MouseScreen.Y >= rowY && Main.MouseScreen.Y < rowY + DiskRowHeight;

                var bg = sel   ? new Color(63, 82, 151) * 0.8f
                       : hover ? new Color(63, 82, 151) * 0.5f
                               : new Color(63, 82, 151) * 0.2f;

                Utils.DrawInvBG(sb,
                    new Rectangle((int)x + 1, (int)rowY + 1, ListPaneWidth - 2, DiskRowHeight - 2), bg);

                // Tier color stripe.
                sb.Draw(TextureAssets.MagicPixel.Value,
                    new Rectangle((int)x + 1, (int)rowY + 1, 4, DiskRowHeight - 2),
                    entry.Disk.Tier.GetColor());

                // Disk icon.
                DrawItemIcon(sb, entry.Item, x + 10, rowY + (DiskRowHeight - 32) / 2f, 32);

                // Name and usage text.
                string name = entry.Disk.Tier.GetName() + " Storage Disk";
                Utils.DrawBorderString(sb, name, new Vector2(x + 48, rowY + 8), Color.White, 0.72f);

                var data = StorageWorldSystem.Instance.GetDiskData(entry.Disk.DiskId);
                if (data != null)
                {
                    string usage = Language.GetText("Mods.TerraStorage.UI.DiskPanel.SlotCount").Format(data.UsedStacks, data.MaxStacks);
                    Utils.DrawBorderString(sb, usage, new Vector2(x + 48, rowY + 26), Color.LightGray, 0.65f);
                }
            }

            sb.End();
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.AnisotropicClamp,
                     DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            sb.GraphicsDevice.ScissorRectangle = savedScissor;
        }

        private void DrawDiskDetail(SpriteBatch sb, float x, float y, float width, float height, DiskEntry entry)
        {
            var data = StorageWorldSystem.Instance.GetDiskData(entry.Disk.DiskId);
            if (data == null)
            {
                Utils.DrawBorderString(sb, Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.DiskUnavailable"), new Vector2(x, y + 10), Color.Gray, 0.8f);
                return;
            }

            bool    isMaxTier = IsMaxTier(entry.Disk.Tier);
            float   upgradeH  = GetUpgradeSectionHeight(entry.Disk.Tier);
            var     tierColor = entry.Disk.Tier.GetColor();

            // Header: name + slot count.
            Utils.DrawBorderString(sb, entry.Disk.Tier.GetName() + " Storage Disk",
                new Vector2(x, y + 2), tierColor, 0.9f);
            Utils.DrawBorderString(sb, Language.GetText("Mods.TerraStorage.UI.DiskPanel.SlotCount").Format(data.UsedStacks, data.MaxStacks),
                new Vector2(x, y + 22), Color.LightGray, 0.75f);

            // Usage bar: track is 1px larger on all sides than the fill.
            const int fillH = 8;
            float barY   = y + UsageBarOffset;
            float barW   = width - 10f;
            float fill   = data.MaxStacks > 0 ? (float)data.UsedStacks / data.MaxStacks : 0f;
            sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle((int)x - 1, (int)barY - 1, (int)barW + 2, fillH + 2), new Color(23, 33, 69));
            if (fill > 0f)
                sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle((int)x, (int)barY, (int)(barW * fill), fillH), tierColor);

            // Contents label and grid.
            float labelY  = barY + ContentsLabelOffset;
            Utils.DrawBorderString(sb, Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.Contents"), new Vector2(x, labelY), Color.White, 0.75f);

            float gridY = labelY + ContentsGridGap;
            float gridH = GetContentsGridHeight(height, entry.Disk.Tier);
            int   cols  = GetContentsGridColumns(width);
            var   items = GetDetailItems(entry.Disk.DiskId);
            DrawContentsGrid(sb, x, gridY, width, gridH, cols, items);

            // Separator.
            float upgradeY = y + height - upgradeH;
            sb.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)x, (int)upgradeY - 4, (int)(width - 10f), 1),
                new Color(60, 80, 140));

            // Upgrade section.
            if (isMaxTier)
            {
                Utils.DrawBorderString(sb, Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.MaxTier"), new Vector2(x, upgradeY + 4), Color.Gold, 0.8f);
                _upgradeButtonRect = Rectangle.Empty;
            }
            else
            {
                DrawUpgradeSection(sb, x, upgradeY, entry.Disk.Tier);
            }
        }

        private void DrawContentsGrid(SpriteBatch sb, float x, float y, float width, float height,
                                      int cols, List<ConsolidatedItem> items)
        {
            var scissor = new Rectangle(
                (int)(x * Main.UIScale),
                (int)(y * Main.UIScale),
                (int)(width * Main.UIScale),
                (int)(Math.Max(0, height) * Main.UIScale));
            var savedScissor = sb.GraphicsDevice.ScissorRectangle;

            sb.End();
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.AnisotropicClamp,
                     DepthStencilState.None, ScissorRasterizer, null, Main.UIScaleMatrix);
            sb.GraphicsDevice.ScissorRectangle = scissor;

            if (items.Count == 0)
            {
                Utils.DrawBorderString(sb, Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.Empty"), new Vector2(x + 4, y + 4), Color.Gray, 0.7f);
            }
            else
            {
                int scrollRow   = (int)_contentsScroll;
                int visibleRows = Math.Max(1, (int)(height / ItemCellSize));

                for (int i = scrollRow * cols; i < Math.Min(items.Count, (scrollRow + visibleRows + 1) * cols); i++)
                {
                    int   row = i / cols - scrollRow;
                    int   col = i % cols;
                    float cx  = x + col * ItemCellSize;
                    float cy  = y + row * ItemCellSize;
                    var   ci  = items[i];

                    // Cell background.
                    Utils.DrawInvBG(sb,
                        new Rectangle((int)cx, (int)cy, ItemCellSize - 2, ItemCellSize - 2),
                        new Color(63, 82, 151) * 0.4f);

                    // Draw icon — same pattern as UIItemGrid.DrawItem: get the current
                    // animation frame via GetFrame so animated items render correctly.
                    Main.instance.LoadItem(ci.ItemType);
                    var tex = TextureAssets.Item[ci.ItemType].Value;
                    var srcRect = Main.itemAnimations[ci.ItemType] != null
                        ? Main.itemAnimations[ci.ItemType].GetFrame(tex)
                        : tex.Frame();
                    float maxDim = Math.Max(srcRect.Width, srcRect.Height);
                    float scale  = maxDim > (ItemCellSize - 4f) ? (ItemCellSize - 4f) / maxDim : 1f;
                    var   center = new Vector2(cx + ItemCellSize / 2f, cy + ItemCellSize / 2f);
                    var   origin = new Vector2(srcRect.Width / 2f, srcRect.Height / 2f);
                    sb.Draw(tex, center, srcRect, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);

                    if (ci.TotalCount > 1)
                    {
                        string cnt = ci.TotalCount >= 1000
                            ? $"{ci.TotalCount / 1000f:0.#}k"
                            : ci.TotalCount.ToString();
                        Utils.DrawBorderString(sb, cnt, new Vector2(cx + 2, cy + ItemCellSize - 14), Color.White, 0.55f);
                    }

                    // Only pay the SetDefaults cost for the single hovered cell.
                    var cellRect = new Rectangle((int)cx, (int)cy, ItemCellSize, ItemCellSize);
                    if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                    {
                        var drawItem = new Item();
                        drawItem.SetDefaults(ci.ItemType);
                        if (ci.PrefixId > 0) drawItem.Prefix(ci.PrefixId);
                        drawItem.stack = ci.TotalCount;
                        if (ci.ModData != null) drawItem.ModItem?.LoadData(ci.ModData);
                        Main.HoverItem     = drawItem;
                        Main.hoverItemName = drawItem.Name;
                    }
                }
            }

            sb.End();
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.AnisotropicClamp,
                     DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            sb.GraphicsDevice.ScissorRectangle = savedScissor;
        }

        private void DrawUpgradeSection(SpriteBatch sb, float x, float y, DiskTier tier)
        {
            EnsureIngredientCache();

            var nextTier = (DiskTier)((int)tier + 1);
            Utils.DrawBorderString(sb, Language.GetText("Mods.TerraStorage.UI.DiskPanel.UpgradeTo").Format(nextTier.GetName() + " Storage Disk"),
                new Vector2(x, y + 2), Color.White, 0.75f);

            float ingY = y + 22f;
            foreach (var s in _ingredientStates)
            {
                // Green  = have enough directly.
                // Yellow = short directly, but craftable from storage.
                // Red    = not achievable.
                Color col = s.Have >= s.Need ? Color.LightGreen
                          : s.Craftable      ? Color.Yellow
                                             : Color.OrangeRed;

                DrawItemIcon(sb, s.ItemType, x, ingY, 20);
                Utils.DrawBorderString(sb, $"{TerminalUIState.GetCachedName(s.ItemType)}  {s.Have}/{s.Need}",
                    new Vector2(x + 24, ingY + 2), col, 0.7f);
                ingY += 22f;
            }

            // Upgrade button — green if achievable, gray otherwise.
            float btnY = ingY + 6f;
            bool btnHover = _ingCacheCanAfford
                && Main.MouseScreen.X >= x && Main.MouseScreen.X < x + 100f
                && Main.MouseScreen.Y >= btnY && Main.MouseScreen.Y < btnY + 24f;

            var btnColor = !_ingCacheCanAfford ? new Color(35, 35, 50) * 0.85f
                         : btnHover            ? new Color(90, 115, 205) * 0.95f
                                               : new Color(63, 82, 151) * 0.85f;

            _upgradeButtonRect = new Rectangle((int)x, (int)btnY, 100, 24);
            Utils.DrawInvBG(sb, _upgradeButtonRect, btnColor);
            string upgradeLabel = Language.GetTextValue("Mods.TerraStorage.UI.DiskPanel.Upgrade");
            var upgradeTextSize = FontAssets.MouseText.Value.MeasureString(upgradeLabel) * 0.75f;
            Utils.DrawBorderString(sb, upgradeLabel,
                new Vector2(x + (100 - upgradeTextSize.X) / 2f, btnY + (24 - upgradeTextSize.Y) / 2f),
                _ingCacheCanAfford ? Color.White : Color.Gray, 0.75f);
        }

        // ---- Helpers -------------------------------------------------------

        private static void DrawItemIcon(SpriteBatch sb, int itemType, float x, float y, int size)
        {
            Main.instance.LoadItem(itemType);
            var tex = TextureAssets.Item[itemType].Value;
            if (tex == null) return;
            float  scale  = Math.Min((float)size / tex.Width, (float)size / tex.Height);
            var    center = new Vector2(x + size / 2f, y + size / 2f);
            var    origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
            sb.Draw(tex, center, null, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);
        }

        private static void DrawItemIcon(SpriteBatch sb, Item item, float x, float y, int size)
        {
            if (item == null || item.IsAir) return;
            DrawItemIcon(sb, item.type, x, y, size);
        }
    }
}
