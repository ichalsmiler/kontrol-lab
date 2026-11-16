# Membangun ..\KontrolLab-Admin.exe dan ..\KontrolLab-Client.exe memakai compiler .NET Framework bawaan Windows.
# Jalankan ulang setelah kode atau skrip .ps1 berubah.
$ErrorActionPreference='Stop'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(!(Test-Path -LiteralPath $csc)){throw "Compiler tidak ditemukan: $csc"}
$package=Split-Path -Parent $PSScriptRoot
$common=@('/nologo','/target:winexe','/optimize+','/platform:anycpu','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll')
$shared=Join-Path $PSScriptRoot 'Bersama.cs'

$admin=Join-Path $package 'KontrolLab-Admin.exe'
& $csc @common "/out:$admin" "/win32manifest:$(Join-Path $PSScriptRoot 'admin.manifest')" $shared (Join-Path $PSScriptRoot 'Internet.cs') (Join-Path $PSScriptRoot 'Admin.cs')
if($LASTEXITCODE -ne 0){throw 'Kompilasi Admin gagal.'}

# Client meminta Administrator saat dibuka, dan membawa semua skrip .ps1 paket.
$client=Join-Path $package 'KontrolLab-Client.exe'
$resources=@(Get-ChildItem -LiteralPath $package -File -Filter *.ps1 | ForEach-Object {"/resource:$($_.FullName),paket.$($_.Name)"})
& $csc @common "/out:$client" "/win32manifest:$(Join-Path $PSScriptRoot 'client.manifest')" @resources $shared (Join-Path $PSScriptRoot 'Client.cs')
if($LASTEXITCODE -ne 0){throw 'Kompilasi Client gagal.'}

foreach($f in $admin,$client){Write-Output "Selesai: $f ($([math]::Round((Get-Item -LiteralPath $f).Length/1KB)) KB)"}
