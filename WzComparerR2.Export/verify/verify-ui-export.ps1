<#
.SYNOPSIS
    自动验证 wzexport 的 UI 图片导出（无需真实 wz 客户端）。

.DESCRIPTION
    仓库里没有 wz 样本，所以这里按 Wz_Image.ExtractImg 的二进制布局现场合成两个 .img 夹具
    （Fixture.img / Other.img），覆盖：属性树、Canvas（含 imageobject 子属性）、Vector2D、
    UOL、_inlink、_outlink、文件名需要清洗的节点名。
    然后真跑导出器，并断言目录结构、meta.json 字段、PNG 尺寸与像素、manifest 统计、
    --verify 的字节稳定性、--force 保护、list 模式与 --node 定向下钻。

    用法（Windows PowerShell 5.1 或 pwsh 均可）：
        powershell -NoProfile -File .\WzComparerR2.Export\verify\verify-ui-export.ps1

    前提：先构建导出器
        dotnet build .\WzComparerR2.Export\WzComparerR2.Export.csproj -c Debug
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$WorkDirectory,
    [switch]$KeepWorkspace
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$binDir = Join-Path $repoRoot "WzComparerR2.Export\bin\$Configuration\net8.0-windows"
$exe = Join-Path $binDir 'wzexport.exe'

if (-not (Test-Path $exe)) {
    throw "找不到 $exe；请先执行 dotnet build .\WzComparerR2.Export\WzComparerR2.Export.csproj -c $Configuration"
}

Add-Type -AssemblyName System.Drawing

if (-not $WorkDirectory) {
    $WorkDirectory = Join-Path $env:TEMP ('wzexport-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$WorkDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path $WorkDirectory) { Remove-Item $WorkDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null

$script:failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { $script:failures.Add($Message) }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ("$Expected" -ne "$Actual") {
        $script:failures.Add("$Message（期望 '$Expected'，实际 '$Actual'）")
    }
}

function Assert-File {
    param([string]$Path, [string]$Message)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $script:failures.Add("$Message；缺少文件 $Path") }
}

# ---------------------------------------------------------------- wz img 二进制写出

function Write-WzRawString {
    param([System.IO.BinaryWriter]$w, [string]$text)
    # 长度前缀：字符数的负数表示 ASCII 串；每个字符与 (0xAA + i) 异或
    $w.Write([sbyte](-$text.Length))
    for ($i = 0; $i -lt $text.Length; $i++) {
        $w.Write([byte]((([int][char]$text[$i]) -bxor (0xAA + $i)) -band 0xFF))
    }
}

function Write-WzTypeName {
    param([System.IO.BinaryWriter]$w, [string]$tag)
    $w.Write([byte]0x73)
    Write-WzRawString $w $tag
}

function Write-WzNodeName {
    param([System.IO.BinaryWriter]$w, [string]$name)
    $w.Write([byte]0x00)
    Write-WzRawString $w $name
}

function Write-WzCInt {
    param([System.IO.BinaryWriter]$w, [int]$value)
    if ($value -ge -127 -and $value -le 127) {
        $w.Write([sbyte]$value)
    }
    else {
        $w.Write([sbyte]-128)
        $w.Write([int]$value)
    }
}

function New-WzCanvasData {
    param([int]$width, [int]$height, [int]$seed)
    # ARGB8888 -> 原始字节序是 BGRA
    $pixels = New-Object byte[] ($width * $height * 4)
    for ($p = 0; $p -lt ($width * $height); $p++) {
        $pixels[$p * 4 + 0] = [byte](($p * 4 + $seed) -band 0xFF)
        $pixels[$p * 4 + 1] = [byte](($p * 12 + $seed) -band 0xFF)
        $pixels[$p * 4 + 2] = [byte](($p * 28 + $seed) -band 0xFF)
        $pixels[$p * 4 + 3] = [byte]255
    }

    $ms = New-Object System.IO.MemoryStream
    $ms.WriteByte(0x00)
    $ms.WriteByte(0x78)
    $ms.WriteByte(0x9C)
    $deflate = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $deflate.Write($pixels, 0, $pixels.Length)
    $deflate.Dispose()
    return , $ms.ToArray()
}

