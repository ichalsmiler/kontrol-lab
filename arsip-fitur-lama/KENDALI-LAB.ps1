param(
    [Parameter(Mandatory=$true)][string]$InventoryCsv,
    [Parameter(Mandatory=$true)][ValidateSet('List','Wake','Shutdown','Status')][string]$Action,
    [string[]]$PC=@(),
    [string]$Room='',
    [switch]$All,
    [switch]$Apply,
    [string]$PrivateKey=(Join-Path $env:USERPROFILE '.ssh\id_ed25519'),
    [string]$KnownHosts=(Join-Path $env:USERPROFILE '.ssh\known_hosts')
)
$ErrorActionPreference='Stop'
$rows=@(Import-Csv -LiteralPath $InventoryCsv)
if(!$rows.Count){throw 'Inventaris kosong.'}
if(@($rows|Group-Object PC|Where-Object Count -gt 1).Count){throw 'Nama PC duplikat dalam inventaris.'}
if($Action -ne 'List' -and !$All -and !$Room -and !$PC.Count){throw 'Pilih -PC, -Room, atau -All.'}
if($All -and ($PC.Count -or $Room)){throw 'Gunakan -All sendiri agar target tidak ambigu.'}
$targets=@($rows | Where-Object {$All -or ($Room -and $_.Room -eq $Room) -or ($PC.Count -and $_.PC -in $PC)})
if($Action -eq 'List'){$rows|Format-Table Room,PC,IP,User,MAC,Broadcast -AutoSize;return}
if(!$targets.Count){throw 'Tidak ada PC yang cocok dengan pilihan.'}
if($PC.Count){$missing=@($PC|Where-Object {$_ -notin $rows.PC});if($missing.Count){throw "PC tidak terdaftar: $($missing -join ', ')"}}
if($Action -eq 'Wake' -and $targets.Count -gt 1 -and !$Room -and !$All){throw 'Untuk banyak PC gunakan -Room atau -All.'}
if($Action -eq 'Shutdown' -and $Apply){
    $answer=Read-Host "Ketik SHUTDOWN-$($targets.Count) untuk mematikan $($targets.Count) PC"
    if($answer -cne "SHUTDOWN-$($targets.Count)"){throw 'Dibatalkan.'}
}
if($Action -in @('Shutdown','Status') -and $Apply){
    if(!(Test-Path -LiteralPath $PrivateKey)){throw "Private key tidak ditemukan: $PrivateKey"}
    if(!(Test-Path -LiteralPath $KnownHosts)){throw "Known hosts tidak ditemukan: $KnownHosts"}
}
$ssh=Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
foreach($row in $targets){
    if($row.IP -notmatch '^\d{1,3}(\.\d{1,3}){3}$' -or $row.User -notmatch '^\w[\w.-]*$' -or $row.PC -notmatch '^[\w.-]+$'){
        Write-Warning "$($row.PC): identitas tidak valid; dilewati.";continue
    }
    if(!$Apply){Write-Output "SIMULASI $Action $($row.Room) $($row.PC) $($row.IP)";continue}
    try {
        if($Action -eq 'Wake'){
            $mac=($row.MAC -replace '[^0-9A-Fa-f]','').ToUpperInvariant()
            if($mac -notmatch '^[0-9A-F]{12}$'){throw 'MAC tidak valid.'}
            $broadcastIP=$null
            if(![Net.IPAddress]::TryParse([string]$row.Broadcast,[ref]$broadcastIP) -or $broadcastIP.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Broadcast IPv4 tidak valid.'}
            $bytes=New-Object byte[] 6
            for($i=0;$i -lt 6;$i++){$bytes[$i]=[Convert]::ToByte($mac.Substring($i*2,2),16)}
            $packet=New-Object byte[] 102
            for($i=0;$i -lt 6;$i++){$packet[$i]=255}
            for($i=0;$i -lt 16;$i++){[Array]::Copy($bytes,0,$packet,6+$i*6,6)}
            $udp=New-Object Net.Sockets.UdpClient
            try {$udp.EnableBroadcast=$true;[void]$udp.Send($packet,$packet.Length,(New-Object Net.IPEndPoint($broadcastIP,9)))}finally{$udp.Dispose()}
            Write-Output "WOL terkirim $($row.PC) ke $($row.Broadcast); ini belum membuktikan PC menyala."
        } else {
            $sshArgs=@('-T','-o','BatchMode=yes','-o','ConnectTimeout=5','-o','StrictHostKeyChecking=yes','-o',"UserKnownHostsFile='$($KnownHosts -replace '\\','/')'",'-i',$PrivateKey,"$($row.User)@$($row.IP)")
            if($Action -eq 'Shutdown'){$sshArgs+='shutdown.exe';$sshArgs+='/s';$sshArgs+='/t';$sshArgs+='0'}else{$sshArgs+='hostname'}
            # PowerShell 5.1 + 2>&1 + Stop membuat stderr biasa dari ssh menjadi error palsu; penentu hasil adalah exit code.
            $ErrorActionPreference='Continue'
            $result=& $ssh @sshArgs 2>&1
            $code=$LASTEXITCODE
            $ErrorActionPreference='Stop'
            if($code -ne 0){throw "SSH gagal: $($result -join ' ')"}
            Write-Output "$Action $($row.PC): $($result -join ' ')"
        }
    }catch{Write-Warning "$($row.PC): $($_.Exception.Message)"}
}
