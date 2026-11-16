param(
    [Parameter(Mandatory=$true)][string]$Target,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedTargetHash,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeHash,
    [ValidateRange(20,300)][int]$TimeoutSeconds=180
)
$ErrorActionPreference='Stop'
$output=Join-Path $env:ProgramData ('Exambro-Startup-'+$env:COMPUTERNAME+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
# Task berjalan tersembunyi; tanpa log, kegagalan seperti hash berubah tidak terlihat oleh operator.
trap {@{PC=$env:COMPUTERNAME;Error=$_.Exception.Message;Actions=@()}|ConvertTo-Json|Set-Content -LiteralPath $output -Encoding UTF8;break}
$targetPath=(Resolve-Path -LiteralPath $Target).Path
$runtime=Join-Path (Split-Path -LiteralPath $targetPath) 'Application\runningexam.exe'
if(!(Test-Path -LiteralPath $runtime)){throw "Runtime Exambro tidak ditemukan: $runtime"}
if((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ine $ExpectedTargetHash){throw 'Hash Exambro berubah. Verifikasi versi sebelum menjalankan otomatis.'}
if((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ine $ExpectedRuntimeHash){throw 'Hash runningexam.exe berubah. Verifikasi versi sebelum menjalankan otomatis.'}
$session=(Get-Process -Id $PID).SessionId
if($session -eq 0){throw 'Jalankan dalam sesi desktop pengguna, bukan tugas SYSTEM/session 0.'}
$mutex=New-Object Threading.Mutex($false,"Local\ExambroStartup-$session")
if(!$mutex.WaitOne(0)){return}
try {
    $already=@(Get-Process -Name ExamBrowser,runningexam -ErrorAction SilentlyContinue | Where-Object {$_.SessionId -eq $session})
    if(!$already.Count){Start-Process -FilePath $targetPath -WorkingDirectory (Split-Path -LiteralPath $targetPath)}
    & (Join-Path $PSScriptRoot 'EXAMBRO-UI.ps1') -Target $targetPath -Output $output -Mode Run -TimeoutSeconds $TimeoutSeconds -ExpectedRuntimeHash $ExpectedRuntimeHash
    Write-Output "Log: $output"
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