function Write-WzObjectBody {
    param([System.IO.BinaryWriter]$w, [hashtable]$node)

    switch ($node.type) {
        'Property' {
            Write-WzTypeName $w 'Property'
            $w.Write([byte]0)
            $w.Write([byte]0)
            $children = @($node.children)
            Write-WzCInt $w $children.Count
            foreach ($child in $children) { Write-WzNodeValue $w $child }
        }
        'Canvas' {
            Write-WzTypeName $w 'Canvas'
            $w.Write([byte]0)
            $props = @($node.props)
            if ($props.Count -eq 0) {
                $w.Write([byte]0)
            }
            else {
                $w.Write([byte]1)
                $w.Write([byte]0)
                $w.Write([byte]0)
                Write-WzCInt $w $props.Count
                foreach ($prop in $props) { Write-WzNodeValue $w $prop }
            }

            $data = New-WzCanvasData $node.width $node.height $node.seed
            Write-WzCInt $w $node.width
            Write-WzCInt $w $node.height
            Write-WzCInt $w 2          # Wz_TextureFormat.ARGB8888
            $w.Write([byte]0)          # scale
            Write-WzCInt $w 0          # pages
            Write-WzCInt $w 0          # unknown1
            $w.Write([byte]0)
            $w.Write([byte]0)
            $w.Write([int]$data.Length)
            $w.Write($data)
        }
        'Vector' {
            Write-WzTypeName $w 'Shape2D#Vector2D'
            Write-WzCInt $w $node.x
            Write-WzCInt $w $node.y
        }
        'Uol' {
            Write-WzTypeName $w 'UOL'
            $w.Write([byte]0)
            $w.Write([byte]0)
            Write-WzRawString $w $node.path
        }
        default { throw "夹具里出现未知对象类型：$($node.type)" }
    }
}

function Write-WzObjectValue {
    param([System.IO.BinaryWriter]$w, [hashtable]$node)
    $tmp = New-Object System.IO.MemoryStream
    $tw = New-Object System.IO.BinaryWriter($tmp)
    Write-WzObjectBody $tw $node
    $tw.Flush()
    $bytes = $tmp.ToArray()
    $w.Write([int]$bytes.Length)
    $w.Write($bytes)
    $tw.Dispose()
    $tmp.Dispose()
}

function Write-WzNodeValue {
    param([System.IO.BinaryWriter]$w, [hashtable]$node)
    Write-WzNodeName $w $node.name
    switch ($node.type) {
        'Int' {
            $w.Write([byte]0x03)
            Write-WzCInt $w $node.value
        }
        'String' {
            $w.Write([byte]0x08)
            $w.Write([byte]0)
            Write-WzRawString $w $node.value
        }
        default {
            $w.Write([byte]0x09)
            Write-WzObjectValue $w $node
        }
    }
}

function New-WzImgFile {
    param([string]$Path, [hashtable]$root)
    $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Create)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        Write-WzObjectBody $writer $root
        $writer.Flush()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

# ---------------------------------------------------------------- 夹具定义

