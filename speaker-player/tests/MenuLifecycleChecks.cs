using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SpeakerPlayer;

internal static class MenuLifecycleChecks
{
    private static int assertions;
    private static readonly List<string> events = new List<string>();
    private static readonly List<string> uiErrors = new List<string>();
    private static readonly HashSet<ContextMenuStrip> observedMenus = new HashSet<ContextMenuStrip>();

    [STAThread]
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(evidence);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
            uiErrors.Add(e.Exception.ToString());
        };
        try {
            string root = Path.Combine(evidence, "fixture-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            Directory.CreateDirectory(root);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(root, "SpeakerStudio.exe"));
            string midi = Path.Combine(root, "two-parts.mid"); WriteMidi(midi, 2);
            CheckChoices(root, midi);
            CheckShutdown(root, midi);
            Assert(uiErrors.Count == 0, "The WinForms message pump reports no exceptions");
            WriteReport(evidence, true, null);
            Console.WriteLine("MENU LIFECYCLE PASS " + assertions + " assertions. Native messages target only the test's own windows; no hardware or audio output.");
            return 0;
        }
        catch (Exception error) {
            WriteReport(evidence, false, error.GetBaseException().ToString());
            Console.Error.WriteLine(error.GetBaseException());
            foreach (string uiError in uiErrors) Console.Error.WriteLine(uiError);
            return 1;
        }
    }

    private static void CheckChoices(string root, string midi)
    {
        using (PlayerForm form = Open(root, midi)) {
            ComboBox choices = Field<ComboBox>(form, "midiTrack");
            Assert(choices.Items.Count == 3, "Synthetic MIDI exposes all-parts and two independent parts");
            ContextMenuStrip previous = null;
            ToolStripItem[] previousItems = null;
            for (int round = 0; round < 6; round++) {
                int choice = round % choices.Items.Count;
                ContextMenuStrip menu = OpenMenu(form);
                if (previous != null) Assert(Object.ReferenceEquals(previous, menu), "Reopening reuses the form-owned menu");
                if (previousItems != null) foreach (ToolStripItem item in previousItems)
                    Assert(item.IsDisposed, "Rebuilding releases the previous menu items");
                Assert(menu.Items.Count == choices.Items.Count, "Reopening does not accumulate menu items");
                for (int index = 0; index < menu.Items.Count; index++) {
                    Assert(menu.Items[index].Text == choices.Items[index].ToString(), "Menu labels match current MIDI parts");
                    Assert(((ToolStripMenuItem)menu.Items[index]).Checked == (index == choices.SelectedIndex), "Check mark follows the selected part");
                }
                NativeClick(menu, Center(menu.Items[choice].Bounds));
                Pump();
                Assert(uiErrors.Count == 0, "Native item click completes without a disposed-menu exception");
                Assert(choices.SelectedIndex == choice, "Native item click selects the requested part");
                int expectedFrequency = choice == 1 ? 262 : 392;
                Assert(Field<List<ToneRow>>(form, "notes")[0].Frequency == expectedFrequency,
                    "Choosing a part loads its expected melody without playback");
                Assert(!menu.Visible && !menu.IsDisposed, "Selection closes the menu while keeping it alive");
                previous = menu;
                previousItems = new ToolStripItem[menu.Items.Count]; menu.Items.CopyTo(previousItems, 0);
            }

            int selected = choices.SelectedIndex;
            ContextMenuStrip dismissed = OpenMenu(form);
            PostMessage(dismissed.Handle, 0x0100, new IntPtr((int)Keys.Escape), IntPtr.Zero);
            PostMessage(dismissed.Handle, 0x0101, new IntPtr((int)Keys.Escape), IntPtr.Zero);
            Pump();
            Assert(!dismissed.Visible && !dismissed.IsDisposed && choices.SelectedIndex == selected,
                "Escape dismisses without changing selection or destroying the menu");

            ContextMenuStrip programmaticallyClosed = OpenMenu(form);
            programmaticallyClosed.Close(ToolStripDropDownCloseReason.AppClicked); Pump();
            Assert(!programmaticallyClosed.Visible && !programmaticallyClosed.IsDisposed,
                "Dismissal without an item click keeps the menu reusable");

            // A different source must rebuild labels and checked state at the next opening.
            string threeParts = Path.Combine(root, "three-parts.mid"); WriteMidi(threeParts, 3);
            form.AddFile(threeParts, true); Pump();
            ContextMenuStrip changed = OpenMenu(form);
            Assert(changed.Items.Count == 4, "Changing MIDI refreshes the available parts");
            NativeClick(changed, Center(changed.Items[3].Bounds)); Pump();
            Assert(uiErrors.Count == 0 && choices.SelectedIndex == 3, "Selection works after switching to a source with more parts");
            form.Close(); Pump();
            Assert(changed.IsDisposed, "Closing the owner disposes its menu");
        }
    }

    private static void CheckShutdown(string root, string midi)
    {
        PlayerForm form = Open(root, midi);
        ContextMenuStrip visible = OpenMenu(form);
        form.Close(); Pump();
        Assert(form.IsDisposed && visible.IsDisposed, "Closing while the menu is open disposes both owner and menu");
        Assert(uiErrors.Count == 0, "Closing an open menu has no queued UI errors");
        form.Dispose();

        form = Open(root, midi);
        ContextMenuStrip disposed = OpenMenu(form);
        form.Dispose(); Pump();
        Assert(disposed.IsDisposed, "Direct owner Dispose also disposes the menu");
        Assert(uiErrors.Count == 0, "Direct owner Dispose has no queued UI errors");
    }

    private static PlayerForm Open(string root, string midi)
    {
        PlayerForm form = new PlayerForm(root, null) { ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        form.Show(); Pump();
        Field<ComboBox>(form, "output").SelectedIndex = (int)OutputMode.Visual;
        form.AddFile(midi, true); Pump();
        Assert(!Field<PlaybackEngine>(form, "engine").IsPlaying, "The regression never starts playback");
        return form;
    }

    private static ContextMenuStrip OpenMenu(PlayerForm form)
    {
        Button button = Field<Button>(form, "partButton");
        Assert(button.Visible && button.Enabled, "The MIDI part button is available");
        NativeClick(button, Center(button.ClientRectangle)); Pump();
        var menus = new List<ContextMenuStrip>();
        EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr window, IntPtr parameter) {
            ContextMenuStrip menu = Control.FromHandle(window) as ContextMenuStrip;
            if (menu != null && menu.Visible) menus.Add(menu);
            return true;
        }, IntPtr.Zero);
        Assert(menus.Count == 1, "Clicking the button opens exactly one menu on the test's STA thread");
        ContextMenuStrip opened = menus[0];
        if (observedMenus.Add(opened)) {
            opened.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs e) { events.Add("Closed: " + e.CloseReason); };
            opened.ItemClicked += delegate(object sender, ToolStripItemClickedEventArgs e) { events.Add("ItemClicked: " + e.ClickedItem.Text); };
        }
        return opened;
    }

    private static void NativeClick(Control control, Point position)
    {
        IntPtr coordinates = new IntPtr((position.Y << 16) | (position.X & 0xffff));
        IntPtr handle = control.Handle;
        SendMessage(handle, 0x0200, IntPtr.Zero, coordinates);
        SendMessage(handle, 0x0201, new IntPtr(1), coordinates);
        SendMessage(handle, 0x0202, IntPtr.Zero, coordinates);
    }

    private static Point Center(Rectangle bounds) { return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2); }
    private static void Pump()
    {
        Stopwatch clock = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (clock.ElapsedMilliseconds < 35);
    }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); assertions++; }

    private static void WriteReport(string evidence, bool passed, string error)
    {
        File.WriteAllText(Path.Combine(evidence, "menu-lifecycle-check.json"), new JavaScriptSerializer().Serialize(new {
            Passed = passed, Assertions = assertions, HardwareOrAudioOutput = false,
            InputPath = "WM_MOUSEMOVE/WM_LBUTTONDOWN/WM_LBUTTONUP through own HWND/WinForms WndProc",
            Events = events, UiErrors = uiErrors, Error = error }));
    }

    private static void WriteMidi(string path, int tracks)
    {
        using (MemoryStream memory = new MemoryStream()) using (BinaryWriter writer = new BinaryWriter(memory)) {
            writer.Write(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 1, 0, (byte)tracks, 1, 0xE0 });
            for (int track = 0; track < tracks; track++) {
                byte[] body = { 0, (byte)(0xC0 + track), 0, 0, (byte)(0x90 + track), (byte)(60 + 7 * track), 100,
                    0x83, 0x60, (byte)(0x80 + track), (byte)(60 + 7 * track), 0, 0, 0xFF, 0x2F, 0 };
                writer.Write(new byte[] { 0x4D, 0x54, 0x72, 0x6B, 0, 0, 0, (byte)body.Length }); writer.Write(body);
            }
            File.WriteAllBytes(path, memory.ToArray());
        }
    }

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowProc callback, IntPtr parameter);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
