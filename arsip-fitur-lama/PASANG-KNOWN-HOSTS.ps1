param([Parameter(Mandatory=$true)][string]$InventoryCsv)
$ErrorActionPreference='Stop'
$keygen=Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe'
$known=Join-Path $env:USERPROFILE '.ssh\known_hosts'
$dir=Split-Path -Parent $known
New-Item -ItemType Directory -Path $dir -Force | Out-Null
if(Test-Path -LiteralPath $known){Copy-Item -LiteralPath $known -Destination ($known+'.before-lab-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
foreach($row in Import-Csv -LiteralPath $InventoryCsv){
    if($row.IP -notmatch '^\d{1,3}(\.\d{1,3}){3}$'){throw "IP tidak valid: $($row.PC)"}
    if($row.HostPublicKey -notmatch '^ssh-ed25519\s+[A-Za-z0-9+/]+={0,2}$'){throw "Host public key tidak valid: $($row.PC)"}
    $temp=[IO.Path]::GetTempFileName()
    try {
        Set-Content -LiteralPath $temp -Value $row.HostPublicKey -Encoding ASCII
        # Jangan disalurkan ke Select-Object -First: proses dihentikan lebih awal dan exit code menjadi -1.
        $fingerprintLine=@(& $keygen -lf $temp)[0]
        if($LASTEXITCODE -ne 0 -or $fingerprintLine -notmatch 'SHA256:[A-Za-z0-9+/]+'){throw "Fingerprint tidak terbaca: $($row.PC)"}
        if($Matches[0] -cne $row.Fingerprint){throw "Fingerprint berbeda: $($row.PC)"}
        $line="$($row.IP) $($row.HostPublicKey)"
        $exists=(Test-Path -LiteralPath $known) -and (Select-String -LiteralPath $known -SimpleMatch -Pattern $line -Quiet)
        if(Test-Path -LiteralPath $known){
            # SSH menerima semua key yang tercatat untuk satu IP, jadi key lama tetap dipercaya sampai dihapus.
            $old=@(Select-String -LiteralPath $known -Pattern ('^'+[regex]::Escape($row.IP)+'\s+ssh-ed25519\s') | Where-Object {$_.Line.Trim() -cne $line})
            if($old.Count){Write-Warning "$($row.PC): known_hosts masih memuat key lain untuk $($row.IP). Jika PC memang diganti/diinstal ulang, hapus dengan: ssh-keygen -R $($row.IP) lalu jalankan ulang."}
        }
        if(!$exists){
            # Baris terakhir file lama bisa tanpa newline; tanpa pemisah, dua entri akan menyatu.
            $text=if(Test-Path -LiteralPath $known){[IO.File]::ReadAllText($known)}else{''}
            $prefix=if($text -and !$text.EndsWith("`n")){"`r`n"}else{''}
            Add-Content -LiteralPath $known -Value ($prefix+$line) -Encoding ASCII
        }
        Write-Output "Known host terpasang: $($row.PC) $($row.IP) $($row.Fingerprint)"
    } finally {Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
}
