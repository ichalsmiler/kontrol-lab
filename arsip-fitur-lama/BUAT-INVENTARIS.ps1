param(
    [Parameter(Mandatory=$true)][string]$CatatanFolder,
    [Parameter(Mandatory=$true)][string]$OutputCsv,
    [Parameter(Mandatory=$true)][string]$AddressPrefix,
    [Parameter(Mandatory=$true)][string]$Broadcast
)
$ErrorActionPreference='Stop'
$rows=@()
foreach($file in Get-ChildItem -LiteralPath $CatatanFolder -Filter 'AKSES-SSH-*.json' -File){
    $note=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    # Jangan bernama $matches: operator -match di bawah menimpa variabel otomatis itu.
    $found=@($note.adapters | Where-Object {$_.ip_address -like "$AddressPrefix*"})
    if($found.Count -ne 1){Write-Warning "$($file.Name): ditemukan $($found.Count) adaptor sesuai prefix; dilewati.";continue}
    $pc=[string]$note.pc_name
    $room=if($pc -match '^SMADA-PC-'){'Lab SMADA'}elseif($pc -match '^L(\d+)PC'){"Lab $($Matches[1])"}else{'Isi ruang secara manual'}
    $rows += [pscustomobject]@{Room=$room;PC=$pc;IP=[string]$found[0].ip_address;User=[string]$note.username;MAC=[string]$found[0].mac_address;Broadcast=$Broadcast;Fingerprint=[string]$note.host_fingerprint_ed25519;HostPublicKey=[string]$note.host_public_key_ed25519}
}
if(!$rows.Count){throw 'Tidak ada catatan JSON yang cocok; tidak menulis CSV.'}
$duplicate=@($rows | Group-Object PC | Where-Object Count -gt 1)
if($duplicate.Count){throw "Nama PC duplikat: $($duplicate.Name -join ', '). Perbaiki catatan dahulu."}
$duplicateIP=@($rows | Group-Object IP | Where-Object Count -gt 1)
if($duplicateIP.Count){throw "IP duplikat: $($duplicateIP.Name -join ', '). Catatan mungkin usang; ambil ulang dari PC terkait."}
$rows | Sort-Object Room,PC | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding UTF8
Write-Output "Inventaris: $OutputCsv ($($rows.Count) PC). Periksa ruang, IP, MAC kabel, broadcast, dan fingerprint sebelum kendali."
