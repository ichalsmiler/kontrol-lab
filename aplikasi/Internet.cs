// Kontrol via internet: Admin mengirim perintah bertanda tangan RSA ke relay; agen di PC lab memeriksa
// tanda tangan dengan kunci publik Admin sebelum menjalankan. Relay hanya perantara dan tidak bisa memalsukan perintah.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KontrolLab
{
    public class InternetSettings
    {
        public string Relay = "", AdminToken = "", ClientToken = "";

        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KontrolLab"); } }
        static string FilePath { get { return Path.Combine(Folder, "internet.txt"); } }
        public static string KeyPath { get { return Path.Combine(Folder, "kunci-admin.xml"); } }

        public static InternetSettings Load()
        {
            var s = new InternetSettings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i), v = line.Substring(i + 1).Trim();
                        if (k == "relay") s.Relay = v; else if (k == "tokenAdmin") s.AdminToken = v; else if (k == "tokenPc") s.ClientToken = v;
                    }
            }
            catch (IOException) { }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(FilePath, new[] { "relay=" + Relay, "tokenAdmin=" + AdminToken, "tokenPc=" + ClientToken });
        }

        // Kunci RSA Admin dibuat sekali. Bila hilang, semua PC lab harus dipasang ulang dengan file konfigurasi baru.
        public static RSACryptoServiceProvider LoadOrCreateKey(out bool created)
        {
            var rsa = new RSACryptoServiceProvider(2048, new CspParameters(24)) { PersistKeyInCsp = false };
            created = false;
            if (File.Exists(KeyPath)) rsa.FromXmlString(File.ReadAllText(KeyPath));
            else
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(KeyPath, rsa.ToXmlString(true));
                created = true;
            }
            return rsa;
        }
    }

    public static class Relay
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        static Relay() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        public static string Base(InternetSettings s) { return s.Relay.Trim().TrimEnd('/'); }

        public static Dictionary<string, object> Call(InternetSettings s, string method, string path, object body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Base(s) + path);
            req.Method = method;
            req.Timeout = req.ReadWriteTimeout = 15000;
            req.Headers["X-Token"] = s.AdminToken.Trim();
            req.Proxy = null;
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body));
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = data.Length;
                using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return (Dictionary<string, object>)Json.DeserializeObject(r.ReadToEnd());
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new Exception("relay tidak terjangkau (" + ex.Message + ")");
                string text;
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
                string msg = text;
                try { var d = (Dictionary<string, object>)Json.DeserializeObject(text); if (d.ContainsKey("error")) msg = Convert.ToString(d["error"]); } catch (ArgumentException) { }
                throw new Exception("relay menolak (" + (int)resp.StatusCode + "): " + msg);
            }
        }

        public static string NewId() { return DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 12); }

        // Format pesan yang ditandatangani harus sama persis dengan AGEN-INTERNET.ps1.
        public static Dictionary<string, object> SignedCommand(RSACryptoServiceProvider key, string action, string[] target, string data = "")
        {
            string id = NewId();
            long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            string message = "v1|" + id + "|" + action + "|" + string.Join(",", target) + "|" + ts + (data.Length > 0 ? "|" + data : "");
            string sig = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(message), "SHA256"));
            return new Dictionary<string, object> { { "id", id }, { "aksi", action }, { "target", target }, { "ts", ts }, { "data", data }, { "sig", sig } };
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        // Mengunggah isi file mentah ke relay; progress dipanggil dengan persen 0-100.
        public static string Upload(InternetSettings s, string path, Action<int> progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(Base(s) + "/api/admin/file");
            req.Method = "POST";
            req.Timeout = req.ReadWriteTimeout = 60 * 60 * 1000;
            req.Headers["X-Token"] = s.AdminToken.Trim();
            req.Proxy = null;
            req.ContentType = "application/octet-stream";
            req.AllowWriteStreamBuffering = false;
            long total = new FileInfo(path).Length;
            req.ContentLength = total;
            using (var f = File.OpenRead(path))
            using (var st = req.GetRequestStream())
            {
                var buf = new byte[256 * 1024];
                long sent = 0; int n, last = -1;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    st.Write(buf, 0, n);
                    sent += n;
                    int pct = total == 0 ? 100 : (int)(sent * 100 / total);
                    if (pct != last) { last = pct; progress(pct); }
                }
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Convert.ToString(((Dictionary<string, object>)Json.DeserializeObject(r.ReadToEnd()))["sha256"]);
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new Exception("relay tidak terjangkau (" + ex.Message + ")");
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) throw new Exception("relay menolak (" + (int)resp.StatusCode + "): " + r.ReadToEnd());
            }
        }

        public static string ClientConfigJson(InternetSettings s, RSACryptoServiceProvider key)
        {
            return Json.Serialize(new Dictionary<string, object> { { "relay", Base(s) }, { "token", s.ClientToken.Trim() }, { "kunciPublik", key.ToXmlString(false) } });
        }
    }

    public class InternetForm : LabForm
    {
        static readonly Dictionary<string, string> ActionNames = new Dictionary<string, string>
        {
            { "exambro-jalankan", "Jalankan Exambro" }, { "exambro-tutup", "Tutup Exambro" },
            { "restart", "Restart PC" }, { "matikan", "Matikan PC" }, { "batal-matikan", "Batalkan restart/matikan" },
        };

        readonly InternetSettings settings = InternetSettings.Load();
        readonly RSACryptoServiceProvider key;
        readonly TextBox relayBox = new TextBox(), adminBox = new TextBox { UseSystemPasswordChar = true }, clientBox = new TextBox { UseSystemPasswordChar = true };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly DataGridView grid = new DataGridView();
        readonly Label summary = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
        readonly Timer timer = new Timer { Interval = 3000 };
        readonly HashSet<string> ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string lastId, lastAction;
        bool refreshing;

        public InternetForm() : base("Kontrol Lab - Kontrol via Internet")
        {
            bool created;
            key = InternetSettings.LoadOrCreateKey(out created);
            relayBox.Text = settings.Relay;
            adminBox.Text = settings.AdminToken;
            clientBox.Text = settings.ClientToken;

            var t = NewForm();
            AddRow(t, "Alamat relay", relayBox, Btn("Tes", async (s, e) => await TestRelay()));
            AddRow(t, "Token admin", adminBox, null);
            AddRow(t, "Token PC lab", clientBox, Btn("Buat file untuk PC lab...", (s, e) => ExportClientConfig()));
            AddWide(t, Note("Contoh alamat: http://192.168.1.10:8790. Token ada di /opt/kontrol-lab-relay/config.json di server. File untuk PC lab dipasang lewat KontrolLab-Client.exe."));

            targetBox.Items.AddRange(new object[] { "Semua PC yang online", "Hanya PC yang dicentang" });
            targetBox.SelectedIndex = 0;
            var run = Btn("Jalankan Exambro", async (s, e) => await Send("exambro-jalankan"));
            run.Font = new Font("Segoe UI Semibold", 11f);
            var bar = Buttons(new Label { Text = "Sasaran:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) }, targetBox, run,
                Btn("Tutup Exambro", async (s, e) => await Send("exambro-tutup")),
                Btn("Restart", async (s, e) => await Send("restart")),
                Btn("Matikan", async (s, e) => await Send("matikan")),
                Btn("Batalkan", async (s, e) => await Send("batal-matikan")),
                Btn("Kirim file ke Documents...", async (s, e) => await SendFile()));
            bar.Dock = DockStyle.Top;
            bar.WrapContents = true; // tombol turun ke baris kedua bila jendela sempit
            bar.Padding = new Padding(10, 0, 10, 0);
            summary.Dock = DockStyle.Top;
            summary.Padding = new Padding(12, 0, 0, 0);

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Name = "Pilih", FillWeight = 10 });
            foreach (var c in new[] { "PC", "Koneksi", "Exambro", "Pengguna", "IP", "Perintah terakhir" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c, Name = c, ReadOnly = true, FillWeight = c == "Perintah terakhir" ? 150 : 50 });
            grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                string pc = Convert.ToString(grid.Rows[e.RowIndex].Cells["PC"].Value);
                if (true.Equals(grid.Rows[e.RowIndex].Cells[0].Value)) ticked.Add(pc); else ticked.Remove(pc);
            };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            content.Controls.Add(grid);
            content.Controls.Add(summary);
            content.Controls.Add(bar);
            content.Controls.Add(t);

            timer.Tick += async (s, e) => await RefreshStatus(false);
            Shown += async (s, e) =>
            {
                if (created) Log("Kunci Admin baru dibuat di " + InternetSettings.KeyPath + ". Simpan cadangannya: bila hilang, semua PC lab harus dipasang ulang.");
                await RefreshStatus(true);
                timer.Start();
            };
            FormClosed += (s, e) => { timer.Stop(); SaveFields(); };
        }

        void SaveFields()
        {
            settings.Relay = relayBox.Text.Trim();
            settings.AdminToken = adminBox.Text.Trim();
            settings.ClientToken = clientBox.Text.Trim();
            try { settings.Save(); } catch (Exception ex) { Log("Pengaturan gagal disimpan: " + ex.Message); }
        }

        bool Ready()
        {
            SaveFields();
            if (!settings.Relay.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !settings.Relay.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            { Log("Isi alamat relay, misalnya http://192.168.1.10:8790"); return false; }
            if (settings.AdminToken.Length == 0) { Log("Isi token admin."); return false; }
            return true;
        }

        async Task TestRelay()
        {
            if (!Ready()) return;
            SetBusy(true);
            try
            {
                var r = await Task.Run(() => Relay.Call(settings, "GET", "/api/admin/status", null));
                Log("Relay terhubung. PC tercatat: " + ((object[])r["pc"]).Length + ".");
            }
            catch (Exception ex) { Log("Tes gagal: " + ex.Message); }
            SetBusy(false);
        }

        void ExportClientConfig()
        {
            if (!Ready()) return;
            if (settings.ClientToken.Length == 0) { Log("Isi token PC lab."); return; }
            using (var d = new SaveFileDialog { FileName = "kontrol-internet.json", Filter = "kontrol-internet.json|*.json", InitialDirectory = Path.GetDirectoryName(Application.ExecutablePath) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, Relay.ClientConfigJson(settings, key), new UTF8Encoding(false));
                Log("File PC lab disimpan: " + d.FileName + ". Letakkan di sebelah KontrolLab-Client.exe (flashdisk). File ini hanya berisi kunci publik; kunci rahasia tetap di komputer ini.");
            }
        }

        string[] Targets(out int count)
        {
            count = 0;
            if (targetBox.SelectedIndex == 0)
            {
                count = grid.Rows.Cast<DataGridViewRow>().Count(r => Convert.ToString(r.Cells["Koneksi"].Value) == "Online");
                return new[] { "*" };
            }
            var picked = ticked.OrderBy(x => x).ToArray();
            count = picked.Length;
            return picked;
        }

        async Task Send(string action)
        {
            if (!Ready()) return;
            int count;
            string[] target = Targets(out count);
            if (target.Length == 0) { Log("Centang dulu PC sasarannya, atau pilih sasaran Semua PC."); return; }
            string name = ActionNames[action];
            string who = target[0] == "*" ? "SEMUA PC yang terhubung (" + count + " online)" : count + " PC: " + string.Join(", ", target);
            if (action != "exambro-jalankan" && action != "batal-matikan" &&
                MessageBox.Show(this, name + " di " + who + "?" + (action == "exambro-tutup" ? "\n\nPeserta yang sedang ujian akan keluar dari Exambro." : "\n\nPC diberi waktu 20 detik; tombol Batalkan masih bisa menghentikannya."),
                    name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            SetBusy(true);
            try
            {
                var cmd = Relay.SignedCommand(key, action, target);
                var r = await Task.Run(() => Relay.Call(settings, "POST", "/api/admin/perintah", cmd));
                lastId = (string)cmd["id"];
                lastAction = name;
                Log(name + " dikirim ke " + who + ". Langsung diterima " + r["langsungTerkirim"] + " PC; sisanya menerima saat menghubungi relay.");
            }
            catch (Exception ex) { Log(name + " gagal dikirim: " + ex.Message); }
            SetBusy(false);
            await RefreshStatus(false);
        }

        async Task SendFile()
        {
            if (!Ready()) return;
            int count;
            string[] target = Targets(out count);
            if (target.Length == 0) { Log("Centang dulu PC sasarannya, atau pilih sasaran Semua PC."); return; }
            string path;
            using (var d = new OpenFileDialog { Title = "Pilih file yang dikirim ke Documents PC lab" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            // '|' dipakai sebagai pemisah data bertanda tangan.
            string fileName = Path.GetFileName(path).Replace("|", "_");
            long size = new FileInfo(path).Length;
            string who = target[0] == "*" ? "SEMUA PC yang terhubung (" + count + " online)" : count + " PC: " + string.Join(", ", target);
            if (MessageBox.Show(this, "Kirim " + fileName + " (" + FormatSize(size) + ") ke folder Documents di " + who + "?\n\nFile dengan nama sama di Documents akan diganti.",
                    "Kirim file", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SetBusy(true);
            try
            {
                Log("Menghitung hash " + fileName + "...");
                string sha = await Task.Run(() => Relay.Sha256File(path));
                Log("Mengunggah " + fileName + " ke relay...");
                int shown = -1;
                string uploaded = await Task.Run(() => Relay.Upload(settings, path, pct =>
                {
                    if (pct / 10 != shown) { shown = pct / 10; summary.BeginInvoke(new Action(() => summary.Text = "Mengunggah " + fileName + ": " + pct + "%")); }
                }));
                if (uploaded != sha) throw new Exception("hash di relay berbeda; file berubah saat diunggah? Coba lagi.");
                var cmd = Relay.SignedCommand(key, "kirim-file", target, fileName + "|" + sha + "|" + size);
                var r = await Task.Run(() => Relay.Call(settings, "POST", "/api/admin/perintah", cmd));
                lastId = (string)cmd["id"];
                lastAction = "Kirim " + fileName;
                Log("File " + fileName + " siap; perintah unduh dikirim ke " + who + ". Langsung diterima " + r["langsungTerkirim"] + " PC. Hasil tiap PC ada di kolom Perintah terakhir.");
            }
            catch (Exception ex) { Log("Kirim file gagal: " + ex.Message); }
            SetBusy(false);
            await RefreshStatus(false);
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " byte";
        }

        async Task RefreshStatus(bool verbose)
        {
            if (refreshing || IsDisposed) return;
            if (relayBox.Text.Trim().Length == 0 || adminBox.Text.Trim().Length == 0) { summary.Text = "Isi alamat relay dan token admin."; return; }
            refreshing = true;
            try
            {
                settings.Relay = relayBox.Text.Trim(); settings.AdminToken = adminBox.Text.Trim();
                var r = await Task.Run(() => Relay.Call(settings, "GET", "/api/admin/status", null));
                ShowStatus(r);
            }
            catch (Exception ex)
            {
                summary.Text = "Relay: " + ex.Message;
                summary.ForeColor = Bad;
                if (verbose) Log(ex.Message);
            }
            refreshing = false;
        }

        void ShowStatus(Dictionary<string, object> r)
        {
            var list = ((object[])r["pc"]).Cast<Dictionary<string, object>>().ToList();
            HashSet<string> delivered = null;
            if (lastId != null)
                foreach (Dictionary<string, object> c in (object[])r["perintah"])
                    if ((string)c["id"] == lastId) delivered = new HashSet<string>(((object[])c["terkirim"]).Select(Convert.ToString), StringComparer.OrdinalIgnoreCase);

            int online = 0, exambro = 0, ok = 0, failed = 0;
            int first = grid.FirstDisplayedScrollingRowIndex;
            grid.Rows.Clear();
            foreach (var p in list)
            {
                string pc = Convert.ToString(p["name"]);
                bool isOnline = (bool)p["online"];
                bool exRunning = (bool)p["exambro"];
                if (isOnline) online++;
                if (isOnline && exRunning) exambro++;
                string last = "";
                Color lastColor = SystemColors.GrayText;
                var hasil = p["hasil"] as Dictionary<string, object>;
                if (lastId != null && hasil != null && hasil.ContainsKey(lastId))
                {
                    var h = (Dictionary<string, object>)hasil[lastId];
                    bool good = (bool)h["ok"];
                    if (good) ok++; else failed++;
                    last = lastAction + ": " + h["pesan"];
                    lastColor = good ? Good : Bad;
                }
                else if (lastId != null && delivered != null && delivered.Contains(pc)) { last = lastAction + ": sedang dikerjakan..."; lastColor = Warn; }

                int age = Convert.ToInt32(p["detikLalu"]);
                int i = grid.Rows.Add(ticked.Contains(pc), pc, isOnline ? "Online" : "Offline " + Ago(age), isOnline ? (exRunning ? "Berjalan" : "Tidak") : "-",
                    p["user"], p["ip"], last);
                var row = grid.Rows[i];
                row.Cells["Koneksi"].Style.ForeColor = isOnline ? Good : Bad;
                row.Cells["Exambro"].Style.ForeColor = !isOnline ? SystemColors.GrayText : exRunning ? Good : Warn;
                row.Cells["Perintah terakhir"].Style.ForeColor = lastColor;
            }
            if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
            summary.ForeColor = SystemColors.ControlText;
            summary.Text = "Online " + online + " dari " + list.Count + " PC  |  Exambro berjalan di " + exambro + " PC" +
                (lastId != null ? "  |  " + lastAction + ": " + ok + " berhasil, " + failed + " gagal" : "");
        }

        static string Ago(int seconds)
        {
            if (seconds < 120) return "(" + seconds + " dtk lalu)";
            if (seconds < 7200) return "(" + seconds / 60 + " mnt lalu)";
            return "(" + seconds / 3600 + " jam lalu)";
        }
    }
}
