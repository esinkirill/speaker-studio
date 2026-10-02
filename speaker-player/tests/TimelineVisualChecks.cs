using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SpeakerPlayer;

internal static class TimelineVisualChecks
{
    private static int assertions;
    private static readonly List<string> images = new List<string>();

    [STAThread]
    private static int Main(string[] args)
    {
        string evidence = null;
        try {
            string root = Path.GetFullPath(args[0]);
            evidence = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(evidence);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            CheckTimeline(evidence);
            CheckGeneratedMidi(evidence);
            CheckSmoothCurve(evidence);
            WriteReport(evidence, true, null);
            Console.WriteLine("TIMELINE VISUAL PASS " + assertions + " assertions. No hardware or audio output.");
            return 0;
        }
        catch (Exception error) {
            if (evidence != null) WriteReport(evidence, false, error.GetBaseException().ToString());
            Console.Error.WriteLine(error.GetBaseException());
            return 1;
        }
    }

    private static void CheckTimeline(string evidence)
    {
        using (Form host = Host())
        using (FrequencyChart chart = new FrequencyChart()) {
            chart.Size = new Size(1000, 350); host.Controls.Add(chart);
            host.Show(); Application.DoEvents();
            var requests = new List<long>();
            chart.SeekRequested += requests.Add;
            Assert(!chart.PianoRoll, "CSV frequency view is default");
            chart.SelectPosition(9999);
            Assert(chart.SelectionPositionMs == 0, "Empty timeline clamps selection to zero");
            Mouse(chart, "OnMouseDown", MouseButtons.Left, 100, 100);
            Assert(requests.Count == 0, "Empty timeline does not request seek");
            var rows = new List<ToneRow> {
                new ToneRow(262, 650, 100), new ToneRow(294, 850, 0),
                new ToneRow(440, 1000, 0), new ToneRow(0, 400, 0)
            };
            chart.SetPreparedSequence(rows);
            Assert(Field<double>(chart, "totalMs") == 3000, "Prepared duration includes note gaps and rests");
            Assert(SegmentCount(chart) == 5, "Prepared sound and rest intervals stay separate");
            rows[0].DurationMs = 1;
            Assert(SegmentNumber(chart, 0, "End") == 650, "Prepared chart is isolated from source row mutation");
            chart.SelectPosition(-1); Assert(chart.SelectionPositionMs == 0, "Negative selection clamps to start");
            chart.SelectPosition(9000); Assert(chart.SelectionPositionMs == 3000, "Selection beyond total clamps to end");
            Assert(requests.Count == 0, "Programmatic selection does not recursively raise seek");
            chart.PianoRoll = true;
            Paint(chart, evidence, "fixture-piano.png", 96);
            RectangleF plot = chart.PlotBounds;
            Assert(Close(plot.Left, 78) && Close(plot.Top, 54), "96 DPI piano axis uses shared drawing and hit-test bounds");
            Assert(plot.Right <= chart.Width && plot.Bottom <= chart.Height, "Piano plot fits canvas");
            int selectedX = X(chart, 1200), y = (int)(plot.Top + plot.Height / 2);
            Mouse(chart, "OnMouseDown", MouseButtons.Left, selectedX, y);
            Assert(requests.Count == 1 && NearTime(chart, requests[0], 1200), "Click at interior position requests matching timeline time");
            Assert(chart.Capture, "Drag retains mouse capture outside graph");
            Mouse(chart, "OnMouseMove", MouseButtons.Left, -500, y);
            Assert(chart.SelectionPositionMs == 0 && requests[requests.Count - 1] == 0, "Dragging before plot clamps exactly to zero");
            Mouse(chart, "OnMouseMove", MouseButtons.Left, chart.Width + 500, y);
            Assert(chart.SelectionPositionMs == 3000, "Dragging past plot clamps exactly to total");
            Mouse(chart, "OnMouseUp", MouseButtons.Left, chart.Width + 500, y);
            Assert(!chart.Capture, "Mouse release ends selection capture");
            int previousCount = requests.Count;
            Mouse(chart, "OnMouseDown", MouseButtons.Right, selectedX, y);
            Mouse(chart, "OnMouseDown", MouseButtons.Left, 10, 10);
            Assert(requests.Count == previousCount, "Right click and title area do not request seek");
            chart.UpdatePlayback(new PlaybackProgress { Frequency = 294, PositionMs = 1000, TotalMs = 3000 });
            chart.SelectPosition(2000);
            Assert(Field<double>(chart, "positionMs") == 1000 && chart.SelectionPositionMs == 2000,
                "Selected start and live playhead remain independent");
            Paint(chart, evidence, "fixture-two-markers.png", 96);
            chart.Reset();
            Assert(chart.SelectionPositionMs == 2000 && Field<double>(chart, "positionMs") == 0,
                "Reset clears playback without losing chosen start");
            chart.Finish();
            Assert(chart.SelectionPositionMs == 2000 && Field<double>(chart, "positionMs") == 3000,
                "Finish moves live playhead to end and preserves chosen start");
            chart.Reset();
            Mouse(chart, "OnMouseMove", MouseButtons.None, X(chart, 300), y);
            string tip = Field<string>(chart, "tooltipText");
            Assert(tip.Contains("C4") && tip.Contains("262 Hz") && tip.Contains("650"),
                "Piano hover describes note name, actual frequency and note duration");
            Mouse(chart, "OnMouseMove", MouseButtons.None, X(chart, 700), y);
            tip = Field<string>(chart, "tooltipText");
            Assert(tip.Contains("Тишина") && tip.Contains("100"), "Musical silence has a separate rest tooltip");
            Paint(chart, evidence, "fixture-piano-144dpi.png", 144);
            Assert(Close(chart.PlotBounds.Left, 117) && Close(chart.PlotBounds.Top, 81), "144 DPI painter updates shared plot bounds");
            y = (int)(chart.PlotBounds.Top + chart.PlotBounds.Height / 2);
            Mouse(chart, "OnMouseDown", MouseButtons.Left, X(chart, 1800), y);
            Mouse(chart, "OnMouseUp", MouseButtons.Left, X(chart, 1800), y);
            Assert(NearTime(chart, chart.SelectionPositionMs, 1800), "Scaled plot maps click to same musical time");
            chart.SetPreparedSequence(new List<ToneRow> { new ToneRow(440, 1000, 0) });
            Assert(chart.SelectionPositionMs == 1000, "Changing prepared total clamps existing selection");
            chart.SetPreparedSequence(null);
            Assert(chart.SelectionPositionMs == 0 && SegmentCount(chart) == 0, "Clearing source clears selectable data");
        }
    }