$fixtureRoot = @{
    type = 'Property'
    children = @(
        @{ name = 'Basic'; type = 'Property'; children = @(
            @{ name = 'backgrnd'; type = 'Property'; children = @(
                @{ name = '0'; type = 'Canvas'; width = 4; height = 3; seed = 11; props = @(
                    @{ name = 'origin'; type = 'Vector'; x = 2; y = -3 }
                    @{ name = 'z'; type = 'Int'; value = -7 }
                ) }
                @{ name = '1'; type = 'Canvas'; width = 4; height = 3; seed = 12; props = @(
                    @{ name = 'delay'; type = 'Int'; value = 90 }
                    @{ name = 'a0'; type = 'Int'; value = 100 }
                    @{ name = 'a1'; type = 'Int'; value = 200 }
                ) }
            ) }
            @{ name = 'tile'; type = 'Canvas'; width = 2; height = 2; seed = 13; props = @(
                @{ name = 'origin'; type = 'Vector'; x = 1; y = 1 }
            ) }
        ) }
        @{ name = 'Ani'; type = 'Property'; children = @(
            @{ name = '0'; type = 'Canvas'; width = 5; height = 5; seed = 100; props = @(
                @{ name = 'origin'; type = 'Vector'; x = 2; y = -4 }
                @{ name = 'z'; type = 'Int'; value = 10 }
                @{ name = 'delay'; type = 'Int'; value = 90 }
            ) }
            @{ name = '1'; type = 'Canvas'; width = 5; height = 5; seed = 101; props = @(
                @{ name = 'origin'; type = 'Vector'; x = 2; y = -4 }
                @{ name = 'z'; type = 'Int'; value = 10 }
                @{ name = 'delay'; type = 'Int'; value = 90 }
            ) }
        ) }
        @{ name = 'Links'; type = 'Property'; children = @(
            @{ name = 'link0'; type = 'Canvas'; width = 1; height = 1; seed = 200; props = @(
                @{ name = 'origin'; type = 'Vector'; x = 1; y = 1 }
                @{ name = '_inlink'; type = 'String'; value = 'Ani/0' }
            ) }
            @{ name = 'uol'; type = 'Uol'; path = '../Ani/1' }
            @{ name = 'a:b'; type = 'Canvas'; width = 3; height = 1; seed = 201; props = @() }
        ) }
        @{ name = 'Out'; type = 'Property'; children = @(
            @{ name = 'linked0'; type = 'Canvas'; width = 1; height = 1; seed = 202; props = @(
                @{ name = '_outlink'; type = 'String'; value = 'Other.img/linked/0' }
            ) }
        ) }
    )
}

$otherRoot = @{
    type = 'Property'
    children = @(
        @{ name = 'linked'; type = 'Property'; children = @(
            @{ name = '0'; type = 'Canvas'; width = 3; height = 2; seed = 300; props = @(
                @{ name = 'origin'; type = 'Vector'; x = 1; y = 0 }
            ) }
        ) }
    )
}

$fixturePath = Join-Path $WorkDirectory 'Fixture.img'
$otherPath = Join-Path $WorkDirectory 'Other.img'
$outDir = Join-Path $WorkDirectory 'out'

New-WzImgFile $fixturePath $fixtureRoot
New-WzImgFile $otherPath $otherRoot

Write-Host "夹具已生成：$WorkDirectory"

# ---------------------------------------------------------------- 辅助：读 PNG 头/像素

function Get-PngSize {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24) { throw "不是有效 PNG：$Path" }
    $width = ([int]$bytes[16] -shl 24) + ([int]$bytes[17] -shl 16) + ([int]$bytes[18] -shl 8) + [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) + ([int]$bytes[21] -shl 16) + ([int]$bytes[22] -shl 8) + [int]$bytes[23]
    return @($width, $height)
}

