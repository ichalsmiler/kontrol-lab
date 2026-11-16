param([ValidateSet('Status','Disable','Restore')][string]$Action='Status')
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$admin=New-Object Security.Principal.WindowsPrincipal($identity)
if(!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Jalankan sebagai Administrator.'}
$path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$backup=Join-Path $env:ProgramData 'LabUacBefore.txt'
$current=(Get-ItemProperty -Path $path -Name EnableLUA).EnableLUA
if($Action -eq 'Status'){Write-Output "EnableLUA=$current (1=aktif, 0=nonaktif)";return}
if($Action -eq 'Disable'){
    Write-Warning 'UAC akan dimatikan untuk seluruh PC. Ini meningkatkan risiko malware dan perubahan tanpa konfirmasi; sebagian aplikasi Windows dapat terganggu. Restart diperlukan.'
    if((Read-Host 'Ketik MATIKAN-UAC untuk melanjutkan') -cne 'MATIKAN-UAC'){throw 'Dibatalkan.'}
    if(!(Test-Path -LiteralPath $backup)){Set-Content -LiteralPath $backup -Value $current -Encoding ASCII}
    Set-ItemProperty -Path $path -Name EnableLUA -Value 0
} else {
    if(!(Test-Path -LiteralPath $backup)){throw "Cadangan tidak ada: $backup. Aktifkan manual dengan EnableLUA=1 bila sesuai kebijakan sekolah."}
    $saved=[int](Get-Content -LiteralPath $backup -TotalCount 1)
    if($saved -notin 0,1){throw 'Nilai cadangan tidak valid.'}
    Set-ItemProperty -Path $path -Name EnableLUA -Value $saved
}
Write-Output "EnableLUA sekarang: $((Get-ItemProperty -Path $path -Name EnableLUA).EnableLUA). Restart manual di luar sesi ujian diperlukan."
