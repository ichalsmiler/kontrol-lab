param([string]$Config=(Join-Path $env:ProgramData 'LabControl\internet.json'))
# Agen di PC lab: berjalan di sesi desktop pengguna (task saat login), menghubungi relay KELUAR,
# dan hanya menjalankan perintah dari daftar tetap yang tanda tangannya cocok dengan kunci publik Admin.
$ErrorActionPreference='Stop'
$dir=Split-Path -LiteralPath $Config
$logFile=Join-Path $dir 'agen-internet.log'
$seenFile=Join-Path $dir 'agen-internet-sudah.txt'
function Write-Log([string]$text){
    try {
        if((Test-Path -LiteralPath $logFile) -and (Get-Item -LiteralPath $logFile).Length -gt 200KB){Move-Item -LiteralPath $logFile ($logFile+'.lama') -Force}
        Add-Content -LiteralPath $logFile -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss')+' '+$text) -Encoding UTF8
    } catch {}
}
$cfg=Get-Content -LiteralPath $Config -Raw -Encoding UTF8 | ConvertFrom-Json
$relay=$cfg.relay.TrimEnd('/')
$session=(Get-Process -Id $PID).SessionId
if($session -eq 0){Write-Log 'Berhenti: berjalan di session 0, bukan desktop pengguna.';exit 1}
$mutex=New-Object Threading.Mutex($false,"Local\LabAgenInternet-$session")
if(!$mutex.WaitOne(0)){exit 0}

$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp=$false
$rsa.FromXmlString($cfg.kunciPublik)
$name=$env:COMPUTERNAME.ToUpperInvariant()
$seen=New-Object 'Collections.Generic.List[string]'
if(Test-Path -LiteralPath $seenFile){foreach($l in Get-Content -LiteralPath $seenFile){if($l){$seen.Add($l)}}}
$results=New-Object 'Collections.Generic.List[object]'
$version='1.1'

function Get-Exambro {@(Get-Process -Name ExamBrowser,runningexam -ErrorAction SilentlyContinue | Where-Object {$_.SessionId -eq $session})}

function Test-Command($c,[double]$relayNow){
    $target=@($c.target | ForEach-Object {[string]$_})
    $message='v1|'+$c.id+'|'+$c.aksi+'|'+($target -join ',')+'|'+([int64]$c.ts)
    if([string]$c.data){$message+='|'+[string]$c.data}
    try {$sig=[Convert]::FromBase64String([string]$c.sig)} catch {return 'tanda tangan rusak'}
    if(!$rsa.VerifyData([Text.Encoding]::UTF8.GetBytes($message),'SHA256',$sig)){return 'tanda tangan tidak cocok dengan kunci Admin'}
    if(!($target -contains '*') -and !($target | Where-Object {$_ -ieq $name})){return 'bukan untuk PC ini'}
    # Jam relay dipakai sebagai patokan; perintah lama yang diputar ulang ditolak.
    if([math]::Abs($relayNow-[double]$c.ts) -gt 600000){return 'perintah kedaluwarsa (cek jam komputer Admin)'}
    if($seen.Contains([string]$c.id)){return 'sudah pernah dijalankan'}
    return $null
}

