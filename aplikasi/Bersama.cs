// Kode bersama KontrolLab-Admin.exe (di komputer server) dan KontrolLab-Client.exe (di tiap PC lab).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KontrolLab
{
    public class PcRow
    {
        public string Name, IP, MAC, Broadcast;
    }

    public class ToolResult
    {
        public int Code;
        public string Output;
    }

    public static class Core
    {
        public static string DefaultDataFolder()
        {
            return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data-pc");
        }

        // Membaca file PC-*.json buatan DAFTAR-PC-LAB.ps1. File rusak dilewati; bila nama atau MAC dobel, file terbaru yang dipakai.
        public static List<PcRow> ReadPcFolder(string dir, List<string> warnings)
        {
            var list = new List<PcRow>();
            if (!Directory.Exists(dir)) { warnings.Add("Folder data belum ada: " + dir); return list; }
            var json = new JavaScriptSerializer();
            foreach (string file in Directory.GetFiles(dir, "PC-*.json").OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
            {
                try
                {
                    var d = json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                    Func<string, string> get = k => d != null && d.ContainsKey(k) && d[k] != null ? d[k].ToString().Trim() : "";
                    var row = new PcRow { Name = get("pc"), IP = get("ip"), MAC = get("mac"), Broadcast = get("broadcast") };
                    MagicPacket(row.MAC);
                    if (row.Name.Length == 0) throw new FormatException("nama PC kosong");
                    if (list.Any(r => string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new FormatException("nama PC " + row.Name + " sudah ada di file yang lebih baru");
                    if (list.Any(r => SameMac(r.MAC, row.MAC)))
                        throw new FormatException("MAC " + row.MAC + " sudah ada di file yang lebih baru");
                    list.Add(row);
                }
                catch (Exception ex) { warnings.Add(Path.GetFileName(file) + " dilewati: " + ex.Message); }
            }
            return list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool SameMac(string a, string b)
        {
            return string.Equals(Regex.Replace(a, "[^0-9A-Fa-f]", ""), Regex.Replace(b, "[^0-9A-Fa-f]", ""), StringComparison.OrdinalIgnoreCase);
        }

        public static byte[] MagicPacket(string macText)
        {
            string mac = Regex.Replace(macText ?? "", "[^0-9A-Fa-f]", "");
            if (mac.Length != 12) throw new FormatException("MAC tidak valid");
            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 255;
            for (int n = 0; n < 16; n++)
                for (int i = 0; i < 6; i++)
                    packet[6 + n * 6 + i] = Convert.ToByte(mac.Substring(i * 2, 2), 16);
            return packet;
        }

        // Dikirim ke broadcast subnet dan 255.255.255.255, port 9 dan 7, tiga kali: paket UDP bisa hilang.
        public static void SendWake(PcRow r)
        {
            var packet = MagicPacket(r.MAC);
            var targets = new List<IPAddress> { IPAddress.Broadcast };
            IPAddress subnet;
            if (IPAddress.TryParse(r.Broadcast, out subnet) && subnet.AddressFamily == AddressFamily.InterNetwork) targets.Insert(0, subnet);
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                for (int round = 0; round < 3; round++)
                    foreach (var ip in targets)
                        foreach (int port in new[] { 9, 7 })
                            udp.Send(packet, packet.Length, new IPEndPoint(ip, port));
            }
        }

        // Mengembalikan jumlah PC yang paketnya terkirim; kegagalan dicatat di errors.
        public static int WakeAll(IEnumerable<PcRow> rows, List<string> errors)
        {
            int sent = 0;
            foreach (var r in rows)
            {
                try { SendWake(r); sent++; }
                catch (Exception ex) { errors.Add(r.Name + ": " + ex.Message); }
            }
            return sent;
        }

        public static async Task<bool> IsAlive(string ip)
        {
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address)) return false;
            using (var ping = new Ping())
            {
                try { return (await ping.SendPingAsync(address, 1500)).Status == IPStatus.Success; }
                catch (PingException) { return false; }
            }
        }

        public static string SystemTool(string relative)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), relative);
        }

        public static ToolResult RunTool(string exe, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var outText = p.StandardOutput.ReadToEndAsync();
                var errText = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return new ToolResult { Code = -1, Output = "Melewati batas waktu." };
                }
                Task.WaitAll(outText, errText);
                return new ToolResult { Code = p.ExitCode, Output = (outText.Result + " " + errText.Result).Trim() };
            }
        }

        public static string PsQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        public static string BuildScriptCommand(string scriptPath, string arguments)
        {
            var sb = new StringBuilder("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;");
            // Tidak ada jendela untuk menjawab; skrip yang bertanya harus gagal, bukan menggantung.
            sb.Append("function Read-Host {throw 'Konfirmasi tidak tersedia.'};");
            // Saat output dialihkan, PowerShell mengirim error dan progress sebagai CLIXML; ubah menjadi teks biasa untuk log.
            // Jangan pakai 2>&1 di sini: stderr program native bisa berubah menjadi error palsu saat mode Stop.
            sb.Append("$ProgressPreference='SilentlyContinue';try{& ").Append(PsQuote(scriptPath)).Append(' ').Append(arguments);
            sb.Append(" 3>&1 | ForEach-Object {[Console]::Out.WriteLine([string]$_)}}");
            sb.Append("catch{[Console]::Out.WriteLine('GALAT: '+$_.Exception.Message);exit 1}");
            return sb.ToString();
        }

        public static string EncodeCommand(string command)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        public static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static string ReadComputerName(string subKey)
        {
            return (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\ComputerName\" + subKey, "ComputerName", null);
        }

        public static string ActiveComputerName() { return ReadComputerName("ActiveComputerName") ?? Environment.MachineName; }

        // Berisi nama baru bila sudah diganti tetapi PC belum di-restart; selain itu null.
        public static string PendingComputerName()
        {
            string saved = ReadComputerName("ComputerName");
            return saved != null && !string.Equals(saved, ActiveComputerName(), StringComparison.OrdinalIgnoreCase) ? saved : null;
        }

        public static string PcNameFromNumber(string number)
        {
            string text = (number ?? "").Trim();
            if (!Regex.IsMatch(text, @"^[0-9]{1,3}$") || int.Parse(text) == 0) return null;
            return "SMADA-PC-" + int.Parse(text).ToString("00");
        }

        // ---------- login otomatis ----------

        const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // Password salah di login otomatis membuat PC berhenti di layar login, jadi dicek dulu.
        public static bool PasswordWorks(string user, string password)
        {
            IntPtr token;
            if (LogonUser(user, ".", password, 2, 0, out token)) { CloseHandle(token); return true; }
            // 1327: Windows menolak password kosong untuk cara cek ini, padahal login di layar tetap bisa.
            return password.Length == 0 && Marshal.GetLastWin32Error() == 1327;
        }

        static RegistryKey OpenWinlogon()
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(WinlogonKey, true);
        }

        // Password disimpan sebagai teks biasa di registry; hanya Administrator PC ini yang bisa membacanya.
        public static void SetAutoLogon(string user, string computer, string password)
        {
            using (var k = OpenWinlogon())
            {
                k.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
                k.SetValue("DefaultUserName", user, RegistryValueKind.String);
                k.SetValue("DefaultDomainName", computer, RegistryValueKind.String);
                k.SetValue("DefaultPassword", password, RegistryValueKind.String);
                k.DeleteValue("AutoLogonCount", false);
            }
        }

        public static void ClearAutoLogon()
        {
            using (var k = OpenWinlogon())
            {
                k.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                k.DeleteValue("DefaultPassword", false);
            }
        }

        // ---------- shortcut ----------

        public static string CreateDesktopShortcut(string dataFolder)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nyalakan Semua PC Lab.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
            linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { "--nyalakan-semua --folder \"" + dataFolder + "\"" });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
            return path;
        }
    }

    // Jendela dasar: area isi di atas, log di bawah, dan pembantu tata letak.
    public class LabForm : Form
    {
        protected static readonly Color Good = Color.FromArgb(0, 110, 50), Bad = Color.FromArgb(170, 30, 30), Warn = Color.FromArgb(150, 90, 0);
        protected readonly Panel content = new Panel { Dock = DockStyle.Fill };
        readonly TextBox log = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 150, BackColor = SystemColors.Window, Font = new Font("Consolas", 9f) };
        int busyCount;

        public LabForm(string title)
        {
            Text = title;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 660);
            MinimumSize = new Size(760, 560);
            Controls.Add(content);
            Controls.Add(log);
        }

        protected static string SettingsPath(string name)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KontrolLab", name);
        }

        public void Log(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }

        protected void SetBusy(bool busy)
        {
            busyCount += busy ? 1 : -1;
            content.Enabled = busyCount == 0;
            UseWaitCursor = busyCount > 0;
        }

        protected static TableLayoutPanel NewForm()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        protected static void AddRow(TableLayoutPanel t, string label, Control field, Control button)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 3) }, 0, row);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(field, 1, row);
            if (button != null) t.Controls.Add(button, 2, row);
        }

        protected static void AddWide(TableLayoutPanel t, Control c)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 3);
        }

        protected static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(840, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 6) };
        }

        protected static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 10, 0, 4) };
        }

        protected static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2) };
            b.Click += click;
            return b;
        }

        protected static FlowLayoutPanel Buttons(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            f.Controls.AddRange(items);
            return f;
        }

        protected Button BrowseFolder(TextBox target)
        {
            return Btn("Telusuri...", (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = target.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
            });
        }
    }
}
