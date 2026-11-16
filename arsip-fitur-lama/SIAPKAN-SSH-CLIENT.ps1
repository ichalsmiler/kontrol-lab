# Jalankan pada PC Windows tujuan melalui Windows PowerShell 64-bit sebagai Administrator.
# Hanya public key yang diterima. Private key tetap berada pada PC pengendali.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ControllerPublicKey,

    [string] $HomePublicKey = '',

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]] $AllowedSource,

    [string] $TargetUser = '',
    [switch] $RenameAccount,
    [switch] $AutomaticUserName,
    [string] $NewUserName = '',

    [ValidateSet('Json', 'Text')]
    [string] $OutputFormat = 'Json',
    [string] $ReportPath = ''
)

$ErrorActionPreference = 'Stop'

function Assert-PublicKey {
    param([string] $Value, [string] $Label)

    $value = $Value.Trim()
    $match = [regex]::Match($value, '^ssh-ed25519\s+([A-Za-z0-9+/]+={0,2})(?:\s+([^\r\n]*))?$')
    if (!$match.Success) {
        throw "$Label harus satu baris public key ssh-ed25519 tanpa opsi authorized_keys."
    }
    try { $bytes = [Convert]::FromBase64String($match.Groups[1].Value) }
    catch { throw "$Label mengandung data Base64 yang tidak valid." }
    if ($bytes.Length -ne 51 -or
        $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or $bytes[2] -ne 0 -or $bytes[3] -ne 11 -or
        [Text.Encoding]::ASCII.GetString($bytes, 4, 11) -cne 'ssh-ed25519' -or
        $bytes[15] -ne 0 -or $bytes[16] -ne 0 -or $bytes[17] -ne 0 -or $bytes[18] -ne 32) {
        throw "$Label bukan public key Ed25519 OpenSSH yang valid."
    }
    return $value
}

function Assert-Source {
    param([string] $Value)

    $value = $Value.Trim()
    $match = [regex]::Match($value, '^(\d{1,3}(?:\.\d{1,3}){3})(?:/(\d{1,2}))?$')
    if (!$match.Success) { throw "Sumber '$value' harus alamat IPv4 atau CIDR IPv4." }
    $parsed = $null
    if (![Net.IPAddress]::TryParse($match.Groups[1].Value, [ref] $parsed) -or
        $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw "Sumber '$value' bukan alamat IPv4 yang valid."
    }
    $first = $parsed.GetAddressBytes()[0]
    if ($first -eq 0 -or $first -eq 127 -or $first -ge 224) {
        throw "Sumber '$value' tidak boleh alamat loopback, multicast, atau alamat umum."
    }
    if ($match.Groups[2].Success) {
        $prefix = [int] $match.Groups[2].Value
        if ($prefix -lt 20 -or $prefix -gt 32) {
            throw "CIDR '$value' terlalu luas atau tidak valid; gunakan /20 sampai /32."
        }
        return "$($parsed.IPAddressToString)/$prefix"
    }
    return $parsed.IPAddressToString
}

function Get-ActiveIpv4Adapters {
    $items = @()
    foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($adapter.OperationalStatus -ne [Net.NetworkInformation.OperationalStatus]::Up) { continue }
        $physical = $adapter.GetPhysicalAddress().GetAddressBytes()
        if ($physical.Length -ne 6) { continue }
        $mac = ($physical | ForEach-Object { $_.ToString('X2') }) -join ':'
        foreach ($address in $adapter.GetIPProperties().UnicastAddresses) {
            if ($address.Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
                [Net.IPAddress]::IsLoopback($address.Address)) { continue }
            $items += [pscustomobject]@{
                interface = $adapter.Name
                ip_address = $address.Address.IPAddressToString
                mac_address = $mac
            }
        }
    }
    return @($items)
}

