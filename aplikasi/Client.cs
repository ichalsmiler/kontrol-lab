// KontrolLab-Client.exe: dijalankan sekali di tiap PC lab (otomatis meminta Administrator) untuk menyiapkan
// nama PC, Wake-on-LAN, login otomatis, dan Exambro otomatis. Skrip .ps1 ditanam di dalam exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Kontrol Lab Client")]
[assembly: AssemblyProduct("Kontrol Lab")]
[assembly: AssemblyVersion("2.2.0.0")]

namespace KontrolLab
{
    public class ClientForm : LabForm
    {
        static readonly string Settings = SettingsPath("client.txt");

        readonly bool isAdmin = Core.IsAdmin();
        string scriptDir;

        readonly Label pcNameLabel = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 6) };
        readonly TextBox numberBox = new TextBox(), exambroBox = new TextBox(), saveBox = new TextBox(), internetBox = new TextBox();
        readonly TextBox passwordBox = new TextBox { UseSystemPasswordChar = true };
        readonly CheckBox autoLogonCheck = new CheckBox { AutoSize = true, Checked = true };

        public ClientForm() : base("Kontrol Lab - Client")
        {
            content.AutoScroll = true;
            saveBox.Text = Core.DefaultDataFolder();
            string nearConfig = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "kontrol-internet.json");
            if (File.Exists(nearConfig)) internetBox.Text = nearConfig;
            LoadSettings();

            var t = NewForm();
            if (!isAdmin)
                AddWide(t, new Label { Text = "Aplikasi ini harus dibuka sebagai Administrator. Tutup, lalu klik kanan dan pilih Run as administrator.", AutoSize = true, ForeColor = Bad, Margin = new Padding(0, 8, 8, 4) });
            AddWide(t, Note("Jalankan sekali di tiap PC lab, di luar sesi ujian, dari akun Windows yang dipakai ujian. Paling mudah: jalankan dari flashdisk, lalu bawa flashdisk ke server; data PC ikut tersimpan di folder data-pc."));

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

            AddWide(t, Heading("Kontrol via internet"));
            AddRow(t, "kontrol-internet.json", internetBox, Btn("Telusuri...", (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "kontrol-internet.json|*.json" })
                    if (d.ShowDialog(this) == DialogResult.OK) internetBox.Text = d.FileName;
            }));
            AddWide(t, Note("File dari KontrolLab-Admin.exe > Kontrol via Internet. Bila ada di sebelah exe ini, terisi sendiri. Agen lalu berjalan setiap login sehingga Exambro bisa dijalankan dari server lewat internet. Kosongkan bila tidak dipakai."));

            AddWide(t, Heading("Login Windows otomatis"));
            autoLogonCheck.Text = "Login otomatis ke akun " + Environment.UserName + " saat PC menyala";
            AddWide(t, autoLogonCheck);
            AddRow(t, "Password akun ini", passwordBox, null);
            AddWide(t, Note("Tanpa login otomatis, PC berhenti di layar login dan Exambro tidak terbuka. Kosongkan password bila akun tidak memakai password. Password disimpan di registry PC ini dan bisa dibaca Administrator."));

            AddWide(t, Heading("Simpan data PC"));
            AddRow(t, "Folder data", saveBox, BrowseFolder(saveBox));
            var install = Btn("Pasang di PC ini", async (s, e) => await SetupThisPc());
            var remove = Btn("Hapus Exambro otomatis, agen internet, dan login otomatis", async (s, e) => await RemoveSetup());
            install.Enabled = remove.Enabled = isAdmin;
            AddWide(t, Buttons(install, remove));
            AddWide(t, Note("Wake-on-LAN juga harus diaktifkan di BIOS/UEFI tiap PC (biasanya bernama Wake on LAN, Power On by PCI-E, atau Resume by LAN). Ini tidak bisa diatur dari Windows."));
            RefreshPcName();
            content.Controls.Add(t);

            FormClosed += (s, e) => { SaveSettings(); CleanupScripts(); };
        }

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
            string internet = internetBox.Text.Trim();
            if (internet.Length > 0 && !File.Exists(internet)) { Log("kontrol-internet.json tidak ditemukan: " + internet); return; }
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
            if (internet.Length > 0) plan.Add("pasang agen kontrol via internet");
            if (autoLogonCheck.Checked) plan.Add("login otomatis ke akun " + Environment.UserName);
            plan.Add("simpan data PC ke " + saveBox.Text.Trim());
            if (MessageBox.Show(this, "Yang akan dilakukan di PC ini:\n- " + string.Join("\n- ", plan) + "\n\nJangan jalankan saat ujian. Lanjutkan?", "Pasang di PC ini", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string args = "-Folder " + Core.PsQuote(saveBox.Text.Trim()) + (newName != null ? " -NamaBaru " + Core.PsQuote(newName) : "");
            if (await RunScript("DAFTAR-PC-LAB.ps1", args) != 0) { RefreshPcName(); return; }
            RefreshPcName();
            if (exambro.Length > 0 && await RunScript("PASANG-EXAMBRO-AUTO.ps1", "-Target " + Core.PsQuote(exambro)) != 0) return;
            if (internet.Length > 0 && await RunScript("PASANG-AGEN-INTERNET.ps1", "-Konfig " + Core.PsQuote(internet)) != 0) return;
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
            if (MessageBox.Show(this, "Exambro otomatis, agen internet, dan login otomatis akan dihapus dari PC ini. Lanjutkan?", "Hapus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            await RunScript("PASANG-EXAMBRO-AUTO.ps1", "-Remove");
            await RunScript("PASANG-AGEN-INTERNET.ps1", "-Remove");
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

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(Settings)) return;
                foreach (string line in File.ReadAllLines(Settings))
                    if (line.StartsWith("exambro=") && line.Length > 8) exambroBox.Text = line.Substring(8);
            }
            catch (IOException) { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Settings));
                File.WriteAllLines(Settings, new[] { "exambro=" + exambroBox.Text.Trim() });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ClientForm());
        }
    }
}
