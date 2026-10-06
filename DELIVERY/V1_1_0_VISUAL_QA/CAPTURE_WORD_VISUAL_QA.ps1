Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WindowCapture {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing
$root=(Resolve-Path 'DELIVERY\V1_1_0_VISUAL_QA').Path
$word=New-Object -ComObject Word.Application
$word.Visible=$true; $word.DisplayAlerts=0
$word.WindowState=1
$results=@()
foreach($name in @('Arabic_Synthetic_Report.docx','English_Synthetic_Report.docx')) {
  $path=Join-Path $root $name
  $doc=$word.Documents.Open($path,$false,$true)
  $word.ActiveWindow.View.Type=3
  $word.ActiveWindow.View.Zoom.PageFit=0
  $word.ActiveWindow.View.Zoom.Percentage=55
  $pages=$doc.ComputeStatistics(2)
  Start-Sleep -Milliseconds 900
  $nums=if($pages -le 3){@(1..$pages)}else{@(1,2,$pages)}
  foreach($page in $nums) {
    [void]$word.Selection.GoTo(1,1,$page)
    Start-Sleep -Milliseconds 500
    $target=[IntPtr]$word.ActiveWindow.Hwnd
    [void][WindowCapture]::ShowWindow($target,9)
    [void][WindowCapture]::MoveWindow($target,0,0,[WindowCapture]::GetSystemMetrics(0),[WindowCapture]::GetSystemMetrics(1),$true)
    [void][WindowCapture]::SetForegroundWindow($target)
    Start-Sleep -Milliseconds 350
    $hwnd=[WindowCapture]::GetForegroundWindow()
    $title=New-Object System.Text.StringBuilder 512
    [void][WindowCapture]::GetWindowText($hwnd,$title,$title.Capacity)
    if($hwnd -ne $target -or $title.ToString() -notmatch 'Word') { throw "Refusing non-Word screen capture (foreground title: $($title.ToString()))" }
    $rect=New-Object WindowCapture+RECT
    [void][WindowCapture]::GetWindowRect($target,[ref]$rect)
    $w=[WindowCapture]::GetSystemMetrics(0); $h=[WindowCapture]::GetSystemMetrics(1)
    $bmp=New-Object System.Drawing.Bitmap($w,$h)
    $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0,0,0,0,$bmp.Size)
    $tag=if($name.StartsWith('Arabic')){'AR'}else{'EN'}
    $file=Join-Path $root ("WORD_{0}_PAGE_{1}.png" -f $tag,$page)
    $bmp.Save($file,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  }
  $results += [pscustomobject]@{File=$name;Pages=$pages;OpenedInWord=$true;ReadOnly=$true}
  $doc.Close(0)
}
$word.Quit()
$results | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $root 'WORD_CAPTURE_MANIFEST.json')

