# Kontrol Lab: nyalakan PC lab dan buka Exambro otomatis

Tujuannya: dari komputer server, satu klik menyalakan semua PC lab. PC lalu login Windows sendiri dan membuka Exambro.

Isi paket:
- `KontrolLab-Admin.exe`: untuk komputer server. Menyalakan PC lab dan mengecek yang sudah menyala.
- `KontrolLab-Client.exe`: untuk tiap PC lab, dijalankan sekali. Otomatis meminta izin Administrator saat dibuka.
- `DAFTAR-PC-LAB.ps1`, `PASANG-EXAMBRO-AUTO.ps1`, `EXAMBRO-STARTUP.ps1`, `EXAMBRO-UI.ps1`: skrip yang sudah tertanam di Client, disimpan untuk referensi.
- `aplikasi\`: kode sumber. Jalankan `aplikasi\BANGUN-EXE.ps1` untuk membangun ulang kedua exe.
- `arsip-fitur-lama\`: fitur SSH, matikan PC, UAC, dan aplikasi versi sebelumnya. Tidak dipakai lagi.

## 1. BIOS tiap PC lab (sekali, manual)

Aktifkan Wake-on-LAN di BIOS/UEFI. Namanya berbeda per merek, misalnya *Wake on LAN*, *Power On by PCI-E*, *Resume by LAN*, atau *PME Event Wake Up*. Matikan *ErP/EuP* bila ada, karena opsi itu memutus listrik siaga ke kartu LAN. PC harus memakai kabel LAN; Wi-Fi tidak bisa.

## 2. Di tiap PC lab: KontrolLab-Client.exe (sekali)

1. Salin `KontrolLab-Client.exe` ke flashdisk. Login ke PC lab dengan akun Windows yang dipakai ujian, lalu buka exe itu dari flashdisk dan pilih **Yes** saat Windows meminta izin Administrator.
2. Isi:
   - **Nomor PC**: misalnya `1` menjadi `SMADA-PC-01`. Kosongkan bila nama PC sudah benar dan tidak dobel.
   - **ExamBrowser.exe**: pilih lokasi Exambro di PC itu.
   - **Login otomatis**: biarkan dicentang, lalu isi password akun itu (kosongkan bila akun tanpa password).
3. Klik **Pasang di PC ini**, lalu pilih **Yes** untuk restart.
4. Pastikan setelah restart Windows login sendiri dan Exambro terbuka.

Yang dikerjakan tombol itu: ganti nama PC, menyalakan Wake-on-LAN di kartu jaringan, mematikan Fast Startup, mengizinkan ping dari LAN, memasang Exambro otomatis, memasang login otomatis, lalu menyimpan `PC-NAMA.json` (nama, IP, MAC) ke folder `data-pc` di sebelah exe, yaitu di flashdisk.

Password login otomatis disimpan di registry PC lab dan bisa dibaca Administrator PC itu. Bila Exambro diperbarui, ulangi langkah ini karena versi baru harus diverifikasi ulang. Tombol **Hapus Exambro otomatis dan login otomatis** membatalkan keduanya.

## 3. Di komputer server: KontrolLab-Admin.exe

1. Salin `KontrolLab-Admin.exe` dan folder `data-pc` dari flashdisk ke satu folder di server, misalnya `D:\KontrolLab`.
2. Buka `KontrolLab-Admin.exe`. Semua PC lab tampil di daftar.
3. Klik **Buat shortcut di Desktop** sekali.

Sesudah itu cukup klik dua kali shortcut **Nyalakan Semua PC Lab** di Desktop. Atau jalankan perintah ini:

```
"D:\KontrolLab\KontrolLab-Admin.exe" --nyalakan-semua
```

Tambahkan `--diam` agar tidak muncul jendela hasil, misalnya untuk dijadwalkan di Task Scheduler setiap pagi.

Server harus berada di jaringan LAN yang sama dengan PC lab. Tunggu 1-2 menit, lalu klik **Cek menyala** di aplikasi untuk melihat PC yang sudah hidup. Bila ada PC lab baru atau dipasang ulang, salin file `PC-*.json` terbarunya ke folder `data-pc` di server lalu klik **Muat ulang**.

## 4. Menjalankan Exambro di semua PC lewat internet

Admin dan PC lab tidak perlu satu jaringan. Keduanya hanya menghubungi **relay** di sebuah server Linux (`/opt/kontrol-lab-relay`, layanan `kontrol-lab-relay`, port 8790). Perintah ditandatangani kunci rahasia Admin, jadi relay atau orang lain tidak bisa memalsukan perintah. PC lab hanya mau menjalankan lima perintah tetap: jalankan Exambro, tutup Exambro, restart, matikan, dan batalkan.

**Sekali di server (Debian/Ubuntu, sebagai root):**

```bash
curl -fsSL https://raw.githubusercontent.com/ichalsmiler/kontrol-lab/main/relay/pasang-relay.sh | bash
```

Skrip ini memasang Node.js bila belum ada, lalu menjalankan relay sebagai layanan. Di akhir, skrip menampilkan **alamat relay**, **token admin**, dan **token PC lab**. Untuk port lain, ganti `| bash` dengan `| PORT=9000 bash`. Untuk menghapus relay dari server:

```bash
curl -fsSL https://raw.githubusercontent.com/ichalsmiler/kontrol-lab/main/relay/hapus-relay.sh | bash
```

**Sekali di komputer Admin:**
1. Buka `KontrolLab-Admin.exe`, lalu klik **Kontrol via Internet...**.
2. Isi **Alamat relay**, **Token admin**, dan **Token PC lab** dari hasil skrip di atas. Bila lupa, tokennya ada di server, di `/opt/kontrol-lab-relay/config.json`.
3. Klik **Buat file untuk PC lab...**, lalu simpan `kontrol-internet.json` di flashdisk, di sebelah `KontrolLab-Client.exe`.
4. Cadangkan `%LOCALAPPDATA%\KontrolLab\kunci-admin.xml` ke tempat aman. Bila kunci ini hilang, semua PC lab harus dipasang ulang dengan file baru. Jangan bagikan file ini.

**Di tiap PC lab:** jalankan `KontrolLab-Client.exe` dari flashdisk seperti di langkah 2. Kolom **kontrol-internet.json** akan terisi sendiri. Klik **Pasang di PC ini**. Agen lalu berjalan setiap kali akun ujian login.

**Saat ujian:** buka **Kontrol via Internet**, pilih sasaran (semua PC atau yang dicentang), lalu klik **Jalankan Exambro**. PC yang online menerimanya dalam 1-2 detik. Kolom *Perintah terakhir* menunjukkan hasil dari tiap PC. Bisa juga dari shortcut:

```
"D:\KontrolLab\KontrolLab-Admin.exe" --internet exambro-jalankan
```

**Mengirim file ke PC lab** (misalnya file SEB atau Exambro terbaru): klik **Kirim file ke Documents...**, lalu pilih filenya. File diunggah sekali ke relay. Setiap PC sasaran lalu mengunduhnya ke folder **Documents** akun yang sedang login. File lama dengan nama yang sama akan diganti. Kolom *Perintah terakhir* menunjukkan lokasi file di tiap PC, atau alasannya bila gagal. PC memeriksa sidik jari (SHA-256) yang ditandatangani Admin, jadi file yang rusak atau ditukar di jalan akan ditolak. File tidak dibuka atau dijalankan otomatis. Relay menyimpan 5 file terakhir (maksimal 1 GB per file) di `/var/lib/kontrol-lab-relay/file`. PC yang sedang mati tidak ikut menerima, jadi kirim ulang setelah PC itu menyala.

**Supaya bisa dipakai dari luar sekolah:** minta pengelola jaringan meneruskan port TCP 8790 dari IP publik sekolah ke IP server relay, port 8790. Setelah itu ganti alamat relay menjadi `http://<IP-publik>:8790`, lalu buat ulang `kontrol-internet.json` untuk PC lab yang juga harus menghubungi relay lewat internet. Koneksi masih HTTP biasa. Perintah tetap aman dari pemalsuan, tetapi token dan status PC bisa terbaca di jalur internet. Pasang HTTPS bila relay akan dibuka ke publik untuk waktu lama.

Log agen di PC lab: `C:\ProgramData\LabControl\agen-internet.log`. Kode relay dan pemasangnya ada di folder `relay\`. Untuk memperbarui relay, jalankan lagi perintah pasang di atas. Token lama tidak berubah, jadi PC lab tidak perlu dipasang ulang. Bila relay pindah ke server baru, token ikut baru: isi ulang di Admin, buat ulang `kontrol-internet.json`, lalu pasang ulang di tiap PC lab.

## Bila PC tidak menyala

- Pastikan Wake-on-LAN di BIOS aktif dan ErP mati.
- Pastikan kabel LAN terpasang dan lampu port LAN tetap menyala saat PC mati.
- Matikan PC lewat **Shut down** biasa. PC yang dicabut listriknya tidak bisa dinyalakan lewat jaringan.
- Bila server dan PC lab berada di jaringan berbeda (VLAN lain), sinyal tidak sampai. Pindahkan server ke jaringan yang sama atau minta pengelola jaringan mengatur *directed broadcast*.
