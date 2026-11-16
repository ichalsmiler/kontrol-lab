# Dijalankan sekali di tiap PC lab sebagai Administrator: (opsional) ganti nama PC, siapkan Wake-on-LAN,
# izinkan ping dari LAN, lalu simpan data PC (nama, IP, MAC, broadcast) untuk daftar di komputer server.
param(
    [string]$NamaBaru='',
    [Parameter(Mandatory=$true)][string]$Folder
)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Jalankan sebagai Administrator.'}

# 1. Nama PC
$nameKey='HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName'
$name=$NamaBaru.Trim()
if($name){
    if($name -notmatch '^(?![0-9]+$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$'){throw 'Nama PC harus 1-15 karakter, hanya huruf, angka, atau minus, dan tidak boleh angka semua.'}
    if((Get-ItemProperty -Path $nameKey).ComputerName -ine $name){
        if((Get-CimInstance Win32_ComputerSystem).PartOfDomain){throw 'PC anggota domain; ganti nama lewat pengelola domain.'}
        Rename-Computer -NewName $name
        if((Get-ItemProperty -Path $nameKey).ComputerName -ine $name){throw 'Nama PC gagal diganti.'}
        Write-Output "Nama PC menjadi $name setelah restart."
    }
}
# Nama yang dicatat adalah nama setelah restart, jadi data tetap benar walau belum restart.
$name=(Get-ItemProperty -Path $nameKey).ComputerName

# 2. Adaptor LAN kabel yang aktif dan punya IPv4
$candidates=@(Get-NetAdapter -Physical | Where-Object {$_.Status -eq 'Up' -and $_.PhysicalMediaType -eq '802.3'} | ForEach-Object {
    $ip=Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {$_.IPAddress -notlike '169.254.*'} | Select-Object -First 1
    if($ip){[pscustomobject]@{Adapter=$_;IP=$ip}}
})
if($candidates.Count -eq 0){throw 'Tidak ada adaptor LAN kabel yang aktif dengan IPv4. Wake-on-LAN butuh kabel LAN, bukan Wi-Fi.'}
if($candidates.Count -gt 1){
    $withGateway=@($candidates | Where-Object {Get-NetRoute -InterfaceIndex $_.Adapter.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue})
    if($withGateway.Count -ne 1){throw "Ada $($candidates.Count) adaptor LAN aktif; cabut yang tidak dipakai lalu ulangi."}
    $candidates=$withGateway
}
$adapter=$candidates[0].Adapter
$ipText=$candidates[0].IP.IPAddress
$prefix=[int]$candidates[0].IP.PrefixLength
$ipBytes=([Net.IPAddress]::Parse($ipText)).GetAddressBytes()
[Array]::Reverse($ipBytes)
$ipValue=[BitConverter]::ToUInt32($ipBytes,0)
$hostMask=if($prefix -ge 32){[uint32]0}else{[uint32]([math]::Pow(2,32-$prefix)-1)}
$bcBytes=[BitConverter]::GetBytes([uint32]($ipValue -bor $hostMask))
[Array]::Reverse($bcBytes)
$broadcast=([Net.IPAddress]$bcBytes).ToString()
$mac=$adapter.MacAddress -replace '-',':'

# 3. Wake-on-LAN di Windows. Pengaturan BIOS/UEFI tetap harus diaktifkan manual.
$wolOk=$true
try{Set-NetAdapterPowerManagement -Name $adapter.Name -WakeOnMagicPacket Enabled -ErrorAction Stop}catch{$wolOk=$false;Write-Warning "Driver tidak menerima pengaturan Wake on Magic Packet: $($_.Exception.Message)"}
# Kata kunci ini standar Microsoft (*WakeOnMagicPacket), Realtek (S5WakeOnLan), dan Intel (EnablePME); nilai 1 berarti aktif.
$keywords=@(Get-NetAdapterAdvancedProperty -Name $adapter.Name -AllProperties -ErrorAction SilentlyContinue | Where-Object {$_.RegistryKeyword -in '*WakeOnMagicPacket','S5WakeOnLan','EnablePME'})
foreach($k in $keywords){
    try{Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $k.RegistryKeyword -RegistryValue 1 -NoRestart -ErrorAction Stop}catch{$wolOk=$false;Write-Warning "$($k.RegistryKeyword) tidak bisa diaktifkan: $($_.Exception.Message)"}
}
$powercfg=Join-Path $env:WINDIR 'System32\powercfg.exe'
# Mode Stop membuat pesan stderr powercfg menjadi error palsu di PowerShell 5.1.
$ErrorActionPreference='Continue'
& $powercfg /deviceenablewake $adapter.InterfaceDescription 2>$null | Out-Null
$code=$LASTEXITCODE
$ErrorActionPreference='Stop'
if($code -ne 0){Write-Warning 'Izin "perangkat ini boleh membangunkan PC" tidak bisa diatur; periksa di Device Manager.'}
# Fast Startup membuat PC tidak benar-benar mati (S5), sehingga WOL sering gagal.
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -Value 0 -Type DWord
Write-Output "Wake-on-LAN Windows disiapkan pada $($adapter.Name) ($mac). Fast Startup dimatikan."

# 4. Izinkan ping dari LAN agar server bisa mengecek PC sudah menyala.
Remove-NetFirewallRule -Name 'KontrolLab-Ping' -ErrorAction SilentlyContinue
New-NetFirewallRule -Name 'KontrolLab-Ping' -DisplayName 'Kontrol Lab - ping dari LAN' -Direction Inbound -Protocol ICMPv4 -IcmpType 8 -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null

# 5. Simpan data PC. File lama dengan MAC yang sama (misalnya sebelum ganti nama) dihapus agar tidak dobel.
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
foreach($old in Get-ChildItem -LiteralPath $Folder -Filter 'PC-*.json' -File){
    try{if(((Get-Content -LiteralPath $old.FullName -Raw | ConvertFrom-Json).mac) -ieq $mac){Remove-Item -LiteralPath $old.FullName}}catch{}
}
$file=Join-Path $Folder "PC-$name.json"
[ordered]@{pc=$name;ip=$ipText;mac=$mac;broadcast=$broadcast;dicatat=(Get-Date -Format 'yyyy-MM-dd HH:mm')} | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding UTF8
Write-Output "Data PC disimpan: $file (IP $ipText, broadcast $broadcast)."
if(!$wolOk){Write-Warning 'Sebagian pengaturan WOL gagal; uji nyalakan PC ini dari server sebelum dipakai.'}
