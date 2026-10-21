# AGENTS.md — WzComparerR2.WzLib

WZ/MS/MCV 文件格式解析层。**整个仓库唯一直接读二进制的地方**，也是缓存/性能最敏感的地方。

上级约定见仓库根 `AGENTS.md`；本文件补充该模块特有规则。

## 1. 边界

- 目标框架：`net462`、`net6.0`、`net8.0`（**不带 `-windows` 后缀**）。
- 依赖：仅 BCL + `System.Drawing.Common`（PNG 解码）。**不允许**引用 `WzComparerR2.Common`、`WzComparerR2.PluginBase`、WinForms、MonoGame；也不要新增 NuGet 包。
- 上层通过 `PluginManager.FindWz(...)` 拿节点；WzLib 不反向依赖插件层。

## 2. 调用链

```
Wz_Structure.Load(file)            或 LoadFile / LoadImg / LoadMsFile / LoadWzFolder
   └─ Wz_Header（WzPkg1Header / WzPkg2Header / WzPkg2Header64：版本、加密类型、目录偏移）
        └─ Wz_File / Ms_File（目录树 + 读取上下文）
             └─ Wz_Image（懒解压，TryExtract() / Unextract()）
                  └─ Wz_Node 树（节点本身无类型标记，靠 Value 运行时类型）
```

| 文件 | 职责 |
| --- | --- |
| `Wz_Structure.cs` | 容器：`wz_files` / `ms_files` / `encryption` / `TextEncoding` / `AutoDetectExtFiles` / `ImgCheckDisabled` |
| `Wz_File.cs`、`Ms_File.cs`、`Ms_FileV2.cs` | 文件头解析、目录树构建、读取锁 |
| `Wz_Image.cs`、`Ms_Image(V2).cs` | img 解压：校验 checksum → 文本 img（`TextImageReaderV1/V2`）→ 常规二进制 img → Lua img 特例 |
| `Wz_Node.cs` | 节点树与扩展方法（`FindNodeByPath`、`GetValueEx`、`ResolveUol`、`GetNodeWzFile`） |
| `Wz_Crypto.cs` | Pkg1/Pkg2 密钥、AES-256-ECB 解密 + 每 512 字节块 IV 派生、字符串 XOR、盐生成 |
| `Cryptography/` | `ChaCha20CryptoTransform`、`Snow2CryptoTransform`（流式分块解密） |
| `Compatibility/` | 版本识别与兼容：`WzVersionProfile`、`WzVersionHasher`、`WzVersionVerifier`、`WzPreReadProvider`、`Pkg2Kmst1205Hash`、`WzDirStringReader`、`WzOffsetCalc` |
| `Utilities/` | `WzBinaryReader`、`WzStreamReader`、`ChunkedEncryptedInputStream`、`ConcatenatedStream`、`PartialStream`、字符串池、`CollectionsMarshal` 等基础设施 |
| `Wz_Png.cs` | Canvas 解码：`Wz_TextureFormat`（`ARGB4444`/`ARGB8888`/`ARGB1555`/`RGB565`/`R16`/`DXT1`/`DXT3`/`DXT5`/`A8`/`BC7`/`RGBA1010102`，含 KMST 新增枚举值）+ scale + 分页，zlib 解压后转成 GDI+ `Bitmap`（`Form` = format 数值 + scale） |
| `Wz_Sound.cs` / `Wz_Video.cs` / `Wz_RawData.cs` | 声音（MP3/WAV…）、视频（`MCV0` 头 + 帧表，含 `AlphaMap`/`PerFrameDelay`/`PerFrameTimeline` 标志；真正的帧解码在上层）、裸数据 |
| `Wz_Uol.cs` / `Wz_Vector.cs` / `Wz_Convex.cs` | 特殊值类型 |

## 3. 硬性规则

1. **三 TFM 必须同时编过**。公共代码里不能用 net6+/net8+ 独有 API；平台差异一律 `#if NET6_0_OR_GREATER`。
2. **不破坏已入库的常量**：`Wz_Crypto` 的 `aesKey`、版本偏移表、盐生成逻辑都是逐字节对照客户端得出的，改动等于全体用户无法读文件。发现确实要改，先证明旧数据仍可读。
3. **校验不能默认关**：`Wz_Image.TryExtract` 会先比对 checksum，只有 `Wz_Structure.ImgCheckDisabled` 为真才跳过。不要为了方便调试把这个开关的默认值改掉。
4. **解压是懒且可回退的**：`TryExtract()` 失败必须保持 `Node` 不被半填充（现有实现失败即 `Unextract()`），新分支要沿用这套"失败即回滚"语义。
5. **并发**：解压共享 `Wz_File.ReadLock`。新增读取路径要进同一个锁，否则并行解压会把文件指针冲掉。
6. **性能**：大 wz 动辄上 GB、上百万节点。解析热路径禁止 LINQ、禁止每节点分配（优先 `Span`/`stackalloc`/复用 buffer，`AllowUnsafeBlocks` 已开）。字符串读取走已有字符串池。
7. **容错优先于严格**：未知/缺失字段读默认值并继续，能读到多少算多少；一张坏图不能让整棵树加载失败。

## 4. 常见改动形态

- **新增客户端属性**：多数情况只需在调用方（CharaSim / MapRender）读新属性，WzLib 不用改；只有出现**新 node 类型 / 新头部格式 / 新加密**时才动这里。
- **新版本兼容**：先在 `Compatibility/WzVersionProfile.cs` 增加 profile（版本号、目录字符串偏移、hash 算法），再在 `WzVersionHasher` / `WzVersionVerifier` 里接线，最后在 `Wz_Structure.IsKMST1125WzFormat` 之类的探测点补齐。
- **新 img 子格式**：在 `Wz_Image.ExtractImg` 的分支表里加读取器，保持"先探测、再解析、失败回滚"。

## 5. 验证

```powershell
dotnet build .\WzComparerR2.WzLib\WzComparerR2.WzLib.csproj -f net462   -c Release
dotnet build .\WzComparerR2.WzLib\WzComparerR2.WzLib.csproj -f net6.0   -c Release
dotnet build .\WzComparerR2.WzLib\WzComparerR2.WzLib.csproj -f net8.0   -c Release
```

没有单测也没有样本数据，构建通过只是必要条件。交付说明里必须写清人工验证路径，例如：`用 2.x 客户端 Data/ 打开 Map.wz → 展开 100000000.img → 期望能解压并列出 back/obj/tile 子节点`。

## 6. 别做

- 不要为了"清理"删除 `Compatibility/` 里的历史 profile——老客户端还在用。
- 不要把解析逻辑改成 `async`（上层全是同步调用；且 net462 无 `ValueTask` 优势）。
- 不要在 WzLib 里打印日志或弹窗；错误通过返回值/异常上抛，由上层 `PluginManager.LogError` 记录。
- 不要顺手格式化 `Wz_Image.cs`（4 万字符）与 `Wz_File.cs`（3 万字符）。
