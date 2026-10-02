using System;
using System.IO;
using System.Windows.Forms;

namespace SpeakerPlayer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string initial = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
            using (PlayerForm form = new PlayerForm(root, initial)) {
                form.LaunchArguments = args;
                Application.Run(form);
            }
        }
    }
}
