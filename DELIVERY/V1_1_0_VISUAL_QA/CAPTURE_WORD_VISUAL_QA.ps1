Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WordWindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
}
"@
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path 'DELIVERY\V1_1_0_VISUAL_QA').Path
$word = New-Object -ComObject Word.Application
$word.Visible = $true
$word.DisplayAlerts = 0
$word.WindowState = 1
$results = @()

try {
  foreach ($name in @('Arabic_Synthetic_Report.docx','English_Synthetic_Report.docx')) {
    $path = Join-Path $root $name
    $doc = $word.Documents.Open($path,$false,$true)
    $word.ActiveWindow.View.Type = 3
    $word.ActiveWindow.View.Zoom.PageFit = 0
    $word.ActiveWindow.View.Zoom.Percentage = 55
    $pages = $doc.ComputeStatistics(2)
    Start-Sleep -Milliseconds 900
    $nums = if ($pages -le 3) { @(1..$pages) } else { @(1,2,$pages) }

    foreach ($page in $nums) {
      [void]$word.Selection.GoTo(1,1,$page)
      Start-Sleep -Milliseconds 500
      $hwnd = [IntPtr]$word.ActiveWindow.Hwnd
      [uint32]$processId = 0
      [void][WordWindowCapture]::GetWindowThreadProcessId($hwnd,[ref]$processId)
      $owner = Get-Process -Id $processId -ErrorAction Stop
      if ($owner.ProcessName -ne 'WINWORD') { throw "Refusing non-Word capture from $($owner.ProcessName)." }

      $title = New-Object System.Text.StringBuilder 512
      [void][WordWindowCapture]::GetWindowText($hwnd,$title,$title.Capacity)
      if ($title.ToString() -notmatch 'Word') { throw "Refusing unexpected Word window title: $($title.ToString())" }

      $rect = New-Object WordWindowCapture+RECT
      if (-not [WordWindowCapture]::GetWindowRect($hwnd,[ref]$rect)) { throw 'Could not read Word window bounds.' }
      $width = $rect.Right - $rect.Left
      $height = $rect.Bottom - $rect.Top
      $bitmap = New-Object System.Drawing.Bitmap($width,$height)
      $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
      $hdc = $graphics.GetHdc()
      try {
        if (-not [WordWindowCapture]::PrintWindow($hwnd,$hdc,2)) { throw 'Windows could not render the live Word window.' }
      }
      finally { $graphics.ReleaseHdc($hdc) }

      # Protect the signed-in Office account shown in Word's title bar while preserving
      # the live Word chrome and the document pages used for visual review.
      $mask = [System.Drawing.Rectangle]::new([int]($width * 0.72),0,[int]($width * 0.17),[int]($height * 0.05))
      $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(31,31,31))
      $graphics.FillRectangle($brush,$mask)
      $brush.Dispose()

      $tag = if ($name.StartsWith('Arabic')) { 'AR' } else { 'EN' }
      $file = Join-Path $root ("WORD_{0}_PAGE_{1}.png" -f $tag,$page)
      $bitmap.Save($file,[System.Drawing.Imaging.ImageFormat]::Png)
      $graphics.Dispose()
      $bitmap.Dispose()
      $results += [pscustomobject]@{ File=$name; Page=$page; Screenshot=(Split-Path $file -Leaf); OpenedInMicrosoftWord=$true; LiveWindowRender=$true; AccountRegionMasked=$true }
    }
    $doc.Close(0)
  }
}
finally { $word.Quit() }

$results | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $root 'WORD_CAPTURE_MANIFEST.json')