    private static void CheckGeneratedMidi(string evidence)
    {
        string fixture = Path.Combine(evidence, "generated-notes.mid");
        WriteGeneratedMidi(fixture);
        List<ToneRow> rows = SequenceFiles.ReadMidi(fixture, 0);
        Assert(rows.Count == 6, "Generated MIDI preserves notes and leading, internal and trailing rests");
        using (Form host = Host())
        using (FrequencyChart chart = new FrequencyChart()) {
            chart.PianoRoll = true; chart.Size = new Size(1050, 365);
            host.Controls.Add(chart); host.Show(); Application.DoEvents();
            chart.SetPreparedSequence(SequenceTiming.Transform(rows, 0, 1, 0));
            Assert(Field<double>(chart, "totalMs") == 3000, "MIDI piano roll uses complete prepared source duration");
            chart.SelectPosition(1800);
            chart.UpdatePlayback(new PlaybackProgress { Frequency = 294, PositionMs = 1500, TotalMs = 3000 });
            Paint(chart, evidence, "generated-piano-large.png", 96);
            Assert(SegmentNumber(chart, 0, "Frequency") == 0 && SegmentNumber(chart, 0, "End") == 500,
                "Leading MIDI silence stays visible on piano timeline");
            chart.Size = new Size(600, 200);
            Paint(chart, evidence, "generated-piano-compact.png", 96);
            Assert(chart.PlotBounds.Height >= 110 && chart.PlotBounds.Width >= 500, "Compact piano timeline retains useful plot area");
            Assert(chart.PianoRoll, "Resizing preserves MIDI representation");
        }
    }

