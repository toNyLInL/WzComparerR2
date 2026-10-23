# AGENTS.md — WzComparerR2.Export（离线导出器）

WZ → 引擎无关中间格式（PNG + JSON）的**离线**导出器，产物供 Unity 等引擎消费。
上级约定见仓库根 `AGENTS.md`；格式契约见 `docs/plan/spec/wz-export-schema.md`，方案与里程碑见 `docs/plan/wz-to-unity-export.md`。

## 1. 边界

- 目标框架：**只有 `net8.0-windows`**（控制台程序）。`System.Drawing.Common` 只保证 Windows 可用，不要加别的 TFM。
- 只引用 `WzComparerR2.WzLib`。**不允许**引用 `Common` / `PluginBase` / WinForms / MonoGame / SharpDX，
  否则导出器就无法在无桌面环境的 CI 上跑。
- **不进 `.sln`、不进打包产物**（`IsPublishable=false`）：CI 只构建 `WzComparerR2.sln`，导出器不在其中，
  所以改完必须自己单独构建（见 §5）。要把它加进 sln 属于构建配置变更，先在计划里定。
- 导出器**不写 `UpdateLogs/`**：它不是随主程序分发的用户可见功能。

## 2. 文件地图

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口：注册码页、解析参数、加载 wz、调度导出/校验、退出码（0/1/2/3） |
| `ExportOptions.cs` | 命令行解析与用法文本 |
| `WzAssetSource.cs` | wz 加载（文件/文件夹/独立 `.img`）、节点定位、`_inlink`/`_outlink`/`UOL` 解析 |
| `NodePathUtility.cs` | 节点符号路径（跨 img 边界）、链接属性读取 |
| `ExportTargetList.cs` | `--list` 批量清单解析（`->` 子目录）与 `--skip` 通配符匹配 |
| `UiImageExporter.cs` | 分组规则、帧元数据读取、PNG 落盘、meta/manifest 组装 |
| `ExportNaming.cs` | 文件名清洗、Windows 保留名、路径长度保护、SHA-256 工具 |
| `PngSaver.cs` | canvas → RGBA32 → PNG 字节；读回尺寸 |
| `JsonTextWriter.cs` / `JsonExportWriter.cs` | 手写 JSON（2 空格缩进、`\n`、UTF-8 无 BOM），保证字节稳定 |
| `WzTreeLister.cs` | `list --depth <n>` 的节点树打印（只读，用来在大 wz 上先看结构） |
| `ExportVerifier.cs` | `--verify` 的断言与目录比对 |
| `verify/verify-ui-export.ps1` | 无 wz 样本的端到端回归：合成 `.img` 夹具 + 断言 |

## 3. 硬性规则

1. **分组语义与主程序一致**：一个节点的直接 canvas 子节点 = 同一分组的帧；帧的 `origin/z/delay/a0/a1`
   取自帧节点自身，缺失才回落到链接目标。改这条等于改产物含义，必须先改 `spec/wz-export-schema.md`。
2. **img 是懒解压的**：任何遍历之前都要 `Wz_Image.TryExtract()`，并且后续遍历 `Wz_Image.Node`
   （内容节点是游离节点，`ParentNode` 为空，靠 `OwnerNode` 回到树里）。
3. **`delay` 固化**：0 一律写成 120，不留 `0` 给下游猜。
4. **字节稳定**：JSON 不用 `System.Text.Json`（缩进/换行不可控），PNG 统一 RGBA32；
   `manifest.generatedAt` 是唯一允许变化的字段，`--verify` 的目录比对会跳过 `manifest.json`。
5. **容错优先**：单帧解码失败、链接解析失败、img 解压失败都只记警告并跳过，不能让整个导出中断；
   警告同时写进 `manifest.warnings`。
6. **路径安全**：所有落盘路径必须过 `ExportNaming`（非法字符、保留名、240 字符上限、大小写去重）。

## 4. 常见改动形态

- **新增一类资产**（音频/spine/地图）：新建 `XxxExporter`，复用 `WzAssetSource` 与 `JsonTextWriter`，
  在 `ExportOptions.Command` 里加子命令；目录前缀与 meta 字段先写进 spec。
- **新增 meta 字段**：加可选字段不升 `schema`，但 spec 必须同步补行；破坏性变更 `schema` +1。
- **支持新的纹理格式**：改 `WzLib/Wz_Png.ExtractPng`（不属于本工程），本工程只负责转 RGBA32 与记录原尺寸。

## 5. 验证

```powershell
dotnet build .\WzComparerR2.Export\WzComparerR2.Export.csproj -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\WzComparerR2.Export\verify\verify-ui-export.ps1
```

- 第一条必须 **0 error 且 0 warning**（新工程没有历史警告）。
- 第二条在没有 wz 样本时是唯一可自动化的端到端验证；改了分组/链接/命名/JSON 就必须跑它。
- 有真实客户端时补一条人工路径：`wzexport list --wz UI.wz --node "UIWindow.img"` 的帧数要与 QuickView 一致。
- 真实 v271 客户端的 `_Canvas` 是伴生子 wz：整包导出 UI 时用 `--skip _Canvas,WZ2Lua` 避免同一张图在
  「引用处 + 伴生处」各写一遍；需要多张图时优先用 `--list` 批量清单定点导出。

## 6. 别做

- 不要在导出器里 `new Form()`、弹窗、写 `error.log`、引入日志库或 DI。
- 不要把 `Common` 的 `Frame`/`FrameAnimationData` 搬过来（会拖进 MonoGame）：只对齐**语义**，不共享代码。
- 不要为了"顺手"改 `WzLib` 的解析行为；解析层的问题回 `WzLib` 单独修。
- 不要加 `async`/并行导出（`Wz_File.ReadLock` 目前按单文件串行设计）。
