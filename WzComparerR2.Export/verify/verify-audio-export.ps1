<#
.SYNOPSIS
    自动验证 wzexport 的音频导出（无需真实 wz 客户端）。

.DESCRIPTION
    仓库里没有 wz 样本，所以这里按 Wz_Image.ExtractImg 的二进制布局现场合成一个 Sound.img 夹具，
    覆盖：MPEG1Audio（裸 MP3 码流）、WAVE 子类型（裸 PCM，需补 RIFF 头）、MIDI（落 .bin）、
    `_inlink`、UOL、非法文件名字符、跨目录同内容去重。
    然后真跑导出器，并断言目录结构、audio.json 字段、WAV 头、manifest 统计、
    --verify 的字节稳定性、verify --out 的音频断言与损坏检出、--force 保护。

    用法（Windows PowerShell 5.1 或 pwsh 均可）：
        powershell -NoProfile -File .\WzComparerR2.Export\verify\verify-audio-export.ps1

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

if (-not $WorkDirectory) {
    $WorkDirectory = Join-Path $env:TEMP ('wzexport-audio-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
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

function Get-GuidBytes {
    param([string]$Text)
    # Guid.ToByteArray() 正好是 wz 里 new Guid(16 bytes) 期望的小端布局。
    # 必须原样输出 byte[]（`-NoEnumerate`），否则 PowerShell 会把 16 个字节拆成 16 个参数，
    # BinaryWriter 的 byte[] 重载匹配不上，一个字节也写不进去。
    Write-Output -NoEnumerate ([byte[]]([guid]$Text).ToByteArray())
}

# 假 MP3 码流：首帧同步字 0xFF 0xFB（MPEG-1 Layer III / 128kbps / 44100Hz）+ 填充
function New-FakeMp3Data {
    param([int]$Length, [int]$Seed)
    $data = New-Object byte[] $Length
    $data[0] = 0xFF
    $data[1] = 0xFB
    $data[2] = 0x90
    $data[3] = 0x00
    for ($i = 4; $i -lt $Length; $i++) {
        $data[$i] = [byte](($i * 7 + $Seed) -band 0x7F)   # 保持在 0xFF 以下，避免出现假同步字
    }
    return , $data
}

# 裸 16 位单声道 PCM：偶数长度、不含 0xFF
function New-FakePcmData {
    param([int]$Length)
    $data = New-Object byte[] $Length
    for ($i = 0; $i -lt $Length; $i++) {
        $data[$i] = [byte](($i * 3) -band 0x7E)
    }
    return , $data
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
        'Sound' {
            # 布局见 Wz_Image.ExtractImg 的 "Sound_DX8" 分支
            Write-WzTypeName $w 'Sound_DX8'
            $props = @($node.props)
            if ($props.Count -eq 0) {
                $w.Write([byte]0)          # soundDX8Ver = 0，没有子属性
            }
            else {
                $w.Write([byte]1)          # soundDX8Ver = 1
                $w.Write([byte]1)          # 有子属性
                $w.Write([byte]0)
                $w.Write([byte]0)
                Write-WzCInt $w $props.Count
                foreach ($prop in $props) { Write-WzNodeValue $w $prop }
            }

            $data = $node.data
            $majorType = Get-GuidBytes 'e436eb83-524f-11ce-9f53-0020af0ba770'    # MEDIATYPE_Stream
            $subType = Get-GuidBytes $node.subType
            $formatType = Get-GuidBytes '05589f81-c356-11ce-bf01-00aa0055595a'   # FORMAT_WaveFormatEx

            Write-WzCInt $w $data.Length
            Write-WzCInt $w $node.duration
            $w.Write([byte]0)              # soundDecl = 0：不带 WAVEFORMATEX（避开 fmt 块的加密）
            $w.Write($majorType)
            $w.Write($subType)
            $w.Write([byte]0)              # FixedSizeSamples
            $w.Write([byte]0)              # TemporalCompression
            $w.Write($formatType)
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

$MPEG1AUDIO = 'e436eb87-524f-11ce-9f53-0020af0ba770'
$WAVE       = 'e436eb8b-524f-11ce-9f53-0020af0ba770'
$MIDI       = 'e436eb8c-524f-11ce-9f53-0020af0ba770'

$click = New-FakeMp3Data 400 1          # MP3：应落 .mp3
$beep  = New-FakePcmData 640            # PCM：应落 .wav（补 44 字节头）
$noise = New-FakeMp3Data 64 0           # 子类型是 MIDI：即使像 MP3 也落 .bin
$noise[0] = 0x4D; $noise[1] = 0x54; $noise[2] = 0x68; $noise[3] = 0x64   # "MThd"（MIDI 头）
$other = New-FakeMp3Data 420 9          # 与 click 不同内容

$soundRoot = @{
    type = 'Property'
    children = @(
        @{ name = 'Basic'; type = 'Property'; children = @(
            @{ name = 'click'; type = 'Sound'; duration = 120; subType = $MPEG1AUDIO; data = $click; props = @(
                @{ name = 'z'; type = 'Int'; value = 3 }
            ) }
            @{ name = 'beep'; type = 'Sound'; duration = 250; subType = $WAVE; data = $beep; props = @() }
            @{ name = 'noise'; type = 'Sound'; duration = 300; subType = $MIDI; data = $noise; props = @() }
        ) }
        @{ name = 'Linked'; type = 'Property'; children = @(
            # ver=1 才能挂 _inlink：值解析到 Basic/click
            @{ name = 'dup'; type = 'Sound'; duration = 120; subType = $MPEG1AUDIO; data = $click; props = @(
                @{ name = '_inlink'; type = 'String'; value = 'Basic/click' }
            ) }
        ) }
        @{ name = 'Uols'; type = 'Property'; children = @(
            @{ name = 'u0'; type = 'Uol'; path = '../Basic/beep' }
        ) }
        @{ name = 'Bad'; type = 'Property'; children = @(
            @{ name = 'a:b'; type = 'Sound'; duration = 90; subType = $MPEG1AUDIO; data = $other; props = @() }
        ) }
        @{ name = 'Nested'; type = 'Property'; children = @(
            @{ name = 'Track'; type = 'Property'; children = @(
                # 与 Basic/click 内容完全相同（跨目录同内容）
                @{ name = 'same'; type = 'Sound'; duration = 120; subType = $MPEG1AUDIO; data = $click; props = @() }
                @{ name = 'other'; type = 'Sound'; duration = 130; subType = $MPEG1AUDIO; data = $other; props = @() }
            ) }
        ) }
    )
}

$soundPath = Join-Path $WorkDirectory 'Sound.img'
$outDir = Join-Path $WorkDirectory 'out'
New-WzImgFile $soundPath $soundRoot
Write-Host "夹具已生成：$WorkDirectory"

# ---------------------------------------------------------------- 辅助

function Read-Json {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-Bytes {
    param([string]$Path)
    return [System.IO.File]::ReadAllBytes($Path)
}

function Get-Sha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Invoke-Exporter {
    param([string[]]$Arguments)
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

# ---------------------------------------------------------------- 跑导出

$result = Invoke-Exporter @('audio', '--wz', $soundPath, '--out', $outDir, '--verify')
Assert-Equal 0 $result.ExitCode "audio 命令退出码（输出：$($result.Output)）"
Assert-True ($result.Output -match '校验通过') '--verify 应报告校验通过'

$audioRoot = Join-Path $outDir 'audio\sound\Sound'

# 1) 目录与文件结构
$expectedFiles = @(
    'Basic\audio.json', 'Basic\click.mp3', 'Basic\beep.wav', 'Basic\noise.bin',
    'Linked\audio.json', 'Linked\dup.mp3',
    'Uols\audio.json', 'Uols\u0.wav',
    'Bad\audio.json', 'Bad\a_b.mp3',
    'Nested\Track\audio.json', 'Nested\Track\same.mp3', 'Nested\Track\other.mp3'
)
foreach ($relative in $expectedFiles) {
    Assert-File (Join-Path $audioRoot $relative) '导出结构不符'
}
Assert-File (Join-Path $outDir 'manifest.json') '缺少 manifest.json'

# 2) Basic 分组：三种容器格式
$basic = Read-Json (Join-Path $audioRoot 'Basic\audio.json')
Assert-True ($null -ne $basic) 'Basic/audio.json 应存在'
if ($basic) {
    Assert-Equal 1 $basic.schema 'audio.schema'
    Assert-Equal 'sound' $basic.kind 'audio.kind'
    Assert-Equal 'Sound.img/Basic' $basic.source 'Basic 分组 source'
    Assert-Equal 3 $basic.entries.Count 'Basic 条目数'

    Assert-Equal 0 $basic.entries[0].index 'entries[0].index'
    Assert-Equal 'click' $basic.entries[0].name 'entries[0].name'
    Assert-Equal 'click.mp3' $basic.entries[0].file 'entries[0].file'
    Assert-Equal 'mp3' $basic.entries[0].format 'entries[0].format 应为 mp3'
    Assert-Equal 400 $basic.entries[0].bytes 'entries[0].bytes'
    Assert-Equal 120 $basic.entries[0].duration 'entries[0].duration'
    Assert-Equal 'Sound.img/Basic/click' $basic.entries[0].source 'entries[0].source'

    Assert-Equal 'wav' $basic.entries[1].format 'entries[1].format 应为 wav'
    Assert-Equal 684 $basic.entries[1].bytes 'entries[1].bytes 应为 640+44'
    Assert-Equal 'bin' $basic.entries[2].format 'entries[2].format 应为 bin（MIDI 子类型）'
    Assert-Equal 64 $basic.entries[2].bytes 'entries[2].bytes'
}

# 3) MP3：原样落盘（首字节就是同步字，没有被补头）
$clickBytes = Get-Bytes (Join-Path $audioRoot 'Basic\click.mp3')
Assert-Equal 400 $clickBytes.Length 'click.mp3 长度应等于原始字节数'
Assert-Equal 255 $clickBytes[0] 'click.mp3 首字节应为 0xFF'
Assert-Equal 251 $clickBytes[1] 'click.mp3 次字节应为 0xFB'

# 4) WAV：44 字节 RIFF 头 + 原始字节，且头里的参数自洽
$wavBytes = Get-Bytes (Join-Path $audioRoot 'Basic\beep.wav')
Assert-Equal 684 $wavBytes.Length 'beep.wav 长度应为 640+44'
Assert-Equal 'RIFF' ([System.Text.Encoding]::ASCII.GetString($wavBytes, 0, 4)) 'beep.wav RIFF 头'
Assert-Equal 'WAVE' ([System.Text.Encoding]::ASCII.GetString($wavBytes, 8, 4)) 'beep.wav WAVE 头'
Assert-Equal 'fmt ' ([System.Text.Encoding]::ASCII.GetString($wavBytes, 12, 4)) 'beep.wav fmt 块'
Assert-Equal 'data' ([System.Text.Encoding]::ASCII.GetString($wavBytes, 36, 4)) 'beep.wav data 块'
Assert-Equal 640 ([System.BitConverter]::ToInt32($wavBytes, 40)) 'beep.wav data 块长度'
Assert-Equal 676 ([System.BitConverter]::ToInt32($wavBytes, 4)) 'beep.wav RIFF 长度 = 文件长-8'
Assert-Equal 1 ([System.BitConverter]::ToUInt16($wavBytes, 22)) 'beep.wav 声道数'
Assert-Equal 22050 ([System.BitConverter]::ToInt32($wavBytes, 24)) 'beep.wav 采样率'
Assert-Equal 16 ([System.BitConverter]::ToUInt16($wavBytes, 34)) 'beep.wav 位宽'
# PCM 数据区必须与原始字节逐字节一致
$pcmOk = $true
for ($i = 0; $i -lt 640; $i++) {
    if ($wavBytes[44 + $i] -ne $beep[$i]) { $pcmOk = $false; break }
}
Assert-True $pcmOk 'beep.wav 数据区应与原始 PCM 字节一致'

# 5) _inlink：解析到 Basic/click 的内容，但保留自己的名字与链接信息
$linked = Read-Json (Join-Path $audioRoot 'Linked\audio.json')
if ($linked) {
    Assert-Equal 1 $linked.entries.Count 'Linked 条目数'
    Assert-Equal 'dup.mp3' $linked.entries[0].file 'Linked 文件名应取自己的节点名'
    Assert-Equal 'mp3' $linked.entries[0].format 'Linked format'
    Assert-Equal 'Basic/click' $linked.entries[0].link 'Linked link 路径'
    Assert-Equal 'True' $linked.entries[0].inlink 'Linked 应标记 inlink'
    Assert-Equal $basic.entries[0].sha256 $linked.entries[0].sha256 '_inlink 内容应与目标一致'
    Assert-True ($null -eq $linked.entries[0].PSObject.Properties['linkError']) '_inlink 不应有解析错误'
}

# 6) UOL：记录原路径并解析到目标内容
$uols = Read-Json (Join-Path $audioRoot 'Uols\audio.json')
if ($uols) {
    Assert-Equal 1 $uols.entries.Count 'Uols 条目数'
    Assert-Equal '../Basic/beep' $uols.entries[0].uol 'UOL 原路径应被记录'
    Assert-Equal 'wav' $uols.entries[0].format 'UOL 目标应是 wav'
    Assert-Equal $basic.entries[1].sha256 $uols.entries[0].sha256 'UOL 内容应与目标一致'
}

# 7) 非法文件名字符被清洗
$bad = Read-Json (Join-Path $audioRoot 'Bad\audio.json')
if ($bad) {
    Assert-Equal 'a:b' $bad.entries[0].name '非法节点名应保留原文'
    Assert-Equal 'a_b.mp3' $bad.entries[0].file '非法字符应被清洗成 _'
}

# 8) 跨目录同内容：同名不同目录各自落盘、内容 hash 相同
$track = Read-Json (Join-Path $audioRoot 'Nested\Track\audio.json')
if ($track) {
    Assert-Equal 2 $track.entries.Count 'Track 条目数'
    Assert-Equal $basic.entries[0].sha256 $track.entries[0].sha256 '跨目录同内容应保持相同 hash'
    Assert-True ($track.entries[0].sha256 -ne $track.entries[1].sha256) '不同内容应有不同 hash'
}

# 9) 每个文件的 sha256 与 audio.json 记录一致
$allGroups = @('Basic', 'Linked', 'Uols', 'Bad', 'Nested\Track')
$checkedFiles = 0
foreach ($groupRel in $allGroups) {
    $meta = Read-Json (Join-Path $audioRoot "$groupRel\audio.json")
    if (-not $meta) { continue }
    foreach ($entry in $meta.entries) {
        $filePath = Join-Path (Join-Path $audioRoot $groupRel) $entry.file
        if (-not (Test-Path -LiteralPath $filePath)) { continue }
        Assert-Equal $entry.sha256 (Get-Sha256 $filePath) "$groupRel/$($entry.file) 的 sha256"
        Assert-Equal $entry.bytes (Get-Bytes $filePath).Length "$groupRel/$($entry.file) 的字节数"
        $checkedFiles++
    }
}
Assert-Equal 8 $checkedFiles '应校验到 8 个音频文件'

# 10) manifest 统计
$manifest = Read-Json (Join-Path $outDir 'manifest.json')
if ($manifest) {
    Assert-Equal 1 $manifest.schema 'manifest.schema'
    Assert-Equal 'wzexport' $manifest.tool 'manifest.tool'
    Assert-Equal 'sound' $manifest.kind 'manifest.kind 默认应为 sound'
    Assert-Equal 'True' $manifest.audioOnly '音频清单应标记 audioOnly'
    Assert-Equal 'Sound.img' $manifest.root 'manifest.root'
    Assert-Equal 5 $manifest.counts.audioGroups 'manifest.counts.audioGroups'
    Assert-Equal 8 $manifest.counts.audioFiles 'manifest.counts.audioFiles'
    Assert-Equal 5 $manifest.audio.Count 'manifest.audio 条目数（每个分组一条）'
    Assert-True ($null -eq $manifest.counts.PSObject.Properties['pngFiles']) '纯音频清单不应有 pngFiles'
    Assert-True ($null -eq $manifest.PSObject.Properties['assets']) '纯音频清单不应有 assets'
    Assert-Equal 1 $manifest.targets.Count 'manifest.targets 数量'
}

# 11) list 模式统计音频（不写文件）
$listResult = Invoke-Exporter @('list', '--wz', $soundPath)
Assert-Equal 0 $listResult.ExitCode 'list 命令退出码'
Assert-True ($listResult.Output -match '音频统计：5 个分组') "list 应统计到 5 个音频分组（输出：$($listResult.Output)）"
Assert-True ($listResult.Output -match '8 个音频') 'list 应统计到 8 个音频'

# 12) ui 命令不导出音频，但会提示改用 audio
$uiOut = Join-Path $WorkDirectory 'out-ui'
$uiResult = Invoke-Exporter @('ui', '--wz', $soundPath, '--out', $uiOut)
Assert-Equal 0 $uiResult.ExitCode "ui 命令退出码（输出：$($uiResult.Output)）"
Assert-True (-not (Test-Path (Join-Path $uiOut 'audio'))) 'ui 命令不应产出 audio/ 目录'
Assert-True ($uiResult.Output -match 'wzexport audio') 'ui 遇到音频应提示改用 audio 命令'
$uiManifest = Read-Json (Join-Path $uiOut 'manifest.json')
if ($uiManifest) {
    Assert-Equal 0 $uiManifest.counts.frames 'ui 在纯音频夹具上应导出 0 帧'
}

# 13) 非空输出目录保护
$guardResult = Invoke-Exporter @('audio', '--wz', $soundPath, '--out', $outDir)
Assert-Equal 1 $guardResult.ExitCode '非空输出目录应返回 1'
Assert-True ($guardResult.Output -match '--force') '非空输出目录的提示应提到 --force'

# 14) 节点不存在
$missingResult = Invoke-Exporter @('audio', '--wz', $soundPath, '--node', 'NoSuchNode', '--out', (Join-Path $WorkDirectory 'out-missing'))
Assert-Equal 1 $missingResult.ExitCode '不存在的节点应返回 1'

# 15) --node 指向单个音频：独成一目录，目录取父节点路径
$oneOut = Join-Path $WorkDirectory 'out-one'
$oneResult = Invoke-Exporter @('audio', '--wz', $soundPath, '--node', 'Basic/click', '--out', $oneOut, '--verify')
Assert-Equal 0 $oneResult.ExitCode "--node 单音频退出码（输出：$($oneResult.Output)）"
$oneMeta = Read-Json (Join-Path $oneOut 'audio\sound\Basic\audio.json')
Assert-True ($null -ne $oneMeta) '单音频导出应落到父目录 Basic/audio.json'
if ($oneMeta) { Assert-Equal 1 $oneMeta.entries.Count '单音频导出应只有 1 个条目' }

# 16) --skip 排除子树
$skipOut = Join-Path $WorkDirectory 'out-skip'
$skipResult = Invoke-Exporter @('audio', '--wz', $soundPath, '--skip', 'Nested', '--out', $skipOut)
Assert-Equal 0 $skipResult.ExitCode '--skip 退出码'
Assert-True (-not (Test-Path (Join-Path $skipOut 'audio\sound\Sound\Nested'))) '--skip 应排除 Nested 子树'
$skipManifest = Read-Json (Join-Path $skipOut 'manifest.json')
if ($skipManifest) {
    Assert-Equal 4 $skipManifest.counts.audioGroups '--skip 后的分组数'
    Assert-Equal 6 $skipManifest.counts.audioFiles '--skip 后的音频数'
}

# 17) --max-audio-bytes 过小应跳过并告警
$limitOut = Join-Path $WorkDirectory 'out-limit'
$limitResult = Invoke-Exporter @('audio', '--wz', $soundPath, '--max-audio-bytes', '100', '--out', $limitOut)
Assert-Equal 0 $limitResult.ExitCode '--max-audio-bytes 退出码'
Assert-True ($limitResult.Output -match '音频过大') '--max-audio-bytes 应给出跳过告警'

# 18) verify --out：通过
$verifyResult = Invoke-Exporter @('verify', '--out', $outDir)
Assert-Equal 0 $verifyResult.ExitCode "verify 已存在产物应通过（输出：$($verifyResult.Output)）"
Assert-True ($verifyResult.Output -match '音频 5 组 / 8 个') 'verify 应报告音频统计'

# 19) verify 能发现音频内容损坏（大小不变、内容被改）
$tamperPath = Join-Path $audioRoot 'Basic\click.mp3'
$tamperBackup = [System.IO.File]::ReadAllBytes($tamperPath)
$tampered = [byte[]]$tamperBackup.Clone()
$tampered[100] = $tampered[100] -bxor 0xFF
[System.IO.File]::WriteAllBytes($tamperPath, $tampered)
$tamperResult = Invoke-Exporter @('verify', '--out', $outDir)
Assert-Equal 3 $tamperResult.ExitCode '被篡改的音频应让 verify 返回 3'
Assert-True ($tamperResult.Output -match 'sha256 不一致') '应报告 sha256 不一致'
[System.IO.File]::WriteAllBytes($tamperPath, $tamperBackup)

# 20) verify 能发现缺失与签名损坏
Remove-Item $tamperPath -Force
$missingAudio = Invoke-Exporter @('verify', '--out', $outDir)
Assert-Equal 3 $missingAudio.ExitCode '缺失的音频应让 verify 返回 3'
Assert-True ($missingAudio.Output -match '缺少音频文件') '应报告缺少音频文件'
[System.IO.File]::WriteAllBytes($tamperPath, $tamperBackup)

$badHeaderPath = Join-Path $audioRoot 'Basic\click.mp3'
[System.IO.File]::WriteAllBytes($badHeaderPath, [byte[]](1, 2, 3, 4, 5, 6, 7, 8))
$badHeader = Invoke-Exporter @('verify', '--out', $outDir)
Assert-Equal 3 $badHeader.ExitCode '签名损坏的音频应让 verify 返回 3'
[System.IO.File]::WriteAllBytes($badHeaderPath, $tamperBackup)

$restoreResult = Invoke-Exporter @('verify', '--out', $outDir)
Assert-Equal 0 $restoreResult.ExitCode '恢复文件后应重新通过'

# 21) 参数约束
$vConflict = Invoke-Exporter @('verify', '--out', $outDir, '--node', 'Basic')
Assert-Equal 1 $vConflict.ExitCode 'verify 与 --node 同时使用应返回 1'
$vNoOut = Invoke-Exporter @('verify')
Assert-Equal 1 $vNoOut.ExitCode 'verify 缺少 --out 应返回 1'
$audioNoOut = Invoke-Exporter @('audio', '--wz', $soundPath)
Assert-Equal 1 $audioNoOut.ExitCode 'audio 缺少 --out 应返回 1'

# ---------------------------------------------------------------- 结果

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host 'PASS：音频导出全部断言通过。' -ForegroundColor Green
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
