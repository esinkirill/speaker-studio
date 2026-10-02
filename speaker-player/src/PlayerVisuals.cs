using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    /// <summary>Prepared playback events; the chart does not measure sound or microphone input.</summary>
    public sealed class FrequencyChart : Control
    {
        private sealed class Segment
        {
            public double Start;
            public double End;
            public int Frequency;
        }

        private readonly List<Segment> segments = new List<Segment>();
        private readonly Timer animation = new Timer();
        private readonly ToolTip noteTip = new ToolTip();
        private readonly Font captionFont = new Font("Segoe UI", 9F, FontStyle.Bold);
        private readonly Font axisFont = new Font("Segoe UI", 8F);
        private readonly Font noteFont = new Font("Segoe UI", 7F, FontStyle.Bold);
        private readonly Font valueFont = new Font("Consolas", 10F, FontStyle.Bold);
        private double totalMs;
        private double positionMs;
        private double upperFrequency = 2000;
        private int currentFrequency;
        private bool currentPause;
        private bool hasProgress;
        private bool pianoRoll;
        private bool selecting;
        private long selectionPositionMs;
        private int lowerNote = 48, upperNote = 84;
        private float glowPhase, lastScale = 1;
        private string tooltipText = String.Empty;

        private static readonly Color Canvas = Color.FromArgb(15, 23, 36);
        private static readonly Color Grid = Color.FromArgb(33, 45, 63);
        private static readonly Color Muted = Color.FromArgb(127, 147, 170);
        private static readonly Color Cyan = Color.FromArgb(82, 217, 224);
        private static readonly Color Violet = Color.FromArgb(169, 147, 255);
        private static readonly Color Selection = Color.FromArgb(249, 190, 98);

        public event Action<long> SeekRequested;

        public bool PianoRoll
        {
            get { return pianoRoll; }
            set { if (pianoRoll != value) { pianoRoll = value; Invalidate(); } }
        }

        public long SelectionPositionMs { get { return selectionPositionMs; } }
        public RectangleF PlotBounds { get { return GetPlotRectangle(lastScale); } }

        public FrequencyChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Canvas;
            ForeColor = Color.FromArgb(228, 236, 247);
            MinimumSize = new Size(250, 150);
            noteTip.InitialDelay = 250; noteTip.ReshowDelay = 80;
            noteTip.AutoPopDelay = 8000; noteTip.ShowAlways = true;
            animation.Interval = 33;
            animation.Tick += delegate { glowPhase += 0.13F; Invalidate(); };
        }

        public void SetSequence(IList<ToneRow> rows, int transpose, double speed, int noteGapMs)
        {
            SetSequence(rows, transpose, speed, noteGapMs, null);
        }

        public void SetSequence(IList<ToneRow> rows, int transpose, double speed, int noteGapMs, RhythmSettings rhythm)
        {
            SetPreparedSequence(rows == null ? null : SequenceTiming.Transform(rows, transpose, speed, noteGapMs, rhythm));
        }

        public void SetPreparedSequence(IList<ToneRow> rows)
        {
            segments.Clear();
            totalMs = 0; positionMs = 0; currentFrequency = 0;
            currentPause = false; hasProgress = false;
            animation.Stop(); SetTooltip(String.Empty);
            int maxFrequency = 0, minPitch = Int32.MaxValue, maxPitch = Int32.MinValue;
            if (rows != null) {
                foreach (ToneRow row in rows) {
                    if (row == null || row.Frequency < 0 || row.DurationMs < 0 || row.PauseMs < 0)
                        throw new ArgumentException("Диаграмме нужны подготовленные неотрицательные события.", "rows");
                    int frequency = row.Frequency;
                    if (row.DurationMs > 0) {
                        segments.Add(new Segment { Start = totalMs, End = totalMs + row.DurationMs, Frequency = frequency });
                        totalMs += row.DurationMs;
                    }
                    if (row.PauseMs > 0) {
                        segments.Add(new Segment { Start = totalMs, End = totalMs + row.PauseMs, Frequency = 0 });
                        totalMs += row.PauseMs;
                    }
                    if (frequency > 0 && row.DurationMs > 0) {
                        int pitch = NoteNumber(frequency);
                        minPitch = Math.Min(minPitch, pitch); maxPitch = Math.Max(maxPitch, pitch);
                        maxFrequency = Math.Max(maxFrequency, frequency);
                    }
                }
            }
            upperFrequency = Math.Max(1000, Math.Min(1000000, maxFrequency * 1.25));
            if (minPitch == Int32.MaxValue) { lowerNote = 48; upperNote = 84; }
            else {
                lowerNote = minPitch - 2; upperNote = maxPitch + 2;
                if (upperNote - lowerNote < 12) {
                    int padding = (12 - upperNote + lowerNote + 1) / 2;
                    lowerNote -= padding; upperNote += padding;
                }
            }
            selectionPositionMs = ClampTime(selectionPositionMs);
            Invalidate();
        }

        public void SelectPosition(long milliseconds)
        {
            selectionPositionMs = ClampTime(milliseconds);
            Invalidate();
        }

        public void UpdatePlayback(PlaybackProgress progress)
        {
            if (progress == null) return;
            currentFrequency = Math.Max(0, progress.Frequency);
            currentPause = progress.IsPause || currentFrequency == 0;
            positionMs = Math.Max(0, Math.Min(totalMs, progress.PositionMs));
            hasProgress = true;
            if (!animation.Enabled) animation.Start();
            Invalidate();
        }

        public void Reset()
        {
            animation.Stop(); positionMs = 0; currentFrequency = 0;
            currentPause = false; hasProgress = false;
            Invalidate();
        }

        public void Finish()
        {
            animation.Stop(); positionMs = totalMs; currentFrequency = 0;
            currentPause = true; hasProgress = true;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Canvas); lastScale = Math.Max(1F, g.DpiX / 96F);
            RectangleF plot = GetPlotRectangle(lastScale);
            using (Brush titleBrush = new SolidBrush(ForeColor))
            using (Brush mutedBrush = new SolidBrush(Muted))
            using (Brush cyanBrush = new SolidBrush(Cyan))
            using (Pen border = new Pen(Grid)) {
                g.DrawString(pianoRoll ? "НОТЫ MIDI" : "ЧАСТОТА ВО ВРЕМЕНИ",
                    captionFont, titleBrush, 16F * lastScale, 11F * lastScale);
                string value = hasProgress ? (currentPause ? "ПАУЗА" :
                    (pianoRoll ? NoteName(NoteNumber(currentFrequency)) + " · " : "") + currentFrequency + " Hz")
                    : pianoRoll ? "PIANO ROLL" : "МЕЛОДИЯ";
                SizeF valueSize = g.MeasureString(value, valueFont);
                g.DrawString(value, valueFont, hasProgress && !currentPause ? cyanBrush : mutedBrush,
                    ClientSize.Width - 20F * lastScale - valueSize.Width, 10F * lastScale);
                string selectionText = "Старт " + FormatPosition(selectionPositionMs) +
                    "  ·  щёлкни или перетащи маркер";
                g.DrawString(selectionText, axisFont, mutedBrush, 16F * lastScale, 31F * lastScale);
                if (pianoRoll) DrawPianoGrid(g, plot, mutedBrush);
                else DrawFrequencyGrid(g, plot, mutedBrush);
                DrawTimeGrid(g, plot, mutedBrush);
                g.DrawRectangle(border, plot.X, plot.Y, plot.Width, plot.Height);
                if (segments.Count == 0 || totalMs <= 0) {
                    string empty = pianoRoll ? "Открой MIDI — здесь появятся ноты" :
                        "Открой CSV — здесь появится последовательность частот";
                    SizeF size = g.MeasureString(empty, axisFont);
                    g.DrawString(empty, axisFont, mutedBrush, Math.Max(plot.Left + 8,
                        plot.Left + (plot.Width - size.Width) / 2), plot.Top + plot.Height / 2 - size.Height / 2);
                    return;
                }
                if (pianoRoll) DrawPianoNotes(g, plot);
                else DrawTimeline(g, plot);
                DrawSelection(g, plot, lastScale);
                if (hasProgress) DrawPlayhead(g, plot, lastScale);
            }
        }

        private RectangleF GetPlotRectangle(float scale)
        {
            float left = (pianoRoll ? 78F : 60F) * scale;
            float right = 20F * scale, top = 54F * scale, bottom = 33F * scale;
            return new RectangleF(left, top, Math.Max(1, ClientSize.Width - left - right),
                Math.Max(1, ClientSize.Height - top - bottom));
        }

        private void DrawFrequencyGrid(Graphics g, RectangleF plot, Brush mutedBrush)
        {
            using (Pen pen = new Pen(Grid)) {
                double[] levels = { 20, 55, 110, 220, 440, 880, 1760, 3520, 7040, 14080, 28160, 56320, 112640, 225280, 450560 };
                float lastY = Single.MaxValue;
                foreach (double level in levels) {
                    if (level > upperFrequency) break;
                    float y = FrequencyY(level, plot);
                    if (Math.Abs(lastY - y) < 22 * lastScale) continue;
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    string label = level >= 1000 ? (level / 1000).ToString("0.#") + "k" : level.ToString("0");
                    SizeF size = g.MeasureString(label, axisFont);
                    g.DrawString(label, axisFont, mutedBrush, plot.Left - size.Width - 9, y - size.Height / 2);
                    lastY = y;
                }
                g.DrawString("Hz", axisFont, mutedBrush, plot.Left - 25, plot.Top - 17);
            }
        }

        private void DrawPianoGrid(Graphics g, RectangleF plot, Brush mutedBrush)
        {
            float height = NoteHeight(plot);
            float keyLeft = plot.Left - 56 * lastScale, keyWidth = 52 * lastScale;
            using (Pen line = new Pen(Grid))
            using (Pen octave = new Pen(Color.FromArgb(56, 73, 94)))
            using (Brush black = new SolidBrush(Color.FromArgb(20, 31, 47)))
            using (Brush white = new SolidBrush(Color.FromArgb(51, 64, 79)))
            using (Brush stripe = new SolidBrush(Color.FromArgb(5, 255, 255, 255)))
            using (Brush current = new SolidBrush(Color.FromArgb(90, Cyan))) {
                for (int note = lowerNote; note <= upperNote; note++) {
                    float y = NoteY(note, plot) - height / 2;
                    bool blackKey = IsBlackKey(note);
                    if (!blackKey) g.FillRectangle(stripe, plot.Left, y, plot.Width, height);
                    g.FillRectangle(blackKey ? black : white, keyLeft, y, keyWidth, height);
                    if (hasProgress && !currentPause && NoteNumber(currentFrequency) == note)
                        g.FillRectangle(current, keyLeft, y, keyWidth, height);
                    g.DrawLine(PitchClass(note) == 0 ? octave : line, plot.Left, y + height, plot.Right, y + height);
                    if (height >= 15 * lastScale || PitchClass(note) == 0) {
                        string label = NoteName(note);
                        SizeF size = g.MeasureString(label, axisFont);
                        g.DrawString(label, axisFont, mutedBrush, keyLeft + 6 * lastScale,
                            y + (height - size.Height) / 2);
                    }
                }
            }
        }

        private void DrawTimeGrid(Graphics g, RectangleF plot, Brush mutedBrush)
        {
            int divisions = Math.Max(2, Math.Min(6, (int)(plot.Width / (140 * lastScale))));
            using (Pen pen = new Pen(Grid)) {
                for (int i = 0; i <= divisions; i++) {
                    float x = plot.Left + plot.Width * i / divisions;
                    g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                    string text = totalMs < 10000 ? FormatPosition((long)(totalMs * i / divisions)).Substring(0, 7) :
                        FormatTime(totalMs * i / divisions);
                    SizeF size = g.MeasureString(text, axisFont);
                    float labelX = i == 0 ? x : i == divisions ? x - size.Width : x - size.Width / 2;
                    g.DrawString(text, axisFont, mutedBrush, labelX, plot.Bottom + 7 * lastScale);
                }
            }
        }

        private void DrawPianoNotes(Graphics g, RectangleF plot)
        {
            GraphicsState state = g.Save(); g.SetClip(plot);
            float height = NoteHeight(plot);
            using (Brush future = new SolidBrush(Color.FromArgb(160, Violet)))
            using (Brush played = new SolidBrush(Color.FromArgb(205, Cyan)))
            using (Brush text = new SolidBrush(Color.FromArgb(240, 244, 252)))
            using (Pen outline = new Pen(Color.FromArgb(220, Violet))) {
                foreach (Segment segment in segments) {
                    if (segment.Frequency <= 0) continue;
                    int note = NoteNumber(segment.Frequency);
                    float start = TimeX(segment.Start, plot), end = TimeX(segment.End, plot);
                    RectangleF box = new RectangleF(start, NoteY(note, plot) - height * .42F,
                        Math.Max(.7F, end - start - .5F), Math.Max(1.8F, height * .84F));
                    g.FillRectangle(future, box);
                    g.DrawRectangle(outline, box.X, box.Y, box.Width, box.Height);
                    if (hasProgress && segment.Start < positionMs) {
                        float completed = TimeX(Math.Min(segment.End, positionMs), plot);
                        g.FillRectangle(played, box.X, box.Y, Math.Max(0, Math.Min(box.Width, completed - box.X)), box.Height);
                    }
                    if (height >= 11 * lastScale && box.Width >= 30 * lastScale)
                        g.DrawString(NoteName(note), noteFont, text, box.X + 4 * lastScale, box.Y + 1);
                }
            }
            g.Restore(state);
        }

        private void DrawTimeline(Graphics g, RectangleF plot)
        {
            GraphicsState state = g.Save(); g.SetClip(plot);
            using (Pen upcoming = new Pen(Color.FromArgb(170, Violet), 1.6F))
            using (Pen played = new Pen(Cyan, 2.2F))
            using (Pen glow = new Pen(Color.FromArgb(25, Cyan), 6F))
            using (Brush upcomingFill = new SolidBrush(Color.FromArgb(9, Violet)))
            using (Brush playedFill = new SolidBrush(Color.FromArgb(20, Cyan))) {
                float previousY = plot.Bottom;
                Segment previous = null;
                foreach (Segment segment in segments) {
                    float startX = TimeX(segment.Start, plot), endX = TimeX(segment.End, plot);
                    float y = FrequencyY(segment.Frequency, plot);
                    bool interpolate = previous != null && previous.Frequency > 0 && segment.Frequency > 0 &&
                        (segment.End - segment.Start <= 20 || previous.End - previous.Start <= 20) &&
                        Math.Abs(12 * Math.Log(segment.Frequency / (double)previous.Frequency, 2)) <= 2.5;
                    if (segment.Frequency > 0)
                        g.FillRectangle(upcomingFill, startX, Math.Min(y, interpolate ? previousY : y),
                            Math.Max(.6F, endX - startX), plot.Bottom - Math.Min(y, interpolate ? previousY : y));
                    if (interpolate) g.DrawLine(upcoming, startX, previousY, endX, y);
                    else {
                        g.DrawLine(upcoming, startX, previousY, startX, y);
                        g.DrawLine(upcoming, startX, y, endX, y);
                    }
                    if (hasProgress && segment.Start <= positionMs) {
                        float completedX = TimeX(Math.Min(segment.End, positionMs), plot);
                        float completedY = interpolate && endX > startX ?
                            previousY + (y - previousY) * (completedX - startX) / (endX - startX) : y;
                        if (segment.Frequency > 0)
                            g.FillRectangle(playedFill, startX, Math.Min(y, interpolate ? previousY : y),
                                Math.Max(0, completedX - startX), plot.Bottom - Math.Min(y, interpolate ? previousY : y));
                        g.DrawLine(glow, startX, interpolate ? previousY : y, completedX, completedY);
                        if (!interpolate) g.DrawLine(played, startX, previousY, startX, y);
                        g.DrawLine(played, startX, interpolate ? previousY : y, completedX, completedY);
                    }
                    previousY = y; previous = segment;
                }
            }
            g.Restore(state);
        }

        private void DrawSelection(Graphics g, RectangleF plot, float scale)
        {
            float x = TimeX(selectionPositionMs, plot);
            using (Pen line = new Pen(Color.FromArgb(210, Selection), 1.5F))
            using (Brush triangle = new SolidBrush(Selection)) {
                line.DashStyle = DashStyle.Dash;
                g.DrawLine(line, x, plot.Top, x, plot.Bottom);
                g.FillPolygon(triangle, new[] { new PointF(x - 5 * scale, plot.Top - 7 * scale),
                    new PointF(x + 5 * scale, plot.Top - 7 * scale), new PointF(x, plot.Top) });
            }
        }

        private void DrawPlayhead(Graphics g, RectangleF plot, float scale)
        {
            float x = TimeX(positionMs, plot);
            float y = currentPause ? plot.Bottom : pianoRoll ? NoteY(NoteNumber(currentFrequency), plot) : FrequencyY(currentFrequency, plot);
            using (Pen line = new Pen(Color.FromArgb(130, Cyan)))
            using (Brush dot = new SolidBrush(Cyan))
            using (Brush halo = new SolidBrush(Color.FromArgb(32, Cyan))) {
                g.DrawLine(line, x, plot.Top, x, plot.Bottom);
                float radius = (8F + (float)Math.Sin(glowPhase) * 2F) * scale;
                g.FillEllipse(halo, x - radius, y - radius, radius * 2, radius * 2);
                g.FillEllipse(dot, x - 3.5F * scale, y - 3.5F * scale, 7F * scale, 7F * scale);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || totalMs <= 0 || !PlotBounds.Contains(e.Location)) return;
            selecting = true; Capture = true; Cursor = Cursors.SizeWE;
            SelectFromMouse(e.X, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (selecting) { SelectFromMouse(e.X, false); return; }
            bool inside = totalMs > 0 && PlotBounds.Contains(e.Location);
            Cursor = inside ? Cursors.Cross : Cursors.Default;
            if (!inside) { SetTooltip(String.Empty); return; }
            Segment segment = SegmentAt(TimeFromX(e.X));
            string text = segment == null ? String.Empty : segment.Frequency > 0 ?
                NoteName(NoteNumber(segment.Frequency)) + " · " + segment.Frequency + " Hz\n" +
                "Длительность " + (segment.End - segment.Start).ToString("0", CultureInfo.CurrentCulture) + " мс · " +
                FormatPosition((long)segment.Start) + " — " + FormatPosition((long)segment.End) :
                "Тишина · " + (segment.End - segment.Start).ToString("0", CultureInfo.CurrentCulture) + " мс";
            SetTooltip(text);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!selecting || e.Button != MouseButtons.Left) return;
            SelectFromMouse(e.X, false); selecting = false; Capture = false; Cursor = Cursors.Cross;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) selecting = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!selecting) { SetTooltip(String.Empty); Cursor = Cursors.Default; }
        }

        private void SelectFromMouse(int x, bool forceEvent)
        {
            long selected = TimeFromX(x);
            bool changed = selectionPositionMs != selected;
            SelectPosition(selected);
            if (changed || forceEvent) {
                Action<long> handler = SeekRequested;
                if (handler != null) handler(selected);
            }
        }

        private long TimeFromX(float x)
        {
            RectangleF plot = PlotBounds;
            double fraction = Math.Max(0, Math.Min(1, (x - plot.Left) / Math.Max(1, plot.Width)));
            return ClampTime((long)Math.Round(fraction * totalMs));
        }

        private long ClampTime(long time) { return Math.Max(0, Math.Min((long)Math.Round(totalMs), time)); }
        private float TimeX(double time, RectangleF plot) { return plot.Left + (float)(Math.Max(0, Math.Min(totalMs, time)) / Math.Max(1, totalMs)) * plot.Width; }
        private float FrequencyY(double frequency, RectangleF plot)
        {
            if (frequency <= 0) return plot.Bottom;
            const double lower = 16;
            double fraction = Math.Log(Math.Max(lower, frequency) / lower) / Math.Log(upperFrequency / lower);
            return plot.Bottom - (float)Math.Max(0, Math.Min(1, fraction)) * plot.Height;
        }
        private float NoteHeight(RectangleF plot) { return plot.Height / Math.Max(1, upperNote - lowerNote + 1); }
        private float NoteY(int note, RectangleF plot) { return plot.Bottom - (note - lowerNote + .5F) * NoteHeight(plot); }
        private static int NoteNumber(int frequency) { return frequency <= 0 ? 0 : (int)Math.Round(69 + 12 * Math.Log(frequency / 440.0, 2), MidpointRounding.AwayFromZero); }
        private static int PitchClass(int note) { return ((note % 12) + 12) % 12; }
        private static bool IsBlackKey(int note) { int key = PitchClass(note); return key == 1 || key == 3 || key == 6 || key == 8 || key == 10; }
        private static string NoteName(int note)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return names[PitchClass(note)] + ((int)Math.Floor(note / 12.0) - 1).ToString(CultureInfo.InvariantCulture);
        }
        private Segment SegmentAt(double time)
        {
            int low = 0, high = segments.Count - 1;
            while (low <= high) {
                int middle = (low + high) / 2;
                Segment segment = segments[middle];
                if (time < segment.Start) high = middle - 1;
                else if (time >= segment.End) low = middle + 1;
                else return segment;
            }
            return null;
        }
        private void SetTooltip(string text)
        {
            if (tooltipText == text) return;
            tooltipText = text; noteTip.SetToolTip(this, text);
        }
        private static string FormatTime(double milliseconds)
        {
            long seconds = (long)Math.Max(0, Math.Round(milliseconds / 1000));
            return (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }
        private static string FormatPosition(long milliseconds)
        {
            long time = Math.Max(0, milliseconds);
            return (time / 60000).ToString("00") + ":" + ((time / 1000) % 60).ToString("00") + "." + (time % 1000).ToString("000");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { animation.Dispose(); noteTip.Dispose(); captionFont.Dispose(); axisFont.Dispose(); noteFont.Dispose(); valueFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
