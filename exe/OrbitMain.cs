// Orbit — entry point, CLI, and GUI. Developed by Dr. Mohamed Shehata.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using Path = System.IO.Path;

namespace OrbitApp
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0) return Gui.Run();
            if (args[0] == "--worker")
            {
                var d = ParseArgs(args);
                return Worker.RunElevated(Get(d, "role"), Get(d, "code"), Get(d, "name"), Get(d, "progress"));
            }
            AttachConsole(-1);
            return Cli.Run(args);
        }

        static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i++)
                if (args[i].StartsWith("--") && i + 1 < args.Length) { d[args[i].Substring(2)] = args[i + 1]; i++; }
            return d;
        }
        static string Get(Dictionary<string, string> d, string k) { return d.ContainsKey(k) ? d[k] : ""; }
    }

    // ============================ CLI ============================
    static class Cli
    {
        static void W(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

        public static int Run(string[] args)
        {
            var verb = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            try
            {
                switch (verb)
                {
                    case "list": return List();
                    case "status": return Status(Need(rest, 0));
                    case "ssh": return Ssh(rest);
                    case "shot": return Shot(rest);
                    case "click": return Vnc(rest[0], new[] { "click", rest[1], rest[2], rest.Length > 3 ? rest[3] : "1" }, "clicked " + rest[1] + "," + rest[2]);
                    case "move": return Vnc(rest[0], new[] { "move", rest[1], rest[2] }, "moved");
                    case "dclick": return Vnc(rest[0], new[] { "dclick", rest[1], rest[2] }, "double-clicked");
                    case "type": return Vnc(rest[0], new[] { "type" }.Concat(rest.Skip(1)).ToArray(), "typed");
                    case "key": return Vnc(rest[0], new[] { "key" }.Concat(rest.Skip(1)).ToArray(), "sent keys");
                    case "tunnel": return Tunnel(rest);
                    case "scan": return Scan();
                    default:
                        W("Orbit CLI " + Core.Version + "\nCommands: list | status | ssh | shot | click | move | dclick | type | key | tunnel | scan");
                        return 0;
                }
            }
            catch (Exception ex) { W("error: " + ex.Message); return 1; }
        }

        static string Need(string[] r, int i) { if (i >= r.Length) throw new Exception("Specify a device name."); return r[i]; }
        static Device Dev(string name)
        {
            var d = Core.GetDevice(name);
            if (d == null) throw new Exception("No device named '" + name + "'. Try: orbit list");
            return d;
        }

        static int List()
        {
            var devs = Core.GetDevices();
            if (devs.Count == 0) { W("No paired devices. Open Orbit on the main computer and pair one."); return 0; }
            foreach (var d in devs)
            {
                var ping = Core.DeviceSsh(d, "echo ok", 10000);
                var st = ping.Out.Contains("ok") ? "online" : "unreachable";
                W(string.Format("{0,-22} {1,-16} {2}  ({3}@{4}:{5}, vnc {6})", d.name, d.alias, st, d.user, d.ip, d.sshPort, d.vncPort));
            }
            return 0;
        }
        static int Status(string name)
        {
            var d = Dev(name);
            var ping = Core.DeviceSsh(d, "echo ok", 10000);
            W("SSH: " + ping.Out.Contains("ok"));
            int rc; try { var o = Vnc(d, new[] { "probe" }, out rc); W("VNC: " + (rc == 0) + (o.Length > 0 ? " (" + o + ")" : "")); } catch (Exception ex) { W("VNC: False - " + ex.Message); }
            return 0;
        }
        static int Ssh(string[] rest)
        {
            var d = Dev(rest[0]);
            var cmd = string.Join(" ", rest.Skip(1));
            if (cmd.Length == 0) throw new Exception("Type the command after the device name.");
            var r = Core.DeviceSsh(d, cmd, 60000);
            if (r.Out.Length > 0) W(r.Out.TrimEnd());
            if (r.Err.Length > 0) W(r.Err.TrimEnd());
            return r.Code;
        }
        static int Shot(string[] rest)
        {
            var d = Dev(rest[0]);
            var outp = rest.Length > 1 ? rest[1] : Path.Combine(Path.GetTempPath(), "orbit-" + d.alias + "-" + DateTime.Now.ToString("HHmmss") + ".png");
            int rc; var res = Vnc(d, new[] { "capture", outp }, out rc);
            if (rc != 0) throw new Exception(res);
            W(res); return 0;
        }
        static int Tunnel(string[] rest)
        {
            var d = Dev(rest[0]); int lp = int.Parse(rest[1]), rp = int.Parse(rest[2]);
            W("Tunnel open: http://127.0.0.1:" + lp + "  ->  " + d.name + ":" + rp + "  (close this window to stop)");
            var args = new[] { "-i", Core.KeyPath, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=NUL", "-o", "ExitOnForwardFailure=yes", "-N", "-p", d.sshPort.ToString(),
                "-L", "127.0.0.1:" + lp + ":127.0.0.1:" + rp, "-l", d.user, d.ip };
            var psi = new ProcessStartInfo { FileName = Core.SshExe, Arguments = string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)), UseShellExecute = false };
            using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }
        static int Scan()
        {
            var found = new List<Banner>();
            foreach (var ip in Core.FindOpenSsh()) { var b = Core.ReadBanner(ip); if (b != null) found.Add(b); }
            if (found.Count == 0) { W("No Orbit devices on the network."); return 0; }
            foreach (var f in found) W(string.Format("{0,-18} {1,-10} {2}@{3}", f.Name, f.Role, f.User, f.IP));
            return 0;
        }

        static int Vnc(string name, string[] vncArgs, string okMsg) { var d = Dev(name); int rc; Vnc(d, vncArgs, out rc); if (rc == 0 && okMsg != null) W(okMsg); return rc; }
        static string Vnc(Device dev, string[] vncArgs, out int rc)
        {
            var code = Core.Unprotect(dev.code);
            if (code == null) throw new Exception("Can't decrypt the code for '" + dev.name + "' (must be the same Windows account that paired it).");
            var py = Core.PyExe(); if (py == null) throw new Exception("Python is not installed on the main computer - it is needed for the screen channel.");
            var drv = Core.DriverPath;
            if (!File.Exists(drv)) Core.ExtractResource("orbit-vnc.py", drv);
            int lp; var t = Core.OpenTunnel(dev, out lp);
            if (t == null) throw new Exception("Can't open an SSH tunnel to '" + dev.name + "' - make sure it is on and on the network.");
            try
            {
                var server = "127.0.0.1::" + lp;
                Environment.SetEnvironmentVariable("ORBIT_VNC_PW", code);
                var argv = new List<string> { drv, vncArgs[0], server };
                argv.AddRange(vncArgs.Skip(1));
                var r = Core.Run(py, argv.ToArray(), 40000);
                rc = r.Code;
                if (r.Code != 0) throw new Exception("Screen channel failed: " + r.Err.Trim());
                return r.Out.Trim();
            }
            finally { Environment.SetEnvironmentVariable("ORBIT_VNC_PW", null); try { if (!t.HasExited) t.Kill(); } catch { } }
        }
    }

    // ============================ GUI ============================
    static class Gui
    {
        static Window win;
        static string curCode;
        static List<Banner> banners = new List<Banner>();
        static Banner selectedBanner;
        static readonly List<Border> deviceCards = new List<Border>();

        public static int Run()
        {
            var app = new Application();
            win = (Window)XamlReader.Load(XmlReader.Create(new StringReader(Core.ReadTextResource("orbit.xaml"))));
            try { var fd = Core.ExtractFonts(); win.FontFamily = new FontFamily(new Uri(fd.TrimEnd('\\') + "\\"), "./#Cairo"); } catch { }
            try { var ico = Path.Combine(Core.HomeDir, "orbit.ico"); if (!File.Exists(ico)) Core.ExtractResource("orbit.ico", ico); win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico)); } catch { }

            Wire<Button>("btnPrimary", b => b.Click += (s, e) => StartPrimary());
            Wire<Button>("btnSecondary", b => b.Click += (s, e) => BeginSecondary());
            Wire<Button>("btnSetup", b => b.Click += (s, e) => StartSecondary());
            Wire<Button>("btnBack", b => b.Click += (s, e) => Show("role"));
            Wire<Button>("btnClose", b => b.Click += (s, e) => win.Close());
            Wire<Button>("btnMinimize", b => b.Click += (s, e) => win.WindowState = WindowState.Minimized);
            Wire<Button>("btnCopyCode", b => b.Click += (s, e) => { try { if (curCode != null) Clipboard.SetText(curCode); } catch { } });
            Wire<Button>("btnConnect", b => b.Click += (s, e) => DoConnect());
            Wire<Button>("btnRescan", b => b.Click += (s, e) => StartPrimary());
            Wire<Button>("btnManualConnect", b => b.Click += (s, e) => DoConnectManual());
            var lm = win.FindName("lnkManual") as System.Windows.Controls.TextBlock;
            if (lm != null) lm.MouseLeftButtonUp += (s, e) => { var pm = win.FindName("pnlManual") as UIElement; if (pm != null) pm.Visibility = pm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
            var rm = win.FindName("lnkRemove") as System.Windows.Controls.TextBlock;
            if (rm != null) rm.MouseLeftButtonUp += (s, e) => RemoveThis();

            win.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { win.DragMove(); } catch { } } };
            win.SourceInitialized += (s, e) => { try { Native.Glass(new WindowInteropHelper(win).Handle); } catch { } };
            Show("role");
            app.Run(win);
            return 0;
        }

        static void Wire<T>(string name, Action<T> act) where T : class { var o = win.FindName(name) as T; if (o != null) act(o); }
        static T F<T>(string name) where T : class { return win.FindName(name) as T; }
        static void SetText(string name, string text) { var t = win.FindName(name) as System.Windows.Controls.TextBlock; if (t != null) t.Text = text; }

        static void Show(string screen)
        {
            foreach (var s in new[] { "scRole", "scSecondary", "scPrimary" }) { var p = win.FindName(s) as UIElement; if (p != null) p.Visibility = Visibility.Collapsed; }
            var back = win.FindName("btnBack") as UIElement; if (back != null) back.Visibility = screen == "role" ? Visibility.Collapsed : Visibility.Visible;
            var cur = win.FindName(screen == "role" ? "scRole" : screen == "secondary" ? "scSecondary" : "scPrimary") as UIElement;
            if (cur != null) cur.Visibility = Visibility.Visible;
        }

        // ---- secondary ----
        static void ShowPanel(string name, bool visible) { var p = win.FindName(name) as UIElement; if (p != null) p.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

        // step 1: name this computer first, so the shown name is the one that reaches the main
        static void BeginSecondary()
        {
            Show("secondary");
            ShowPanel("pnlName", true); ShowPanel("pnlCode", false);
            var nameBox = F<TextBox>("txtName");
            if (nameBox != null) { nameBox.Text = Environment.MachineName; nameBox.Focus(); nameBox.SelectAll(); }
        }

        // step 2: run the elevated setup with that name, then show the code
        static void StartSecondary()
        {
            var nameBox = F<TextBox>("txtName");
            var name = nameBox != null && nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : Environment.MachineName;
            curCode = Core.NewCode();
            SetText("lblCode", Core.FormatCode(curCode));
            SetText("lblSecName", name);
            SetText("lblSecStatus", "Starting setup...");
            ShowPanel("pnlName", false); ShowPanel("pnlCode", true);
            var prog = Path.Combine(Path.GetTempPath(), "orbit-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            File.WriteAllText(prog, "");
            var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--worker --role secondary --code " + curCode + " --name \"" + name + "\" --progress \"" + prog + "\"",
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            Process proc = null;
            try { proc = Process.Start(psi); }
            catch { SetText("lblSecStatus", "You must click Yes on the security prompt."); return; }
            WatchProgress(prog, proc);
        }

        static void WatchProgress(string file, Process proc)
        {
            int pos = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    if (File.Exists(file))
                    {
                        var lines = File.ReadAllLines(file);
                        for (; pos < lines.Length; pos++)
                        {
                            var line = lines[pos];
                            if (line.StartsWith("STATUS ")) SetText("lblSecStatus", line.Substring(7));
                            else if (line.StartsWith("READY")) { SetText("lblSecStatus", "Ready - waiting for the main computer"); timer.Stop(); }
                            else if (line.StartsWith("FAIL ")) { SetText("lblSecStatus", "⚠ " + line.Substring(5)); timer.Stop(); }
                        }
                    }
                    if (proc != null && proc.HasExited && pos == 0) { SetText("lblSecStatus", "⚠ Setup did not start (was the prompt approved?)"); timer.Stop(); }
                }
                catch { }
            };
            timer.Start();
        }

        // ---- primary ----
        static void StartPrimary()
        {
            Show("primary");
            SetText("lblFound", "Choose a computer");
            selectedBanner = null;
            ShowPanel("spSearch", true);
            ShowPanel("svDevices", false); ShowPanel("spEmpty", false);
            ShowPanel("cardDetail", false); ShowPanel("pnlManual", false); ShowPanel("lnkManual", false);
            var host = F<StackPanel>("spDevices"); if (host != null) host.Children.Clear();
            deviceCards.Clear();
            Task.Run(() =>
            {
                try { Core.InstallSelf(); } catch { }
                try { Core.EnsureVncdotool(); } catch { }
                var found = new List<Banner>();
                foreach (var ip in Core.FindOpenSsh()) { var b = Core.ReadBanner(ip); if (b != null && b.Role == "secondary") found.Add(b); }
                win.Dispatcher.Invoke(() => FillDevices(found));
            });
        }

        static void FillDevices(List<Banner> found)
        {
            banners = found;
            ShowPanel("spSearch", false);
            ShowPanel("lnkManual", true);
            var host = F<StackPanel>("spDevices"); if (host != null) host.Children.Clear();
            deviceCards.Clear();
            selectedBanner = null;
            if (found.Count == 0)
            {
                SetText("lblFound", "No computers found");
                ShowPanel("svDevices", false); ShowPanel("spEmpty", true); ShowPanel("cardDetail", false);
                return;
            }
            SetText("lblFound", found.Count == 1 ? "1 computer found" : found.Count + " computers found");
            ShowPanel("spEmpty", false); ShowPanel("svDevices", true);
            foreach (var b in found) host.Children.Add(BuildDeviceCard(b));
            if (deviceCards.Count > 0) SelectCard(deviceCards[0]);
        }

        static Border BuildDeviceCard(Banner b)
        {
            bool paired = Core.GetDevice(b.Name) != null;
            var brush = new SolidColorBrush(paired ? Color.FromRgb(0x22, 0xD3, 0xB0) : Color.FromRgb(0xF0, 0xB0, 0x3C));
            var card = new Border { Style = (Style)win.FindResource("DeviceCard"), Tag = b };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Fill = brush };
            Grid.SetColumn(dot, 0);
            var mid = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = b.Name, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)) });
            mid.Children.Add(new TextBlock { Text = (paired ? "Connected" : "Needs code") + "  ·  " + b.IP, FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA0, 0xB8)) });
            Grid.SetColumn(mid, 1);
            var chev = new TextBlock { Text = paired ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = paired ? 15 : 13, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chev, 2);
            g.Children.Add(dot); g.Children.Add(mid); g.Children.Add(chev);
            card.Child = g;
            card.MouseLeftButtonUp += (s, e) => SelectCard(card);
            deviceCards.Add(card);
            return card;
        }

        static void SelectCard(Border card)
        {
            if (card == null) return;
            selectedBanner = (Banner)card.Tag;
            foreach (var c in deviceCards)
            {
                if (c == card) { c.Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x6E, 0xA8, 0xFF)); c.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0xA8, 0xFF)); }
                else { c.ClearValue(Border.BackgroundProperty); c.ClearValue(Border.BorderBrushProperty); }
            }
            UpdateCard();
        }

        static void RefreshCards()
        {
            var sel = selectedBanner;
            FillDevices(banners);
            if (sel != null) { var m = deviceCards.FirstOrDefault(c => ((Banner)c.Tag).IP == sel.IP); if (m != null) SelectCard(m); }
        }

        static void UpdateCard()
        {
            var b = selectedBanner; if (b == null) { ShowPanel("cardDetail", false); return; }
            ShowPanel("cardDetail", true);
            bool paired = Core.GetDevice(b.Name) != null;
            ShowPanel("stConnected", paired); ShowPanel("stNeedCode", !paired);
            if (paired) SetText("lblConnName", b.Name);
            else { SetText("lblNeedName", "Enter the code shown on “" + b.Name + "”"); var tc = F<TextBox>("txtCode"); if (tc != null) tc.Text = ""; SetText("lblConnErr", ""); }
        }

        static void DoConnect()
        {
            var b = selectedBanner; if (b == null) return;
            var tc = F<TextBox>("txtCode");
            var code = new string((tc != null ? tc.Text : "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (code.Length != 8) { SetText("lblConnErr", "The code is 8 letters/digits."); return; }
            SetText("lblConnErr", "Connecting...");
            Task.Run(() =>
            {
                string err = null;
                try { Pair(b, code); } catch (Exception ex) { err = ex.Message; }
                win.Dispatcher.Invoke(() => { if (err == null) { SetText("lblConnErr", ""); RefreshCards(); } else SetText("lblConnErr", "⚠ " + err); });
            });
        }

        // Connect straight to a typed address - for links (direct Ethernet, 169.254.x) or any
        // network where auto-discovery can't see the other computer. Reads its banner over SSH,
        // then pairs exactly like a discovered device.
        static void DoConnectManual()
        {
            var ipBox = F<TextBox>("txtManualIp"); var codeBox = F<TextBox>("txtManualCode");
            var ip = (ipBox != null ? ipBox.Text : "").Trim();
            var code = new string((codeBox != null ? codeBox.Text : "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (ip.Length == 0) { SetText("lblManualErr", "Type the other computer's address."); return; }
            if (code.Length != 8) { SetText("lblManualErr", "The code is 8 letters/digits."); return; }
            SetText("lblManualErr", "Connecting...");
            Task.Run(() =>
            {
                string err = null; Banner b = null;
                try
                {
                    b = Core.ReadBanner(ip);
                    if (b == null) throw new Exception("No Orbit computer answered at " + ip + " - check the address and that it shows Ready.");
                    if (b.Role != "secondary") throw new Exception("That address isn't set up as the other computer.");
                    Pair(b, code);
                }
                catch (Exception ex) { err = ex.Message; }
                win.Dispatcher.Invoke(() =>
                {
                    if (err != null) { SetText("lblManualErr", "⚠ " + err); return; }
                    SetText("lblManualErr", "");
                    var found = new List<Banner>(banners);
                    if (!found.Any(x => x.IP == b.IP)) found.Add(b);
                    FillDevices(found);
                    var m = deviceCards.FirstOrDefault(c => ((Banner)c.Tag).IP == b.IP);
                    if (m != null) SelectCard(m);
                    var pm = win.FindName("pnlManual") as UIElement; if (pm != null) pm.Visibility = Visibility.Collapsed;
                });
            });
        }

        static void Pair(Banner b, string code)
        {
            var dev = new Device { name = b.Name, alias = "orbit-" + b.Name.Replace(" ", "-"), ip = b.IP, user = b.User, sshPort = b.SshPort, vncPort = b.VncPort, code = Core.Protect(code) };
            RunResult ping = null;
            for (int i = 0; i < 3; i++)
            {
                ping = Core.DeviceSsh(dev, "echo ok", 12000);
                if (ping.Out.Contains("ok")) break;
                System.Threading.Thread.Sleep(900);
            }
            if (ping == null || !ping.Out.Contains("ok")) throw new Exception("Can't reach it over the command channel - make sure it is on and set up as the other computer.");
            int lp; var t = Core.OpenTunnel(dev, out lp); if (t == null) throw new Exception("Command channel works, but the screen tunnel would not open.");
            try
            {
                var py = Core.PyExe(); if (py == null) throw new Exception("Python is missing on this computer (needed for the screen channel).");
                var drv = Core.DriverPath; if (!File.Exists(drv)) Core.ExtractResource("orbit-vnc.py", drv);
                Environment.SetEnvironmentVariable("ORBIT_VNC_PW", code);
                var r = Core.Run(py, new[] { drv, "probe", "127.0.0.1::" + lp }, 15000);
                if (r.Code == 5) throw new Exception("That code doesn't match. Re-check the 8 characters shown on the other computer.");
                if (r.Code != 0) throw new Exception("Couldn't verify the screen channel. " + r.Err.Trim());
            }
            finally { Environment.SetEnvironmentVariable("ORBIT_VNC_PW", null); try { if (!t.HasExited) t.Kill(); } catch { } }
            Core.SetDevice(dev);
        }

        static void RemoveThis()
        {
            var res = MessageBox.Show("Remove remote control from this computer (key, service, firewall)?", "Orbit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
            Show("secondary"); ShowPanel("pnlName", false); ShowPanel("pnlCode", true); SetText("lblSecName", Environment.MachineName); SetText("lblCode", "· · · ·"); SetText("lblSecStatus", "Removing..."); curCode = null;
            var prog = Path.Combine(Path.GetTempPath(), "orbit-remove-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            File.WriteAllText(prog, "");
            var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var psi = new ProcessStartInfo { FileName = exe, Arguments = "--worker --role remove --progress \"" + prog + "\"", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process proc = null;
            try { proc = Process.Start(psi); } catch { SetText("lblSecStatus", "You must click Yes on the security prompt."); return; }
            WatchProgress(prog, proc);
        }
    }
}
