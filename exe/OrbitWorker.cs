// Orbit — elevated worker (set up / remove the "other computer"). Developed by Dr. Mohamed Shehata.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace OrbitApp
{
    public class State
    {
        public string role, name, user, host; public int vncPort = Core.VncPort;
        public bool installedSsh, installedVnc, fw, setDefaultShell;
    }

    public static class Worker
    {
        static string _progress;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static void P(string line) { if (_progress != null) { try { File.AppendAllText(_progress, line + "\r\n", Core.Utf8); } catch { } } }

        public static int RunElevated(string role, string code, string name, string progress)
        {
            _progress = progress;
            try
            {
                if (role == "secondary") InstallSecondary(code, name);
                else if (role == "remove") RemoveSecondary();
                return 0;
            }
            catch (Exception ex) { P("FAIL " + ex.Message); return 1; }
        }

        static bool ServiceExists(string n) { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(n, StringComparison.OrdinalIgnoreCase)); }
        static ServiceControllerStatus? ServiceStatus(string n)
        {
            try { using (var sc = new ServiceController(n)) return sc.Status; } catch { return null; }
        }
        static void Sc(params string[] a) { Core.Run("sc.exe", a, 30000); }

        static void AddKeyLine(string file, string pub)
        {
            Core.EnsureDir(Path.GetDirectoryName(file));
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new System.Collections.Generic.List<string>();
            lines = lines.Where(l => !string.IsNullOrEmpty(l) && !Regex.IsMatch(l, " " + Regex.Escape(Core.KeyComment) + @"\s*$")).ToList();
            lines.Add(pub);
            File.WriteAllText(file, string.Join("\n", lines) + "\n", Core.Utf8);
        }
        static void RemoveKeyLine(string file)
        {
            if (!File.Exists(file)) return;
            var keep = File.ReadAllLines(file).Where(l => !string.IsNullOrEmpty(l) && !Regex.IsMatch(l, " " + Regex.Escape(Core.KeyComment) + @"\s*$")).ToList();
            if (keep.Count > 0) File.WriteAllText(file, string.Join("\n", keep) + "\n", Core.Utf8);
            else File.Delete(file);
        }
        static void SetSshdConfig(string[] wanted)
        {
            var lines = File.Exists(Core.SshdConfig) ? File.ReadAllLines(Core.SshdConfig).Where(l => !l.Contains(Core.Tag)).ToList() : new System.Collections.Generic.List<string>();
            var newLines = wanted.Select(w => w + " " + Core.Tag).Concat(lines);
            File.WriteAllText(Core.SshdConfig, string.Join("\r\n", newLines) + "\r\n", Core.Utf8);
        }

        static void InstallSecondary(string code, string name)
        {
            if (!Core.IsAdmin()) throw new Exception("Setup needs administrator rights.");
            Core.EnsureDir(Core.StateDir);
            var state = new State { role = "secondary", name = name, user = Environment.UserName, host = Environment.MachineName, vncPort = Core.VncPort };
            if (File.Exists(Core.StateFile)) { try { var old = Json.Deserialize<State>(File.ReadAllText(Core.StateFile)); if (old != null) { state.installedSsh = old.installedSsh; state.installedVnc = old.installedVnc; state.fw = old.fw; state.setDefaultShell = old.setDefaultShell; } } catch { } }

            P("STATUS Setting up the command channel...");
            if (!ServiceExists("sshd"))
            {
                P("LOG - Downloading OpenSSH Server (needs internet once)...");
                var r = Core.Run("dism.exe", new[] { "/online", "/add-capability", "/capabilityname:OpenSSH.Server~~~~0.0.1.0", "/quiet", "/norestart" }, 600000);
                if (!ServiceExists("sshd")) throw new Exception("Could not install OpenSSH Server (dism " + r.Code + "). Check internet and try again.");
                state.installedSsh = true;
            }
            Sc("config", "sshd", "start=", "auto");
            if (ServiceStatus("sshd") != ServiceControllerStatus.Running) { try { using (var sc = new ServiceController("sshd")) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); } } catch { } }
            var t0 = DateTime.Now;
            while (!File.Exists(Core.SshdConfig) && (DateTime.Now - t0).TotalSeconds < 30) Thread.Sleep(500);
            if (!File.Exists(Core.SshdConfig)) throw new Exception("The sshd config file was not created.");

            P("LOG - Registering the main computer key (one-way - no key goes to this computer)");
            AddKeyLine(Core.AdminKeys, Core.PubKey);
            Core.Run(Path.Combine(Core.Win, "System32", "icacls.exe"), new[] { Core.AdminKeys, "/inheritance:r", "/grant", "*S-1-5-32-544:F", "/grant", "*S-1-5-18:F" }, 15000);
            AddKeyLine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys"), Core.PubKey);
            var bp = Core.BannerFile.Replace("\\", "/");
            SetSshdConfig(new[] { "Banner " + bp, "PubkeyAuthentication yes", "PasswordAuthentication no" });
            File.WriteAllText(Core.BannerFile, "orbit|1|secondary|" + name + "|" + Environment.UserName + "|" + Core.VncPort + "\n", Core.Utf8);
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\OpenSSH"))
            {
                if (k.GetValue("DefaultShell") == null) { k.SetValue("DefaultShell", Path.Combine(Core.Win, @"System32\WindowsPowerShell\v1.0\powershell.exe"), RegistryValueKind.String); state.setDefaultShell = true; }
            }
            var fw = Core.Run("netsh.exe", new[] { "advfirewall", "firewall", "show", "rule", "name=" + Core.FwSsh }, 15000);
            if (!fw.Out.Contains(Core.FwSsh))
            {
                Core.Run("netsh.exe", new[] { "advfirewall", "firewall", "add", "rule", "name=" + Core.FwSsh, "dir=in", "action=allow", "protocol=TCP", "localport=22", "profile=private,domain" }, 15000);
                state.fw = true;
            }
            try { using (var sc = new ServiceController("sshd")) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); } } catch { }

            P("STATUS Setting up the screen channel (computer use)...");
            InstallVnc(code, state);

            SaveState(state);
            P("STATUS Ready - waiting for the main computer");
            P("READY");
        }

        static void InstallVnc(string code, State state)
        {
            var tvn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TightVNC", "tvnserver.exe");
            if (!File.Exists(tvn))
            {
                P("LOG - Downloading the small screen server (~2 MB)...");
                var msi = Path.Combine(Path.GetTempPath(), "orbit-tvnc.msi");
                bool ok = false;
                foreach (var u in Core.TightUrls)
                {
                    try { using (var wc = new System.Net.WebClient()) wc.DownloadFile(u, msi); if (new FileInfo(msi).Length > 500000) { ok = true; break; } } catch { }
                }
                if (!ok) throw new Exception("Could not download the screen server - make sure this computer has internet once.");
                var args = new[] {
                    "/i", msi, "/quiet", "/norestart", "ADDLOCAL=Server",
                    "SERVER_REGISTER_AS_SERVICE=1", "SERVER_ADD_FIREWALL_EXCEPTION=0", "SERVER_ALLOW_SAS=1",
                    "SET_USECONTROLAUTHENTICATION=1", "VALUE_OF_USECONTROLAUTHENTICATION=1",
                    "SET_CONTROLPASSWORD=1", "VALUE_OF_CONTROLPASSWORD=" + code,
                    "SET_USEVNCAUTHENTICATION=1", "VALUE_OF_USEVNCAUTHENTICATION=1",
                    "SET_PASSWORD=1", "VALUE_OF_PASSWORD=" + code,
                    "SET_LOOPBACKONLY=1", "VALUE_OF_LOOPBACKONLY=1",
                    "SET_ALLOWLOOPBACK=1", "VALUE_OF_ALLOWLOOPBACK=1",
                    "SET_RFBPORT=1", "VALUE_OF_RFBPORT=" + Core.VncPort };
                var r = Core.Run("msiexec.exe", args, 300000);
                if (r.Code != 0) throw new Exception("Screen server install failed (msiexec " + r.Code + ").");
                state.installedVnc = true;
            }
            Sc("config", "tvnserver", "start=", "auto");
            if (ServiceStatus("tvnserver") != ServiceControllerStatus.Running) { try { using (var sc = new ServiceController("tvnserver")) sc.Start(); } catch { } }
        }

        static void RemoveSecondary()
        {
            if (!Core.IsAdmin()) throw new Exception("Removal needs administrator rights.");
            State state = null;
            if (File.Exists(Core.StateFile)) { try { state = Json.Deserialize<State>(File.ReadAllText(Core.StateFile)); } catch { } }
            P("STATUS Removing the command channel...");
            RemoveKeyLine(Core.AdminKeys);
            RemoveKeyLine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys"));
            if (File.Exists(Core.SshdConfig)) { var keep = File.ReadAllLines(Core.SshdConfig).Where(l => !l.Contains(Core.Tag)); File.WriteAllText(Core.SshdConfig, string.Join("\r\n", keep) + "\r\n", Core.Utf8); }
            try { File.Delete(Core.BannerFile); } catch { }
            Core.Run("netsh.exe", new[] { "advfirewall", "firewall", "delete", "rule", "name=" + Core.FwSsh }, 15000);
            if (ServiceExists("sshd"))
            {
                try { using (var sc = new ServiceController("sshd")) { if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); } } } catch { }
                Sc("config", "sshd", "start=", "disabled");
                if (state != null && state.installedSsh) Core.Run("dism.exe", new[] { "/online", "/remove-capability", "/capabilityname:OpenSSH.Server~~~~0.0.1.0", "/quiet", "/norestart" }, 300000);
            }
            P("STATUS Removing the screen channel...");
            if (state != null && state.installedVnc)
            {
                var key = FindUninstall("TightVNC");
                if (key != null) Core.Run("msiexec.exe", new[] { "/x", key, "/quiet", "/norestart" }, 300000);
            }
            else { try { using (var sc = new ServiceController("tvnserver")) if (sc.Status == ServiceControllerStatus.Running) sc.Stop(); } catch { } }
            try { Directory.Delete(Core.StateDir, true); } catch { }
            P("READY");
        }

        static string FindUninstall(string displayNameLike)
        {
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using (var k = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        using (var s = k.OpenSubKey(sub))
                        {
                            var dn = s.GetValue("DisplayName") as string;
                            if (dn != null && dn.StartsWith(displayNameLike) && sub.StartsWith("{")) return sub;
                        }
                    }
                }
            }
            return null;
        }

        static void SaveState(State s) { Core.EnsureDir(Core.StateDir); File.WriteAllText(Core.StateFile, Json.Serialize(s), Core.Utf8); }
    }
}
