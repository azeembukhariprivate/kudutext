using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kudutext.Core;
using Kudutext.Views;

namespace Kudutext
{
    public static class Program
    {
        /// <summary>
        /// KUDUTEXT.exe                          open
        /// KUDUTEXT.exe file...                  open and add these videos or recordings to the list
        /// KUDUTEXT.exe --self-test --model ggml-tiny.bin --samples folder-with-jfk.mp3 --ffmpeg ffmpeg-essentials.zip   automated checks for the build
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new App();
            if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                string Value(string name)
                {
                    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                }
                return app.RunSelfTest(Value("--model"), Value("--samples"), Value("--ffmpeg"));
            }

            // One copy at a time: two would share (and fight over) the same list.
            using (var mutex = new Mutex(true, @"Local\KUDUTEXT-" + Environment.UserName, out var first))
            {
                if (!first)
                {
                    MessageBox.Show("KUDUTEXT is already open.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                return app.Run(args.Where(a => !a.StartsWith("--")).Select(Full).Where(a => a != null).ToArray());
            }
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }
    }

    public sealed class App : Application
    {
        public int Run(string[] files)
        {
            Startup += (s, e) =>
            {
                Init();
                var window = new MainWindow();
                MainWindow = window;
                window.Show();
                if (files.Length > 0) window.Add(files);
            };
            return Run();
        }

        public int RunSelfTest(string model, string samples, string ffmpeg)
        {
            var code = 1;
            Startup += (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = SelfTest.Run(model, samples, ffmpeg);
                Shutdown(code);
            };
            Run();
            return code;
        }

        private void Init()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            SafetyNet.Install(); // says so if an error behind the window closes the app
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
