# Panduan kontrol PC lab Windows melalui SSH dan Wake-on-LAN

Paket umum untuk satu sekolah. Berlaku untuk PC Windows 10/11; bukan Chromebook/ChromeOS. Gunakan hanya pada perangkat yang dikelola sekolah. Uji pada satu PC terlebih dahulu, di luar sesi ujian. Jangan menjalankan shutdown saat ada pekerjaan yang belum disimpan.

Jalankan contoh perintah dari folder paket. Jika Windows menolak `.ps1` karena Execution Policy, buka proses PowerShell khusus dengan `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass`, lalu jalankan perintah di jendela tersebut. Ini tidak mengubah kebijakan eksekusi secara permanen.

## 1. Prinsip dan persiapan

- PC pengendali sebaiknya berada pada LAN/VLAN yang dapat menjangkau klien. SSH perlu jalur TCP/22 dan izin firewall. WOL mengirim *magic packet* ke MAC adaptor LAN; pada VLAN lain dibutuhkan relay/directed broadcast yang diizinkan router. Tailscale/SSH dapat melewati internet, tetapi WOL tidak otomatis menyeberang VPN.
- WOL dari kondisi mati perlu dukungan motherboard, BIOS/UEFI, adaptor LAN, dan suplai daya siaga. Aktifkan opsi seperti Wake on LAN/PME, *Wake on Magic Packet*, dan izin perangkat membangunkan PC; uji secara fisik per model. Wi-Fi umumnya tidak dapat diasumsikan mendukung WOL. Matikan Fast Startup bila S5 WOL gagal.
- Alamat IP klien sebaiknya stabil melalui reservasi DHCP. Catat MAC adaptor LAN fisik, bukan adaptor virtual. Siapkan akun lokal admin di setiap PC. Skrip tidak membuat akun admin atau mengubah kata sandinya.
- Nama PC **tidak wajib seragam**. SSH menggunakan IP dan username, WOL menggunakan MAC. Namun nama unik seperti `L1PC01`, `L1PC02`, `L2PC01` memudahkan inventaris dan pilihan per ruang. Nama pengguna boleh berbeda. Jangan samakan nama pengguna dengan nama PC pada OpenSSH Windows lama; skrip akan menolak kombinasi itu.

## 2. Siapkan key pada PC pengendali

Buka Windows PowerShell biasa pada PC pengendali. Jangan menimpa key yang sudah ada.

```powershell
$key = Join-Path $env:USERPROFILE '.ssh\id_ed25519'
if (!(Test-Path -LiteralPath $key)) { ssh-keygen.exe -t ed25519 -f $key }
Get-Content -LiteralPath ($key + '.pub')
```

**Hanya isi berkas `.pub`** yang dipasang ke PC lab. Jangan menyalin private key (`id_ed25519`) ke PC klien atau membagikannya. Cari IPv4 PC pengendali dengan `ipconfig`; bila IP berubah, aturan sumber SSH harus diperbarui dengan menjalankan ulang pemasang pada klien.

## 3. Pasang SSH pada setiap PC lab

PC hasil cloning biasanya bernama sama. Ganti namanya lebih dulu dan restart, lewat tab **Pasang di PC ini** pada `KontrolLab.exe` (cukup isi nomornya, misalnya `1` menjadi `SMADA-PC-01`) atau `& '.\GANTI-NAMA-PC.ps1' -NewName 'SMADA-PC-01'` sebagai Administrator; catatan dan inventaris menolak nama yang sama.

Salin `SIAPKAN-SSH-CLIENT.ps1` ke PC klien. Buka Windows PowerShell 64-bit **Run as administrator**, lalu sesuaikan path key publik dan IP sumber berikut:

```powershell
$pub = 'ssh-ed25519 AAAA... komentar-key-pc-pengendali'
& 'C:\LokasiPaket\SIAPKAN-SSH-CLIENT.ps1' `
  -ControllerPublicKey $pub `
  -AllowedSource @('192.168.10.10') `
  -OutputFormat Json
```

Ganti contoh key/IP. Untuk dua pengendali, gunakan `-HomePublicKey 'ssh-ed25519 AAAA...'` dan masukkan dua IP/CIDR sumber ke `-AllowedSource`. Skrip memasang OpenSSH Server jika perlu (mungkin perlu Windows Update/FoD), mengaktifkan `sshd`, membatasi firewall TCP/22 ke sumber, memasang public key pada akun admin, memperketat ACL, lalu menyimpan `AKSES-SSH-NAMAPC.json` di Desktop. Ada backup pengaturan SSH di `C:\ProgramData\ssh`. Jika pemasangan komponen meminta restart, restart manual dan jalankan ulang skrip.

Rename akun **opsional**, bukan syarat koneksi. Bila nama akun sama dengan nama PC, gunakan `-RenameAccount -NewUserName NAMA_BARU`. Untuk pola PC `SMADA-PC-01` menjadi user `SMADA01` atau `L1PC01` menjadi `L101`, gunakan `-RenameAccount -AutomaticUserName`. Rename mempertahankan SID/password/profil, tetapi task dan autologin yang menyebut nama lama perlu diperiksa; folder `C:\Users` tidak ikut berganti. Jangan rename saat ujian.

## 4. Gabungkan catatan dan verifikasi host

Salin semua `AKSES-SSH-*.json` ke satu folder di PC pengendali. Pilih prefix IP LAN dan alamat broadcast subnet yang benar (contoh `192.168.10.255` untuk `/24`):

```powershell
& '.\BUAT-INVENTARIS.ps1' -CatatanFolder 'D:\Catatan-Lab' `
  -OutputCsv 'D:\Inventaris-Lab.csv' -AddressPrefix '192.168.10.' `
  -Broadcast '192.168.10.255'
