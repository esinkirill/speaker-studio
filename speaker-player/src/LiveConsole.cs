using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    public sealed class LiveConsole : Control
    {
        private sealed class Line { public string Text; public Color Color; }
        private readonly List<Line> lines = new List<Line>();
        private int offset;
        public string LogText { get { return String.Join(Environment.NewLine, lines.Select(l => l.Text)); } }
        public LiveConsole()
        {
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Копировать журнал", null, delegate { if (lines.Count > 0) Clipboard.SetText(LogText); });
            menu.Items.Add("Очистить журнал", null, delegate { lines.Clear(); offset = 0; Invalidate(); });
            ContextMenuStrip = menu;
        }
        public void AppendLine(string text, Color color)
        {
            foreach (string part in text.Replace("\r", "").Split('\n')) lines.Add(new Line { Text = part, Color = color });
            if (lines.Count > 800) lines.RemoveRange(0, lines.Count - 800);
            offset = 0; Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            offset = Math.Max(0, Math.Min(Math.Max(0, lines.Count - VisibleLines()), offset + Math.Sign(e.Delta) * 3)); Invalidate();
        }
        private int VisibleLines() { return Math.Max(1, Height / Math.Max(1, Font.Height + 3)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int count = VisibleLines(); int first = Math.Max(0, lines.Count - count - offset);
            for (int i = 0; i < count && first + i < lines.Count; i++) {
                Line line = lines[first + i];
                TextRenderer.DrawText(e.Graphics, line.Text, Font, new Rectangle(0, i * (Font.Height + 3), Math.Max(0, Width - 10), Font.Height + 3), line.Color, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            if (lines.Count > count) {
                using (SolidBrush track = new SolidBrush(Color.FromArgb(30, 43, 59))) e.Graphics.FillRectangle(track, Width - 4, 0, 3, Height);
                int thumb = Math.Max(14, Height * count / lines.Count);
                int y = (int)((Height - thumb) * (1 - offset / (double)Math.Max(1, lines.Count - count)));
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(94, 226, 204))) e.Graphics.FillRectangle(fill, Width - 4, y, 3, thumb);
            }
        }
    }
}
