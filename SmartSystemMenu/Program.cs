﻿using System;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.IO;
using System.Drawing.Imaging;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using SmartSystemMenu.Forms;
using SmartSystemMenu.Utils;
using SmartSystemMenu.Native;
using SmartSystemMenu.Native.Enums;
using SmartSystemMenu.Settings;
using SmartSystemMenu.Extensions;

namespace SmartSystemMenu
{
    static class Program
    {
        private static Mutex _mutex;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            Application.ThreadException += OnThreadException;

            var settingsFileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "SmartSystemMenu.xml");
            var languageFileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Language.xml");
#if WIN32
            var windowFileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Window.xml");
#else
            var windowFileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Window64.xml");
#endif

            var settings = File.Exists(settingsFileName) && File.Exists(languageFileName) ? ApplicationSettingsFile.Read(settingsFileName, languageFileName) : new ApplicationSettings();
            var windowSettings = File.Exists(windowFileName) ? WindowSettings.Read(windowFileName) : new WindowSettings();

            // Enable High DPI Support
            if (settings.EnableHighDPI)
            {
                SystemUtils.EnableHighDPISupport();
            }

            // Command Line Interface
            var toggleParser = new ToggleParser(args);
            if (toggleParser.HasToggle("help"))
            {
                var dialog = new MessageBoxForm
                {
                    Message = BuildHelpString(),
                    Text = "Help"
                };
                dialog.ShowDialog();
                return;
            }

            string hookFileName = null;
            string hook64FileName = null;
            FileSecurity hookSecurity = null;
            FileSecurity hook64Security = null;
            IdentityReference hookOwner = null;
            IdentityReference hook64Owner = null;

            if (toggleParser.HasToggle("trustedinstaller"))
            {
                SystemUtils.SetProcessTokenPrivileges(Process.GetCurrentProcess().Handle, "SeTakeOwnershipPrivilege");
                hookFileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "SmartSystemMenuHook.dll");
                hook64FileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "SmartSystemMenuHook64.dll");
                hookFileName = SystemUtils.GetUniversalName(hookFileName);
                hook64FileName = SystemUtils.GetUniversalName(hook64FileName);
                hookSecurity = File.GetAccessControl(hookFileName);
                hook64Security = File.GetAccessControl(hook64FileName);
                hookOwner = hookSecurity.GetOwner(typeof(NTAccount));
                hook64Owner = hook64Security.GetOwner(typeof(NTAccount));
                var trustedInstallerAccount = new NTAccount("NT SERVICE\\TrustedInstaller");
                hookSecurity.SetOwner(trustedInstallerAccount);
                File.SetAccessControl(hookFileName, hookSecurity);
                hook64Security.SetOwner(trustedInstallerAccount);
                File.SetAccessControl(hook64FileName, hook64Security);
            }

            ProcessCommandLine(toggleParser, settings);

            if (toggleParser.HasToggle("n") || toggleParser.HasToggle("nogui"))
            {
                if (toggleParser.HasToggle("trustedinstaller"))
                {
                    if (hookFileName != null && hookSecurity != null && hookOwner != null)
                    {
                        hookSecurity.SetOwner(hookOwner);
                        File.SetAccessControl(hookFileName, hookSecurity);
                    }

                    if (hook64FileName != null && hook64Security != null && hook64Owner != null)
                    {
                        hook64Security.SetOwner(hook64Owner);
                        File.SetAccessControl(hook64FileName, hook64Security);
                    }
                }

                return;
            }

            var parentHandle = toggleParser.HasToggle("parentHandle") && long.TryParse(toggleParser.GetToggleValueOrDefault("parentHandle", string.Empty), out var parentHandleValue) ? parentHandleValue : (long?)null;

#if WIN32
            var mutexName = "SmartSystemMenuMutex";
#else
            var mutexName = "SmartSystemMenuMutex64";
