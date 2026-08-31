using System.Collections.Generic;

namespace TerraStorage.Content.UI
{
    // Display cache for the Favorited Recipes panel: which rows exist, what each ingredient
    // slot needs, and the last rendered have/need string per slot. The panel draws every frame
    // (it stays visible during gameplay when pinned), so everything here is gated on version
    // stamps: rows rebuild only when the favorites set changes, the storage snapshot refreshes
    // only when storage contents or the connected disk set change, and a slot's display string
    // is re-allocated only when its count actually changed.
    //
    // Deliberately free of Terraria and XNA types so Tests.csproj can compile it directly and
    // assert the steady state allocates nothing.
    public sealed class FavoritesRowCache
    {
        // Row height = output slot size (36) + 4 spacing; body starts 3px below the header.
        public const float RowHeight = 40f;
        public const float TopPad = 3f;

        public sealed class Slot
        {
            public int ItemType;
            public int Needed;
            // -1 = never computed, so the first UpdateSlotCount always builds the string.
            public int Have = -1;
            public string Text;
            public float TextWidth;
            public float TextHeight;
        }

        public sealed class Row
        {
            public int RecipeIndex;
            public int OutputType;
            public readonly List<Slot> Slots = new();
        }

        public readonly List<Row> Rows = new();

        // O(1) read for hit-testing; recomputed when rows rebuild. Callers still clamp to
        // their own maximum visible height.
        public float BodyHeight { get; private set; } = TopPad;

        private long _favoritesVersion = -1;
        private long _storageVersion = -1;
        private int _diskIdsToken = int.MinValue;

        public bool NeedsRowRebuild(long favoritesVersion) => favoritesVersion != _favoritesVersion;

        public bool NeedsStorageRefresh(long storageVersion, int diskIdsToken)
            => storageVersion != _storageVersion || diskIdsToken != _diskIdsToken;

        public void MarkRowsRebuilt(long favoritesVersion)
        {
            _favoritesVersion = favoritesVersion;
            BodyHeight = TopPad + Rows.Count * RowHeight;
        }

        public void MarkStorageRefreshed(long storageVersion, int diskIdsToken)
        {
            _storageVersion = storageVersion;
            _diskIdsToken = diskIdsToken;
        }

        // World/character switch: the cache outlives both, and neither FavoritesVersion (fresh
        // player instance) nor StorageVersion (not reset or bumped on world load) is guaranteed
        // to differ in the new session — so the stamps must be forced stale explicitly.
        public void ResetVersionStamps()
        {
            _favoritesVersion = -1;
            _storageVersion = -1;
            _diskIdsToken = int.MinValue;
        }

        // True when a row's slot may register a hit rect. Rows past the clipped body are still
        // drawn - the scissor hides them - but registering their rects turns an ordinary inventory
        // alt-click, the vanilla favorite gesture, into unfavoriting a row the player cannot see.
        //
        // The test is on the rect's BOTTOM edge, which is what makes a row scrolling in at the top
        // clickable as soon as any of it shows, while one running past the bottom is not.
        public static bool IsHitRectVisible(float rectBottom, float bodyTop, float bodyBottom)
            => rectBottom >= bodyTop && rectBottom <= bodyBottom;

        // Where the clipped body ends: rows are drawn from BodyHeight but the panel never grows
        // past its own maximum.
        public static float GetBodyBottom(float bodyTop, float bodyHeight, float maxBodyHeight)
            => bodyTop + (bodyHeight < maxBodyHeight ? bodyHeight : maxBodyHeight);

        // Returns true when the display string changed and the caller must re-measure it.
        public static bool UpdateSlotCount(Slot slot, int have)
        {
            if (have == slot.Have && slot.Text != null)
                return false;
            slot.Have = have;
            slot.Text = FormatCount(have, slot.Needed);
            return true;
        }

        public static string FormatCount(int have, int needed) => $"{have}/{needed}";
    }
}
