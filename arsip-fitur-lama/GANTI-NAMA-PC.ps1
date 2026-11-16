param([Parameter(Mandatory=$true)][string]$NewName)
$ErrorActionPreference='Stop'
$name=$NewName.Trim()
if($name -notmatch '^(?![0-9]+$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$'){throw 'Nama PC harus 1-15 karakter, hanya huruf, angka, atau minus, tidak diawali/diakhiri minus, dan tidak boleh angka semua.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$admin=New-Object Security.Principal.WindowsPrincipal($identity)
if(!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Jalankan sebagai Administrator.'}
if((Get-CimInstance Win32_ComputerSystem).PartOfDomain){throw 'PC anggota domain; ganti nama lewat pengelola domain, bukan skrip ini.'}
$key='HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName'
if((Get-ItemProperty -Path $key).ComputerName -ieq $name){Write-Output "Nama PC sudah $name; tidak ada perubahan.";return}
Rename-Computer -NewName $name
$saved=(Get-ItemProperty -Path $key).ComputerName
if($saved -ine $name){throw "Nama PC belum berubah; tercatat $saved."}
if($identity.Name.Split('\')[-1] -ieq $name){Write-Warning 'Nama PC baru sama dengan nama akun ini. Pemasangan SSH akan menolaknya; pilih nama lain atau rename akun.'}
Write-Output "Nama PC menjadi $name setelah restart. Windows tidak di-restart oleh skrip ini."
