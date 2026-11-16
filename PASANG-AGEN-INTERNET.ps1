param([string]$Konfig,[switch]$Remove)
# Memasang agen Kontrol Lab via internet di PC ini: berjalan otomatis setiap akun ini login.
# -Konfig: file kontrol-internet.json buatan KontrolLab-Admin.exe (berisi alamat relay, token, kunci publik Admin).
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Jalankan sebagai Administrator dalam akun desktop yang dipakai ujian.'}
$user=$identity.Name
$taskName='Lab-Agen-Internet'
$dir=Join-Path $env:ProgramData 'LabControl'
if($Remove){
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {$_.CommandLine -like '*AGEN-INTERNET.ps1*'} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    Remove-Item -LiteralPath (Join-Path $dir 'internet.json') -Force -ErrorAction SilentlyContinue
    Write-Output 'Agen internet dihapus dari PC ini.'
    return
}
if(!$Konfig -or !(Test-Path -LiteralPath $Konfig)){throw 'File kontrol-internet.json tidak ditemukan. Buat dulu dari KontrolLab-Admin.exe > Kontrol via Internet.'}
$cfg=Get-Content -LiteralPath $Konfig -Raw -Encoding UTF8 | ConvertFrom-Json
if(!$cfg.relay -or !$cfg.token -or !($cfg.kunciPublik -like '<RSAKeyValue>*')){throw 'Isi kontrol-internet.json tidak lengkap.'}
if($cfg.kunciPublik -like '*<D>*'){throw 'kontrol-internet.json berisi kunci RAHASIA Admin. Buat ulang file dari Admin.'}
try {
    $ping=Invoke-RestMethod -Uri ($cfg.relay.TrimEnd('/')+'/api/ping') -TimeoutSec 10 -UseBasicParsing
    if($ping.relay -ne 'kontrol-lab'){throw 'jawaban bukan dari relay Kontrol Lab'}
    Write-Output "Relay terjangkau: $($cfg.relay)"
} catch {Write-Output "PERINGATAN: relay belum terjangkau dari PC ini ($($_.Exception.Message)). Agen tetap dipasang dan akan terus mencoba."}

New-Item -ItemType Directory -Path $dir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AGEN-INTERNET.ps1') -Destination (Join-Path $dir 'AGEN-INTERNET.ps1') -Force
Copy-Item -LiteralPath $Konfig -Destination (Join-Path $dir 'internet.json') -Force
# Pengguna biasa tidak boleh mengganti skrip agen atau kunci publik (agen berjalan dengan hak tertinggi).
foreach($f in 'AGEN-INTERNET.ps1','internet.json'){
    $p=Join-Path $dir $f
    & icacls.exe $p /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-32-545:R' | Out-Null
}
$script=Join-Path $dir 'AGEN-INTERNET.ps1'
$action=New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$script+'"')
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$taskPrincipal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Output "Agen internet terpasang untuk $user dan sudah berjalan. PC ini muncul di Admin sebagai $($env:COMPUTERNAME.ToUpperInvariant())."
if(!(Get-ScheduledTask -TaskName 'Lab-Exambro-Auto-Run' -ErrorAction SilentlyContinue)){Write-Output 'PERINGATAN: Exambro otomatis belum dipasang; perintah Jalankan Exambro akan gagal. Isi ExamBrowser.exe lalu pasang lagi.'}