function Test-Port22AllowRule {
    param($Rule)

    $port = $Rule | Get-NetFirewallPortFilter
    $protocol = [string] $port.Protocol
    if ($protocol -notin @('TCP', '6', 'Any')) { return $false }
    $application = $Rule | Get-NetFirewallApplicationFilter
    $program = [string] $application.Program
    if ($program -and $program -ne 'Any' -and
        [IO.Path]::GetFileName($program.Trim('"')) -ine 'sshd.exe') { return $false }
    $service = $Rule | Get-NetFirewallServiceFilter
    if ($service.Service -and [string] $service.Service -notin @('Any', 'sshd')) { return $false }
    foreach ($value in @($port.LocalPort)) {
        foreach ($part in ([string] $value -split ',')) {
            $part = $part.Trim()
            if ($part -in @('22', 'Any')) { return $true }
            if ($part -match '^(\d+)-(\d+)$' -and [int] $Matches[1] -le 22 -and [int] $Matches[2] -ge 22) {
                return $true
            }
        }
    }
    return $false
}

function Get-ReportText {
    param($Report)
    $adapterText = @($Report.adapters | ForEach-Object {
        "  $($_.ip_address) | $($_.mac_address) | $($_.interface)"
    }) -join "`r`n"
    return @"
CATATAN AKSES SSH PC LAB
Waktu                 : $($Report.observed_at)
Nama PC               : $($Report.pc_name)
Username              : $($Report.username)
SID                   : $($Report.sid)
SSH                   : $($Report.ssh_status), port $($Report.ssh_port)
Fingerprint ED25519   : $($Report.host_fingerprint_ed25519)
Host public key       : $($Report.host_public_key_ed25519)
Alamat sumber diizinkan: $($Report.allowed_sources -join ', ')
Antarmuka aktif:
$adapterText
Backup                : $($Report.backup_path)
Username sebelumnya   : $($Report.previous_username)
Rename akun           : $($Report.account_renamed)
Task nama lama        : $($Report.old_username_task_count)
Autologin nama lama   : $($Report.old_username_autologin)

Fingerprint perlu dicocokkan dari PC pengendali sebelum menerima host SSH.
Skrip ini tidak mengubah nama PC, password, folder profil, atau me-restart Windows.
Jika akun di-rename, periksa task dan autologin yang masih memakai nama lama.
"@
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Jalankan Windows PowerShell dengan Run as administrator.'
}
if (![Environment]::Is64BitProcess) { throw 'Gunakan Windows PowerShell 64-bit.' }
if ($AutomaticUserName -and !$RenameAccount) { throw '-AutomaticUserName memerlukan -RenameAccount.' }
if ($NewUserName -and !$RenameAccount) { throw '-NewUserName memerlukan -RenameAccount.' }
if ($AutomaticUserName -and $NewUserName) { throw 'Pilih -AutomaticUserName atau -NewUserName.' }
if ($RenameAccount -and !$AutomaticUserName -and !$NewUserName) {
    throw '-RenameAccount memerlukan -AutomaticUserName atau -NewUserName.'
}

$controllerKey = Assert-PublicKey $ControllerPublicKey 'ControllerPublicKey'
$homeKey = if (![string]::IsNullOrWhiteSpace($HomePublicKey)) { Assert-PublicKey $HomePublicKey 'HomePublicKey' } else { '' }
$sources = @($AllowedSource | ForEach-Object { Assert-Source $_ } | Select-Object -Unique)
if (!$sources.Count) { throw 'Setidaknya satu -AllowedSource diperlukan.' }
$from = $sources -join ','

if (!$TargetUser.Trim()) {
    $currentLocal = Get-LocalUser -SID $identity.User -ErrorAction SilentlyContinue
    if (!$currentLocal) { throw 'Akun pelaksana bukan akun lokal; isi -TargetUser.' }
    $TargetUser = $currentLocal.Name
}
$target = Get-LocalUser -Name $TargetUser
if (!$target.Enabled) { throw 'Akun tujuan sedang nonaktif; skrip tidak mengaktifkannya.' }
if ($target.SID.Value -match '-(500|501|503|504)$') { throw 'Akun bawaan Windows tidak diproses.' }
$adminGroup = Get-LocalGroup -SID 'S-1-5-32-544'
$isAdmin = @(Get-LocalGroupMember -Group $adminGroup.Name | Where-Object {
    $_.SID.Value -eq $target.SID.Value
}).Count -gt 0
if (!$isAdmin) { throw 'Akun tujuan harus sudah anggota Administrators; skrip tidak menaikkan hak akun.' }

