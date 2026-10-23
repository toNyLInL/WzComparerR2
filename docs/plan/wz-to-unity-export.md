# 计划：WZ 资源导出到外部引擎（以 Unity 为例）

| 项 | 值 |
| --- | --- |
| 状态 | **进行中**：M1 已定稿，M2 的 UI canvas 部分已落地，见 §11 |
| 创建日期 | 2026-10-21 |
| 分支 | `chore/submodule-repoint` |
| 来源 | 本次会话：资源查看器导出能力调研 + Debug 构建基线 + Unity adapter 方案设计 |
| 关联代码 | `WzComparerR2.WzLib`、`WzComparerR2.Common`、`WzComparerR2`、`WzComparerR2.Export` |
| 本计划产物 | `docs/plan/`（本文件 + `spec/wz-export-schema.md`） |

> 本文件只描述**待做**的事。已经完成的事见 §10/§11，不重复排期。
> 本次不涉及用户可见行为变更，因此**不写 `UpdateLogs/dev.md`**（该文件只记行为变化）。

---

## 0. 一句话

不要在 Unity 里运行时读 wz，改成「**离线导出器 → 引擎无关中间格式 → Unity 侧薄 adapter**」三段式，先定 schema 再写代码。

---

## 1. 背景

### 1.1 现有导出能力（已核实到代码行）