# Data kirim-file: "nama|sha256|ukuran", ikut ditandatangani Admin. File disimpan di Documents pengguna ini
# dan hanya dipakai bila hash-nya cocok; file tidak pernah dibuka atau dijalankan otomatis.
function Save-LabFile([string]$data){
    $parts=$data.Split('|')
    if($parts.Count -ne 3){return @{ok=$false;pesan='data file rusak'}}
    $fileName=$parts[0].Trim()
    if(!$fileName -or $fileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $fileName -match '^\.+$' -or $fileName.Length -gt 150){return @{ok=$false;pesan='nama file tidak sah'}}
    $sha=$parts[1].ToLowerInvariant()
    if($sha -notmatch '^[a-f0-9]{64}$'){return @{ok=$false;pesan='hash file tidak sah'}}
    $size=[int64]$parts[2]
    $docs=[Environment]::GetFolderPath('MyDocuments')
    $dest=Join-Path $docs $fileName
    if((Test-Path -LiteralPath $dest) -and (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ieq $sha){return @{ok=$true;pesan="Sudah ada (sama): $dest"}}
    $tmp=Join-Path $docs ('~unduhan-'+$sha.Substring(0,12)+'.tmp')
    try {
        $wc=New-Object Net.WebClient
        $wc.Headers.Add('X-Token',[string]$cfg.token)
        $wc.DownloadFile($relay+'/api/agen/file/'+$sha,$tmp)
        $wc.Dispose()
        if((Get-Item -LiteralPath $tmp).Length -ne $size){throw 'ukuran file tidak sama'}
        if((Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash -ine $sha){throw 'isi file tidak cocok dengan hash dari Admin'}
        Move-Item -LiteralPath $tmp -Destination $dest -Force
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        return @{ok=$false;pesan='Gagal unduh: '+$_.Exception.Message}
    }
    return @{ok=$true;pesan="Tersimpan: $dest"}
}

function Invoke-LabCommand([string]$action,[string]$data){
    switch($action){
        'kirim-file' {return Save-LabFile $data}
        'exambro-jalankan' {
            if((Get-Exambro).Count){return @{ok=$true;pesan='Exambro sudah berjalan'}}
            $task=Get-ScheduledTask -TaskName 'Lab-Exambro-Auto-Run' -ErrorAction SilentlyContinue
            if(!$task){return @{ok=$false;pesan='Exambro otomatis belum dipasang di PC ini (jalankan KontrolLab-Client.exe)'}}
            Start-ScheduledTask -TaskName 'Lab-Exambro-Auto-Run'
            $deadline=(Get-Date).AddSeconds(25)
            while((Get-Date) -lt $deadline){if((Get-Exambro).Count){return @{ok=$true;pesan='Exambro dibuka'}};Start-Sleep -Milliseconds 700}
            return @{ok=$false;pesan='Exambro tidak terbuka dalam 25 detik; lihat log Exambro-Startup di ProgramData'}
        }
        'exambro-tutup' {
            $p=Get-Exambro
            if(!$p.Count){return @{ok=$true;pesan='Exambro memang tidak berjalan'}}
            $p | Stop-Process -Force
            Start-Sleep -Seconds 1
            if((Get-Exambro).Count){return @{ok=$false;pesan='Exambro masih berjalan'}}
            return @{ok=$true;pesan='Exambro ditutup'}
        }
        'restart' {& "$env:WINDIR\System32\shutdown.exe" /r /t 20 /c 'PC akan restart dari Kontrol Lab dalam 20 detik.' | Out-Null; return @{ok=($LASTEXITCODE -eq 0);pesan='Restart dalam 20 detik'}}
        'matikan' {& "$env:WINDIR\System32\shutdown.exe" /s /t 20 /c 'PC akan dimatikan dari Kontrol Lab dalam 20 detik.' | Out-Null; return @{ok=($LASTEXITCODE -eq 0);pesan='Mati dalam 20 detik'}}
        'batal-matikan' {& "$env:WINDIR\System32\shutdown.exe" /a 2>$null | Out-Null; return @{ok=$true;pesan='Restart/matikan dibatalkan'}}
        default {return @{ok=$false;pesan='aksi tidak dikenal'}}
    }
}

Write-Log "Agen mulai: $name, relay $relay"
$wait=5
while($true){
    $body=@{name=$name;user=$env:USERNAME;exambro=[bool](Get-Exambro).Count;versi=$version;hasil=@($results.ToArray())} | ConvertTo-Json -Depth 4 -Compress
    try {
        $r=Invoke-RestMethod -Uri ($relay+'/api/agen') -Method Post -Headers @{'X-Token'=[string]$cfg.token} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 45 -UseBasicParsing
        $results.Clear()
        $wait=5
    } catch {
        Write-Log ('Relay tidak terjangkau: '+$_.Exception.Message)
        Start-Sleep -Seconds $wait
        $wait=[math]::Min($wait*2,60)
        continue
    }
    foreach($c in @($r.perintah)){
        if(!$c){continue}
        $problem=Test-Command $c ([double]$r.now)
        if($problem){
            Write-Log "Tolak $($c.id) $($c.aksi): $problem"
            if($problem -ne 'bukan untuk PC ini' -and $problem -ne 'sudah pernah dijalankan'){$results.Add(@{id=[string]$c.id;ok=$false;pesan='Ditolak: '+$problem})}
            continue
        }
        $seen.Add([string]$c.id)
        while($seen.Count -gt 200){$seen.RemoveAt(0)}
        try {Set-Content -LiteralPath $seenFile -Value $seen -Encoding ASCII} catch {}
        try {$out=Invoke-LabCommand ([string]$c.aksi) ([string]$c.data)} catch {$out=@{ok=$false;pesan=$_.Exception.Message}}
        Write-Log "Jalankan $($c.id) $($c.aksi): $($out.pesan)"
        $results.Add(@{id=[string]$c.id;ok=[bool]$out.ok;pesan=[string]$out.pesan})
    }
}