#endif
            _mutex = new Mutex(false, mutexName, out var createNew);
            if (!createNew)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var mainForm = new MainForm(settings, windowSettings, parentHandle.HasValue ? new IntPtr(parentHandle.Value) : IntPtr.Zero);
            Application.Run(mainForm);

            if (toggleParser.HasToggle("trustedinstaller"))
            {
                if (hookFileName != null && hookSecurity != null && hookOwner != null)
                {
                    hookSecurity.SetOwner(hookOwner);
                    File.SetAccessControl(hookFileName, hookSecurity);
                }

                if (hook64FileName != null && hook64Security != null && hook64Owner != null)
                {
                    hook64Security.SetOwner(hook64Owner);
                    File.SetAccessControl(hook64FileName, hook64Security);
                }
            }
        }

        static void ProcessCommandLine(ToggleParser toggleParser, ApplicationSettings settings)
        {
            // Delay
            if (toggleParser.HasToggle("d") || toggleParser.HasToggle("delay"))
            {
                var delayString = toggleParser.GetToggleValueOrDefault("d", null) ?? toggleParser.GetToggleValueOrDefault("delay", null);
                if (int.TryParse(delayString, out var delay))
                {
                    Thread.Sleep(delay);
                }
            }

            // Clear Clipboard
            if (toggleParser.HasToggle("clearclipboard"))
            {
                Clipboard.Clear();
            }

            var windowHandles = new List<IntPtr>();
            var processId = (int?)null;
            if (toggleParser.HasToggle("processId"))
            {
                var processIdString = toggleParser.GetToggleValueOrDefault("processId", null);
                processId = !string.IsNullOrWhiteSpace(processIdString) && int.TryParse(processIdString, out var pid) ? pid : (int?)null;
            }

            if (toggleParser.HasToggle("handle"))
            {
                var windowHandleString = toggleParser.GetToggleValueOrDefault("handle", null);
                if (!string.IsNullOrWhiteSpace(windowHandleString))
                {
                    var windowHandle = windowHandleString.StartsWith("0x") ? int.TryParse(windowHandleString.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var number) ? new IntPtr(number) :
                        IntPtr.Zero : int.TryParse(windowHandleString, out var number2) ? new IntPtr(number2) : IntPtr.Zero;
                    windowHandles.Add(windowHandle);
                }
            }

            if (toggleParser.HasToggle("title"))
            {
                var windowTitle = toggleParser.GetToggleValueOrDefault("title", null);
                var handles = WindowUtils.FindWindowByTitle(windowTitle, processId, (value, title) => string.Compare(value, title, true) == 0);
                windowHandles.AddRange(handles);
            }

            if (toggleParser.HasToggle("titleBegins"))
            {
                var windowTitle = toggleParser.GetToggleValueOrDefault("titleBegins", null);
                var handles = WindowUtils.FindWindowByTitle(windowTitle, processId, (value, title) => title.StartsWith(value, StringComparison.OrdinalIgnoreCase));
                windowHandles.AddRange(handles);
            }

            if (toggleParser.HasToggle("titleEnds"))
            {
                var windowTitle = toggleParser.GetToggleValueOrDefault("titleEnds", null);
                var handles = WindowUtils.FindWindowByTitle(windowTitle, processId, (value, title) => title.EndsWith(value, StringComparison.OrdinalIgnoreCase));
                windowHandles.AddRange(handles);
            }

            if (toggleParser.HasToggle("titleContains"))
            {
                var windowTitle = toggleParser.GetToggleValueOrDefault("titleContains", null);
                var handles = WindowUtils.FindWindowByTitle(windowTitle, processId, (value, title) => title.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
                windowHandles.AddRange(handles);
            }


            foreach (var windowHandle in windowHandles.Where(x => x != IntPtr.Zero && User32.GetParent(x) == IntPtr.Zero))
            {
                var window = new Window(windowHandle);

                // Set a Window monitor
                if (toggleParser.HasToggle("m") || toggleParser.HasToggle("monitor"))
                {
                    var monitorString = toggleParser.GetToggleValueOrDefault("m", null) ?? toggleParser.GetToggleValueOrDefault("monitor", null);
                    if (int.TryParse(monitorString, out var monitor))
                    {
                        var monitorItem = SystemUtils.GetMonitors().Select((x, i) => new { Index = i, MonitorHandle = x }).FirstOrDefault(x => x.Index == monitor);
                        if (monitorItem != null)
                        {
                            window.MoveToMonitor(monitorItem.MonitorHandle);
                        }
                    }
                }

                // Set a Window width
                if (toggleParser.HasToggle("w") || toggleParser.HasToggle("width"))
                {
                    var widthString = toggleParser.GetToggleValueOrDefault("w", null) ?? toggleParser.GetToggleValueOrDefault("width", null);
                    if (int.TryParse(widthString, out var width))
                    {
                        window.SetWidth(width);
                    }
                }

                // Set a Window height
                if (toggleParser.HasToggle("h") || toggleParser.HasToggle("height"))
                {
                    var heightString = toggleParser.GetToggleValueOrDefault("h", null) ?? toggleParser.GetToggleValueOrDefault("height", null);
                    if (int.TryParse(heightString, out var height))
                    {
                        window.SetHeight(height);
                    }
                }

                // Set a Window left position
                if (toggleParser.HasToggle("l") || toggleParser.HasToggle("left"))
                {
                    var leftString = toggleParser.GetToggleValueOrDefault("l", null) ?? toggleParser.GetToggleValueOrDefault("left", null);
                    if (int.TryParse(leftString, out var left))
                    {
                        window.SetLeft(left);
                    }
                }

                // Set a Window top position
                if (toggleParser.HasToggle("t") || toggleParser.HasToggle("top"))
                {
                    var topString = toggleParser.GetToggleValueOrDefault("t", null) ?? toggleParser.GetToggleValueOrDefault("top", null);
                    if (int.TryParse(topString, out var top))
                    {
                        window.SetTop(top);
                    }
                }

                // Set a Window position
                if (toggleParser.HasToggle("a") || toggleParser.HasToggle("alignment"))
                {
                    var windowAlignmentString = toggleParser.GetToggleValueOrDefault("a", null) ?? toggleParser.GetToggleValueOrDefault("alignment", null);
                    var windowAlignment = Enum.TryParse<WindowAlignment>(windowAlignmentString, true, out var alignment) ? alignment : 0;
                    window.SetAlignment(windowAlignment);
                }

                // Set a Window transparency
                if (toggleParser.HasToggle("transparency"))
                {
                    if (byte.TryParse(toggleParser.GetToggleValueOrDefault("transparency", null), out var transparency))
                    {
                        transparency = transparency > 100 ? (byte)100 : transparency;
                        window.SetTransparency(transparency);
                    }
                }

                // Set a Process priority
                if (toggleParser.HasToggle("p") || toggleParser.HasToggle("priority"))
                {
                    var processPriorityString = toggleParser.GetToggleValueOrDefault("p", null) ?? toggleParser.GetToggleValueOrDefault("priority", null);
                    var processPriority = Enum.TryParse<Priority>(processPriorityString, true, out var priority) ? priority : 0;
                    window.SetPriority(processPriority);
                }

                // Set a Window AlwaysOnTop
                if (toggleParser.HasToggle("alwaysontop"))
                {
                    var alwaysontopString = toggleParser.GetToggleValueOrDefault("alwaysontop", string.Empty).ToLower();

                    if (alwaysontopString == "on")
                    {
                        window.MakeTopMost(true);
                    }

                    if (alwaysontopString == "off")
                    {
                        window.MakeTopMost(false);
                    }
                }

                // Set a Window Aero Glass
                if (toggleParser.HasToggle("g") || toggleParser.HasToggle("aeroglass"))
                {
                    var aeroglassString = (toggleParser.GetToggleValueOrDefault("g", null) ?? toggleParser.GetToggleValueOrDefault("aeroglass", string.Empty)).ToLower();
                    var enabled = aeroglassString == "on" ? true : aeroglassString == "off" ? false : (bool?)null;

                    if (enabled.HasValue)
                    {
                        var version = Environment.OSVersion.Version;
                        if (version.Major == 6 && (version.Minor == 0 || version.Minor == 1))
                        {
                            WindowUtils.AeroGlassForVistaAndSeven(window.Handle, enabled.Value);
                        }
                        else if (version.Major >= 6 || (version.Major == 6 && version.Minor > 1))
                        {
                            WindowUtils.AeroGlassForEightAndHigher(window.Handle, enabled.Value);
                        }
                    }
                }

                // Hide For Alt+Tab
                if (toggleParser.HasToggle("hidealttab"))
                {
                    var hideAltTabString = toggleParser.GetToggleValueOrDefault("hidealttab", string.Empty).ToLower();

                    if (hideAltTabString == "on")
                    {
                        window.HideForAltTab(true);
                    }

                    if (hideAltTabString == "off")
                    {
                        window.HideForAltTab(false);
                    }
                }

                // Click Through
                if (toggleParser.HasToggle("clickthrough"))
                {
                    var clickthroughString = toggleParser.GetToggleValueOrDefault("clickthrough", string.Empty).ToLower();

                    if (clickthroughString == "on")
                    {
                        window.ClickThrough(true);
                    }

                    if (clickthroughString == "off")
                    {
                        window.ClickThrough(false);
                    }
                }

                // Send To Bottom Window
                if (toggleParser.HasToggle("sendtobottom"))
                {
                    window.SendToBottom();
                }

                // Open File In Explorer
                if (toggleParser.HasToggle("o") || toggleParser.HasToggle("openinexplorer"))
                {
                    try
                    {
                        SystemUtils.RunAs("explorer.exe", "/select, " + window.Process.GetMainModuleFileName(), true, UserType.Normal);
                    }
                    catch
                    {
                    }
                }

                // Copy to clipboard
                if (toggleParser.HasToggle("c") || toggleParser.HasToggle("copytoclipboard"))
                {
                    var text = window.ExtractText();
                    if (text != null)
                    {
                        Clipboard.SetText(text);
                    }
                }

                // Copy Screenshot
                if (toggleParser.HasToggle("copyscreenshot"))
                {
                    var result = WindowUtils.PrintWindow(window.Handle, out var bitmap);
                    if (!result || !WindowUtils.IsCorrectScreenshot(window.Handle, bitmap))
                    {
                        WindowUtils.CaptureWindow(window.Handle, false, out bitmap);
                    }

                    Clipboard.SetImage(bitmap);
                }

                //Information dialog
                if (toggleParser.HasToggle("i") || toggleParser.HasToggle("information"))
                {
                    var dialog = new InformationForm(window.GetWindowInfo(), settings.Language);
                    dialog.ShowDialog();
                }

                //Save Screenshot
                if (toggleParser.HasToggle("s") || toggleParser.HasToggle("savescreenshot"))
                {
                    var result = WindowUtils.PrintWindow(window.Handle, out var bitmap);
                    if (!result || !WindowUtils.IsCorrectScreenshot(window.Handle, bitmap))
                    {
                        WindowUtils.CaptureWindow(window.Handle, false, out bitmap);
                    }

                    var dialog = new SaveFileDialog
                    {
                        OverwritePrompt = true,
                        ValidateNames = true,
                        Title = settings.Language.GetValue("save_screenshot_title"),
                        FileName = settings.Language.GetValue("save_screenshot_filename"),
                        DefaultExt = settings.Language.GetValue("save_screenshot_default_ext"),
                        RestoreDirectory = false,
                        Filter = settings.Language.GetValue("save_screenshot_filter")
                    };
                    if (dialog.ShowDialog(window.Win32Window) == DialogResult.OK)
                    {
                        var fileExtension = Path.GetExtension(dialog.FileName).ToLower();
                        var imageFormat = fileExtension == ".bmp" ? ImageFormat.Bmp :
                            fileExtension == ".gif" ? ImageFormat.Gif :
                            fileExtension == ".jpeg" ? ImageFormat.Jpeg :
                            fileExtension == ".png" ? ImageFormat.Png :
                            fileExtension == ".tiff" ? ImageFormat.Tiff : ImageFormat.Wmf;
                        bitmap.Save(dialog.FileName, imageFormat);
                    }
                }

                // Disable "Minimize" Button 
                if (toggleParser.HasToggle("minimizebutton"))
                {
                    var minimizebuttonString = toggleParser.GetToggleValueOrDefault("minimizebutton", string.Empty).ToLower();

                    if (minimizebuttonString == "on")
                    {
                        window.DisableMinimizeButton(false);
                    }

                    if (minimizebuttonString == "off")
                    {
                        window.DisableMinimizeButton(true);
                    }
                }

                // Disable "Maximize" Button 
                if (toggleParser.HasToggle("maximizebutton"))
                {
                    var maximizebuttonString = toggleParser.GetToggleValueOrDefault("maximizebutton", string.Empty).ToLower();

                    if (maximizebuttonString == "on")
                    {
                        window.DisableMaximizeButton(false);
                    }

                    if (maximizebuttonString == "off")
                    {
                        window.DisableMaximizeButton(true);
                    }
                }
            }
        }

        static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            ex ??= new Exception("OnCurrentDomainUnhandledException");
            OnThreadException(sender, new ThreadExceptionEventArgs(ex));
        }

        static void OnThreadException(object sender, ThreadExceptionEventArgs e) =>
            MessageBox.Show(e.Exception.ToString(), AssemblyUtils.AssemblyTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);

        static string BuildHelpString() =>
                @"   --help             The help
   --title            Title
   --titleBegins      Title begins 
   --titleEnds        Title ends
   --titleContains    Title contains
   --handle           Handle (1234567890) (0xFFFFFF)
   --processId        PID (1234567890)