```

Periksa CSV: ruang, nama PC, IP, user, MAC LAN, broadcast, dan fingerprint. Untuk lebih dari satu subnet, sunting broadcast tiap baris. Catatan yang tidak cocok atau ambigu dilewati, bukan ditebak. Verifikasi fingerprint terhadap catatan yang diambil langsung dari masing-masing PC, lalu jalankan:

```powershell
& '.\PASANG-KNOWN-HOSTS.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv'
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action List
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action Status -PC 'L1PC01' -Apply
```

`PASANG-KNOWN-HOSTS.ps1` mencocokkan fingerprint dan memasang host key yang tercatat; jangan lanjut jika ada mismatch. SSH tidak memakai `StrictHostKeyChecking=no`.

## 5. Nyalakan dan matikan

Tanpa `-Apply`, perintah hanya simulasi. Contoh:

```powershell
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action Wake -PC 'L1PC01' -Apply
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action Wake -Room 'Lab 1' -Apply
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action Wake -All -Apply
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action Shutdown -PC 'L1PC01' -Apply
& '.\KENDALI-LAB.ps1' -InventoryCsv 'D:\Inventaris-Lab.csv' -Action Shutdown -Room 'Lab 2' -Apply
```

Shutdown meminta konfirmasi tambahan dan **tidak** memakai `/f` agar pekerjaan pengguna tidak dipaksa tertutup. Lakukan `Status` setelah WOL; pesan “paket terkirim” belum membuktikan PC berhasil menyala. Jika mesin mati total, WOL tidak mungkin menghidupkannya. Pastikan suplai listrik aktif.

## 6. Exambro: jalankan otomatis sampai tombol RUN

Fitur ini khusus Exambro Windows yang diuji. Paket `EXAMBRO-UI.ps1` hanya mengenali dialog peringatan internet dan tombol RUN pada jendela spesifikasi; tidak mengklik halaman login/ujian. Perubahan versi atau tata letak bisa membuatnya berhenti aman. Periksa kebijakan penyelenggara ujian sebelum mengaktifkan otomatisasi.

Salin tiga berkas `EXAMBRO-STARTUP.ps1`, `EXAMBRO-UI.ps1`, `PASANG-EXAMBRO-AUTO.ps1` ke PC. Jalankan pemasang dari **akun Windows yang akan menjalankan ujian**, melalui PowerShell Administrator, di luar sesi ujian:

```powershell
& '.\PASANG-EXAMBRO-AUTO.ps1' -Target 'C:\Users\NamaUser\Desktop\Exambro\ExamBrowser.exe'
```

Sesuaikan path `ExamBrowser.exe` sebenarnya. Pemasang menyalin helper ke `C:\ProgramData\LabControl`, mencatat hash dua EXE, dan membuat Scheduled Task dengan hak tertinggi untuk akun yang sedang login. Ini biasanya menghindari prompt UAC khusus task tersebut **tanpa mematikan UAC seluruh Windows**. Exambro mulai **sesudah akun Windows login**; WOL hanya menyalakan mesin dan tidak melakukan login otomatis. Versi Exambro baru perlu diverifikasi lalu task dipasang ulang. Uji satu PC sampai halaman login; jangan asumsikan semua varian Exambro cocok. Hapus task dengan `-Remove`.

## 7. Opsi terakhir: mematikan UAC

Jika task terjadwal tidak cocok dan sekolah memutuskan mematikan UAC, gunakan skrip terpisah `ATUR-UAC-OPSIONAL.ps1` sebagai Administrator:

```powershell
& '.\ATUR-UAC-OPSIONAL.ps1' -Action Status
& '.\ATUR-UAC-OPSIONAL.ps1' -Action Disable
```

Skrip meminta teks konfirmasi, mencadangkan nilai awal, dan **tidak** me-restart PC. Restart manual diperlukan. **Peringatan: UAC nonaktif membuat seluruh PC lebih rentan terhadap malware/perubahan tanpa persetujuan, dan dapat mengganggu sebagian aplikasi Windows.** Gunakan hanya pada PC lab yang dikelola, bukan PC pribadi, dan pertimbangkan mengaktifkannya kembali setelah ujian:

```powershell
& '.\ATUR-UAC-OPSIONAL.ps1' -Action Restore
```

## 8. Pemecahan masalah cepat

- SSH timeout: cek PC menyala, IP aktual, TCP/22, service `Get-Service sshd`, dan firewall dari IP pengendali.
- SSH `Permission denied`: cek username lokal aktual, public key, aturan sumber `from=`, ACL `administrators_authorized_keys`, dan fingerprint host.
- WOL terkirim tetapi tidak menyala: cek MAC adaptor kabel, broadcast/VLAN, BIOS PME/WOL, power-management adaptor, Fast Startup, dan daya siaga; uji satu model dahulu.
- Exambro berhenti di RUN: cek hash/versi, akun desktop sedang login, task berjalan dalam sesi interaktif, dan file log `C:\ProgramData\Exambro-Startup-*.json`. Jangan mengotomatisasi klik dengan koordinat layar atau klik UAC secara membabi buta.

Paket ini tidak berisi private key, password, atau inventaris sekolah tertentu. Simpan CSV inventaris dan catatan SSH di tempat terbatas karena memuat identitas jaringan.
