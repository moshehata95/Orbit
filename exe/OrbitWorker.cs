// Orbit — elevated worker (set up / remove the "other computer"). Developed by Dr. Mohamed Shehata.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Security.Cryptography;
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
        static string Trunc(string s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > n ? s.Substring(0, n) : s; }
        static bool PortOpen(int port) { try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", port); return true; } } catch { return false; } }

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
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var osdir = Path.Combine(pf, "Orbit", "OpenSSH-Win64");
            var sshdExe = Path.Combine(osdir, "sshd.exe");
            if (!ServiceExists("sshd"))
            {
                if (!File.Exists(sshdExe))
                {
                    P("LOG - Unpacking the SSH server...");
                    try { Core.ExtractZipResource("openssh.zip", Path.Combine(pf, "Orbit")); }
                    catch (Exception ex) { throw new Exception("unpack failed: " + ex.Message); }
                }
                if (!File.Exists(sshdExe)) throw new Exception("sshd.exe missing after unpack (" + sshdExe + ").");
                P("LOG - Registering the SSH service...");
                var inst = Core.Run("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(osdir, "install-sshd.ps1") }, 120000);
                if (!ServiceExists("sshd"))
                    Core.Run("sc.exe", new[] { "create", "sshd", "binPath=", sshdExe, "start=", "auto", "DisplayName=", "OpenSSH SSH Server" }, 30000);
                if (!ServiceExists("sshd"))
                    throw new Exception("SSH service not created: " + Trunc(inst.Out + " " + inst.Err, 180));
                state.installedSsh = true;
            }
            Sc("config", "sshd", "start=", "auto");
            Core.EnsureDir(Core.SshdDir);
            // host keys + permissions (sshd refuses host keys that are too readable)
            Core.Run(Path.Combine(osdir, "ssh-keygen.exe"), new[] { "-A" }, 30000);
            try { foreach (var hk in Directory.GetFiles(Core.SshdDir, "ssh_host_*_key")) Core.Run(Path.Combine(Core.Win, "System32", "icacls.exe"), new[] { hk, "/inheritance:r", "/grant", "*S-1-5-18:F", "/grant", "*S-1-5-32-544:R" }, 15000); } catch { }

            P("LOG - Registering the main computer key (one-way - no key goes to this computer)");
            AddKeyLine(Core.AdminKeys, Core.PubKey);
            Core.Run(Path.Combine(Core.Win, "System32", "icacls.exe"), new[] { Core.AdminKeys, "/inheritance:r", "/grant", "*S-1-5-32-544:F", "/grant", "*S-1-5-18:F" }, 15000);
            AddKeyLine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys"), Core.PubKey);
            var bp = Core.BannerFile.Replace("\\", "/");
            SetSshdConfig(new[] { "Banner " + bp, "PubkeyAuthentication yes", "PasswordAuthentication no", "StrictModes no" });
            File.WriteAllText(Core.BannerFile, "orbit|1|secondary|" + name + "|" + Environment.UserName + "|" + Core.VncPort + "\n", Core.Utf8);
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\OpenSSH"))
            {
                if (k.GetValue("DefaultShell") == null) { k.SetValue("DefaultShell", Path.Combine(Core.Win, @"System32\WindowsPowerShell\v1.0\powershell.exe"), RegistryValueKind.String); state.setDefaultShell = true; }
            }
            Core.Run("netsh.exe", new[] { "advfirewall", "firewall", "delete", "rule", "name=" + Core.FwSsh }, 15000);
            Core.Run("netsh.exe", new[] { "advfirewall", "firewall", "add", "rule", "name=" + Core.FwSsh, "dir=in", "action=allow", "protocol=TCP", "localport=22", "profile=any" }, 15000);
            state.fw = true;
            // (re)start, then verify it is actually listening
            try { using (var sc = new ServiceController("sshd")) { if (sc.Status == ServiceControllerStatus.Running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); } } } catch { }
            try { using (var sc = new ServiceController("sshd")) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); } } catch (Exception ex) { P("LOG start: " + ex.Message); }
            Thread.Sleep(1500);
            if (!PortOpen(22))
            {
                // last resort: run sshd -D directly (service mode can be finicky; this is the proven path)
                P("LOG - Starting SSH directly...");
                try { Process.Start(new ProcessStartInfo { FileName = sshdExe, Arguments = "-D", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = osdir }); } catch (Exception ex) { P("LOG direct: " + ex.Message); }
                Thread.Sleep(2500);
            }
            if (!PortOpen(22))
            {
                var dbg = Core.Run(sshdExe, new[] { "-d", "-p", "2200" }, 4000); // capture startup reason (killed after 4s)
                throw new Exception("SSH not listening on 22 (svc=" + ServiceStatus("sshd") + "). " + Trunc(dbg.Out + " " + dbg.Err, 170));
            }

            P("STATUS Setting up the screen channel (computer use)...");
            var effCode = InstallVnc(code, state);
            P("CODE " + effCode);

            SaveState(state);
            P("STATUS Ready - waiting for the main computer");
            P("READY");
        }

        static string InstallVnc(string code, State state)
        {
            var tvn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TightVNC", "tvnserver.exe");
            var fresh = !File.Exists(tvn);
            if (fresh)
            {
                P("LOG - Installing the bundled screen server...");
                var msi = Path.Combine(Path.GetTempPath(), "orbit-tvnc.msi");
                Core.ExtractResource("tvnc.msi", msi);
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
            // Keep the code stable across re-runs: reuse a valid existing code so an existing
            // pairing on the main computer doesn't silently break; only apply the fresh code on a
            // first-time setup (or if the stored one is unreadable). Write it straight to the
            // registry, since msiexec only sets the password on a first install.
            var existing = ReadVncPassword();
            var effective = (!fresh && IsValidCode(existing)) ? existing : code;
            P("LOG - Applying the screen password...");
            try { using (var sc = new ServiceController("tvnserver")) { if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); } } } catch { }
            Thread.Sleep(500);
            try { SetVncPassword(effective); } catch (Exception ex) { P("LOG vnc-pw: " + ex.Message); }
            try { using (var sc = new ServiceController("tvnserver")) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); } } catch (Exception ex) { P("LOG vnc-start: " + ex.Message); }
            return effective;
        }

        // TightVNC stores the RFB password as an 8-byte single-DES blob of the (null-padded, 8-char)
        // password under the classic fixed VNC key - the same vncpasswd obfuscation vncviewer reads.
        // Writing it straight to the registry applies the code reliably on every setup.
        static byte[] ReverseBits(byte[] data)
        {
            var r = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                int b = data[i], v = 0;
                for (int k = 0; k < 8; k++) if ((b & (1 << k)) != 0) v |= 1 << (7 - k);
                r[i] = (byte)v;
            }
            return r;
        }
        static void SetVncPassword(string code)
        {
            var key = ReverseBits(new byte[] { 23, 82, 107, 6, 35, 78, 88, 7 });
            var data = new byte[8];
            var ascii = Encoding.ASCII.GetBytes(code ?? "");
            Array.Copy(ascii, data, Math.Min(8, ascii.Length));
            byte[] blob;
            using (var des = new DESCryptoServiceProvider { Mode = CipherMode.ECB, Padding = PaddingMode.None, Key = key })
            using (var enc = des.CreateEncryptor())
                blob = enc.TransformFinalBlock(data, 0, 8);
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\TightVNC\Server"))
            {
                if (k != null)
                {
                    k.SetValue("Password", blob, RegistryValueKind.Binary);
                    k.SetValue("UseVncAuthentication", 1, RegistryValueKind.DWord);
                }
            }
        }
        static string ReadVncPassword()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TightVNC\Server"))
                {
                    if (k == null) return null;
                    var blob = k.GetValue("Password") as byte[];
                    if (blob == null || blob.Length != 8) return null;
                    var key = ReverseBits(new byte[] { 23, 82, 107, 6, 35, 78, 88, 7 });
                    using (var des = new DESCryptoServiceProvider { Mode = CipherMode.ECB, Padding = PaddingMode.None, Key = key })
                    using (var dec = des.CreateDecryptor())
                    {
                        var pt = dec.TransformFinalBlock(blob, 0, 8);
                        int n = 0; while (n < 8 && pt[n] != 0) n++;
                        return Encoding.ASCII.GetString(pt, 0, n);
                    }
                }
            }
            catch { return null; }
        }
        static bool IsValidCode(string c)
        {
            if (string.IsNullOrEmpty(c) || c.Length != 8) return false;
            foreach (var ch in c) if (!((ch >= 'A' && ch <= 'Z') || (ch >= '2' && ch <= '9'))) return false;
            return true;
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
                if (state != null && state.installedSsh)
                {
                    Sc("delete", "sshd"); Sc("delete", "ssh-agent");
                    try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Orbit", "OpenSSH-Win64"), true); } catch { }
                }
                else { Sc("config", "sshd", "start=", "disabled"); }
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