-d --delay            Delay in milliseconds
-l --left             Left
-t --top              Top
-w --width            Width
-h --height           Height
-i --information      Information dialog
-s --savescreenshot   Save Screenshot
-m --monitor          [0, 1, 2, 3, ...]
-a --alignment        [topleft,
                       topcenter,
                       topright,
                       middleleft,
                       middlecenter,
                       middleright,
                       bottomleft,
                       bottomcenter,
                       bottomright,
                       centerhorizontally,
                       centervertically]
-p --priority         [realtime,
                       high,
                       abovenormal,
                       normal,
                       belownormal,
                       idle]
   --transparency     [0 ... 100]
   --alwaysontop      [on, off]
-g --aeroglass        [on, off]
   --hidealttab       [on, off]
   --clickthrough     [on, off]
   --minimizebutton   [on, off]
   --maximizebutton   [on, off]
   --sendtobottom     Send To Bottom
-o --openinexplorer   Open File In Explorer
-c --copytoclipboard  Copy Window Text To Clipboard
   --copyscreenshot   Copy Screenshot To Clipboard
   --clearclipboard   Clear Clipboard
   --trustedinstaller Sets TrustedInstaller owner for SmartSystemMenuHook.dll and SmartSystemMenuHook64.dll
-n --nogui            No GUI

Example:
SmartSystemMenu.exe --title ""Untitled - Notepad"" -a topleft -p high --alwaysontop on --nogui";
    }
}