    private static void WriteGeneratedMidi(string path)
    {
        byte[] body = { 0x83, 0x60, 0x90, 60, 100, 0x83, 0x60, 0x80, 60, 0,
            0x81, 0x70, 0x90, 62, 100, 0x83, 0x60, 0x80, 62, 0,
            0, 0x90, 67, 100, 0x87, 0x40, 0x80, 67, 0, 0x81, 0x70, 0xFF, 0x2F, 0 };
        using (FileStream file = File.Create(path))
        using (BinaryWriter writer = new BinaryWriter(file)) {
            writer.Write(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0 });
            writer.Write(new byte[] { 0x4D, 0x54, 0x72, 0x6B, 0, 0, 0, (byte)body.Length });
            writer.Write(body);
        }
    }

    private static void CheckSmoothCurve(string evidence)
    {
        var rows = new List<ToneRow> { new ToneRow(440, 350, 0) };
        for (int i = 1; i <= 120; i++) rows.Add(new ToneRow((int)Math.Round(440 * Math.Pow(2, i / 120.0)), 10, 0));
        rows.Add(new ToneRow(880, 450, 180)); rows.Add(new ToneRow(659, 600, 0));
        using (Form host = Host())
        using (FrequencyChart chart = new FrequencyChart()) {
            chart.Size = new Size(1050, 300); host.Controls.Add(chart); host.Show(); Application.DoEvents();
            chart.SetPreparedSequence(rows);
            Assert(Field<double>(chart, "totalMs") == 2780 && SegmentCount(chart) == 124, "CSV glide retains prepared frequency steps and rests");
            chart.SelectPosition(1980);
            chart.UpdatePlayback(new PlaybackProgress { Frequency = 600, PositionMs = 900, TotalMs = 2780 });
            Paint(chart, evidence, "csv-smooth-large.png", 96);
            Assert(Close(chart.PlotBounds.Left, 60), "CSV uses frequency axis with matching hit-test geometry");
            int y = (int)(chart.PlotBounds.Top + chart.PlotBounds.Height / 2);
            Mouse(chart, "OnMouseMove", MouseButtons.None, X(chart, 780), y);
            string tip = Field<string>(chart, "tooltipText");
            int actualFrequency = rows[44].Frequency;
            Assert(tip.Contains("Hz") && tip.Contains("10"), "Smooth frequency tooltip still reports actual prepared short event");
            chart.Size = new Size(600, 170);
            Paint(chart, evidence, "csv-smooth-compact.png", 96);
            Assert(chart.PlotBounds.Height >= 80, "Compact CSV graph keeps frequency trace and time labels visible");
        }
    }

    private static Form Host()
    {
        return new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ClientSize = new Size(1200, 500) };
    }

    private static void Paint(FrequencyChart chart, string evidence, string filename, float dpi)
    {
        using (Bitmap bitmap = new Bitmap(chart.Width, chart.Height, PixelFormat.Format32bppArgb)) {
            bitmap.SetResolution(dpi, dpi);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                Invoke(chart, "OnPaint", new PaintEventArgs(graphics, chart.ClientRectangle));
            string path = Path.Combine(evidence, filename); bitmap.Save(path, ImageFormat.Png); images.Add(path);
        }
    }

    private static void Mouse(FrequencyChart chart, string method, MouseButtons button, int x, int y)
    { Invoke(chart, method, new MouseEventArgs(button, button == MouseButtons.None ? 0 : 1, x, y, 0)); }
    private static void Invoke(object target, string method, params object[] args)
    { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static int SegmentCount(FrequencyChart chart) { return Field<IList>(chart, "segments").Count; }
    private static double SegmentNumber(FrequencyChart chart, int index, string name)
    {
        object segment = Field<IList>(chart, "segments")[index];
        return Convert.ToDouble(segment.GetType().GetField(name).GetValue(segment), CultureInfo.InvariantCulture);
    }
    private static int X(FrequencyChart chart, long time)
    { RectangleF plot = chart.PlotBounds; return (int)Math.Round(plot.Left + plot.Width * time / Field<double>(chart, "totalMs")); }
    private static bool NearTime(FrequencyChart chart, long actual, long expected)
    { return Math.Abs(actual - expected) <= Field<double>(chart, "totalMs") / chart.PlotBounds.Width + 1; }
    private static bool Close(double a, double b) { return Math.Abs(a - b) < .001; }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); assertions++; }
    private static void WriteReport(string evidence, bool passed, string error)
    {
        string json = new JavaScriptSerializer().Serialize(new { Passed = passed, Assertions = assertions,
            HardwareOrAudioOutput = false, Images = images, Error = error });
        File.WriteAllText(Path.Combine(evidence, "timeline-visual-check.json"), json);
    }
}
