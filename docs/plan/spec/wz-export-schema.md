# 规范：wz-export 中间格式（schema 1）

| 项 | 值 |
| --- | --- |
| 状态 | **生效（schema 1）** |
| 创建日期 | 2026-10-23 |
| 适用计划 | [wz-to-unity-export.md](../wz-to-unity-export.md) |
| 对应实现 | `WzComparerR2.Export/`（命令 `wzexport`） |
| 覆盖范围 | **UI 类静态图片**（canvas → PNG + 元数据）。动画帧序列、音频、spine、地图、图集尚未实现 |

本文件是中间格式的**契约**：产物、字段、命名、排序、校验规则都以这里为准。
改字段属于破坏性变更，必须把 `schema` 加 1 并同步改本文件。

---

## 1. 设计约束（来源：计划 §3 ADR、§5 语义映射）

1. **引擎无关**：产物不含 Unity/Godot 专有概念（没有 Y 翻转、没有 PPU、没有 sortingOrder）。
2. **语义不丢**：`origin` 保持 wz 像素偏移（Y 向下、整数），`z` 保持整数，`delay` 保持毫秒且**固化默认值**。
3. **纹理统一**：首版一律烘成 **RGBA32 PNG**（ARGB4444/1555、RGB565、DXT、BC7、R16 全部转换），
   下游不需要猜通道序。BC7 按 4 对齐裁边时，用 `sourceWidth/sourceHeight` 记录原始尺寸。
4. **字节稳定**：同一输入、同一路径重跑，PNG 与 meta.json 必须逐字节一致（manifest.json 例外，它含时间戳）。
5. **可反查**：每个条目都记录 `source`（wz 符号路径），用于回查与增量。

---

## 2. 目录结构

```
<out>/
├── manifest.json                     # 本次导出的总清单
└── sprite/
    └── <kind>/                       # kind 默认 ui，可用 --kind 改
        └── <相对路径>/                # 分组目录，见 §3
            ├── <帧名>.png
            ├── ...
            └── meta.json
```

- `<kind>` 同时决定 `sprite/<kind>/` 目录名与 manifest 的 `kind` 字段。
- `<相对路径>` 是分组节点在 wz 里的路径，**去掉 `.img` 后缀**，用 `/` 分隔，
  首段是 wz 文件名（不含扩展名）。例：`sprite/ui/UI/UIWindow/Main/backgrnd/`。
- 单个 `.img` 文件直接导出时，开头不会重复出现同名层（`Fixture.img` → `sprite/ui/Fixture/...`）。
- `--skip <模式>` 命中的节点**既不会成为帧、也不会被下钻**，因此不出现在产物与 manifest 里；
  典型用法是排除 v271 客户端的 `_Canvas` 伴生子树（它的图已通过 `_outlink` 物化到引用处）。
- `--list <文件>` 一次导出多个节点：每行一个路径，`-> <子目录>` 可改写该目标的输出目录；
  清单里找不到的节点只警告并跳过，不影响其余条目。

---

## 3. 分组（group）语义

分组规则与 `WzComparerR2.Common/Animation` 的 `Frame.CreateFromNode` + `FrameAnimationData` 帧语义一致：

1. **一个节点的直接子节点里，凡是能解析出 canvas 的，都是同一个分组里的帧**，按 wz 节点顺序编号。
   - `Ani/{0,1}` → 1 个分组，2 帧；
   - `backgrnd/{0,1}` → 1 个分组，2 帧。
2. **单张 canvas 直接被其他属性节点包含时**，它成为父分组的唯一帧：
   `Basic/{backgrnd, tile}` → `Basic` 分组只有 `tile.png` 一帧，`backgrnd` 另立子分组。
3. **帧的元数据取自帧节点自己的子属性**（`origin`/`z`/`delay`/`a0`/`a1`）；
   缺失时回落到链接（`_inlink`/`_outlink`/`source`）目标的同名属性。
4. `--node` 指向的节点**本身就是 canvas** 时，单独成组，目录即该节点的相对路径。
5. 分组目录重名（大小写不敏感或非法字符被清洗后撞车）时，追加 `~2`、`~3`……并记一条警告。

多页 canvas（`pages > 1`）**每页各成一帧**，文件名加 `__p<page>` 后缀（见 §6）。

---

## 4. `meta.json`（每个分组一份）