function Get-PngPixel {
    param([string]$Path, [int]$X, [int]$Y)
    $bitmap = New-Object System.Drawing.Bitmap($Path)
    try {
        $color = $bitmap.GetPixel($X, $Y)
        return @($color.A, $color.R, $color.G, $color.B)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Read-Json {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# ---------------------------------------------------------------- 跑导出

function Invoke-Exporter {
    param([string[]]$Arguments)
    # 原生命令的 stderr 在 $ErrorActionPreference = 'Stop' 下会变成终止错误，
    # 这里临时放宽，改为按退出码断言。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return @{ ExitCode = $exitCode; Output = ($output -join "`n") }
}

$result = Invoke-Exporter @('ui', '--wz', $fixturePath, '--wz', $otherPath, '--out', $outDir, '--verify')
Assert-Equal 0 $result.ExitCode "ui 命令退出码（输出：$($result.Output)）"
Assert-True ($result.Output -match '校验通过') '--verify 应报告校验通过'

$uiRoot = Join-Path $outDir 'sprite\ui\Fixture'

# 1) 目录与文件结构
$expectedFiles = @(
    'Basic\meta.json', 'Basic\tile.png',
    'Basic\backgrnd\meta.json', 'Basic\backgrnd\0.png', 'Basic\backgrnd\1.png',
    'Ani\meta.json', 'Ani\0.png', 'Ani\1.png',
    'Links\meta.json', 'Links\link0.png', 'Links\uol.png', 'Links\a_b.png',
    'Out\meta.json', 'Out\linked0.png'
)
foreach ($relative in $expectedFiles) {
    Assert-File (Join-Path $uiRoot $relative) '导出结构不符'
}
Assert-File (Join-Path $outDir 'manifest.json') '缺少 manifest.json'

# 2) Ani 分组：uniform canvas + 帧属性
$ani = Read-Json (Join-Path $uiRoot 'Ani\meta.json')
Assert-True ($null -ne $ani) 'Ani/meta.json 应存在'
if ($ani) {
    Assert-Equal 1 $ani.schema 'meta.schema'
    Assert-Equal 'ui' $ani.kind 'meta.kind'
    Assert-Equal 'Fixture.img/Ani' $ani.source 'Ani 分组 source'
    Assert-Equal 'True' $ani.uniform 'Ani 分组应 uniform'
    Assert-Equal 5 $ani.canvas.width 'Ani canvas 宽'
    Assert-Equal 5 $ani.canvas.height 'Ani canvas 高'
    Assert-Equal 'ARGB8888' $ani.canvas.format 'Ani canvas 格式'
    Assert-Equal 1 $ani.canvas.scale 'Ani canvas scale'
    Assert-Equal 1 $ani.canvas.pages 'Ani canvas pages'
    Assert-Equal 2 $ani.frames.Count 'Ani 帧数'
    Assert-Equal 0 $ani.frames[0].index 'Ani frames[0].index'
    Assert-Equal '0.png' $ani.frames[0].png 'Ani frames[0].png'
    Assert-Equal 10 $ani.frames[0].z 'Ani frames[0].z'
    Assert-Equal 90 $ani.frames[0].delay 'Ani frames[0].delay'
    Assert-Equal 2 $ani.frames[0].origin[0] 'Ani frames[0].origin.x'
    Assert-Equal -4 $ani.frames[0].origin[1] 'Ani frames[0].origin.y'
    Assert-Equal 255 $ani.frames[0].a0 'Ani frames[0].a0'
    Assert-Equal 'Fixture.img/Ani/0' $ani.frames[0].source 'Ani frames[0].source'
    Assert-Equal 1 $ani.frames[1].index 'Ani frames[1].index'
}

# 3) backgrnd 分组：delay 默认值固化 + a0/a1
$backgrnd = Read-Json (Join-Path $uiRoot 'Basic\backgrnd\meta.json')
if ($backgrnd) {
    Assert-Equal 2 $backgrnd.frames.Count 'backgrnd 帧数'
    Assert-Equal 2 $backgrnd.frames[0].origin[0] 'backgrnd frames[0].origin.x'
    Assert-Equal -3 $backgrnd.frames[0].origin[1] 'backgrnd frames[0].origin.y'
    Assert-Equal -7 $backgrnd.frames[0].z 'backgrnd frames[0].z'
    Assert-Equal 120 $backgrnd.frames[0].delay 'backgrnd frames[0].delay 应固化默认 120'
    Assert-Equal 90 $backgrnd.frames[1].delay 'backgrnd frames[1].delay'
    Assert-Equal 100 $backgrnd.frames[1].a0 'backgrnd frames[1].a0'
    Assert-Equal 200 $backgrnd.frames[1].a1 'backgrnd frames[1].a1'
    Assert-Equal 0 $backgrnd.frames[1].origin[0] 'backgrnd frames[1].origin 应缺省为 0'
}

# 4) Basic 分组：单帧（tile 是 Basic 的直接子节点）
$basic = Read-Json (Join-Path $uiRoot 'Basic\meta.json')
if ($basic) {
    Assert-Equal 1 $basic.frames.Count 'Basic 帧数'
    Assert-Equal 'tile.png' $basic.frames[0].png 'Basic 帧文件名'
    Assert-Equal 1 $basic.frames[0].origin[0] 'Basic 帧 origin.x'
}

# 5) Links 分组：_inlink / UOL / 文件名清洗，尺寸不一致 -> uniform=false
$links = Read-Json (Join-Path $uiRoot 'Links\meta.json')
if ($links) {
    Assert-Equal 3 $links.frames.Count 'Links 帧数'
    Assert-Equal 'False' $links.uniform 'Links 分组不应 uniform'
    Assert-True ($null -eq $links.PSObject.Properties['canvas']) 'uniform=false 时不应有 canvas 块'

    Assert-Equal 'Ani/0' $links.frames[0].link 'link0 的链接路径'
    Assert-Equal 'True' $links.frames[0].inlink 'link0 应标记 inlink'
    Assert-Equal 5 $links.frames[0].width '_inlink 帧宽度应取自目标 canvas'
    Assert-Equal 1 $links.frames[0].origin[0] '_inlink 帧 origin 应取自链接节点'

    Assert-Equal '../Ani/1' $links.frames[1].uol 'uol 帧应记录 UOL 路径'
    Assert-Equal 5 $links.frames[1].width 'UOL 帧宽度应取自目标 canvas'
    Assert-Equal 10 $links.frames[1].z 'UOL 帧 z 应取自目标 canvas'
    Assert-Equal 90 $links.frames[1].delay 'UOL 帧 delay 应取自目标 canvas'

    Assert-Equal 'a:b' $links.frames[2].name '第三帧节点名应保留原文'
    Assert-Equal 'a_b.png' $links.frames[2].png '非法文件名字符应被清洗'
    Assert-Equal 3 $links.frames[2].width '清洗节点名后的帧宽度'
}

# 6) Out 分组：跨文件 _outlink
$outMeta = Read-Json (Join-Path $uiRoot 'Out\meta.json')
if ($outMeta) {
    Assert-Equal 1 $outMeta.frames.Count 'Out 帧数'
    Assert-Equal 'Other.img/linked/0' $outMeta.frames[0].link 'linked0 的 outlink 路径'
    Assert-Equal 'False' $outMeta.frames[0].inlink 'linked0 不应标记 inlink'
    Assert-Equal 3 $outMeta.frames[0].width '跨文件帧宽度'
    Assert-Equal 2 $outMeta.frames[0].height '跨文件帧高度'
    Assert-Equal 1 $outMeta.frames[0].origin[0] '跨文件帧 origin 应取自目标 canvas'
    Assert-True ($null -eq $outMeta.frames[0].PSObject.Properties['linkError']) '跨文件帧不应有解析错误'
}

# 7) manifest 统计
$manifest = Read-Json (Join-Path $outDir 'manifest.json')
if ($manifest) {
    Assert-Equal 1 $manifest.schema 'manifest.schema'
    Assert-Equal 'wzexport' $manifest.tool 'manifest.tool'
    Assert-Equal 'ui' $manifest.kind 'manifest.kind'
    Assert-Equal 'Fixture.img' $manifest.root 'manifest.root'
    Assert-Equal 5 $manifest.counts.groups 'manifest.counts.groups'
    Assert-Equal 9 $manifest.counts.frames 'manifest.counts.frames'
    Assert-Equal 9 $manifest.counts.pngFiles 'manifest.counts.pngFiles'
    Assert-Equal 5 $manifest.assets.Count 'manifest.assets 数量'
    Assert-Equal 'Fixture.img' $manifest.files[0].file 'manifest.files[0].file'
    Assert-True ($manifest.toolVersion -match '^\d+\.\d+') 'manifest.toolVersion 应有版本号'
}

# 8) PNG 尺寸与 meta 一致（独立读 IHDR，不复用导出器的校验）
foreach ($item in @(
        @{ Meta = 'Ani\meta.json'; Dir = 'Ani' },
        @{ Meta = 'Basic\backgrnd\meta.json'; Dir = 'Basic\backgrnd' },
        @{ Meta = 'Links\meta.json'; Dir = 'Links' },
        @{ Meta = 'Out\meta.json'; Dir = 'Out' })) {
    $meta = Read-Json (Join-Path $uiRoot $item.Meta)
    if (-not $meta) { continue }
    foreach ($frame in $meta.frames) {
        $pngPath = Join-Path (Join-Path $uiRoot $item.Dir) $frame.png
        if (-not (Test-Path -LiteralPath $pngPath)) { continue }
        $size = Get-PngSize $pngPath
        Assert-Equal $frame.width $size[0] "$($item.Dir)/$($frame.png) 宽度"
        Assert-Equal $frame.height $size[1] "$($item.Dir)/$($frame.png) 高度"
    }
}

# 9) 像素抽查：Ani/0（5x5，seed=100）在 (1,0) 的 BGRA 应为 B=104 G=112 R=128 A=255
$pixel = Get-PngPixel (Join-Path $uiRoot 'Ani\0.png') 1 0
Assert-Equal 255 $pixel[0] 'Ani/0.png 像素 alpha'
Assert-Equal 128 $pixel[1] 'Ani/0.png 像素 R'
Assert-Equal 112 $pixel[2] 'Ani/0.png 像素 G'
Assert-Equal 104 $pixel[3] 'Ani/0.png 像素 B'

# 10) list 模式
$listResult = Invoke-Exporter @('list', '--wz', $fixturePath, '--wz', $otherPath)
Assert-Equal 0 $listResult.ExitCode 'list 命令退出码'
Assert-True ($listResult.Output -match '5 个分组') 'list 应统计到 5 个分组'
Assert-True ($listResult.Output -match '9 帧') 'list 应统计到 9 帧'

# 11) --node 下钻（目录）
$aniOut = Join-Path $WorkDirectory 'out-ani'
$aniResult = Invoke-Exporter @('ui', '--wz', $fixturePath, '--node', 'Ani', '--out', $aniOut)
Assert-Equal 0 $aniResult.ExitCode '--node Ani 退出码'
$aniSub = Read-Json (Join-Path $aniOut 'sprite\ui\Ani\meta.json')
Assert-True ($null -ne $aniSub) '--node Ani 应产出 sprite/ui/Ani/meta.json'
if ($aniSub) { Assert-Equal 2 $aniSub.frames.Count '--node Ani 帧数' }

# 12) --node 指向单张 canvas
$tileOut = Join-Path $WorkDirectory 'out-tile'
$tileResult = Invoke-Exporter @('ui', '--wz', $fixturePath, '--node', 'Basic/tile', '--out', $tileOut, '--verify')
Assert-Equal 0 $tileResult.ExitCode '--node Basic/tile 退出码'
$tileMeta = Read-Json (Join-Path $tileOut 'sprite\ui\Basic\tile\meta.json')
Assert-True ($null -ne $tileMeta) '单 canvas 导出应产出 Basic/tile/meta.json'
if ($tileMeta) {
    Assert-Equal 1 $tileMeta.frames.Count '单 canvas 帧数'
    Assert-Equal 'tile.png' $tileMeta.frames[0].png '单 canvas 帧文件名'
    Assert-Equal 2 $tileMeta.canvas.width '单 canvas 宽度'
}

# 13) 非空输出目录保护
$guardResult = Invoke-Exporter @('ui', '--wz', $fixturePath, '--out', $outDir)
Assert-Equal 1 $guardResult.ExitCode '非空输出目录应返回 1'
Assert-True ($guardResult.Output -match '--force') '非空输出目录的提示应提到 --force'

# 14) 节点不存在
$missingResult = Invoke-Exporter @('ui', '--wz', $fixturePath, '--node', 'NoSuchNode', '--out', (Join-Path $WorkDirectory 'out-missing'))
Assert-Equal 1 $missingResult.ExitCode '不存在的节点应返回 1'

# ---------------------------------------------------------------- 结果

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host 'PASS：UI 图片导出全部断言通过。' -ForegroundColor Green
    if (-not $KeepWorkspace) { Remove-Item $WorkDirectory -Recurse -Force }
    exit 0
}

Write-Host ("FAIL：{0} 项断言未通过。" -f $script:failures.Count) -ForegroundColor Red
foreach ($failure in $script:failures) {
    Write-Host ("  - " + $failure) -ForegroundColor Red
}
Write-Host ''
Write-Host "工作目录保留在 $WorkDirectory，便于复查。"
exit 1