$oldName = $target.Name
$desiredName = $oldName
if ($RenameAccount) {
    if ($AutomaticUserName) {
        if ($env:COMPUTERNAME -match '^SMADA-PC-([0-9]{2,3})$') {
            $desiredName = 'SMADA' + $Matches[1]
        } elseif ($env:COMPUTERNAME -match '^L([1-9][0-9]?)PC([0-9]{2})$') {
            $desiredName = 'L' + $Matches[1] + $Matches[2]
        } else {
            throw 'Nama PC harus berpola SMADA-PC-01 atau L1PC01 agar nama akun otomatis dapat dibuat.'
        }
    } else {
        $desiredName = $NewUserName.Trim()
    }
}
if ($desiredName.Length -gt 20 -or $desiredName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw 'Nama akun maksimal 20 karakter dan hanya memakai huruf, angka, titik, minus, atau underscore.'
}
if ($desiredName -ieq $env:COMPUTERNAME) {
    throw 'Username sama dengan nama PC; OpenSSH Windows lama dapat gagal. Pakai -RenameAccount.'
}
$collision = Get-LocalUser -Name $desiredName -ErrorAction SilentlyContinue
if ($collision -and $collision.SID.Value -ne $target.SID.Value) { throw 'Nama akun baru sudah dipakai akun lain.' }
$renameNeeded = $oldName -cne $desiredName

$oldQualified = "$env:COMPUTERNAME\$oldName"
$taskRefs = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
    $_.Principal.UserId -ieq $oldQualified -or $_.Principal.UserId -ieq $oldName
} | Select-Object TaskPath, TaskName)
$logon = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$autoRef = $logon.AutoAdminLogon -eq '1' -and $logon.DefaultUserName -in @($oldName, $oldQualified)
if ($renameNeeded) {
    Write-Warning 'Rename mempertahankan SID, password, dan profil. Folder C:\Users tidak berubah.'
    if ($taskRefs.Count -or $autoRef) {
        Write-Warning 'Ada task/autologin memakai nama lama; periksa sesudah rename.'
    }
    if ((Read-Host "Ketik YA untuk rename '$oldName' menjadi '$desiredName'") -cne 'YA') {
        throw 'Rename dibatalkan sebelum perubahan.'
    }
}

# Pengumpulan identitas dilakukan sebelum rename. CIM dapat gagal memetakan token lama setelah rename.
$adapters = @(Get-ActiveIpv4Adapters)
$desktop = [Environment]::GetFolderPath('Desktop')

$service = Get-Service -Name sshd -ErrorAction SilentlyContinue
if (!$service) {
    $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
    if ($capability.State -ne 'Installed') {
        Write-Verbose 'Memasang OpenSSH Server; unduhan Windows dapat memerlukan waktu.'
        $installed = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
        if ($installed.RestartNeeded) {
            throw 'OpenSSH meminta restart. Restart manual lalu jalankan ulang; kunci dan rename belum diterapkan.'
        }
    }
    $service = Get-Service -Name sshd
}

$sshd = Join-Path $env:WINDIR 'System32\OpenSSH\sshd.exe'
$keygen = Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe'
$serviceInfo = Get-CimInstance Win32_Service -Filter "Name='sshd'"
if ($serviceInfo.PathName.Trim('"') -ine $sshd -or
    $serviceInfo.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) {
    throw 'Layanan sshd memakai executable atau identitas kustom; perlu pemeriksaan manual.'
}
Set-Service -Name sshd -StartupType Automatic
# Pada pemasangan baru, ProgramData\ssh dan sshd_config baru dibuat saat layanan pertama kali berjalan.
if (!(Test-Path -LiteralPath (Join-Path $env:ProgramData 'ssh\sshd_config'))) {
    Start-Service -Name sshd
    Stop-Service -Name sshd
    if (!(Test-Path -LiteralPath (Join-Path $env:ProgramData 'ssh\sshd_config'))) {
        throw 'sshd_config belum dibuat oleh layanan sshd; periksa pemasangan OpenSSH lalu jalankan ulang.'
    }
}
# Siapkan host key yang belum ada. -A tidak mengganti host key yang sudah ada.
& $keygen -A
if ($LASTEXITCODE -ne 0) { throw 'Gagal menyiapkan host key OpenSSH.' }
& $sshd -t
if ($LASTEXITCODE -ne 0) { throw 'sshd_config gagal validasi.' }