```json
{
  "schema": 1,
  "kind": "ui",
  "source": "UI.wz/UIWindow.img/Main/backgrnd",
  "uniform": true,
  "canvas": { "width": 10, "height": 20, "format": "ARGB8888", "scale": 1, "pages": 1 },
  "frames": [
    {
      "index": 0,
      "name": "0",
      "png": "0.png",
      "page": 0,
      "width": 10,
      "height": 20,
      "format": "ARGB8888",
      "scale": 1,
      "pages": 1,
      "origin": [2, -3],
      "z": 0,
      "delay": 120,
      "a0": 255,
      "a1": 255,
      "source": "UI.wz/UIWindow.img/Main/backgrnd/0"
    }
  ]
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schema` | int | 本规范版本，破坏性变更 +1 |
| `kind` | string | 与 `--kind` 一致 |
| `source` | string | 分组节点的 wz 符号路径（`/` 分隔） |
| `uniform` | bool | 组内所有帧的 `width/height/format/scale/pages` 是否完全一致 |
| `canvas` | object | **仅 `uniform=true` 时存在**：`width/height/format/scale/pages` |
| `frames[]` | array | 帧列表，`index` 与数组下标一致 |

`frames[]` 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `index` | int | 帧序号，从 0 连续递增 |
| `name` | string | 帧节点原名（可能含非法文件名字符，保留原文便于反查） |
| `png` | string | 同目录下的 PNG 文件名 |
| `page` | int | 该帧来自 canvas 的第几页（`pages=1` 时恒为 0） |
| `width`/`height` | int | **导出 PNG 的实际像素尺寸** |
| `sourceWidth`/`sourceHeight` | int | 仅当与 `width/height` 不同时出现（BC7 4 对齐裁边） |
| `format` | string | wz 原始纹理格式：`ARGB4444` / `ARGB8888` / `ARGB1555` / `RGB565` / `R16` / `DXT1` / `DXT3` / `DXT5` / `A8` / `BC7` / `RGBA1010102` / `RGBA32Float` |
| `scale` | int | 已换算的实际倍率（`1 << Scale`，即 1/2/4…） |
| `pages` | int | canvas 页数（`Pages <= 0` 视为 1） |
| `origin` | int[2] | 绘制原点偏移，**wz 像素、Y 向下**；绘制矩形为 `(-x, -y, width, height)` |
| `z` | int | wz 的 z 值，原样保留（默认 0） |
| `delay` | int | 毫秒，**0 已固化成 120**，恒 `> 0`；客户端会用 `600000`/`180000` 这类超长值表示「定格」，属正常数据，**不设上界** |
| `a0`/`a1` | int | 帧内 alpha 端点，默认 255 |
| `source` | string | 帧节点的 wz 符号路径 |
| `uol` | string | 仅当帧由 `Wz_Uol` 跳转而来时出现，记录原始 UOL 路径。以 `/` 开头表示 **wz 全局路径**（v271 客户端用它复用别的道具贴图，如 `/Item/Cash/0515.img/05158018/info/icon`），导出器按全局路径解析后再退回相对语义 |
| `link` | string | 仅当帧带 `_inlink`/`_outlink`/`source` 时出现，记录链接路径原文 |
| `inlink` | bool | `link` 是 `_inlink`（同 img 相对路径）时为 true |
| `linkError` | string | 链接解析失败原因（此时 PNG 退回链接节点自身的 canvas） |

---

## 5. `manifest.json`（每次导出一份）

```json
{
  "schema": 1,
  "tool": "wzexport",
  "toolVersion": "2.2.12345",
  "generatedAt": "2026-10-23T02:57:12Z",
  "kind": "ui",
  "root": "UI.wz/UIWindow.img",
  "files": [ { "file": "UI.wz", "version": 95 } ],
  "counts": { "groups": 5, "frames": 9, "pngFiles": 9 },
  "assets": [
    {
      "source": "UI.wz/UIWindow.img/Main/backgrnd",
      "dir": "sprite/ui/UI/UIWindow/Main/backgrnd",
      "meta": "sprite/ui/UI/UIWindow/Main/backgrnd/meta.json",
      "frames": 2,
      "width": 10,
      "height": 20,
      "hash": "de3ee846…"
    }
  ],
  "warnings": [ "…" ]
}
```

| 字段 | 说明 |
| --- | --- |
| `toolVersion` | 取 `AssemblyInformationalVersion`（CI 写入 `2.2.<build>`），本地缺失时回落 `1.0.0.0` |
| `generatedAt` | UTC，`yyyy-MM-ddTHH:mm:ssZ`。**唯一不参与字节比对的字段** |
| `targets[]` | 本次导出覆盖的目标节点符号路径：单目标时只有一个元素（与 `root` 相同），`--list` 批量时按清单顺序列出 |
| `files[]` | 参与解析的 wz 文件（第一个是导出根，其余用于跨文件链接） |
| `counts` | 分组数 / 帧数 / 落盘 PNG 数（当前一帧一张，后两者相等） |
| `assets[].hash` | 该分组 `meta.json` 内容的 SHA-256（十六进制小写），用于增量与去重 |
| `assets[].width/height` | 仅 `uniform=true` 时出现 |
| `warnings[]` | 导出期间的非致命问题（节点跳过、链接/解码失败、目录重名），为空时省略该字段 |