| 入口 | 产出 | 位置 |
| --- | --- | --- |
| wz 树右键 → Export | 选中 img 解压后的 `.img` 原始字节 | [MainForm.cs:1777](../../WzComparerR2/MainForm.cs#L1777) |
| wz 树右键 → Dump as Xml | img 节点树 → XML | [MainForm.cs:1809](../../WzComparerR2/MainForm.cs#L1809) |
| 元素树右键 → Save as... (Ctrl+S) | string→`.txt`；`Wz_Sound`/`Wz_Video`/`Wz_RawData`→原始字节；`Wz_Png`→**裸数据 `.bin`** | [MainForm.cs:2452](../../WzComparerR2/MainForm.cs#L2452) |
| QuickView → SavePicture / Save with options | 当前帧 PNG | [MainForm.cs:429](../../WzComparerR2/MainForm.cs#L429) |
| QuickView → ExtractGif / ExtractGifEx | GIF / APNG / FFmpeg 编码 | [MainForm.cs:686](../../WzComparerR2/MainForm.cs#L686) |
| QuickView → Auto Save | 自动命名落盘（最接近批量） | [MainForm.cs:654](../../WzComparerR2/MainForm.cs#L654) |
| 声音面板 → Save | 当前音频原始字节 | [MainForm.cs:2363](../../WzComparerR2/MainForm.cs#L2363) |
| Tools → ExportSkill | 每张 DataTable 一个 `.csv`（UTF-8） | [MainForm.cs:3253](../../WzComparerR2/MainForm.cs#L3253) |
| Tools → ExportSkillOption | `SkillOption.html` + `items/*.png` + `skills/*.png` | [DBConnection.cs:251](../../WzComparerR2/DBConnection.cs#L251) |
| tooltip 悬浮窗 → Save | tooltip 位图 → PNG | [AfrmTooltip.cs:204](../../WzComparerR2/CharaSimControl/AfrmTooltip.cs#L204) |
| Avatar 插件 | PNG（序列存 `名称_i.png`） | [AvatarForm.cs:968](../../WzComparerR2.Avatar/UI/AvatarForm.cs#L968) |
| MapRender 插件 | 场景截图（背景色可配） | `FrmMapRender2.SceneRendering.cs` 的 `Capture` |

**结论**：提取单件能力齐全，但**没有「整目录批量导出 + 元数据」的管线**，也**没有引擎可消费的格式**（PNG 有，origin/z/delay 这类语义全丢）。

### 1.2 三个已确认的坑（不要重犯）

1. `Wz_Png` 节点「Save as...」存的是 `.bin` 裸像素，不是 PNG；拿图片必须走 QuickView 或 tooltip。
2. `buttonItemSaveChara`（“Save”）在 Designer 里创建但**没有绑定 `Click`**（[MainForm.Designer.cs:45](../../WzComparerR2/MainForm.Designer.cs#L45)），是死按钮。
3. 动画编码器四选一（`BuildInGif` / `IndexGif` / `BuildInApng` / `FFmpeg`），透明度表现不同，导出异常要换编码器对比。

### 1.3 为什么不能在 Unity 运行时读 wz

| 约束 | 依据 |
| --- | --- |
| WzLib 依赖 GDI+ | `net462` 用 `System.Drawing`、`net8` 用 `System.Drawing.Common`（PNG 解码） |
| 依赖 Windows 桌面栈 | WinForms、`System.Configuration`（`Setting.config`）、SharpDX、MonoGame、原生 libvpx/libyuv |
| 运行时成本 | 大 wz 上 GB；img 懒解压 + checksum 校验 + `Wz_File.ReadLock`，不适合游戏运行时 |

### 1.4 Debug 构建基线（已实测，`--no-incremental`）

| 配置 | 结果 | 耗时 | 警告 |
| --- | --- | --- | --- |
| `Debug \| Any CPU` | 0 错误 | 19.5s | 91 |
| `Debug \| x86` | 0 错误 | 25.5s | 91 |

注意：`bin\x86\Debug\<tfm>\Plugin` 为空是**正常现象**（`MainProgramOutputDir` 不含 Platform 段，插件统一落 Any CPU 目录；CI 也只从 x86 取 exe）。详见根 `AGENTS.md` §2。

---

## 2. 目标 / 非目标

**目标**

- 建立可重复、可增量、可断言的导出管线：`wz → (PNG + 图集 + JSON + WAV + spine) → Unity`。
- 中间格式**引擎无关**（同一份产物 Godot/Cocos 也能用），语义不丢（origin/z/delay/alpha）。
- 导出器复用现有 `WzComparerR2.WzLib`，不重写解析。

**非目标**

- 不在 Unity 端复刻 CharaSim 的数值/tooltip 业务逻辑。
- 不搬迁 MapRender 的运行时代码（依赖 `GraphicsDevice`）。
- 不改动 wz 解析层（除非新格式确实读不出来，那属于 WzLib 自己的任务）。
- 不做「整个客户端一键全量导出」（首版按指定 wz/目录导出）。

---

## 3. 架构决策

**ADR-1｜离线导出，运行时零 wz 依赖。**
导出器是独立 .NET 控制台程序，跑在开发机/CI；Unity 只消费产物。理由见 §1.3。

**ADR-2｜中间格式保留 wz 原始语义，不做引擎归一化。**
`origin` 保持像素偏移、`z` 保持整数、`delay` 保持毫秒。归一化（Y 翻转、除以 PPU、sortingOrder）放在 Unity adapter。理由：一份产物多引擎复用；语义丢失不可逆。

**ADR-3｜不用 Unity `Sprite.pivot` 表达 origin。**
`Frame.Origin` 的绘制矩形是 `(-Origin.X, -Origin.Y, w, h)`（[Frame.cs](../../WzComparerR2.Common/Animation/Frame.cs)），而 Unity pivot 是 (0..1) 归一化，wz 的 origin 经常越界（负值、图外）。因此：pivot 固定，**位置偏移由数据驱动** `localPosition = (-origin.x, +origin.y) / PPU`。

**ADR-4｜动画用自定义播放器，不用 AnimationClip 烘。**
wz 的 `A0/A1` 帧内 alpha 插值与 `Delay`（0 会被强制成 120ms）用 Unity 曲线烘会失真。用 `FrameAnimator` 语义的播放器 + `MaterialPropertyBlock` 改 alpha。

---

## 4. 中间格式规范 v0（草案，待评审后定型）

```
wz-export/
├── manifest.json                  # schema 版本、wz 版本号、导出时间、工具版本、条目 hash
├── sprite/item/1302000/{0.png, meta.json}
├── anim/mob/8144006/stand/{0.png,1.png,…, meta.json}
├── spine/mob/8144006/{skeleton.json, atlas.json, tex/*.png}
├── audio/bgm/xxx.wav
└── atlas/                         # 可选：合并图集 + 映射表
```

`meta.json`：

```json
{
  "schema": 2,
  "source": "Mob.wz/8144006.img/stand",
  "canvas": { "width": 32, "height": 40, "format": "ARGB8888", "scale": 1, "pages": 1 },
  "frames": [
    { "page": 0, "png": "0.png", "origin": [-10, -20], "z": 0, "delay": 120, "a0": 255, "a1": 255 }
  ]
}
```

约束：

- `schema` 为整数，破坏性变更 +1。
- `delay` 导出时**固化默认值**（0 → 120），不留歧义。
- 多页画布（`pages > 1`）与 `scale`（双倍图）必须显式记录，否则缺帧/尺寸错。
- `source` 记录 wz 全路径，便于反查与增量。

---

## 5. 语义映射契约（6 条，实现时的硬约束）

1. **坐标系**：wz 像素、Y 向下、整数；Unity 米、PPU=100（或 1），相机 `orthographicSize = 屏高/2/PPU`，开像素对齐。
2. **锚点**：pivot 固定 + 数据驱动偏移；若必须用 pivot，公式 `(origin.x/w, 1 - origin.y/h)`，且仅当 origin 在图内有效。
3. **时间基**：按 `delay`（ms）累加，禁用「帧率假设」；`A0→A1` 线性插值由材质/MaterialPropertyBlock 驱动。
4. **排序**：`SortingOrder = z`（int16 够用），但**同 z 必须加 tie-breaker**（层级索引、数组索引），Unity 同值顺序不稳定。
5. **纹理**：`ARGB4444/1555/RGB565` 通道序与 Unity 不同 → 首版统一烘 RGBA32 PNG；`BC7` 需 4 对齐（参考 [WzLibExtension.cs](../../WzComparerR2.Common/Rendering/WzLibExtension.cs) 的 `width & ~3`）；`DXT1/3/5`、`A8` 单独处理；多页/scale 见 §4。
6. **引用解析**：`_inlink`/`_outlink`/`Wz_Uol` 导出时解引用到实体并保留引用关系；同内容 canvas 按 hash 去重但保留引用计数；`String.wz` 走本地化表，不写死文案。

---

## 6. 里程碑与任务

> 规模：S ≈ 半个会话内可完成；M ≈ 1 个会话；L ≈ 多个会话。

### M1｜规范定稿（规模 S，无代码风险）

- **状态：已完成（2026-10-23）** → [spec/wz-export-schema.md](spec/wz-export-schema.md)（schema 1：字段字典、命名规则、校验规则）。
- 产出：`docs/plan/spec/wz-export-schema.md`（本文件 §4/§5 的完整版 + 字段字典 + 示例产物）。
- 验收：能据此**不写代码**手工核对一份导出目录是否合规；`schema` 版本策略写明。
- 依赖：无。**建议 M1 先于一切代码**（schema 定错则 M4 返工）。

### M2｜导出器 P0：静态 canvas（规模 M）

- **状态：部分完成（2026-10-23）** —— UI 类 canvas 的「PNG + meta.json + manifest.json + `--verify`」已可用，见 §11；
  未做：`--list <file>` 批量清单、同内容去重、音频（属 M3）。
- 新建 `WzComparerR2.Export/`（`net8.0-windows` 控制台，`ProjectReference` 仅 `WzComparerR2.WzLib`）。
- 命令形态：`wzexport --wz <file|folder> --node "<wz 路径>" --out <dir>`，支持 `--list <file>` 批量。
- 产出：PNG + `meta.json` + `manifest.json`。
- 验收：① 导出后可被 `System.Drawing` 重新打开且尺寸 = 声明值；② 同一输入重跑**字节一致**；③ 目录结构与 §4 一致。
- 风险：新项目会进 `WzComparerR2.sln`，CI 会连带构建。缓解：`IsPublishable=false`、不进打包产物；若 CI 时间敏感则 sln 内排除。
  **实际处理**：当前**未加进 `.sln`**（CI 不受影响），构建命令见 §11。

### M3｜动画 + 音频（规模 M）

- 帧序列 + `frames[]`（page/origin/z/delay/a0/a1）；音频用 `Wz_Sound.ExtractSound()`（**已会补 WAV 头**，[Wz_Sound.cs:126](../../WzComparerR2.WzLib/Wz_Sound.cs#L126)），MP3 原样落 `.mp3` 或转 OGG。
- 验收：帧数 = 节点数；`0 → 120ms` 默认值已固化；WAV 能被 `ffprobe`/播放器识别。

### M4｜Unity adapter（规模 M，代码在 Unity 工程侧）

- 本仓库只维护契约与测试数据；Unity 侧实现：`MetaAsset` 导入器（JSON→ScriptableObject）、`FrameAnimator`（delay/alpha/z）、`SortingOrder` 映射、像素对齐相机预设。
- 验收：同一条动画在 Unity 与 QuickView 中逐帧对比截图一致（至少抽 3 条：装备、怪物、特效）。

### M5｜Spine（规模 L）

- 版本对齐是主要成本：仓库里存在 V2 与 V4 两套（`SpineAnimationDataV2/V4`，对应 spine 3.8 / 4.x），Unity 侧 `spine-unity` 必须同版本；atlas 需转 Unity 版格式。
- 验收：一侧怪物动画在 Unity 中骨骼动画完整播放、无贴图错位。

### M6｜视频与地图（规模 L，可拆）

- 视频：MCV0 = **VP8/VP9 裸流 + 可选独立 alpha 通道**（[MaplestoryCanvasVideoLoader.cs](../../WzComparerR2.Common/Animation/MaplestoryCanvasVideoLoader.cs)），Unity `VideoPlayer` 读不了该容器 → 走 ffmpeg 转 mp4/h264（alpha 单独合成）或直接烘序列帧。
- 地图：拆「分层几何 JSON + 图集」，参考 [MapScene.cs](../../WzComparerR2.MapRender/MapScene.cs) 的 `Back/Layers[0..7]/Fly/Front/Effect` 结构；**不搬 `MapData` 运行时代码**。

### M7｜数据表（规模 M，条件触发）

- 复用 `Common/CharaSim` 的 40 个模型（**仅 `Gear.cs`、`DamageSkin.cs` 因 `BitmapOrigin` 依赖 GDI+**，且不依赖 XNA），把 `BitmapOrigin` 换成「png 路径 + origin」后导出 JSON。
- 只有当计划要「图鉴/数值展示」时才做；公式与 tooltip 文案留在展示层，不进游戏逻辑。

---

## 7. 风险登记

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| schema 定错，M4 返工 | 高 | M1 先定稿并冻结 `schema` 版本 |
| 新项目进 sln 拖慢/拖坏 CI | 中 | `IsPublishable=false`，必要时 sln 排除；CI 不改发布逻辑 |
| 纹理格式转换出现色差 | 中 | 首版统一 RGBA32；建立「同图两次导出 + 与 QuickView 截图」比对 |
| BC7 尺寸 4 对齐导致边缘丢失 | 中 | 导出时补边/裁切并在 meta 记录原尺寸 |
| Spine 版本错配 | 高 | 先盘点客户端 spine 版本分布，再决定对齐哪一版 |
| 大 wz 导出内存/耗时 | 中 | 按 img 并行、单文件内串行（`Wz_File.ReadLock`）、增量缓存 |
| 无可自动化回归（仓库无测试工程） | 中 | 用断言脚本（尺寸/帧数/z 范围/字节稳定）替代单测 |

---

## 8. 验证与回归策略

本仓库没有测试工程、CI 只构建不运行，所以每个里程碑自带：

1. **断言脚本**（导出器内 `--verify` 模式）：尺寸 = `canvas.width/height`、帧数 = 节点数、`z ∈ [-9999, 9999]`、`delay > 0`（实测客户端有 `600000` 的长定格，不设上界）。
2. **字节稳定**：固定 PNG 编码参数，同输入二次导出 diff 为空。
3. **人工抽样**：每类资产抽 1~3 个，与 QuickView/MapRender 截图比对（附截图进 PR/计划更新）。
4. **构建门槛**：仍以根 `AGENTS.md` §8 为准（`net8.0-windows` 主程序构建通过且不新增警告）。

---

## 9. 需要决策的开放问题（阻塞 M1 定稿）

1. **用途**：外观复刻（编辑器/图鉴）还是游戏资产管线？→ 决定是否做 M6 地图、M7 数据表。
2. **Unity 版本与渲染管线**：Built-in / URP？→ 决定 alpha 材质与 shader 方案。
3. **spine-unity 版本**：3.8 还是 4.x？→ 决定 M5 导出哪一类 spine。
4. **产物落地方式**：`Assets/Resources` 还是 Addressables？→ 决定 manifest 的组织粒度。
5. **是否需要多语言**（String.wz 多区域）→ 决定本地化表结构。

---

## 10. 本次会话已完成（不在本计划内，仅记录）

| 事项 | 结果 |
| --- | --- |
| 代理协作文档 | 新增根 `AGENTS.md` + 5 个模块 `AGENTS.md`（WzLib / Common / PluginBase / 主程序 / MapRender），含 7 个角色的子代理分工与提示词模板 |
| Debug 构建基线 | `Any CPU` 与 `x86` 均 0 错误（19.5s / 25.5s，91 警告，全为历史遗留） |
| 导出能力调研 | 结论见 §1.1，三个坑见 §1.2 |
| 中间格式与语义映射 | 结论见 §4/§5（尚是草案，M1 定稿） |

---

## 11. 进展（按时间倒序）

### 2026-10-23：M2 收尾 —— 批量清单 `--list` 与子树排除 `--skip`

| 事项 | 结果 |
| --- | --- |
| 命令 | `ui` 新增 `--list <文件>`（每行一个节点路径，`-> 子目录` 改写输出位置，找不到的行只警告）与 `--skip <模式>`（逗号分隔、`*`/`?` 通配、大小写不敏感、可重复） |
| 导出器 | 重构为累加式 `ExportTarget` + `CreateManifest`，多目标合并成一份 `manifest.json`；manifest 新增 `targets[]` |
| 真实数据 | `UI -> Basic.img --skip Cursor`：800 组/2554 帧/6.33MB 变成 **639 组/2131 帧/0.79MB**（Cursor 单目录占 5.5MB）；`--list` 三项批量（含 `-> buttons` 重定向与一个故意写错的节点）0.5s 完成，`--verify` 通过 |
| 回归 | `verify-ui-export.ps1` 新增 4 组用例：`_Canvas` 精确排除、逗号+通配符排除、批量清单（`->` 与缺失节点）、`--list`/`--node` 互斥 |
| 仍未做 | 同内容 PNG 的 hash 去重与引用计数（§5.6）。当前用 `--skip _Canvas,WZ2Lua` 规避重复，属临时手段 |

### 2026-10-23：真实客户端（GMS v271 / Steam）实测

数据目录 `G:\SteamLibrary\steamapps\common\MapleStory\Data`，16 个 wz 分组（Base / Character / Effect / Etc / Item / Map / Mob / Morph / Npc / Quest / Reactor / Skill / Sound / String / TamingMob / UI），
每组是「空 entry wz + `<名>_000.wz…` 分卷 + `<名>.ini` + `_Canvas` 伴生 wz」结构，合计约 50GB。WzLib 识别为 **wz 版本 271**，全部读得通。

| 导出目标 | 分组 | 帧 | PNG 体积 | 耗时（含 `--verify` 两遍） | 结果 |
| --- | --- | --- | --- | --- | --- |
| `Base`（整包，0.2MB） | 535 | 1123 | 0.13MB | 4.2s | 通过 |
| `Morph`（整包，1.5MB） | 4209 | 17440 | 254MB | 102s | 通过 |
| `UI` → `Basic.img` | 800 | 2554 | 6.3MB | 9.5s | 通过 |
| `UI` → `UIWindow.img` | 2607 | 5159 | 47.3MB | 44.4s | 通过 |

实测结论（都已回写到实现/规范）：

1. **`_outlink` 是 v271 的主形态**：`UI.wz` 里的帧大量 `_outlink` 指向 `UI/_Canvas/UIWindow.img/...`（抽样 400 份 meta：934 帧带 `link`、2 帧带 `uol`、0 个 `linkError`）。跨 wz 解引用与 `link` 字段记录正常。
2. **`delay` 上界不存在**：实测最高 `600000ms`（另有 `180000`），原计划里 `(0, 60000]` 的断言过窄 → 改为只断言 `> 0`，`> 60000` 只在导出时汇总提示一次（9/25153 帧）。
3. **整包导出 `UI` 会重复**：`_Canvas` 子树会被当成普通目录再导一遍，与 outlink 物化出来的图内容重复 → 内容去重（§5.6）的优先级应提前；当前建议按 img 定点导出。
4. `--verify` 会把导出跑两遍，耗时翻倍；大 wz 上建议先 `list` 定点再导出。

### 2026-10-23：UI 图片导出落地（M1 + M2 的 UI 部分）

| 事项 | 结果 |
| --- | --- |
| 规范 | 新增 [spec/wz-export-schema.md](spec/wz-export-schema.md)（schema 1，冻结 UI canvas 的目录/meta/manifest/命名/校验契约） |
| 新工程 | 新增 `WzComparerR2.Export/`（`net8.0-windows` 控制台，仅引用 `WzComparerR2.WzLib`，未加入 `.sln`，`IsPublishable=false`） |
| 命令 | `wzexport ui --wz <路径> [--wz …] [--node <节点>] --out <目录> [--kind ui] [--force] [--verify]`；`wzexport list …` 只统计不落盘 |
| 分组语义 | 以「父节点下的 canvas 子节点 = 同一分组的帧」为唯一规则，兼容 `Ani/{0,1}` 与 `Basic/{backgrnd,tile}` 两种结构；与 `Frame.CreateFromNode` 的 origin/z/delay/a0/a1 语义对齐 |
| 引用解析 | 支持 `Wz_Uol` 链、`_inlink`（同 img 相对路径）、`_outlink`/`source`（跨 wz 文件，需额外 `--wz`）；解析失败保留 `linkError` 并回退到链接节点自身 |
| 纹理 | 一律烘 RGBA32 PNG；BC7 的 4 对齐裁边用 `sourceWidth/sourceHeight` 记录原尺寸；`delay=0` 固化为 120 |
| 校验 | `--verify`：PNG 可重开且尺寸 = meta、帧数/索引/hash 一致、`z/delay/a0/a1` 区间、二次导出逐字节一致 |
| 无样本回归 | 新增 `WzComparerR2.Export/verify/verify-ui-export.ps1`：按 `Wz_Image.ExtractImg` 二进制布局合成 `.img` 夹具（含 Canvas 子属性、Vector2D、UOL、`_inlink`、跨文件 `_outlink`、非法文件名），14 组断言全绿 |

构建与验证命令：

```powershell
dotnet build .\WzComparerR2.Export\WzComparerR2.Export.csproj -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\WzComparerR2.Export\verify\verify-ui-export.ps1
```

真实客户端手工验证（无 wz 样本时唯一的正确性来源）：

```powershell
.\WzComparerR2.Export\bin\Debug\net8.0-windows\wzexport.exe list --wz "<客户端>\Data\UI.wz" --node "UIWindow.img"
.\WzComparerR2.Export\bin\Debug\net8.0-windows\wzexport.exe ui --wz "<客户端>\Data\UI.wz" --node "UIWindow.img" --out D:\export\ui --verify
```

期望：`list` 报告的帧数与主程序 QuickView 展开该目录看到的图片数一致；导出目录里每个 PNG 的尺寸与
QuickView「SavePicture」保存出来的图一致；`--verify` 输出「校验通过」。

尚未做（对应 M2 剩余项与 M3）：`--list <file>` 批量清单、同内容 canvas 的 hash 去重/引用计数、
音频 `Wz_Sound`、条目化（`sprite/<wztype>/<id>` 的 id 级目录）。