$sshDir = Join-Path $env:ProgramData 'ssh'
$configPath = Join-Path $sshDir 'sshd_config'
$configText = [IO.File]::ReadAllText($configPath)
if ($configText -notmatch '(?im)^\s*Match\s+Group\s+administrators\s*$' -or
    $configText -notmatch '(?im)^\s*AuthorizedKeysFile\s+__PROGRAMDATA__/ssh/administrators_authorized_keys\s*$' -or
    $configText -match '(?im)^\s*(AllowUsers|AllowGroups|DenyUsers|DenyGroups)\s+' -or
    $configText -match '(?im)^\s*PubkeyAuthentication\s+no\s*$' -or
    $configText -match '(?im)^\s*Port\s+(?!22(?:\s|$))\d+') {
    throw 'Konfigurasi sshd tidak sesuai jalur kunci admin standar/port 22; tidak ditimpa otomatis.'
}

# Pengaturan sumber hanya bermakna bila Windows Firewall aktif pada profil yang digunakan.
$activeProfiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.NetworkCategory -eq 'DomainAuthenticated') { 'Domain' } else { [string] $_.NetworkCategory }
} | Select-Object -Unique)
foreach ($profileName in $activeProfiles) {
    $profile = Get-NetFirewallProfile -Name $profileName -ErrorAction SilentlyContinue
    if (!$profile) { throw "Profil Windows Firewall $profileName tidak dapat dibaca." }
    if (!$profile.Enabled) {
        throw "Windows Firewall profil $profileName nonaktif; pembatasan sumber SSH tidak bisa dijamin."
    }
    if ([string] $profile.DefaultInboundAction -eq 'Allow') {
        throw "Windows Firewall profil $profileName mengizinkan koneksi masuk secara umum."
    }
    if ($profile.AllowInboundRules -eq $false) {
        throw "Profil $profileName mengabaikan aturan koneksi masuk; SSH tidak akan dapat dijangkau."
    }
    if ($profile.AllowLocalFirewallRules -eq $false) {
        throw "Profil $profileName mengabaikan aturan firewall lokal; aturan SSH terbatas tidak akan berlaku."
    }
}

$ruleName = 'SPR-Lab-SSH-Controller'
$defaultRuleName = 'OpenSSH-Server-In-TCP'
$allowRules = @(Get-NetFirewallRule -Enabled True -Direction Inbound -Action Allow |
    Where-Object { Test-Port22AllowRule $_ })