---

## 6. 命名与路径规则

- 目录/文件名来自 wz 节点名，清洗规则：
  - Windows 非法字符（`\ / : * ? " < > |`）与控制字符 → `_`；
  - 去掉结尾的空格与点；空名 → `_`；`.`/`..` → `_`；
  - `CON`/`PRN`/`AUX`/`NUL`/`COM1-9`/`LPT1-9` → 前置 `_`；
  - 单段超过 64 字符 → 前 24 字符 + `~` + 名字的 SHA-256 前 8 位。
- 帧文件名：`<清洗后的帧名>.png`；多页 canvas 用 `<帧名>__p<page>.png`；同目录重名追加 `~2`、`~3`。
- 目录段去掉 `.img` 后缀（`UIWindow.img` → `UIWindow`），但 `source` 保留完整名字。
- 完整路径超过 240 字符时，自动缩短最深的段（同样的 `~hash` 规则）；仍超长则报错并提示换 `--out`。
- 目录名大小写不敏感去重，避免 Windows 上互相覆盖。

---

## 7. 校验

两种模式，任一断言失败都返回退出码 3：

**A. 导出后立刻校验**：`wzexport ui … --verify`（读 wz，导出两遍）

1. **可重开**：每个 PNG 用 GDI+ 重新打开，尺寸必须等于 meta 里的 `width/height`；
2. **一致性**：`frames[].index` 连续、帧数等于 manifest 计数、`meta.json` 的 SHA-256 等于 manifest 记录；
3. **区间**：`z ∈ [-9999, 9999]`、`delay > 0`（不设上界：实测 v271 客户端最大 `600000`）、`a0/a1 ∈ [0, 255]`；
4. **字节稳定**：同一输入再导出一份到 `<out>.verify`，逐文件比对（跳过 `manifest.json`），
   比对完删除临时目录。

> 大规模产物的经验值：Image/Item 级（30 万帧 / 2.4GB）导出约 9 分钟，`--verify` 全流程约 60 分钟
> （其中校验占大头：305k 次图片重开 + 784k 次 SHA-256）；改用 `verify --out` 复检则是分钟级。

**B. 只校验已有产物**：`wzexport verify --out <目录> [--deep]`（**不读 wz、不重新导出**）

- 依据 `manifest.json` 逐个资产核对：`meta.json` 存在且 SHA-256 与 manifest 记录一致、
  帧数与 manifest 一致、`frames[].index` 连续；
- 逐帧检查 PNG：读出签名与 IHDR 尺寸与 `meta.width/height` 比对，`z/delay/a0/a1` 在合法区间；
- `--deep` 额外用 GDI+ 重新打开每张图（能查出「头部合法但内容损坏」，代价高）；
- 默认按资产并行读盘（≤8 线程），30 万帧的包约数分钟；`--verify` 则要重导一遍、慢一个量级。
- 用途：交付前的复检、拷贝/传输后的一致性抽查（不依赖 wz 与 WzLib）。

不含 wz 样本的自动化回归见 `WzComparerR2.Export/verify/verify-ui-export.ps1`：
它按 `Wz_Image.ExtractImg` 的二进制布局现场合成 `.img` 夹具，覆盖属性树、Canvas 子属性、
Vector2D、UOL（含绝对路径）、`_inlink`、`_outlink`、非法文件名、`--force` 保护，
以及 `verify` 的通过/损坏/缺失/参数互斥用例。

---

## 8. 尚未定义（后续里程碑）

| 内容 | 归属 |
| --- | --- |
| 动画（帧序列 + `frames[]` 已有雏形）、音频 `Wz_Sound` | 计划 M3 |
| spine（skeleton/atlas）、视频 MCV0 转码 | 计划 M5 / M6 |
| 图集（`atlas/` 合并图 + 映射表）、同内容 canvas 的 hash 去重与引用计数 | 计划 §4、§5.6 |
| 地图分层几何、数据表（Gear 等模型 JSON） | 计划 M6 / M7 |
| `String.wz` 本地化表 | 计划 §9 决策 5 之后 |

`schema` 只在**破坏性**变更时 +1；新增可选字段不升版本，但必须在本文件补一行说明。
