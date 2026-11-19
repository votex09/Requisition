using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.Elements
{
    // Reusable window chrome for all Requisition floating panels.
    // Handles background, optional title bar, resize handle, drag-to-move,
    // and position/size persistence via UIPositionStore.
    // Usage:
    //   var win = new TSWindowElement {
    //       StoreKey    = "terminal",
    //       HasTitleBar = false,
    //       Resizable   = true,
    //       WinMinWidth    = 650f, WinMaxWidth = 1200f,
    //       WinMinHeight   = 300f, WinMaxHeight = 900f,
    //   };
    //   win.GetDragZone = () => /* rectangle user can drag */;
    //   win.OnResized   += (w, h) => RecalculateLayout();
    //   win.LoadSavedBounds();   // call after Width/Height are set
    // 
    public class TSWindowElement : UIPanel
    {
        // ── Shared visual constants ──────────────────────────────────────────
        public static readonly Color BgColor      = new Color(63, 82, 151) * 0.70f;
        public static readonly Color TitleBgColor = new Color(63, 82, 151) * 0.85f;
        public static readonly Color HandleColor  = new Color(100, 120, 180, 180);
        public static readonly Color HandleHoverColor = new Color(140, 160, 220, 220);

        public const float DefaultTitleBarHeight = 28f;
        public const float ResizeHandleSize      = 16f;

        // ── Configuration (set before LoadSavedBounds) ───────────────────────
        public string StoreKey    { get; init; }
        public bool   HasTitleBar { get; init; } = true;
        public string Title       { get; init; } = "";
        public bool   Resizable   { get; init; } = true;
        public float  WinMinWidth  { get; init; } = 300f;
        public float  WinMaxWidth  { get; init; } = 1600f;
        public float  WinMinHeight { get; init; } = 200f;
        public float  WinMaxHeight { get; init; } = 1200f;

        // Override the drag hit test. Return true if the given screen-space mouse position
        // is in a valid drag zone. If null and HasTitleBar is true, the title bar is used.
        // If null and HasTitleBar is false, dragging is disabled.
        public Func<Vector2, bool> GetDragZone { get; set; }

        //Fired when a resize operation ends, passing the new width and height.
        public event Action<float, float> OnResized;

        // ── Internal state ────────────────────────────────────────────────────
        private bool    _dragging;
        private Vector2 _dragOffset;
        private bool    _resizing;
        private Vector2 _resizeStart;
        private float   _resizeOrigW;
        private float   _resizeOrigH;
        private bool    _prevMouseLeft;

        // ── Init ──────────────────────────────────────────────────────────────

        // Call after Width/Height are set on this element to restore saved position/size.
        public void LoadSavedBounds()
        {
            if (StoreKey == null) return;

            if (UIPositionStore.TryGetSize(StoreKey, out float w, out float h))
            {
                Width.Set(Math.Clamp(w, WinMinWidth, WinMaxWidth), 0f);
                Height.Set(Math.Clamp(h, WinMinHeight, WinMaxHeight), 0f);
            }

            if (UIPositionStore.TryGet(StoreKey, out float x, out float y))
            {
                HAlign = 0f;
                VAlign = 0f;
                Left.Set(x, 0f);
                Top.Set(y, 0f);
            }
        }

        // ── Drawing ───────────────────────────────────────────────────────────

        protected override void DrawSelf(SpriteBatch sb)
        {
            // Intentionally skip base.DrawSelf() — we draw custom chrome.
            var dims = GetDimensions();
            var rect = new Rectangle((int)dims.X, (int)dims.Y, (int)dims.Width, (int)dims.Height);

            // Background
            UIDrawHelpers.DrawUnderlay(sb, rect);
            Utils.DrawInvBG(sb, rect, BgColor);

            // Title bar
            if (HasTitleBar)
            {
                var titleRect = new Rectangle((int)dims.X, (int)dims.Y, (int)dims.Width, (int)DefaultTitleBarHeight);
                Utils.DrawInvBG(sb, titleRect, TitleBgColor);

                if (!string.IsNullOrEmpty(Title))
                    Utils.DrawBorderString(sb, Title,
                        new Vector2(dims.X + 8, dims.Y + (DefaultTitleBarHeight - FontAssets.MouseText.Value.MeasureString(Title).Y * 0.8f) / 2f),
                        Color.White, 0.8f);
            }

            // Resize handle
            if (Resizable)
            {
                var hr = ResizeRect(dims);
                bool hov = hr.Contains(Main.MouseScreen.ToPoint());
                UIDrawHelpers.DrawResizeHandle(sb, hr, hov ? HandleHoverColor : HandleColor);
            }
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            bool leftDown   = Main.mouseLeft;
            bool justDown   = leftDown && !_prevMouseLeft && !UIClickBlocker.IsConsumed;
            _prevMouseLeft  = UIClickBlocker.RealMouseLeft;

            var dims = GetDimensions();

            // Finish resize
            if (_resizing)
            {
                UIClickBlocker.MarkGesture();
                Main.LocalPlayer.mouseInterface = true;
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _resizing = false;
                    SaveBounds();
                    OnResized?.Invoke(Width.Pixels, Height.Pixels);
                }
                else
                {
                    float newW = Math.Clamp(_resizeOrigW + (Main.MouseScreen.X - _resizeStart.X), WinMinWidth, WinMaxWidth);
                    // Logical height, and never let max drop below min: Math.Clamp throws when
                    // min > max, which would spam an exception every frame of the resize.
                    float maxH = Math.Max(WinMinHeight, Math.Min(WinMaxHeight, UIDrawHelpers.UiHeight - dims.Y - 10));
                    float newH = Math.Clamp(_resizeOrigH + (Main.MouseScreen.Y - _resizeStart.Y), WinMinHeight, maxH);
                    Width.Set(newW, 0f);
                    Height.Set(newH, 0f);
                    Recalculate();
                    OnResized?.Invoke(newW, newH);
                }
                return;
            }

            // Finish drag
            if (_dragging)
            {
                UIClickBlocker.MarkGesture();
                Main.LocalPlayer.mouseInterface = true;
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _dragging = false;
                    SaveBounds();
                }
                else
                {
                    float nx = Math.Clamp(Main.MouseScreen.X - _dragOffset.X, 0, Math.Max(0f, UIDrawHelpers.UiWidth  - dims.Width));
                    float ny = Math.Clamp(Main.MouseScreen.Y - _dragOffset.Y, 0, Math.Max(0f, UIDrawHelpers.UiHeight - dims.Height));
                    Left.Set(nx, 0f);
                    Top.Set(ny, 0f);
                    Recalculate();
                }
                return;
            }

            if (!justDown) return;

            // Start resize
            if (Resizable && ResizeRect(dims).Contains(Main.MouseScreen.ToPoint()))
            {
                _resizing    = true;
                UIClickBlocker.MarkGesture();
                _resizeStart = Main.MouseScreen;
                _resizeOrigW = Width.Pixels;
                _resizeOrigH = Height.Pixels;
                UIClickBlocker.Consume();
                return;
            }

            // Start drag
            bool inDragZone = GetDragZone != null
                ? GetDragZone(Main.MouseScreen)
                : (HasTitleBar && TitleBarRect(dims).Contains(Main.MouseScreen.ToPoint()));

            if (inDragZone)
            {
                // Convert alignment-based positioning to absolute before dragging
                HAlign = 0f;
                VAlign = 0f;
                Left.Set(dims.X, 0f);
                Top.Set(dims.Y, 0f);
                Recalculate();
                dims = GetDimensions();

                _dragging   = true;
                UIClickBlocker.MarkGesture();
                _dragOffset = Main.MouseScreen - new Vector2(dims.X, dims.Y);
                UIClickBlocker.Consume();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SaveBounds()
        {
            if (StoreKey == null) return;
            UIPositionStore.SaveWithSize(StoreKey, Left.Pixels, Top.Pixels, Width.Pixels, Height.Pixels);
        }

        private static Rectangle ResizeRect(CalculatedStyle dims) =>
            new Rectangle(
                (int)(dims.X + dims.Width  - ResizeHandleSize),
                (int)(dims.Y + dims.Height - ResizeHandleSize),
                (int)ResizeHandleSize, (int)ResizeHandleSize);

        private static Rectangle TitleBarRect(CalculatedStyle dims) =>
            new Rectangle((int)dims.X, (int)dims.Y, (int)dims.Width, (int)DefaultTitleBarHeight);
    }
}
