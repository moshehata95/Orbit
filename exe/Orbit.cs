// Orbit — remote control. A single self-contained WPF executable (no PowerShell).
// Developed by Dr. Mohamed Shehata.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;

namespace OrbitApp
{
    public class Device
    {
        public string name { get; set; }
        public string alias { get; set; }
        public string ip { get; set; }
        public string user { get; set; }
        public int sshPort { get; set; }
        public int vncPort { get; set; }
        public string code { get; set; }
    }

    public class Banner
    {
        public string Ver, Role, Name, User, IP; public int VncPort, SshPort;
    }

    public class RunResult { public int Code; public string Out = "", Err = ""; public bool TimedOut; }

    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public static void Glass(IntPtr h)
        {
            int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4);
            int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);
            int backdrop = 3; DwmSetWindowAttribute(h, 38, ref backdrop, 4); // 3 = Acrylic
        }
    }

    public static class Core
    {
        public const string Version = "1.1";
        public const int VncPort = 5900;
        public const string BannerUser = "orbit-probe";
        public const string KeyComment = "orbit-primary";

        // === main computer public key, injected at build time ===
        public const string PubKey = "__PUBKEY__";

        public static readonly string[] TightUrls = {
            "https://www.tightvnc.com/download/2.8.85/tightvnc-2.8.85-gpl-setup-64bit.msi"
        };

        public static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        public static string SysOpenSsh { get { return Path.Combine(Win, "System32", "OpenSSH"); } }
        public static string SshExe { get { return Path.Combine(SysOpenSsh, "ssh.exe"); } }
        public static string KeygenExe { get { return Path.Combine(SysOpenSsh, "ssh-keygen.exe"); } }
        public static string HomeSsh { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"); } }
        public static string KeyPath { get { return Path.Combine(HomeSsh, "orbit_primary"); } }
        public static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit"); } }
        public static string HomeDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit"); } }
        public static string DevicesFile { get { return Path.Combine(DataDir, "devices.json"); } }
        public static string ProgramData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        public static string SshdDir { get { return Path.Combine(ProgramData, "ssh"); } }
        public static string SshdConfig { get { return Path.Combine(SshdDir, "sshd_config"); } }
        public static string AdminKeys { get { return Path.Combine(SshdDir, "administrators_authorized_keys"); } }
        public static string BannerFile { get { return Path.Combine(SshdDir, "orbit-banner.txt"); } }
        public static string StateDir { get { return Path.Combine(ProgramData, "Orbit"); } }
        public static string StateFile { get { return Path.Combine(StateDir, "state.json"); } }
        public const string FwSsh = "Orbit-SSH";
        public const string Tag = "# orbit";

        public static readonly Encoding Utf8 = new UTF8Encoding(false);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static void EnsureDir(string p) { if (!Directory.Exists(p)) Directory.CreateDirectory(p); }
        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ---- run a process with timeout ----
        public static RunResult Run(string exe, string[] argv, int timeoutMs = 20000)
        {
            var quoted = argv.Select(a => (a.Length == 0 || a.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0) ? "\"" + a.Replace("\"", "\\\"") + "\"" : a);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", quoted),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var r = new RunResult();
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { r.TimedOut = true; try { p.Kill(); } catch { } p.WaitForExit(); }
                r.Out = so.Result; r.Err = se.Result; r.Code = p.ExitCode;
            }
            return r;
        }

        // ---- DPAPI ----
        public static string Protect(string plain)
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain ?? ""), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        public static string Unprotect(string b64)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

        // ---- code ----
        public static string NewCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var buf = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
            var sb = new StringBuilder();
            foreach (var b in buf) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }
        public static string FormatCode(string c) { return c != null && c.Length == 8 ? c.Substring(0, 4) + " · " + c.Substring(4, 4) : c; }
        // ---- devices ----
        public static List<Device> GetDevices()
        {
            try
            {
                if (!File.Exists(DevicesFile)) return new List<Device>();
                var raw = File.ReadAllText(DevicesFile, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return new List<Device>();
                var list = Json.Deserialize<List<Device>>(raw) ?? new List<Device>();
                return list.Where(d => d != null && !string.IsNullOrEmpty(d.ip) && !string.IsNullOrEmpty(d.alias)).ToList();
            }
            catch { return new List<Device>(); }
        }
        public static void SaveDevices(List<Device> devs)
        {
            EnsureDir(DataDir);
            var arr = devs.Where(d => d != null && !string.IsNullOrEmpty(d.ip) && !string.IsNullOrEmpty(d.alias)).ToList();
            File.WriteAllText(DevicesFile, Json.Serialize(arr), Utf8);
        }
        public static Device GetDevice(string name)
        {
            return GetDevices().FirstOrDefault(d => d.name == name || d.alias == name || d.alias == "orbit-" + name);
        }
        public static void SetDevice(Device dev)
        {
            var all = GetDevices().Where(d => d.alias != dev.alias).ToList();
            all.Add(dev); SaveDevices(all); WriteSshConfig();
        }
        public static void RemoveDevice(string name)
        {
            var all = GetDevices().Where(d => d.name != name && d.alias != name && d.alias != "orbit-" + name).ToList();
            SaveDevices(all); WriteSshConfig();
        }
        public static void WriteSshConfig()
        {
            EnsureDir(HomeSsh);
            var cfg = Path.Combine(HomeSsh, "config");
            var existing = File.Exists(cfg) ? File.ReadAllText(cfg, Encoding.UTF8) : "";
            existing = Regex.Replace(existing, "(?s)# orbit begin.*?# orbit end\r?\n?", "");
            var sb = new StringBuilder();
            sb.Append("# orbit begin (auto-generated - re-run Orbit instead of editing)\n");
            foreach (var d in GetDevices())
                sb.Append("Host " + d.alias + "\n  HostName " + d.ip + "\n  User " + d.user + "\n  Port " + d.sshPort +
                          "\n  IdentityFile ~/.ssh/orbit_primary\n  IdentitiesOnly yes\n  StrictHostKeyChecking no\n  UserKnownHostsFile NUL\n  ConnectTimeout 6\n");
            sb.Append("# orbit end\n");
            var text = (existing.TrimEnd() + "\n\n" + sb.ToString()).TrimStart();
            File.WriteAllText(cfg, text, Utf8);
        }

        // ---- ssh / tunnel ----
        public static RunResult DeviceSsh(Device dev, string command, int timeoutMs = 30000)
        {
            return Run(SshExe, new[] {
                "-i", KeyPath, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL", "-o", "ConnectTimeout=8",
                "-p", dev.sshPort.ToString(), "-l", dev.user, dev.ip, command }, timeoutMs);
        }

        public static Process OpenTunnel(Device dev, out int localPort)
        {
            localPort = new Random().Next(55000, 59000);
            var args = new[] {
                "-i", KeyPath, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL", "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=15", "-N", "-p", dev.sshPort.ToString(),
                "-L", "127.0.0.1:" + localPort + ":127.0.0.1:" + dev.vncPort, "-l", dev.user, dev.ip };
            var quoted = args.Select(a => a.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + a + "\"" : a);
            var psi = new ProcessStartInfo { FileName = SshExe, Arguments = string.Join(" ", quoted), UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi);
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                if (p.HasExited) break;
                try { using (var c = new TcpClient()) { c.Connect("127.0.0.1", localPort); } return p; } catch { }
            }
            try { if (!p.HasExited) p.Kill(); } catch { }
            return null;
        }

        // ---- scan ----
        public static List<string> LanIPs()
        {
            var res = new List<string>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.") || s == "127.0.0.1") continue;
                    res.Add(s);
                }
            }
            return res.Distinct().ToList();
        }

        public static List<string> FindOpenSsh(int port = 22)
        {
            var bases = LanIPs().Select(ip => string.Join(".", ip.Split('.').Take(3))).Distinct().ToList();
            var targets = new List<string>();
            foreach (var b in bases) for (int i = 1; i <= 254; i++) targets.Add(b + "." + i);
            var open = new List<string>();
            for (int i = 0; i < targets.Count; i += 128)
            {
                var batch = targets.Skip(i).Take(128).ToList();
                var clients = new List<Tuple<string, TcpClient, IAsyncResult>>();
                foreach (var t in batch)
                {
                    var c = new TcpClient();
                    IAsyncResult ar = null;
                    try { ar = c.BeginConnect(t, port, null, null); } catch { }
                    clients.Add(Tuple.Create(t, c, ar));
                }
                Thread.Sleep(1400);
                foreach (var tup in clients)
                {
                    try { if (tup.Item3 != null && tup.Item3.IsCompleted) { tup.Item2.EndConnect(tup.Item3); if (tup.Item2.Connected) open.Add(tup.Item1); } } catch { }
                    try { tup.Item2.Close(); } catch { }
                }
            }
            return open;
        }

        public static Banner ReadBanner(string ip, int port = 22)
        {
            var r = Run(SshExe, new[] { "-n", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL",
                "-o", "ConnectTimeout=5", "-o", "PreferredAuthentications=none", "-p", port.ToString(), "-l", BannerUser, ip, "exit" }, 12000);
            var m = Regex.Match(r.Out + "\n" + r.Err, @"orbit\|(\d+)\|(\w+)\|([^|\r\n]*)\|([^|\r\n]*)\|(\d+)");
            if (!m.Success) return null;
            return new Banner { Ver = m.Groups[1].Value, Role = m.Groups[2].Value, Name = m.Groups[3].Value.Trim(),
                User = m.Groups[4].Value.Trim(), VncPort = int.Parse(m.Groups[5].Value), IP = ip, SshPort = port };
        }

        // ---- primary self-install + python ----
        public static string PyExe()
        {
            foreach (var n in new[] { "python", "py" })
            {
                try { var r = Run(n, new[] { "--version" }, 6000); if (r.Code == 0) return n; } catch { }
            }
            return null;
        }
        public static bool EnsureVncdotool()
        {
            var py = PyExe(); if (py == null) return false;
            if (Run(py, new[] { "-c", "import vncdotool" }, 8000).Code == 0) return true;
            Run(py, new[] { "-m", "pip", "install", "--user", "--quiet", "vncdotool" }, 240000);
            return Run(py, new[] { "-c", "import vncdotool" }, 8000).Code == 0;
        }
        public static void InstallSelf()
        {
            EnsureDir(HomeDir);
            var exe = Assembly.GetExecutingAssembly().Location;
            var dst = Path.Combine(HomeDir, "Orbit.exe");
            try { if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) File.Copy(exe, dst, true); } catch { }
            ExtractResource("orbit-vnc.py", Path.Combine(HomeDir, "orbit-vnc.py"));
        }
        public static string DriverPath { get { return Path.Combine(HomeDir, "orbit-vnc.py"); } }
        // ---- embedded resources ----
        public static string ReadTextResource(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("Orbit." + name))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }
        public static void ExtractResource(string name, string dest)
        {
            var asm = Assembly.GetExecutingAssembly();
            EnsureDir(Path.GetDirectoryName(dest));
            using (var s = asm.GetManifestResourceStream("Orbit." + name))
            using (var f = File.Create(dest)) s.CopyTo(f);
        }
        public static string ExtractFonts()
        {
            var dir = Path.Combine(HomeDir, "fonts");
            EnsureDir(dir);
            foreach (var w in new[] { "400", "600", "700" })
            {
                var dest = Path.Combine(dir, "Cairo-" + w + ".ttf");
                if (!File.Exists(dest)) ExtractResource("Cairo-" + w + ".ttf", dest);
            }
            return dir;
        }
    }
}
