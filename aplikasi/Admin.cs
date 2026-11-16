// KontrolLab-Admin.exe: dijalankan di komputer server untuk menyalakan PC lab dengan Wake-on-LAN.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Kontrol Lab Admin")]
[assembly: AssemblyProduct("Kontrol Lab")]
[assembly: AssemblyVersion("2.2.0.0")]

namespace KontrolLab
{
    public class AdminForm : LabForm
    {
        static readonly string Settings = SettingsPath("admin.txt");

        readonly TextBox folderBox = new TextBox();
        readonly DataGridView grid = new DataGridView();

        public AdminForm() : base("Kontrol Lab - Admin")
        {
            folderBox.Text = SavedDataFolder();

            var t = NewForm();
            AddRow(t, "Folder data PC", folderBox, Buttons(BrowseFolder(folderBox), Btn("Muat ulang", (s, e) => LoadPcList()), Btn("Buka folder", (s, e) => OpenFolder())));
            AddWide(t, Note("Berisi file PC-*.json dari KontrolLab-Client.exe. Komputer ini harus berada di jaringan LAN yang sama dengan PC lab."));

            var wakeAll = Btn("Nyalakan SEMUA PC", (s, e) => Wake(AllRows()));
            wakeAll.Font = new Font("Segoe UI Semibold", 11f);
            var bar = Buttons(
                wakeAll,
                Btn("Nyalakan yang dicentang", (s, e) => Wake(CheckedRows())),
                Btn("Cek menyala", async (s, e) => await CheckAlive()),
                Btn("Buat shortcut di Desktop", (s, e) => MakeShortcut()),
                Btn("Kontrol via Internet...", (s, e) => new InternetForm().Show()));
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

            content.Controls.Add(grid);
            content.Controls.Add(bar);
            content.Controls.Add(t);

            Shown += (s, e) => LoadPcList();
            FormClosed += (s, e) => SaveSettings();
        }

        static void SetStatus(DataGridViewRow row, string text, Color color)
        {
            row.Cells["Status"].Value = text;
            row.Cells["Status"].Style.ForeColor = color;
        }

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
            if (rows.Count == 0) { if (grid.Rows.Count == 0) Log("Daftar PC kosong. Jalankan KontrolLab-Client.exe di tiap PC lab, lalu salin file PC-*.json ke folder data."); return; }
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

        public static string SavedDataFolder()
        {
            try
            {
                if (File.Exists(Settings))
                    foreach (string line in File.ReadAllLines(Settings))
                        if (line.StartsWith("folder=") && line.Length > 7) return line.Substring(7);
            }
            catch (IOException) { }
            return Core.DefaultDataFolder();
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Settings));
                File.WriteAllLines(Settings, new[] { "folder=" + folderBox.Text.Trim() });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class Program
    {
        // KontrolLab-Admin.exe --nyalakan-semua [--folder "..."] [--diam]: satu perintah untuk menyalakan semua PC.
        static int WakeFromCommandLine(string[] args)
        {
            int at = Array.IndexOf(args, "--folder");
            string folder = at >= 0 && at + 1 < args.Length ? args[at + 1] : AdminForm.SavedDataFolder();
            bool quiet = args.Contains("--diam");
            var warnings = new List<string>();
            var rows = Core.ReadPcFolder(folder, warnings);
            int sent = Core.WakeAll(rows, warnings);
            string message = "Sinyal nyalakan terkirim ke " + sent + " dari " + rows.Count + " PC.\nPC biasanya menyala dalam 1-2 menit."
                + (warnings.Count > 0 ? "\n\nPeringatan:\n" + string.Join("\n", warnings) : "");
            if (!quiet) MessageBox.Show(message, "Nyalakan Semua PC Lab", MessageBoxButtons.OK, sent > 0 && warnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return sent > 0 ? 0 : 1;
        }

        // KontrolLab-Admin.exe --internet exambro-jalankan [--diam]: kirim perintah ke semua PC lewat relay.
        static int InternetFromCommandLine(string[] args)
        {
            int at = Array.IndexOf(args, "--internet");
            string action = at + 1 < args.Length ? args[at + 1] : "";
            bool quiet = args.Contains("--diam");
            string message; bool ok = false;
            try
            {
                if (!new[] { "exambro-jalankan", "exambro-tutup", "batal-matikan" }.Contains(action))
                    throw new Exception("Aksi baris perintah hanya exambro-jalankan, exambro-tutup, atau batal-matikan.");
                var settings = InternetSettings.Load();
                if (settings.Relay.Length == 0 || settings.AdminToken.Length == 0) throw new Exception("Isi dulu alamat relay dan token di jendela Kontrol via Internet.");
                bool created;
                var key = InternetSettings.LoadOrCreateKey(out created);
                if (created) throw new Exception("Kunci Admin belum ada; buka jendela Kontrol via Internet dan pasang ulang PC lab.");
                var r = Relay.Call(settings, "POST", "/api/admin/perintah", Relay.SignedCommand(key, action, new[] { "*" }));
                message = "Perintah " + action + " dikirim ke semua PC. Langsung diterima " + r["langsungTerkirim"] + " PC.";
                ok = true;
            }
            catch (Exception ex) { message = "Gagal: " + ex.Message; }
            if (!quiet) MessageBox.Show(message, "Kontrol Lab via Internet", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return ok ? 0 : 1;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--nyalakan-semua")) return WakeFromCommandLine(args);
            if (args.Contains("--internet")) return InternetFromCommandLine(args);
            Application.Run(new AdminForm());
            return 0;
        }
    }
}
