// Kontrol Lab: satu aplikasi untuk paket kontrol PC lab.
// Tab Kendali berjalan langsung di sini; pemasangan memakai skrip .ps1 yang ditanam di dalam exe.
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
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kontrol Lab")]
[assembly: AssemblyProduct("Kontrol Lab")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace KontrolLab
{
    public class PcRow
    {
        public string Room, PC, IP, User, MAC, Broadcast;
    }

    public class SshResult
    {
        public int Code;
        public string Output;
    }

    public static class Core
    {
        public static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n')
                {
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                    row.Clear();
                }
                else if (c != '\r') cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }

        public static List<PcRow> ReadInventory(string path)
        {
            var table = ParseCsv(File.ReadAllText(path));
            if (table.Count < 2) throw new InvalidDataException("Inventaris kosong.");
            var header = table[0].ToList();
            Func<string[], string, string> get = (r, name) =>
            {
                int i = header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw new InvalidDataException("Kolom " + name + " tidak ada di inventaris.");
                return i < r.Length ? r[i].Trim() : "";
            };
            var list = table.Skip(1).Select(r => new PcRow
            {
                Room = get(r, "Room"), PC = get(r, "PC"), IP = get(r, "IP"),
                User = get(r, "User"), MAC = get(r, "MAC"), Broadcast = get(r, "Broadcast")
            }).ToList();
            var dup = list.GroupBy(r => r.PC, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0) throw new InvalidDataException("Nama PC duplikat dalam inventaris: " + string.Join(", ", dup));
            return list;
        }

        public static bool IsValidIdentity(PcRow r)
        {
            IPAddress ip;
            return Regex.IsMatch(r.IP, @"^\d{1,3}(\.\d{1,3}){3}$") && IPAddress.TryParse(r.IP, out ip)
                && Regex.IsMatch(r.User, @"^\w[\w.-]*$") && Regex.IsMatch(r.PC, @"^[\w.-]+$");
        }

        public static byte[] MagicPacket(string macText)
        {
            string mac = Regex.Replace(macText ?? "", "[^0-9A-Fa-f]", "");
            if (mac.Length != 12) throw new FormatException("MAC tidak valid.");
            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 255;
            for (int n = 0; n < 16; n++)
                for (int i = 0; i < 6; i++)
                    packet[6 + n * 6 + i] = Convert.ToByte(mac.Substring(i * 2, 2), 16);
            return packet;
        }

        public static void SendWake(PcRow r)
        {
            var packet = MagicPacket(r.MAC);
            IPAddress broadcast;
            if (!IPAddress.TryParse(r.Broadcast, out broadcast) || broadcast.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException("Broadcast IPv4 tidak valid.");
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Send(packet, packet.Length, new IPEndPoint(broadcast, 9));
            }
        }

        // Kutip tunggal wajib: OpenSSH memecah nilai UserKnownHostsFile pada spasi.
        public static string SshArguments(PcRow r, string privateKey, string knownHosts, string remote)
        {
            return "-T -o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=yes"
                + " -o \"UserKnownHostsFile='" + knownHosts.Replace('\\', '/') + "'\""
                + " -i \"" + privateKey + "\" " + r.User + "@" + r.IP + " " + remote;
        }

        public static string SystemTool(string relative)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), relative);
        }

        public static SshResult RunTool(string exe, string arguments, int timeoutMs)
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
                    return new SshResult { Code = -1, Output = "Melewati batas waktu." };
                }
                Task.WaitAll(outText, errText);
                return new SshResult { Code = p.ExitCode, Output = (outText.Result + " " + errText.Result).Trim() };
            }
        }

        public static string PsQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        // Konfirmasi sudah diminta lewat dialog aplikasi, jadi Read-Host di skrip dijawab dari sini.
        public static string BuildScriptCommand(string scriptPath, string arguments, string confirmAnswer)
        {
            var sb = new StringBuilder("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;");
            sb.Append("function Read-Host {param([string]$Prompt) ");
            sb.Append(confirmAnswer == null ? "throw 'Konfirmasi tidak tersedia.'" : "return " + PsQuote(confirmAnswer));
            // Saat output dialihkan, PowerShell mengirim error dan progress sebagai CLIXML; ubah menjadi teks biasa untuk log.
            // Jangan pakai 2>&1 di sini: stderr program native bisa berubah menjadi error palsu saat mode Stop.
            sb.Append("};$ProgressPreference='SilentlyContinue';try{& ").Append(PsQuote(scriptPath)).Append(' ').Append(arguments);
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

        public static string LocalIPv4()
        {
            var found = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) found.Add(a.Address.ToString());
            }
            return found.Count > 0 ? string.Join(", ", found) : "tidak ada";
        }
    }

    public class ConfirmDialog : Form
    {
        ConfirmDialog(string title, string message, string expected)
        {
            Text = title;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(14), WrapContents = false };
            flow.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 10) });
            flow.Controls.Add(new Label { Text = "Ketik " + expected + " untuk melanjutkan:", AutoSize = true });
            var input = new TextBox { Width = 460 };
            flow.Controls.Add(input);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = 460, Height = 40, Margin = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = "Batal", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "Lanjutkan", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            CancelButton = cancel;
            AcceptButton = ok;
            input.TextChanged += (s, e) => ok.Enabled = input.Text == expected;
        }

        public static bool Ask(IWin32Window owner, string title, string message, string expected)
        {
            using (var d = new ConfirmDialog(title, message, expected))
                return d.ShowDialog(owner) == DialogResult.OK;
        }
    }

    public class MainForm : Form
    {
        static readonly string SshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KontrolLab", "pengaturan.txt");
        static readonly Color Good = Color.FromArgb(0, 110, 50), Bad = Color.FromArgb(170, 30, 30), Warn = Color.FromArgb(150, 90, 0);

        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly TextBox log = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 150, BackColor = SystemColors.Window, Font = new Font("Consolas", 9f) };
        readonly bool isAdmin = Core.IsAdmin();
        string scriptDir;
        int busyCount;

        // Kendali
        readonly TextBox csvBox = new TextBox(), keyBox = new TextBox(), knownBox = new TextBox();
        readonly ComboBox roomBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        readonly DataGridView grid = new DataGridView();

        // Persiapan
        readonly TextBox pubBox = new TextBox { ReadOnly = true };
        readonly TextBox notesBox = new TextBox(), prefixBox = new TextBox(), broadcastBox = new TextBox(), outBox = new TextBox();

        // Pasang di PC ini
        readonly TextBox pcNameBox = new TextBox();
        readonly Label pcNameLabel = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 6) };
        readonly TextBox controllerBox = new TextBox(), homeBox = new TextBox(), sourceBox = new TextBox(), targetUserBox = new TextBox(), newUserBox = new TextBox();
        readonly CheckBox renameCheck = new CheckBox { Text = "Rename akun (opsional, jangan saat ujian)", AutoSize = true };
        readonly CheckBox autoNameCheck = new CheckBox { Text = "Nama otomatis dari nama PC (SMADA-PC-01 menjadi SMADA01)", AutoSize = true };

        // Exambro
        readonly TextBox exambroBox = new TextBox();

        public MainForm(int startTab)
        {
            Text = "Kontrol Lab" + (isAdmin ? " (Administrator)" : "");
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 700);
            MinimumSize = new Size(820, 600);

            tabs.TabPages.Add(BuildControlTab());
            tabs.TabPages.Add(BuildPrepareTab());
            tabs.TabPages.Add(BuildInstallTab());
            tabs.TabPages.Add(BuildExambroTab());
            tabs.TabPages.Add(BuildUacTab());
            Controls.Add(tabs);
            Controls.Add(log);
            if (startTab >= 0 && startTab < tabs.TabCount) tabs.SelectedIndex = startTab;

            LoadSettings();
            RefreshPublicKey();
            Shown += (s, e) => { if (File.Exists(csvBox.Text)) LoadInventory(); };
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
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 6) };
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

        Button BrowseFile(TextBox target, string filter, bool save)
        {
            return Btn("Telusuri...", (s, e) =>
            {
                using (FileDialog d = save ? (FileDialog)new SaveFileDialog() : new OpenFileDialog())
                {
                    d.Filter = filter;
                    d.CheckFileExists = !save;
                    if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.FileName;
                }
            });
        }

        Button BrowseFolder(TextBox target)
        {
            return Btn("Telusuri...", (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
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

        void AddAdminBanner(TableLayoutPanel t, int tabIndex)
        {
            if (isAdmin) return;
            var label = new Label { Text = "Tab ini butuh hak Administrator.", AutoSize = true, ForeColor = Bad, Margin = new Padding(0, 8, 8, 4) };
            AddWide(t, Buttons(label, Btn("Buka ulang sebagai Administrator", (s, e) => RestartAsAdmin(tabIndex))));
        }

        void RestartAsAdmin(int tabIndex)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--tab " + tabIndex) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Win32Exception) { Log("Permintaan Administrator dibatalkan."); }
        }

        TabPage BuildControlTab()
        {
            var page = new TabPage("Kendali");
            keyBox.Text = Path.Combine(SshDir, "id_ed25519");
            knownBox.Text = Path.Combine(SshDir, "known_hosts");

            var t = NewForm();
            AddRow(t, "Inventaris CSV", csvBox, Buttons(BrowseFile(csvBox, "CSV (*.csv)|*.csv", false), Btn("Muat", (s, e) => LoadInventory())));
            AddRow(t, "Private key", keyBox, BrowseFile(keyBox, "Semua berkas (*.*)|*.*", false));
            AddRow(t, "Known hosts", knownBox, BrowseFile(knownBox, "Semua berkas (*.*)|*.*", false));
            roomBox.SelectedIndexChanged += (s, e) => ApplyRoomFilter();
            AddRow(t, "Ruang", roomBox, null);
            roomBox.Anchor = AnchorStyles.Left;

            var bar = Buttons(
                Btn("Pilih semua", (s, e) => SetAllChecked(true)),
                Btn("Kosongkan", (s, e) => SetAllChecked(false)),
                Btn("Nyalakan (WOL)", (s, e) => WakeSelected()),
                Btn("Cek Status", (s, e) => RunOverSsh(false)),
                Btn("Matikan", (s, e) => RunOverSsh(true)));
            bar.Dock = DockStyle.Top;

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Name = "Pilih", FillWeight = 12 });
            foreach (var c in new[] { "Ruang", "PC", "IP", "User", "Status" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c, Name = c, ReadOnly = true, FillWeight = c == "Status" ? 170 : 55 });

            page.Controls.Add(grid);
            page.Controls.Add(bar);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildPrepareTab()
        {
            var page = new TabPage("Persiapan") { AutoScroll = true };
            outBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Inventaris-Lab.csv");
            var t = NewForm();
            AddWide(t, Heading("1. Key PC pengendali"));
            AddRow(t, "Public key", pubBox, Buttons(
                Btn("Salin", (s, e) => { if (pubBox.Text.Length > 0) Clipboard.SetText(pubBox.Text); }),
                Btn("Buat key", (s, e) => CreateKey())));
            AddWide(t, Note("IPv4 PC ini: " + Core.LocalIPv4() + ". Pakai public key dan IP ini di tab \"Pasang di PC ini\" pada tiap PC lab."));
            AddWide(t, Heading("2. Gabungkan catatan AKSES-SSH-*.json dari PC lab"));
            AddRow(t, "Folder catatan", notesBox, BrowseFolder(notesBox));
            AddRow(t, "Prefix IP LAN", prefixBox, null);
            AddRow(t, "Broadcast", broadcastBox, null);
            AddRow(t, "Simpan CSV ke", outBox, BrowseFile(outBox, "CSV (*.csv)|*.csv", true));
            AddWide(t, Note("Contoh prefix 192.168.10. dan broadcast 192.168.10.255 untuk subnet /24."));
            AddWide(t, Buttons(
                Btn("Buat inventaris", async (s, e) => await BuildInventory()),
                Btn("Daftarkan host key", async (s, e) => await InstallKnownHosts())));
            AddWide(t, Note("Periksa ruang, IP, MAC kabel, dan fingerprint di CSV sebelum mendaftarkan host key. Jangan lanjut bila ada fingerprint berbeda."));
            AddWide(t, Buttons(Btn("Buka panduan lengkap", (s, e) => OpenGuide())));
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildInstallTab()
        {
            var page = new TabPage("Pasang di PC ini") { AutoScroll = true };
            var t = NewForm();
            AddAdminBanner(t, 2);
            AddWide(t, Heading("1. Nama PC (wajib unik, terutama untuk PC hasil cloning)"));
            AddWide(t, pcNameLabel);
            AddRow(t, "Nomor PC", pcNameBox, AdminBtn("Ganti nama PC", async (s, e) => await RenamePc()));
            AddWide(t, Note("Cukup isi nomornya: 1 menjadi SMADA-PC-01, 12 menjadi SMADA-PC-12. Restart diperlukan sebelum lanjut ke langkah 2."));
            RefreshPcName();
            AddWide(t, Heading("2. Pasang SSH"));
            AddWide(t, Note("Jalankan di tiap PC lab, sekali, di luar sesi ujian. Hanya public key yang dipasang; private key tetap di PC pengendali."));
            AddRow(t, "Public key pengendali", controllerBox, null);
            AddRow(t, "Public key kedua (opsional)", homeBox, null);
            AddRow(t, "IP/CIDR pengendali", sourceBox, null);
            AddRow(t, "Akun admin tujuan (opsional)", targetUserBox, null);
            AddWide(t, Note("Pisahkan beberapa IP dengan koma, misalnya 192.168.10.10, 192.168.10.11. Akun tujuan kosong berarti akun yang sedang dipakai."));
            AddWide(t, renameCheck);
            AddWide(t, autoNameCheck);
            AddRow(t, "Nama akun baru", newUserBox, null);
            AddWide(t, Buttons(AdminBtn("Pasang SSH", async (s, e) => await InstallSsh())));
            AddWide(t, Note("Catatan AKSES-SSH-NAMAPC.json tersimpan di Desktop. Salin ke PC pengendali untuk tab Persiapan."));
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildExambroTab()
        {
            var page = new TabPage("Exambro");
            var t = NewForm();
            AddAdminBanner(t, 3);
            AddWide(t, Note("Membuka Exambro otomatis setelah login dan mengklik tombol Jalankan pada jendela spesifikasi, tanpa menyentuh halaman login atau ujian. Periksa kebijakan penyelenggara ujian lebih dulu. Task dibuat untuk akun yang menjalankan aplikasi ini, jadi pastikan Administrator-nya akun yang dipakai ujian."));
            AddRow(t, "ExamBrowser.exe", exambroBox, BrowseFile(exambroBox, "ExamBrowser.exe|ExamBrowser.exe|Program (*.exe)|*.exe", false));
            AddWide(t, Buttons(
                AdminBtn("Pasang task", async (s, e) =>
                {
                    if (exambroBox.Text.Trim().Length == 0) { Log("Isi path ExamBrowser.exe."); return; }
                    await RunScript("PASANG-EXAMBRO-AUTO.ps1", "-Target " + Core.PsQuote(exambroBox.Text.Trim()), null);
                }),
                AdminBtn("Hapus task", async (s, e) => await RunScript("PASANG-EXAMBRO-AUTO.ps1", "-Remove", null))));
            AddWide(t, Note("Versi Exambro baru perlu diverifikasi lalu task dipasang ulang. Log ada di C:\\ProgramData\\Exambro-Startup-*.json."));
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildUacTab()
        {
            var page = new TabPage("UAC");
            var t = NewForm();
            AddAdminBanner(t, 4);
            AddWide(t, Note("Opsi terakhir bila task Exambro tidak cocok. UAC nonaktif membuat seluruh PC lebih rentan terhadap malware dan dapat mengganggu sebagian aplikasi Windows. Restart manual diperlukan setelah perubahan."));
            AddWide(t, Buttons(
                AdminBtn("Lihat status", async (s, e) => await RunScript("ATUR-UAC-OPSIONAL.ps1", "-Action Status", null)),
                AdminBtn("Matikan UAC", async (s, e) =>
                {
                    if (ConfirmDialog.Ask(this, "Matikan UAC", "UAC akan dimatikan untuk seluruh PC. Ini meningkatkan risiko malware dan perubahan tanpa konfirmasi.", "MATIKAN-UAC"))
                        await RunScript("ATUR-UAC-OPSIONAL.ps1", "-Action Disable", "MATIKAN-UAC");
                }),
                AdminBtn("Pulihkan UAC", async (s, e) => await RunScript("ATUR-UAC-OPSIONAL.ps1", "-Action Restore", null))));
            page.Controls.Add(t);
            return page;
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

        void SetStatus(DataGridViewRow row, string text, Color color)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(row, text, color))); return; }
            row.Cells["Status"].Value = text;
            row.Cells["Status"].Style.ForeColor = color;
        }

        // ---------- kendali ----------

        void LoadInventory()
        {
            try
            {
                var rows = Core.ReadInventory(csvBox.Text.Trim());
                grid.Rows.Clear();
                foreach (var r in rows)
                {
                    int i = grid.Rows.Add(false, r.Room, r.PC, r.IP, r.User, "");
                    grid.Rows[i].Tag = r;
                }
                roomBox.Items.Clear();
                roomBox.Items.Add("Semua ruang");
                roomBox.Items.AddRange(rows.Select(r => r.Room).Distinct().OrderBy(x => x).Cast<object>().ToArray());
                roomBox.SelectedIndex = 0;
                Log("Inventaris dimuat: " + rows.Count + " PC.");
            }
            catch (Exception ex) { Log("Gagal memuat inventaris: " + ex.Message); }
        }

        void ApplyRoomFilter()
        {
            grid.CurrentCell = null;
            string room = roomBox.SelectedIndex > 0 ? (string)roomBox.SelectedItem : null;
            foreach (DataGridViewRow row in grid.Rows)
                row.Visible = room == null || ((PcRow)row.Tag).Room == room;
        }

        void SetAllChecked(bool value)
        {
            grid.EndEdit();
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Visible) row.Cells["Pilih"].Value = value;
        }

        // PC yang tersembunyi oleh filter ruang tidak pernah ikut diproses.
        List<DataGridViewRow> SelectedRows()
        {
            grid.EndEdit();
            var picked = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Visible && true.Equals(r.Cells["Pilih"].Value)).ToList();
            if (picked.Count == 0) Log("Centang dulu PC yang akan diproses.");
            return picked;
        }

        void WakeSelected()
        {
            foreach (var row in SelectedRows())
            {
                var r = (PcRow)row.Tag;
                try
                {
                    Core.SendWake(r);
                    SetStatus(row, "Paket WOL terkirim, belum tentu menyala", Warn);
                    Log("WOL terkirim " + r.PC + " ke " + r.Broadcast + ". Cek Status untuk memastikan.");
                }
                catch (Exception ex)
                {
                    SetStatus(row, "WOL gagal", Bad);
                    Log(r.PC + ": " + ex.Message);
                }
            }
        }

        async void RunOverSsh(bool shutdown)
        {
            var picked = SelectedRows();
            if (picked.Count == 0) return;
            string key = keyBox.Text.Trim(), known = knownBox.Text.Trim();
            string ssh = Core.SystemTool(@"System32\OpenSSH\ssh.exe");
            if (!File.Exists(key)) { Log("Private key tidak ditemukan: " + key); return; }
            if (!File.Exists(known)) { Log("Known hosts tidak ditemukan: " + known + ". Daftarkan host key di tab Persiapan."); return; }
            if (!File.Exists(ssh)) { Log("ssh.exe tidak ditemukan: " + ssh); return; }
            if (shutdown)
            {
                string code = "SHUTDOWN-" + picked.Count;
                string names = string.Join(", ", picked.Select(r => ((PcRow)r.Tag).PC).Take(12)) + (picked.Count > 12 ? ", ..." : "");
                if (!ConfirmDialog.Ask(this, "Matikan PC", "Yang akan dimatikan: " + names + ". Shutdown tidak memaksa aplikasi tertutup, tetapi jangan jalankan saat ujian atau saat ada pekerjaan belum disimpan.", code))
                    return;
            }

            SetBusy(true);
            using (var gate = new SemaphoreSlim(16))
            {
                await Task.WhenAll(picked.Select(row => Task.Run(() =>
                {
                    gate.Wait();
                    try { ProcessSshRow(row, shutdown, ssh, key, known); }
                    finally { gate.Release(); }
                })));
            }
            SetBusy(false);
        }

        void ProcessSshRow(DataGridViewRow row, bool shutdown, string ssh, string key, string known)
        {
            var r = (PcRow)row.Tag;
            if (!Core.IsValidIdentity(r))
            {
                SetStatus(row, "Identitas tidak valid, dilewati", Bad);
                return;
            }
            SetStatus(row, "Memproses...", SystemColors.GrayText);
            var result = Core.RunTool(ssh, Core.SshArguments(r, key, known, shutdown ? "shutdown.exe /s /t 0" : "hostname"), 20000);
            if (result.Code == 0)
            {
                if (shutdown) SetStatus(row, "Perintah shutdown terkirim, tidak memaksa aplikasi", Good);
                else if (string.Equals(result.Output.Split('\n')[0].Trim(), r.PC, StringComparison.OrdinalIgnoreCase)) SetStatus(row, "Online", Good);
                else SetStatus(row, "Online, nama berbeda: " + result.Output, Warn);
                return;
            }
            // Koneksi bisa terputus karena PC keburu mati, jadi ini tidak dilaporkan sebagai gagal pasti.
            if (shutdown && Regex.IsMatch(result.Output, "closed|reset", RegexOptions.IgnoreCase))
                SetStatus(row, "Koneksi terputus, mungkin sudah mati. Cek Status", Warn);
            else
                SetStatus(row, shutdown ? "Gagal" : "Offline / gagal SSH", Bad);
            Log(r.PC + ": " + result.Output);
        }

        // ---------- persiapan ----------

        void RefreshPublicKey()
        {
            string pub = Path.Combine(SshDir, "id_ed25519.pub");
            pubBox.Text = File.Exists(pub) ? File.ReadAllText(pub).Trim() : "";
            if (controllerBox.Text.Length == 0) controllerBox.Text = pubBox.Text;
        }

        void CreateKey()
        {
            string key = Path.Combine(SshDir, "id_ed25519");
            if (File.Exists(key)) { Log("Key sudah ada dan tidak ditimpa: " + key); return; }
            var answer = MessageBox.Show(this, "Key dibuat tanpa passphrase agar perintah massal bisa berjalan otomatis. Siapa pun yang bisa membaca berkas ini dapat mengendalikan PC lab, jadi jaga akun Windows PC pengendali.\n\nLanjutkan?", "Buat key", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            Directory.CreateDirectory(SshDir);
            var result = Core.RunTool(Core.SystemTool(@"System32\OpenSSH\ssh-keygen.exe"), "-q -t ed25519 -N \"\" -f \"" + key + "\"", 20000);
            Log(result.Code == 0 ? "Key dibuat: " + key : "Gagal membuat key: " + result.Output);
            RefreshPublicKey();
        }

        async Task BuildInventory()
        {
            if (notesBox.Text.Trim().Length == 0 || prefixBox.Text.Trim().Length == 0 || broadcastBox.Text.Trim().Length == 0 || outBox.Text.Trim().Length == 0)
            {
                Log("Isi folder catatan, prefix IP, broadcast, dan lokasi CSV.");
                return;
            }
            int code = await RunScript("BUAT-INVENTARIS.ps1",
                "-CatatanFolder " + Core.PsQuote(notesBox.Text.Trim()) + " -OutputCsv " + Core.PsQuote(outBox.Text.Trim())
                + " -AddressPrefix " + Core.PsQuote(prefixBox.Text.Trim()) + " -Broadcast " + Core.PsQuote(broadcastBox.Text.Trim()), null);
            if (code == 0)
            {
                csvBox.Text = outBox.Text.Trim();
                LoadInventory();
            }
        }

        async Task InstallKnownHosts()
        {
            string csv = File.Exists(outBox.Text.Trim()) ? outBox.Text.Trim() : csvBox.Text.Trim();
            if (!File.Exists(csv)) { Log("Buat inventaris dulu."); return; }
            await RunScript("PASANG-KNOWN-HOSTS.ps1", "-InventoryCsv " + Core.PsQuote(csv), null);
        }

        void OpenGuide()
        {
            try
            {
                string path = Path.Combine(ScriptDirectory(), "PANDUAN-KONTROL-PC-LAB.pdf");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Log("Panduan tidak bisa dibuka: " + ex.Message); }
        }

        // ---------- pasang ----------

        void RefreshPcName()
        {
            string pending = Core.PendingComputerName();
            pcNameLabel.Text = "Nama PC sekarang: " + Core.ActiveComputerName() + (pending != null ? " (menjadi " + pending + " setelah restart)" : "");
            pcNameLabel.ForeColor = pending != null ? Warn : SystemColors.ControlText;
        }

        async Task RenamePc()
        {
            string name = Core.PcNameFromNumber(pcNameBox.Text);
            if (name == null) { Log("Isi nomor PC dengan angka 1 sampai 999."); return; }
            if (!ConfirmDialog.Ask(this, "Ganti nama PC", "Nama PC ini akan diganti menjadi " + name + ". Perubahan berlaku setelah restart, jadi jangan lakukan saat ujian.", "YA")) return;
            int code = await RunScript("GANTI-NAMA-PC.ps1", "-NewName " + Core.PsQuote(name), null);
            RefreshPcName();
            if (code != 0 || Core.PendingComputerName() == null) return;
            var answer = MessageBox.Show(this, "Restart PC sekarang agar nama baru berlaku? Simpan dulu pekerjaan yang terbuka.", "Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { Log("Restart ditunda. Pasang SSH baru bisa dijalankan setelah restart."); return; }
            var result = Core.RunTool(Core.SystemTool(@"System32\shutdown.exe"), "/r /t 0", 20000);
            if (result.Code != 0) Log("Restart gagal: " + result.Output);
        }

        async Task InstallSsh()
        {
            // Catatan SSH memakai nama aktif; tanpa restart, catatan akan tercatat dengan nama lama.
            string pendingName = Core.PendingComputerName();
            if (pendingName != null)
            {
                Log("Restart dulu agar nama PC menjadi " + pendingName + ", baru jalankan Pasang SSH.");
                return;
            }
            var sources = sourceBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (controllerBox.Text.Trim().Length == 0 || sources.Length == 0)
            {
                Log("Isi public key pengendali dan IP pengendali.");
                return;
            }
            var args = new StringBuilder("-ControllerPublicKey " + Core.PsQuote(controllerBox.Text.Trim()));
            args.Append(" -AllowedSource @(" + string.Join(",", sources.Select(Core.PsQuote)) + ")");
            if (homeBox.Text.Trim().Length > 0) args.Append(" -HomePublicKey " + Core.PsQuote(homeBox.Text.Trim()));
            if (targetUserBox.Text.Trim().Length > 0) args.Append(" -TargetUser " + Core.PsQuote(targetUserBox.Text.Trim()));
            string answer = null;
            if (renameCheck.Checked)
            {
                args.Append(" -RenameAccount");
                if (autoNameCheck.Checked) args.Append(" -AutomaticUserName");
                else if (newUserBox.Text.Trim().Length > 0) args.Append(" -NewUserName " + Core.PsQuote(newUserBox.Text.Trim()));
                else { Log("Isi nama akun baru atau centang nama otomatis."); return; }
                if (!ConfirmDialog.Ask(this, "Rename akun", "Rename mempertahankan SID, password, dan profil. Folder C:\\Users tidak ikut berganti, dan task atau autologin yang memakai nama lama perlu diperiksa.", "YA")) return;
                answer = "YA";
            }
            args.Append(" -OutputFormat Json");
            await RunScript("SIAPKAN-SSH-CLIENT.ps1", args.ToString(), answer);
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

        public async Task<int> RunScript(string script, string arguments, string confirmAnswer)
        {
            SetBusy(true);
            Log("Menjalankan " + script + "...");
            int code;
            try
            {
                string command = Core.BuildScriptCommand(Path.Combine(ScriptDirectory(), script), arguments, confirmAnswer);
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

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string value = line.Substring(i + 1);
                    switch (line.Substring(0, i))
                    {
                        case "csv": csvBox.Text = value; break;
                        case "notes": notesBox.Text = value; break;
                        case "prefix": prefixBox.Text = value; break;
                        case "broadcast": broadcastBox.Text = value; break;
                        case "out": outBox.Text = value; break;
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
                File.WriteAllLines(SettingsPath, new[]
                {
                    "csv=" + csvBox.Text, "notes=" + notesBox.Text, "prefix=" + prefixBox.Text,
                    "broadcast=" + broadcastBox.Text, "out=" + outBox.Text, "exambro=" + exambroBox.Text
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int tab = 0;
            int at = Array.IndexOf(args, "--tab");
            if (at >= 0 && at + 1 < args.Length) int.TryParse(args[at + 1], out tab);
            Application.Run(new MainForm(tab));
        }
    }
}
