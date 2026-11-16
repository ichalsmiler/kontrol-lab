// Kontrol Lab: menyalakan PC lab dengan Wake-on-LAN dari komputer server, dan menyiapkan tiap PC lab
// agar login otomatis lalu membuka Exambro. Pemasangan memakai skrip .ps1 yang ditanam di dalam exe.
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

[assembly: AssemblyTitle("Kontrol Lab")]
[assembly: AssemblyProduct("Kontrol Lab")]
[assembly: AssemblyVersion("2.0.0.0")]

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

    public class MainForm : Form
    {
        static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KontrolLab", "pengaturan.txt");
        static readonly Color Good = Color.FromArgb(0, 110, 50), Bad = Color.FromArgb(170, 30, 30), Warn = Color.FromArgb(150, 90, 0);

        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly TextBox log = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 150, BackColor = SystemColors.Window, Font = new Font("Consolas", 9f) };
        readonly bool isAdmin = Core.IsAdmin();
        string scriptDir;
        int busyCount;

        // Nyalakan PC (di server)
        readonly TextBox folderBox = new TextBox();
        readonly DataGridView grid = new DataGridView();

        // Pasang di PC lab
        readonly Label pcNameLabel = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 6) };
        readonly TextBox numberBox = new TextBox(), exambroBox = new TextBox(), saveBox = new TextBox();
        readonly TextBox passwordBox = new TextBox { UseSystemPasswordChar = true };
        readonly CheckBox autoLogonCheck = new CheckBox { AutoSize = true, Checked = true };

        public MainForm(int startTab)
        {
            Text = "Kontrol Lab" + (isAdmin ? " (Administrator)" : "");
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 660);
            MinimumSize = new Size(760, 560);

            folderBox.Text = saveBox.Text = Core.DefaultDataFolder();
            LoadSettings();
            tabs.TabPages.Add(BuildWakeTab());
            tabs.TabPages.Add(BuildSetupTab());
            Controls.Add(tabs);
            Controls.Add(log);
            if (startTab >= 0 && startTab < tabs.TabCount) tabs.SelectedIndex = startTab;

            Shown += (s, e) => LoadPcList();
            FormClosed += (s, e) => { SaveSettings(); CleanupScripts(); };
        }

        // ---------- pembangun tampilan ----------

        static TableLayoutPanel NewForm()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        static void AddRow(TableLayoutPanel t, string label, Control field, Control button)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 3) }, 0, row);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(field, 1, row);
            if (button != null) t.Controls.Add(button, 2, row);
        }

        static void AddWide(TableLayoutPanel t, Control c)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 3);
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(840, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 6) };
        }

        static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 10, 0, 4) };
        }

        static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2) };
            b.Click += click;
            return b;
        }

        static FlowLayoutPanel Buttons(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            f.Controls.AddRange(items);
            return f;
        }

        Button BrowseFolder(TextBox target)
        {
            return Btn("Telusuri...", (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = target.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
            });
        }

        // Tombol yang mengubah sistem dimatikan bila aplikasi belum berjalan sebagai Administrator.
        Button AdminBtn(string text, EventHandler click)
        {
            var b = Btn(text, click);
            b.Enabled = isAdmin;
            return b;
        }

        TabPage BuildWakeTab()
        {
            var page = new TabPage("Nyalakan PC");
            var t = NewForm();
            AddRow(t, "Folder data PC", folderBox, Buttons(BrowseFolder(folderBox), Btn("Muat ulang", (s, e) => LoadPcList()), Btn("Buka folder", (s, e) => OpenFolder())));
            AddWide(t, Note("Berisi file PC-*.json dari tab \"Pasang di PC lab\". Komputer ini harus berada di jaringan LAN yang sama dengan PC lab."));

            var wakeAll = Btn("Nyalakan SEMUA PC", (s, e) => Wake(AllRows()));
            wakeAll.Font = new Font("Segoe UI Semibold", 11f);
            var bar = Buttons(
                wakeAll,
                Btn("Nyalakan yang dicentang", (s, e) => Wake(CheckedRows())),
                Btn("Cek menyala", async (s, e) => await CheckAlive()),
                Btn("Buat shortcut di Desktop", (s, e) => MakeShortcut()));
            bar.Dock = DockStyle.Top;

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Name = "Pilih", FillWeight = 12 });
            foreach (var c in new[] { "PC", "IP", "MAC", "Status" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c, Name = c, ReadOnly = true, FillWeight = c == "Status" ? 120 : 60 });

            page.Controls.Add(grid);
            page.Controls.Add(bar);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildSetupTab()
        {
            var page = new TabPage("Pasang di PC lab") { AutoScroll = true };
            var t = NewForm();
            if (!isAdmin)
            {
                var label = new Label { Text = "Tab ini butuh hak Administrator.", AutoSize = true, ForeColor = Bad, Margin = new Padding(0, 8, 8, 4) };
                AddWide(t, Buttons(label, Btn("Buka ulang sebagai Administrator", (s, e) => RestartAsAdmin())));
            }
            AddWide(t, Note("Jalankan sekali di tiap PC lab, di luar sesi ujian, dari akun Windows yang dipakai ujian. Paling mudah: jalankan aplikasi ini dari flashdisk, lalu bawa flashdisk ke server; data PC ikut tersimpan di folder data-pc."));

            AddWide(t, Heading("Nama PC"));
            AddWide(t, pcNameLabel);
            AddRow(t, "Nomor PC", numberBox, null);
            AddWide(t, Note("1 menjadi SMADA-PC-01. Kosongkan bila nama PC sudah benar."));

            AddWide(t, Heading("Exambro otomatis"));
            AddRow(t, "ExamBrowser.exe", exambroBox, Btn("Telusuri...", (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "ExamBrowser.exe|ExamBrowser.exe|Program (*.exe)|*.exe" })
                    if (d.ShowDialog(this) == DialogResult.OK) exambroBox.Text = d.FileName;
            }));
            AddWide(t, Note("Exambro dibuka setelah Windows login, dan tombol Jalankan diklik otomatis. Kosongkan bila tidak dipakai."));

            AddWide(t, Heading("Login Windows otomatis"));
            autoLogonCheck.Text = "Login otomatis ke akun " + Environment.UserName + " saat PC menyala";
            AddWide(t, autoLogonCheck);
            AddRow(t, "Password akun ini", passwordBox, null);
            AddWide(t, Note("Tanpa login otomatis, PC berhenti di layar login dan Exambro tidak terbuka. Kosongkan password bila akun tidak memakai password. Password disimpan di registry PC ini dan bisa dibaca Administrator."));

            AddWide(t, Heading("Simpan data PC"));
            AddRow(t, "Folder data", saveBox, BrowseFolder(saveBox));
            AddWide(t, Buttons(
                AdminBtn("Pasang di PC ini", async (s, e) => await SetupThisPc()),
                AdminBtn("Hapus Exambro otomatis dan login otomatis", async (s, e) => await RemoveSetup())));
            AddWide(t, Note("Wake-on-LAN juga harus diaktifkan di BIOS/UEFI tiap PC (biasanya bernama Wake on LAN, Power On by PCI-E, atau Resume by LAN). Ini tidak bisa diatur dari Windows."));
            RefreshPcName();
            page.Controls.Add(t);
            return page;
        }

        void RestartAsAdmin()
        {
            try
            {
                SaveSettings();
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--tab 1") { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Win32Exception) { Log("Permintaan Administrator dibatalkan."); }
        }

        // ---------- log dan status ----------

        public void Log(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }

        void SetBusy(bool busy)
        {
            busyCount += busy ? 1 : -1;
            tabs.Enabled = busyCount == 0;
            UseWaitCursor = busyCount > 0;
        }

        static void SetStatus(DataGridViewRow row, string text, Color color)
        {
            row.Cells["Status"].Value = text;
            row.Cells["Status"].Style.ForeColor = color;
        }

        // ---------- nyalakan ----------

        void LoadPcList()
        {
            var warnings = new List<string>();
            var rows = Core.ReadPcFolder(folderBox.Text.Trim(), warnings);
            grid.Rows.Clear();
            foreach (var r in rows)
            {
                int i = grid.Rows.Add(false, r.Name, r.IP, r.MAC, "");
                grid.Rows[i].Tag = r;
            }
            foreach (string w in warnings) Log(w);
            Log("Daftar PC: " + rows.Count + " PC.");
        }

        List<DataGridViewRow> AllRows()
        {
            return grid.Rows.Cast<DataGridViewRow>().ToList();
        }

        List<DataGridViewRow> CheckedRows()
        {
            grid.EndEdit();
            var picked = grid.Rows.Cast<DataGridViewRow>().Where(r => true.Equals(r.Cells["Pilih"].Value)).ToList();
            if (picked.Count == 0) Log("Centang dulu PC yang akan dinyalakan.");
            return picked;
        }

        void Wake(List<DataGridViewRow> rows)
        {
            if (rows.Count == 0) { if (grid.Rows.Count == 0) Log("Daftar PC kosong. Pasang dulu di tiap PC lab, lalu salin file PC-*.json ke folder data."); return; }
            foreach (var row in rows)
            {
                var r = (PcRow)row.Tag;
                try
                {
                    Core.SendWake(r);
                    SetStatus(row, "Sinyal terkirim, tunggu 1-2 menit lalu Cek menyala", Warn);
                }
                catch (Exception ex)
                {
                    SetStatus(row, "Gagal: " + ex.Message, Bad);
                }
            }
            Log("Sinyal nyalakan dikirim ke " + rows.Count + " PC.");
        }

        async Task CheckAlive()
        {
            var rows = AllRows();
            if (rows.Count == 0) return;
            SetBusy(true);
            foreach (var row in rows) SetStatus(row, "Mengecek...", SystemColors.GrayText);
            var results = await Task.WhenAll(rows.Select(row => Core.IsAlive(((PcRow)row.Tag).IP)));
            for (int i = 0; i < rows.Count; i++)
                SetStatus(rows[i], results[i] ? "Menyala" : "Tidak menjawab", results[i] ? Good : Bad);
            Log("Menyala: " + results.Count(x => x) + " dari " + rows.Count + " PC.");
            SetBusy(false);
        }

        void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(folderBox.Text.Trim());
                Process.Start(new ProcessStartInfo(folderBox.Text.Trim()) { UseShellExecute = true });
            }
            catch (Exception ex) { Log("Folder tidak bisa dibuka: " + ex.Message); }
        }

        void MakeShortcut()
        {
            try { Log("Shortcut dibuat: " + Core.CreateDesktopShortcut(folderBox.Text.Trim()) + ". Klik dua kali untuk menyalakan semua PC."); }
            catch (Exception ex) { Log("Shortcut gagal dibuat: " + ex.Message); }
        }

        // ---------- pasang di PC lab ----------

        void RefreshPcName()
        {
            string pending = Core.PendingComputerName();
            pcNameLabel.Text = "Nama PC sekarang: " + Core.ActiveComputerName() + (pending != null ? " (menjadi " + pending + " setelah restart)" : "");
            pcNameLabel.ForeColor = pending != null ? Warn : SystemColors.ControlText;
        }

        async Task SetupThisPc()
        {
            string newName = null;
            if (numberBox.Text.Trim().Length > 0)
            {
                newName = Core.PcNameFromNumber(numberBox.Text);
                if (newName == null) { Log("Isi nomor PC dengan angka 1 sampai 999, atau kosongkan."); return; }
            }
            string exambro = exambroBox.Text.Trim();
            if (exambro.Length > 0 && !File.Exists(exambro)) { Log("ExamBrowser.exe tidak ditemukan: " + exambro); return; }
            if (saveBox.Text.Trim().Length == 0) { Log("Isi folder data."); return; }
            if (autoLogonCheck.Checked && !Core.PasswordWorks(Environment.UserName, passwordBox.Text))
            {
                Log("Password akun " + Environment.UserName + " salah. Login otomatis tidak dipasang; tidak ada yang diubah.");
                return;
            }

            var plan = new List<string>();
            if (newName != null) plan.Add("ganti nama PC menjadi " + newName);
            plan.Add("siapkan Wake-on-LAN dan matikan Fast Startup");
            if (exambro.Length > 0) plan.Add("buka Exambro otomatis setelah login");
            if (autoLogonCheck.Checked) plan.Add("login otomatis ke akun " + Environment.UserName);
            plan.Add("simpan data PC ke " + saveBox.Text.Trim());
            if (MessageBox.Show(this, "Yang akan dilakukan di PC ini:\n- " + string.Join("\n- ", plan) + "\n\nJangan jalankan saat ujian. Lanjutkan?", "Pasang di PC ini", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string args = "-Folder " + Core.PsQuote(saveBox.Text.Trim()) + (newName != null ? " -NamaBaru " + Core.PsQuote(newName) : "");
            if (await RunScript("DAFTAR-PC-LAB.ps1", args) != 0) { RefreshPcName(); return; }
            RefreshPcName();
            if (exambro.Length > 0 && await RunScript("PASANG-EXAMBRO-AUTO.ps1", "-Target " + Core.PsQuote(exambro)) != 0) return;
            if (autoLogonCheck.Checked)
            {
                try
                {
                    Core.SetAutoLogon(Environment.UserName, Core.PendingComputerName() ?? Core.ActiveComputerName(), passwordBox.Text);
                    Log("Login otomatis ke akun " + Environment.UserName + " dipasang.");
                }
                catch (Exception ex) { Log("Login otomatis gagal dipasang: " + ex.Message); return; }
            }
            passwordBox.Clear();
            Log("Selesai. Restart PC ini untuk mencoba: Windows harus login sendiri" + (exambro.Length > 0 ? " lalu membuka Exambro." : "."));
            if (MessageBox.Show(this, "Pemasangan selesai. Restart sekarang untuk mencoba? Simpan dulu pekerjaan yang terbuka.", "Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var result = Core.RunTool(Core.SystemTool(@"System32\shutdown.exe"), "/r /t 0", 20000);
                if (result.Code != 0) Log("Restart gagal: " + result.Output);
            }
        }

        async Task RemoveSetup()
        {
            if (MessageBox.Show(this, "Exambro otomatis dan login otomatis akan dihapus dari PC ini. Lanjutkan?", "Hapus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            await RunScript("PASANG-EXAMBRO-AUTO.ps1", "-Remove");
            try { Core.ClearAutoLogon(); Log("Login otomatis dimatikan."); }
            catch (Exception ex) { Log("Login otomatis gagal dimatikan: " + ex.Message); }
        }

        // ---------- menjalankan skrip tertanam ----------

        string ScriptDirectory()
        {
            if (scriptDir != null) return scriptDir;
            string dir = Path.Combine(Path.GetTempPath(), "KontrolLab-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("paket.")))
            {
                using (var input = assembly.GetManifestResourceStream(name))
                using (var output = File.Create(Path.Combine(dir, name.Substring("paket.".Length))))
                    input.CopyTo(output);
            }
            scriptDir = dir;
            return dir;
        }

        void CleanupScripts()
        {
            if (scriptDir == null) return;
            try { Directory.Delete(scriptDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public async Task<int> RunScript(string script, string arguments)
        {
            SetBusy(true);
            Log("Menjalankan " + script + "...");
            int code;
            try
            {
                string command = Core.BuildScriptCommand(Path.Combine(ScriptDirectory(), script), arguments);
                string ps = Core.SystemTool(@"System32\WindowsPowerShell\v1.0\powershell.exe");
                var psi = new ProcessStartInfo(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand " + Core.EncodeCommand(command))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                code = await Task.Run(() =>
                {
                    using (var p = new Process { StartInfo = psi })
                    {
                        DataReceivedEventHandler sink = (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                        p.OutputDataReceived += sink;
                        p.ErrorDataReceived += sink;
                        p.Start();
                        p.StandardInput.Close();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        p.WaitForExit();
                        return p.ExitCode;
                    }
                });
                Log(code == 0 ? script + " selesai." : script + " GAGAL (kode " + code + "). Baca pesan di atas.");
            }
            catch (Exception ex)
            {
                Log("Gagal menjalankan " + script + ": " + ex.Message);
                code = -1;
            }
            SetBusy(false);
            return code;
        }

        // ---------- pengaturan ----------

        public static string SavedDataFolder()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    foreach (string line in File.ReadAllLines(SettingsPath))
                        if (line.StartsWith("folder=") && line.Length > 7) return line.Substring(7);
            }
            catch (IOException) { }
            return Core.DefaultDataFolder();
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0 || i == line.Length - 1) continue;
                    string value = line.Substring(i + 1);
                    switch (line.Substring(0, i))
                    {
                        case "folder": folderBox.Text = value; break;
                        case "exambro": exambroBox.Text = value; break;
                    }
                }
            }
            catch (IOException) { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { "folder=" + folderBox.Text.Trim(), "exambro=" + exambroBox.Text.Trim() });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class Program
    {
        // KontrolLab.exe --nyalakan-semua [--folder "..."] [--diam]: satu perintah untuk menyalakan semua PC.
        static int WakeFromCommandLine(string[] args)
        {
            int at = Array.IndexOf(args, "--folder");
            string folder = at >= 0 && at + 1 < args.Length ? args[at + 1] : MainForm.SavedDataFolder();
            bool quiet = args.Contains("--diam");
            var warnings = new List<string>();
            var rows = Core.ReadPcFolder(folder, warnings);
            int sent = Core.WakeAll(rows, warnings);
            string message = "Sinyal nyalakan terkirim ke " + sent + " dari " + rows.Count + " PC.\nPC biasanya menyala dalam 1-2 menit."
                + (warnings.Count > 0 ? "\n\nPeringatan:\n" + string.Join("\n", warnings) : "");
            if (!quiet) MessageBox.Show(message, "Nyalakan Semua PC Lab", MessageBoxButtons.OK, sent > 0 && warnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return sent > 0 ? 0 : 1;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--nyalakan-semua")) return WakeFromCommandLine(args);
            int tab = 0;
            int at = Array.IndexOf(args, "--tab");
            if (at >= 0 && at + 1 < args.Length) int.TryParse(args[at + 1], out tab);
            Application.Run(new MainForm(tab));
            return 0;
        }
    }
}
