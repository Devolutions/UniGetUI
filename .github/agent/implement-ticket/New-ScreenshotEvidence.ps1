# Makes the agent's screenshots easier to review:
#   - crops the Windows taskbar off every screenshot of the whole desktop,
#   - compares each step of a criterion with the next (after-1a-* then after-1b-*), outlines
#     what changed in red on a copy of the later screenshot (<name>-changes.png), and adds
#     that copy to the pull request description right after the screenshot,
#   - flags pairs that are nearly identical, which show too little to prove anything.
# Writes evidence.json and evidence.md to -OutputDir.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ScreenshotsDir,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    # The pull request description to add the highlighted screenshots to, if it exists.
    [string] $PullRequestBody,

    [int] $DesktopHeight = 1080,

    [int] $TaskbarHeight = 48,

    # Share of pixels below which a pair counts as nearly identical.
    [double] $NearIdenticalShare = 0.002
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
if (-not ('AgentEvidence.ImageDiff' -as [type])) {
    $references = @(
        [System.Drawing.Bitmap].Assembly.Location
        [System.Drawing.Rectangle].Assembly.Location
        'System.Collections'
        'System.Runtime.InteropServices'
    )
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AgentEvidence
{
    public sealed class DiffResult
    {
        public double ChangedShare;
        public List<Rectangle> Regions = new List<Rectangle>();
    }

    public static class ImageDiff
    {
        static byte[] Pixels(Bitmap bitmap, out int stride)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                stride = data.Stride;
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { bitmap.UnlockBits(data); }
        }

        // Compares two images of the same size; a pixel changed when a channel moved by more
        // than the tolerance. Changed cells are merged into rectangles of touching cells.
        public static DiffResult Compare(Bitmap a, Bitmap b, int cell, int tolerance)
        {
            int width = Math.Min(a.Width, b.Width), height = Math.Min(a.Height, b.Height);
            int strideA, strideB;
            var pa = Pixels(a, out strideA);
            var pb = Pixels(b, out strideB);
            int cols = (width + cell - 1) / cell, rows = (height + cell - 1) / cell;
            var changedCells = new bool[cols, rows];
            long changed = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int ia = y * strideA + x * 4, ib = y * strideB + x * 4;
                    if (Math.Abs(pa[ia] - pb[ib]) > tolerance || Math.Abs(pa[ia + 1] - pb[ib + 1]) > tolerance || Math.Abs(pa[ia + 2] - pb[ib + 2]) > tolerance)
                    {
                        changed++;
                        changedCells[x / cell, y / cell] = true;
                    }
                }
            }

            var result = new DiffResult { ChangedShare = (double)changed / ((long)width * height) };
            var seen = new bool[cols, rows];
            for (int cy = 0; cy < rows; cy++)
            {
                for (int cx = 0; cx < cols; cx++)
                {
                    if (!changedCells[cx, cy] || seen[cx, cy]) continue;
                    int minX = cx, maxX = cx, minY = cy, maxY = cy;
                    var queue = new Queue<Point>();
                    queue.Enqueue(new Point(cx, cy));
                    seen[cx, cy] = true;
                    while (queue.Count > 0)
                    {
                        var p = queue.Dequeue();
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = p.X + dx, ny = p.Y + dy;
                            if (nx < 0 || ny < 0 || nx >= cols || ny >= rows || seen[nx, ny] || !changedCells[nx, ny]) continue;
                            seen[nx, ny] = true;
                            queue.Enqueue(new Point(nx, ny));
                        }
                    }
                    var rect = Rectangle.FromLTRB(minX * cell, minY * cell, Math.Min(width, (maxX + 1) * cell), Math.Min(height, (maxY + 1) * cell));
                    rect.Inflate(4, 4);
                    rect.Intersect(new Rectangle(0, 0, width, height));
                    result.Regions.Add(rect);
                }
            }
            return result;
        }
    }
}
'@
}

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

# Runs again after the fix round: start from the screenshots and description as they are now.
Get-ChildItem -LiteralPath $ScreenshotsDir -Filter '*-changes.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force
if ($PullRequestBody -and (Test-Path -LiteralPath $PullRequestBody)) {
    $kept = [System.Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath $PullRequestBody) {
        if ($line -match '\]\(\s*(\./)?screenshots/[^)\s]+-changes\.png\s*\)') {
            if ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count - 1])) { $kept.RemoveAt($kept.Count - 1) }
            continue
        }
        $kept.Add($line)
    }
    $kept -join "`n" | Set-Content -LiteralPath $PullRequestBody -Encoding utf8NoBOM
}

$screenshots = @(Get-ChildItem -LiteralPath $ScreenshotsDir -Filter '*.png' -File -ErrorAction SilentlyContinue)

# Crop the taskbar off screenshots of the whole desktop (UniGetUI is maximized above it).
foreach ($file in $screenshots) {
    $bitmap = [System.Drawing.Bitmap]::new($file.FullName)
    try {
        if ($bitmap.Height -ne $DesktopHeight) { continue }
        $cropped = $bitmap.Clone([System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height - $TaskbarHeight), $bitmap.PixelFormat)
    }
    finally {
        $bitmap.Dispose()
    }
    $cropped.Save($file.FullName, [System.Drawing.Imaging.ImageFormat]::Png)
    $cropped.Dispose()
}

