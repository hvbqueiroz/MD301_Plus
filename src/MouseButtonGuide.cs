using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace MD301Plus
{
    internal enum EditorButton
    {
        Unassigned,
        Dpi,
        Back,
        Forward,
        Middle,
        Keyboard
    }

    internal sealed class MouseButtonGuide : Control
    {
        private const float CanvasWidth = 1000;
        private const float CanvasHeight = 700;
        private readonly Bitmap illustration;
        private EditorButton selected;
        private EditorButton hovered;
        internal event Action<EditorButton> ButtonChosen;
        internal EditorButton SelectedButton
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        internal MouseButtonGuide()
        {
            using (Stream stream = typeof(MouseButtonGuide).Assembly.GetManifestResourceStream("MD301Plus.MouseIllustration"))
            {
                if (stream == null) throw new InvalidOperationException("The MD301 mouse illustration is missing.");
                using (Image image = Image.FromStream(stream)) illustration = new Bitmap(image);
            }
            Name = "MouseButtonGuide";
            DoubleBuffered = true;
            BackColor = Theme.Background;
            AccessibleName = "ASUS ProArt MD301 button guide";
            AccessibleDescription = "Three-quarter view showing the main scroll wheel, metal thumb scroll wheel, " +
                "two side buttons and thumb rest. The DPI switch is behind the main scroll wheel.";
        }

        private float DrawingScale { get { return Math.Min(Width / CanvasWidth, Height / CanvasHeight); } }
        private float OffsetX { get { return (Width - CanvasWidth * DrawingScale) / 2; } }
        private float OffsetY { get { return (Height - CanvasHeight * DrawingScale) / 2; } }

        private static GraphicsPath Hotspot(EditorButton button)
        {
            PointF[] points;
            switch (button)
            {
                case EditorButton.Dpi:
                    points = new[] { new PointF(325, 201), new PointF(460, 62), new PointF(508, 75), new PointF(376, 215) };
                    break;
                case EditorButton.Middle:
                    points = new[] { new PointF(197, 349), new PointF(255, 230), new PointF(332, 217),
                        new PointF(343, 236), new PointF(291, 344), new PointF(249, 369) };
                    break;
                case EditorButton.Forward:
                    points = new[] { new PointF(569, 366), new PointF(638, 305), new PointF(649, 313),
                        new PointF(654, 332), new PointF(585, 381), new PointF(571, 380) };
                    break;
                case EditorButton.Back:
                    points = new[] { new PointF(651, 293), new PointF(711, 244), new PointF(726, 241),
                        new PointF(733, 253), new PointF(730, 267), new PointF(657, 320) };
                    break;
                default: throw new ArgumentOutOfRangeException("button");
            }
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(points);
            return path;
        }

        internal Rectangle ButtonBounds(EditorButton button)
        {
            using (GraphicsPath path = Hotspot(button))
            {
                RectangleF area = path.GetBounds();
                float scale = DrawingScale;
                return Rectangle.Round(new RectangleF(OffsetX + area.X * scale, OffsetY + area.Y * scale,
                    area.Width * scale, area.Height * scale));
            }
        }

        internal EditorButton ButtonAt(Point point)
        {
            float scale = DrawingScale;
            if (scale <= 0) return EditorButton.Unassigned;
            PointF canvasPoint = new PointF((point.X - OffsetX) / scale, (point.Y - OffsetY) / scale);
            foreach (EditorButton button in new[] { EditorButton.Dpi, EditorButton.Middle, EditorButton.Back, EditorButton.Forward })
                using (GraphicsPath path = Hotspot(button))
                    if (path.IsVisible(canvasPoint)) return button;
            return EditorButton.Unassigned;
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            EditorButton[] buttons = { EditorButton.Dpi, EditorButton.Back, EditorButton.Forward, EditorButton.Middle };
            string[] names = { "DPI switch behind the scroll wheel", "Back side button", "Forward side button", "Scroll-wheel click" };
            return new ActionAccessibility(this, 4,
                delegate(int index) { return names[index]; },
                delegate(int index) { return ButtonBounds(buttons[index]); },
                delegate(int index)
                {
                    Action<EditorButton> handler = ButtonChosen;
                    if (handler != null) handler(buttons[index]);
                }, delegate(int index) { return selected == buttons[index]; });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = DrawingScale;
            if (scale <= 0) return;
            Graphics graphics = e.Graphics;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.TranslateTransform(OffsetX, OffsetY);
                graphics.ScaleTransform(scale, scale);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawImage(illustration, new RectangleF(0, 0, CanvasWidth, 606));
                foreach (EditorButton button in new[] { EditorButton.Dpi, EditorButton.Middle, EditorButton.Back, EditorButton.Forward })
                {
                    if (button != selected && button != hovered) continue;
                    using (GraphicsPath path = Hotspot(button))
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(button == selected ? 90 : 45, Theme.Accent)))
                    using (Pen border = new Pen(Theme.Accent, button == selected ? 8 : 5))
                    {
                        graphics.FillPath(fill, path);
                        graphics.DrawPath(border, path);
                    }
                }
                EditorButton caption = hovered != EditorButton.Unassigned ? hovered : selected;
                string text = caption == EditorButton.Dpi ? "DPI switch" : caption == EditorButton.Middle ? "Scroll-wheel click" :
                    caption == EditorButton.Back ? "Back side button" : caption == EditorButton.Forward ? "Forward side button" :
                    "Select a button";
                using (Font font = new Font("Segoe UI Semibold", 52, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush ink = new SolidBrush(Theme.Accent))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center })
                    graphics.DrawString(text, font, ink, new RectangleF(0, 628, CanvasWidth, 65), format);
            }
            finally { graphics.Restore(state); }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            EditorButton next = ButtonAt(e.Location);
            Cursor = next == EditorButton.Unassigned ? Cursors.Default : Cursors.Hand;
            if (hovered != next) { hovered = next; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = EditorButton.Unassigned;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            EditorButton button = ButtonAt(e.Location);
            Action<EditorButton> handler = ButtonChosen;
            if (e.Button == MouseButtons.Left && button != EditorButton.Unassigned && handler != null)
                handler(button);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) illustration.Dispose();
            base.Dispose(disposing);
        }
    }
}
