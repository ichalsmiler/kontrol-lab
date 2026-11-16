param([string]$Target,[string]$Output,[ValidateSet('Inspect','Confirm','Run')][string]$Mode='Inspect',[ValidateRange(8,300)][int]$TimeoutSeconds=35,[Parameter(Mandatory=$true)][string]$ExpectedRuntimeHash)
$ErrorActionPreference='Stop'
Add-Type -ReferencedAssemblies Accessibility -TypeDefinition @'
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public class EBWindow {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h,uint id,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out Accessibility.IAccessible a);
 public static bool AccessibleClick(IntPtr h){var iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");Accessibility.IAccessible a;int hr=AccessibleObjectFromWindow(h,0xFFFFFFFC,ref iid,out a);if(hr!=0||a==null)return false;a.accDoDefaultAction(0);return true;}
 public delegate bool Callback(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(Callback c,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h,Callback c,IntPtr p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
 [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r);
 public static string Text(IntPtr h){var s=new StringBuilder(4096);GetWindowText(h,s,s.Capacity);return s.ToString();}
 public static string Class(IntPtr h){var s=new StringBuilder(256);GetClassName(h,s,s.Capacity);return s.ToString();}
 public static IntPtr[] Windows(){var a=new List<IntPtr>();EnumWindows((h,p)=>{a.Add(h);return true;},IntPtr.Zero);return a.ToArray();}
 public static IntPtr[] Children(IntPtr h){var a=new List<IntPtr>();EnumChildWindows(h,(x,p)=>{a.Add(x);return true;},IntPtr.Zero);return a.ToArray();}
 public static uint PID(IntPtr h){uint p;GetWindowThreadProcessId(h,out p);return p;}
 public static bool Click(IntPtr h){IntPtr r;return SendMessageTimeout(h,0xF5,IntPtr.Zero,IntPtr.Zero,2,2000,out r)!=IntPtr.Zero;}
 public static bool Confirm(IntPtr dialog,IntPtr button){IntPtr r;return SendMessageTimeout(dialog,0x111,new IntPtr(GetDlgCtrlID(button)),button,2,2000,out r)!=IntPtr.Zero;}
}
'@
[void][EBWindow]::SetProcessDPIAware()
$session=(Get-Process -Id $PID).SessionId
$actions=New-Object 'Collections.Generic.List[object]'
$runtime=Join-Path (Split-Path -LiteralPath $Target) 'Application\runningexam.exe'
$runtimeVerified=(Test-Path -LiteralPath $runtime) -and (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -eq $ExpectedRuntimeHash
function Snapshot {
 $processes=@(Get-CimInstance Win32_Process | Where-Object {$_.SessionId -eq $session -and ($_.ExecutablePath -ieq $Target -or ($runtimeVerified -and $_.ExecutablePath -ieq $runtime))})
 $ids=@($processes.ProcessId)
 @(foreach($h in [EBWindow]::Windows()) {
  if([EBWindow]::PID($h) -in $ids -and [EBWindow]::IsWindowVisible($h)) {
   # The runtime owns the specification launcher. Never inspect its other windows.
   $proc=@($processes|Where-Object ProcessId -eq ([EBWindow]::PID($h)))[0]
   if($proc.ExecutablePath -ieq $runtime -and ([EBWindow]::Text($h) -ine 'Exambrowser Client' -or [EBWindow]::Class($h) -notmatch '^WindowsForms10.Window')){continue}
   [pscustomobject]@{Handle=$h.ToInt64();PID=[EBWindow]::PID($h);Title=[EBWindow]::Text($h);Class=[EBWindow]::Class($h);Controls=@(foreach($ch in [EBWindow]::Children($h)) {
    if([EBWindow]::IsWindowVisible($ch)){[pscustomobject]@{Handle=$ch.ToInt64();Text=[EBWindow]::Text($ch);Class=[EBWindow]::Class($ch);Enabled=[EBWindow]::IsWindowEnabled($ch)}}
   })}
  }
 })
}
try {
 $before=@(Snapshot)
 if($Mode -in @('Confirm','Run')) {
  $deadline=(Get-Date).AddSeconds($(if($Mode -eq 'Confirm'){8}else{$TimeoutSeconds}));$confirmed=$false;$started=$false
  do {
   $windows=@(Snapshot)
   foreach($w in $windows) {
    $text=($w.Controls.Text -join ' ')
    if(!$confirmed -and $w.Class -eq '#32770' -and $text -match '(?i)tidak memiliki akses internet' -and $text -match '(?i)update|diperbarui') {
     $ok=@($w.Controls | Where-Object {$_.Class -eq 'Button' -and $_.Text.Replace('&','').Trim() -ieq 'OK' -and $_.Enabled})
     if($ok.Count -eq 1){
      $sent=[EBWindow]::Confirm([IntPtr]$w.Handle,[IntPtr]$ok[0].Handle)
      Start-Sleep -Milliseconds 900
      $confirmed=@(Snapshot | Where-Object {$_.Handle -eq $w.Handle -and ($_.Controls.Text -join ' ') -match '(?i)tidak memiliki akses internet'}).Count -eq 0
      $actions.Add(@{Action='KonfirmasiPeringatanInternet';MessageSent=$sent;Success=$confirmed})
     }
    }
    # Only the launcher specification page; never browser/login/exam controls.
    if($Mode -eq 'Run' -and !$started -and $w.Class -ne '#32770' -and $text -match '(?i)RAM|processor|prosesor' -and $text -match '(?i)versi|version') {
     $run=@($w.Controls | Where-Object {$_.Class -match '^WindowsForms10.BUTTON' -and $_.Text.Replace('&','').Trim() -imatch '^(Jalankan|Run)$' -and $_.Enabled})
     if($run.Count -eq 1){
      $sent=[EBWindow]::AccessibleClick([IntPtr]$run[0].Handle)
      Start-Sleep -Seconds 3
      $stillReady=@(Snapshot|Where-Object {$_.Handle -eq $w.Handle -and @($_.Controls|Where-Object {$_.Text.Replace('&','').Trim() -imatch '^(Jalankan|Run)$' -and $_.Enabled}).Count -gt 0}).Count -gt 0
      $actions.Add(@{Action='JalankanLauncher';MessageSent=$sent;Success=($sent -and !$stillReady);Method='MSAA-DefaultAction'})
      # Never repeat RUN automatically after a message has been delivered.
      $started=$sent
     }
    }
   }
   if($started -or ($Mode -eq 'Confirm' -and ($confirmed -or $actions.Count -gt 0))){break};Start-Sleep -Milliseconds 700
  }while((Get-Date) -lt $deadline)
 }
 @{PC=$env:COMPUTERNAME;Mode=$Mode;Session=$session;Before=$before;Actions=@($actions.ToArray());After=@(Snapshot)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding UTF8
} catch {@{PC=$env:COMPUTERNAME;Error=$_.Exception.Message;Actions=@($actions.ToArray())}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $Output -Encoding UTF8;exit 1}