foreach ($rule in $allowRules) {
    if ($rule.Name -in @($ruleName, $defaultRuleName)) { continue }
    $remote = @($rule | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress)
    $unexpected = @($remote | Where-Object { $_ -notin $sources })
    if ($unexpected.Count) {
        throw "Aturan firewall '$($rule.Name)' juga mengizinkan port 22 dari $($unexpected -join ', '). Periksa aturan itu sebelum melanjutkan."
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backup = Join-Path $sshDir ('backup-lab-control-' + $stamp)
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -LiteralPath $configPath -Destination (Join-Path $backup 'sshd_config')
$keyPath = Join-Path $sshDir 'administrators_authorized_keys'
if (Test-Path -LiteralPath $keyPath) {
    Copy-Item -LiteralPath $keyPath -Destination (Join-Path $backup 'administrators_authorized_keys')
    Get-Acl -LiteralPath $keyPath | Export-Clixml -LiteralPath (Join-Path $backup 'key-acl.xml')
} else {
    New-Item -ItemType File -Path $keyPath | Out-Null
}

$publicKeys = @($controllerKey)
if ($homeKey) { $publicKeys += $homeKey }
# Baris lama untuk key yang sama diganti agar sumber from= lama tidak tetap berlaku saat IP pengendali berubah.
$keyLines = @(Get-Content -LiteralPath $keyPath | Where-Object { $_.Trim() })
foreach ($publicKey in $publicKeys) {
    $blob = ($publicKey -split '\s+')[1]
    $keyLines = @($keyLines | Where-Object { ($_ -split '\s+') -cnotcontains $blob })
    $keyLines += 'from="' + $from + '" ' + $publicKey
}
Set-Content -LiteralPath $keyPath -Value $keyLines -Encoding ASCII

# OpenSSH Windows mensyaratkan ACL ketat untuk administrators_authorized_keys.
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
$adminSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$acl.SetOwner($adminSid)
foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
    $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')))
}
Set-Acl -LiteralPath $keyPath -AclObject $acl

# Batasi juga aturan bawaan OpenSSH yang biasanya dibuat dengan RemoteAddress=Any.
$defaultRule = Get-NetFirewallRule -Name $defaultRuleName -ErrorAction SilentlyContinue
if ($defaultRule) {
    $defaultRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $sources
}
$ourRule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if ($ourRule) {
    Set-NetFirewallRule -Name $ruleName -Enabled True -Direction Inbound -Action Allow -Profile Any
    $ourRule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort 22
    $ourRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $sources
} else {
    New-NetFirewallRule -Name $ruleName -DisplayName 'SPR SSH Pengendali Lab' `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 `
        -RemoteAddress $sources -Profile Any | Out-Null
}

if ($renameNeeded) { Rename-LocalUser -SID $target.SID -NewName $desiredName }
$actual = Get-LocalUser -SID $target.SID
if ((Get-Service -Name sshd).Status -eq 'Running') {
    Restart-Service -Name sshd
} else {
    Start-Service -Name sshd
}
if ((Get-Service -Name sshd).Status -ne 'Running') { throw 'Layanan sshd belum Running.' }
& $sshd -t
if ($LASTEXITCODE -ne 0) { throw 'sshd_config gagal validasi setelah pemasangan kunci.' }

$hostPublicKeyPath = Join-Path $sshDir 'ssh_host_ed25519_key.pub'
if (!(Test-Path -LiteralPath $hostPublicKeyPath)) { throw 'Host key ED25519 belum dibuat oleh OpenSSH.' }
# Jangan disalurkan ke Select-Object -First: proses dihentikan lebih awal dan exit code menjadi -1.
$fingerprintLine = @(& $keygen -lf $hostPublicKeyPath)[0]
$fingerprintMatch = [regex]::Match([string] $fingerprintLine, 'SHA256:[A-Za-z0-9+/]+')
if ($LASTEXITCODE -ne 0 -or !$fingerprintMatch.Success) {
    throw 'Fingerprint host ED25519 tidak dapat dibaca.'
}
$fingerprint = $fingerprintMatch.Value
$hostPublicKeyLine = (Get-Content -LiteralPath $hostPublicKeyPath -TotalCount 1).Trim()
$hostPublicKeyMatch = [regex]::Match($hostPublicKeyLine, '^ssh-ed25519\s+([A-Za-z0-9+/]+={0,2})(?:\s|$)')
if (!$hostPublicKeyMatch.Success) { throw 'Public host key ED25519 tidak valid.' }
$hostPublicKey = 'ssh-ed25519 ' + $hostPublicKeyMatch.Groups[1].Value

$report = [ordered]@{
    observed_at = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    pc_name = $env:COMPUTERNAME
    username = $actual.Name
    previous_username = $oldName
    sid = $actual.SID.Value
    adapters = $adapters
    ssh_status = (Get-Service -Name sshd).Status.ToString()
    ssh_port = 22
    host_fingerprint_ed25519 = $fingerprint
    host_public_key_ed25519 = $hostPublicKey
    allowed_sources = $sources
    account_renamed = [bool] $renameNeeded
    old_username_task_count = $taskRefs.Count
    old_username_autologin = [bool] $autoRef
    backup_path = $backup
}
$content = if ($OutputFormat -eq 'Json') {
    $report | ConvertTo-Json -Depth 5
} else {
    Get-ReportText $report
}
if (!$ReportPath.Trim()) {
    $directory = if ($desktop -and (Test-Path -LiteralPath $desktop)) { $desktop } else { $sshDir }
    $extension = if ($OutputFormat -eq 'Json') { 'json' } else { 'txt' }
    $ReportPath = Join-Path $directory "AKSES-SSH-$env:COMPUTERNAME.$extension"
}
Set-Content -LiteralPath $ReportPath -Value $content -Encoding UTF8
Write-Output $content
Write-Verbose "Catatan tersimpan: $ReportPath"