# Consecutive steps of each criterion: after-<n><step>-<name>.png. When a step was taken more
# than once, the one the description shows counts, or else the latest.
$body = if ($PullRequestBody -and (Test-Path -LiteralPath $PullRequestBody)) { Get-Content -LiteralPath $PullRequestBody -Raw } else { '' }
$steps = @($screenshots | ForEach-Object {
        if ($_.Name -match '^after-(?<n>\d+)(?<step>[a-z])-') {
            [pscustomobject]@{
                File = $_
                Criterion = [int] $Matches.n
                Step = $Matches.step
                Shown = $body.Contains("screenshots/$($_.Name)")
            }
        }
    })
$pairs = [System.Collections.Generic.List[object]]::new()
foreach ($group in $steps | Group-Object Criterion) {
    $ordered = @($group.Group | Group-Object Step | Sort-Object Name | ForEach-Object {
            $_.Group | Sort-Object @{ Expression = 'Shown'; Descending = $true }, @{ Expression = { $_.File.LastWriteTimeUtc }; Descending = $true } |
                Select-Object -First 1
        })
    for ($i = 1; $i -lt $ordered.Count; $i++) {
        $before = $ordered[$i - 1].File
        $after = $ordered[$i].File
        $a = [System.Drawing.Bitmap]::new($before.FullName)
        $b = [System.Drawing.Bitmap]::new($after.FullName)
        try {
            $diff = [AgentEvidence.ImageDiff]::Compare($a, $b, 24, 24)
            $highlightName = $null
            if ($diff.Regions.Count -gt 0) {
                $highlight = [System.Drawing.Bitmap]::new($b)
                $graphics = [System.Drawing.Graphics]::FromImage($highlight)
                $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230, 220, 30, 30), 4)
                foreach ($region in $diff.Regions) { $graphics.DrawRectangle($pen, $region) }
                $pen.Dispose(); $graphics.Dispose()
                $highlightName = [System.IO.Path]::GetFileNameWithoutExtension($after.Name) + '-changes.png'
                $highlight.Save((Join-Path $ScreenshotsDir $highlightName), [System.Drawing.Imaging.ImageFormat]::Png)
                $highlight.Dispose()
            }
        }
        finally {
            $a.Dispose(); $b.Dispose()
        }
        $pairs.Add([pscustomobject]@{
                before = $before.Name
                after = $after.Name
                changedShare = [Math]::Round($diff.ChangedShare, 5)
                regions = $diff.Regions.Count
                nearlyIdentical = $diff.ChangedShare -lt $NearIdenticalShare
                highlight = $highlightName
            })
    }
}

# Show each highlighted copy right after the screenshot it outlines.
if ($PullRequestBody -and (Test-Path -LiteralPath $PullRequestBody)) {
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath $PullRequestBody) {
        $lines.Add($line)
        foreach ($pair in $pairs | Where-Object { $_.highlight }) {
            if ($line -match "\]\(\s*(\./)?screenshots/$([regex]::Escape($pair.after))\s*\)") {
                $lines.Add('')
                $lines.Add("![What changed from $($pair.before) ($($pair.regions) region(s) outlined in red)](screenshots/$($pair.highlight))")
            }
        }
    }
    $lines -join "`n" | Set-Content -LiteralPath $PullRequestBody -Encoding utf8NoBOM
}

$warnings = @(
    if ($screenshots.Count -eq 0) { 'No screenshots were saved.' }
    elseif ($pairs.Count -eq 0) { 'No criterion has a before/after pair of steps (after-1a-*, after-1b-*), so nothing shows a change happening.' }
    foreach ($pair in $pairs | Where-Object nearlyIdentical) {
        "$($pair.before) and $($pair.after) are nearly identical ($([Math]::Round($pair.changedShare * 100, 2))% of pixels changed): they don't show the result clearly."
    }
)
[pscustomobject]@{ screenshots = $screenshots.Count; pairs = $pairs; warnings = $warnings } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $OutputDir 'evidence.json') -Encoding utf8NoBOM

$markdown = @(
    "$($screenshots.Count) screenshot(s), $($pairs.Count) before/after pair(s)."
    if ($pairs.Count -gt 0) {
        ''
        '| From | To | Changed | Regions |'
        '| --- | --- | --- | --- |'
        foreach ($pair in $pairs) {
            $flag = if ($pair.nearlyIdentical) { ' ⚠️ nearly identical' } else { '' }
            "| $($pair.before) | $($pair.after) | $([Math]::Round($pair.changedShare * 100, 2))%$flag | $($pair.regions) |"
        }
    }
    foreach ($warning in $warnings) { ''; "> [!WARNING]"; "> $warning" }
)
$markdown -join "`n" | Set-Content -LiteralPath (Join-Path $OutputDir 'evidence.md') -Encoding utf8NoBOM
foreach ($warning in $warnings) { Write-Host "::warning::$warning" }
