param([string]$Target,[switch]$Remove)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Jalankan sebagai Administrator dalam akun desktop yang menjalankan Exambro.'}
$user=$identity.Name
$taskName='Lab-Exambro-Auto-Run'
if($Remove){Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue;Write-Output 'Task Exambro otomatis dihapus.';return}
if(!$Target){throw 'Isi -Target dengan path ExamBrowser.exe.'}
$targetPath=(Resolve-Path -LiteralPath $Target).Path
$runtime=Join-Path (Split-Path -LiteralPath $targetPath) 'Application\runningexam.exe'
if(!(Test-Path -LiteralPath $runtime)){throw "runningexam.exe tidak ditemukan: $runtime"}
$dir=Join-Path $env:ProgramData 'LabControl'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
foreach($name in 'EXAMBRO-STARTUP.ps1','EXAMBRO-UI.ps1'){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $dir $name) -Force}
$hash=(Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
$runtimeHash=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
$script=Join-Path $dir 'EXAMBRO-STARTUP.ps1'
$argument='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$script+'" -Target "'+$targetPath+'" -ExpectedTargetHash '+$hash+' -ExpectedRuntimeHash '+$runtimeHash
$action=New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument $argument
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$taskPrincipal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
# Bawaan task menolak berjalan saat memakai baterai; laptop lab akan gagal diam-diam.
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null
Write-Output "Task terpasang untuk $user. Exambro dibuka setelah login Windows; WOL saja tidak melakukan login. Versi baru Exambro perlu verifikasi dan pemasangan ulang task."
Write-Output "Target SHA256=$hash Runtime SHA256=$runtimeHash"
