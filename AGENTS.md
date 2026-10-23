# AGENTS.md — WzComparerR2 代理协作指南

面向在本仓库工作的 AI 编码代理（人也可以读）。内容：项目结构、可用命令、代码约定、已知坑点、交付门槛、多角色子代理分工。

`AGENTS.md` 是分层文件：根目录这份讲全局，模块目录里的那份讲该模块的细节。**两者冲突时以更靠近代码的模块级文件为准**。

现有分层文件：`WzComparerR2.WzLib/`、`WzComparerR2.Common/`、`WzComparerR2.PluginBase/`、`WzComparerR2/`（主程序）、`WzComparerR2.MapRender/`、`WzComparerR2.Export/`（离线导出器）。

---

## 1. 项目速览

- **是什么**：冒险岛（MapleStory）WZ/MS/MCV 资源查看器 + 客户端比对器 + 补丁器 + 装备模拟器（CharaSim）+ 地图渲染器（MapRender）。C# / WinForms / MonoGame + SharpDX（DirectX 11）。
- **多目标**：`net462`、`net6.0(-windows)`、`net8.0-windows`。同一份源码要同时编过这三条 TFM。
- **维护状态**：README 声明为**深度维护**。上游只接受关键 bug 与 WZ 格式破坏性变更，不要主动发大重构。
- **语言**：代码注释、UI 文案、变更日志均为中文。新增内容保持中文风格一致。
- **无测试工程**：仓库里没有任何测试项目，CI 也只构建不运行。这一点直接决定了"什么叫改完"（见 §8）。

---

## 2. 构建与运行

本机已装 .NET SDK 10.0.401，运行时只有 .NET 8 / 10。以下命令均已实测通过（0 error）。

| 场景 | 命令 |
| --- | --- |
| 快速验证（推荐） | `dotnet build .\WzComparerR2\WzComparerR2.csproj -f net8.0-windows -c Debug` |
| 单库验证 | `dotnet build .\WzComparerR2.WzLib\WzComparerR2.WzLib.csproj -f net8.0 -c Release`（`net6.0`、`net462` 同样可换） |
| 全量（含全部插件） | `dotnet build .\WzComparerR2.sln -c Release -p:Platform="Any CPU"` |
| 导出器（**不在 sln**，必须单独构建） | `dotnet build .\WzComparerR2.Export\WzComparerR2.Export.csproj -c Debug` |
| 导出器回归（无 wz 样本） | `powershell -NoProfile -ExecutionPolicy Bypass -File .\WzComparerR2.Export\verify\verify-ui-export.ps1` |
| CI 同款 | 见 `azure-pipelines.yml`：Restore → Build `Any CPU` → Build `x86` → 打 netfx/net6/net8 三个 zip |

- 解决方案平台：`Any CPU` / `x86` / `x64`；CI 用 `Any CPU` 与 `x86` 各构建一次。
- 运行：`.\WzComparerR2\bin\Debug\net8.0-windows\WzComparerR2.exe`。**必须配合真实客户端 WZ 文件**才有意义，仓库不含任何 wz 数据。
- `dotnet test` 无对象，不要跑。

构建产物布局（三种 TFM 相同）：

```
WzComparerR2/bin/Debug/net8.0-windows/
├── WzComparerR2.exe            主程序
├── *.dll                       托管依赖（DotNetBar2、MonoGame、SharpDX…）
├── Lib/{x86,x64,ARM64}/        原生库（References 下的 x86/x64/ARM64 目录 xcopy 而来）
└── Plugin/<插件项目名>/         插件 dll + deps.json + runtimeconfig.json
```

上述 `Lib/` 与 `Plugin/` 拷贝由 `Build/WcR2Plugin.targets` 与主程序 csproj 的 `PostBuild` 自动完成，**只构建单个插件工程时 `$(SolutionDir)` 未定义，拷贝目标会错位**——需要产出插件目录时请从解决方案构建。

注意 `WcR2Plugin.targets` 里的 `MainProgramOutputDir` 是 `WzComparerR2\bin\$(Configuration)\$(TargetFramework)`，**不含 Platform 段**。所以 `-p:Platform="x86"` 构建时：

- `WzComparerR2\bin\x86\Debug\<tfm>\` 只有 32 位主程序 exe（+ `.exe.config` / `.dll`），`Plugin/` 为空是**正常现象**；
- 插件与托管依赖仍旧拷进**Any CPU** 的 `bin\Debug\<tfm>\`。

CI 正是利用这一点：先 `Any CPU` 再 `x86` 构建，然后只把 x86 的 `WzComparerR2.exe` / `WzComparerR2.exe.config` 覆盖到 Any CPU 目录再打包（`azure-pipelines.yml` 的 "Prepare output files" 步骤）。所以 `bin\x86\...\Plugin` 为空**不要当 bug 去"修"**。

Debug 全量实测（`--no-incremental`）：`Any CPU` 19.5s / 91 警告 / 0 错误，`x86` 25.5s / 91 警告 / 0 错误；三条 TFM 的 `Plugin/`（Avatar、LuaConsole、MapRender、Network）与 `Lib/{x86,x64,ARM64}` 均齐全。

---

## 3. 项目地图

| 项目 | 类型 / TFM | 职责 | 先看这些文件 |
| --- | --- | --- | --- |
| `WzComparerR2` | WinExe，net462 / net6.0-windows / net8.0-windows | 主程序：主窗体、插件宿主、比对器、补丁器、CharaSim 界面、配置 | `Program.cs`、`MainForm.cs`、`Comparer/`、`Patcher/`、`CharaSimControl/` |
| `WzComparerR2.Common` | 库，同主程序（RootNamespace 为 `WzComparerR2`） | 配置框架、文本与字体渲染、动画/Spine、GIF/APNG/FFmpeg 编码器、CharaSim 数据模型、`StringLinker`、通用控件 | `Config/ConfigManager.cs`、`CharaSim/`、`Text/`、`Rendering/`、`Encoders/` |
| `WzComparerR2.WzLib` | 库，net462 / net6.0 / net8.0（**不带 -windows**） | WZ/MS/MCV 文件格式解析、加密解密、版本兼容、节点树 | `Wz_Structure.cs`、`Wz_File.cs`、`Wz_Image.cs`、`Wz_Node.cs`、`Wz_Crypto.cs`、`Compatibility/` |
| `WzComparerR2.PluginBase` | 库（插件契约） | `PluginEntry` / `PluginContext` / `PluginManager` / 事件参数 | `PluginEntry.cs`、`PluginContext.cs`、`PluginManager.cs` |
| `WzComparerR2.MapRender` | 插件 | 地图渲染器（V2 为唯一在编译的版本） | `Entry.cs`、`FrmMapRender2*.cs`、`MapData.cs`、`Patches2/` |
| `WzComparerR2.Avatar` | 插件 | 纸娃娃（avatar 代码解析与预览） | `Entry.cs`、`UI/AvatarForm.cs` |
| `WzComparerR2.Network` | 插件 | 在线聊天/用户列表，包体定义在 `Contracts/` | `Entry.cs`、`Contracts/Pack*.cs` |
| `WzComparerR2.LuaConsole` | 插件 | NLua 脚本控制台 | `Entry.cs`、`Lua/`、`Examples/` |
| `WzComparerR2.Updater` | WinExe | 独立更新器；其产物**以 EmbeddedResource 形式嵌入主程序** | `Program.cs`、`Updater` 相关 |
| `WzComparerR2.Export` | 控制台 Exe，**仅 net8.0-windows，不在 sln** | 离线导出器：WZ → 引擎无关中间格式（当前：UI canvas → PNG + JSON） | `Program.cs`、`UiImageExporter.cs`、`verify/` |
| `CharaSimResource` | 资源库（**git submodule**，toNyLInL fork，commit `fc9bdc6`） | 装备/道具模拟用的 resx 图片等资源 | `Resources/` |

---

## 4. 架构要点

**插件宿主**：`PluginManager.GetPluginFiles()` 递归扫描主程序目录下 `Plugin/WzComparerR2.*.dll`，用 `Assembly.GetExportedTypes()` 找出非抽象 `PluginEntry` 子类，以 `(PluginContext)` 构造后调用 `OnLoad()`；net6+ 走 `PluginLoadContext`（可卸载 ALC），net462 走 `Assembly.Load`。插件通过 `PluginContext` 拿主窗体、`DotNetBarManager`、三路选中节点（`SelectedNode1/2/3`）、`StringLinker`，通过 `AddRibbonBar/AddTab` 挂 UI。**新功能优先做成插件，而不是往主窗体和 `PluginBase` 里塞。**

**WZ 数据模型**：`Wz_Structure`（容器，`wz_files`/`ms_files`/`encryption`/`TextEncoding`）→ `Wz_File`/`Ms_File`（文件头、目录树）→ `Wz_Image`（可懒解压的 img）→ `Wz_Node` 树。节点没有类型标记，**靠 `Value` 的运行时类型判断**（`int`/`long`/`string`/`Wz_Png`/`Wz_Uol`/`Wz_Vector`/`Wz_Convex`/`Wz_Sound`/`Wz_Video`…）。常用 API：`FindNodeByPath(path, extractImage)`、`GetValueEx<T>()`、`ResolveUol()`、`GetNodeWzFile()`、`TryExtract()`。

**配置**：`WzComparerR2.Common/Config` 自定义一套基于 `System.Configuration` 的 section 框架，配置文件是 exe 同目录的 `Setting.config`。每个 section 是 `ConfigSectionBase<T>` 子类，通过 `ConfigManager.RegisterAllSection(assembly)` 反射批量注册（主程序、各插件在 `OnLoad` 里各注册自己的）。新增配置项必须走这套机制，不要另起 JSON/ini。

**渲染栈**：主程序与 CharaSim 用 Direct2D/XNA 混合（`Common/Rendering/`）；MapRender 与 Avatar 用 MonoGame（`SpriteBatch` + 自定义 `MeshBatcher`/`TextureAtlas`）；视频走 SharpDX MediaFoundation + 原生 libvpx/libyuv。

**版本号**：`Build/CommonAssemblyInfo.cs` 由 CI 生成（写入 `AssemblyInformationalVersion = 2.2.<BuildNumber>`），本地通常不存在（csproj 用 `Exists(...)` 条件包含）。`Program.ApplicationVersion` 与插件 `PluginEntry.FileVersion` 都读它。

**条件编译**：`#if NET6_0_OR_GREATER`（运行时差异、Harmony patch）、`#if MapRenderV1`（**V1 渲染器整套代码被包在 `#if MapRenderV1` 里，默认不参与编译**，不要以为它坏了）。

---

## 5. 代码约定

- **编码**：`.cs` 一律 **UTF-8 with BOM**（464 个文件里 449 个带 BOM）。net462 下编译器对无 BOM 文件按系统 ANSI 码页解析，去 BOM 会造成中文注释/文案乱码。`README.md`、`UpdateLogs/*.md` 为 UTF-8。
- **控制台读中文**：Windows PowerShell 默认按 GBK 解码，直接 `Get-Content` 会花屏，请显式 `-Encoding utf8`。
- **风格**：Allman 大括号、4 空格、`Nullable disable`、`ImplicitUsings disable`、`LangVersion latest`、`AllowUnsafeBlocks`、分析器整体关闭。老代码里 `this.` 前缀和 `var` 混用——**改哪段就沿哪段的写法**，不要顺手统一。
- **不要引入**：nullable 注解、新分析器、`async/await` 化的 IO 改造、DI 容器、新的日志框架。现有 `try/catch` + `PluginManager.LogError` / `error.log` 就是全局错误通道。
- **UI**：WinForms + DevComponents.DotNetBar（`MessageBoxEx`、`RibbonBar`、`SuperTabControl`）。提示语为中文。DotNetBar 是混淆过的第三方库，`Dotnet6Patches.cs` 里按**混淆后的方法名**（如 `ᑧ`）打 Harmony patch，改动这些名字等于让 net6+ 启动崩溃。
- **Designer/resx**：`*.Designer.cs` 与 `*.resx` 由设计器生成。改布局时三件套（`.cs` / `.Designer.cs` / `.resx`）要一起改并保持控件名一致；只改逻辑时别碰 Designer。
- **警告**：主程序 net8 Debug 现有约 26 条历史警告（CS0168/CS0618/SYSLIB0014 等），全解决方案约 65 条。**不要为了消警告做无关重构**，但新增代码不要引入新警告。

---

## 6. 已知坑（都是实际踩过的）

1. **没有 WZ 数据就无法回归**：解析、渲染、模拟器的正确性只能靠"打开哪个 wz、点哪里、期望看到什么"来验证。仓库与 CI 都不含 wz 文件。
2. **`References/*.dll` 是二进制私有依赖**（DotNetBar2、spine-monogame、ImageManipulation、EmptyKeys、IMEHelper、TSF）。没有源码，不要尝试替换/升级/反编译重写。
3. **Updater 先于主程序**：主程序把 `WzComparerR2.Updater` 的 exe/dll/deps.json 作为 `EmbeddedResource` 打包，路径指向 Updater 的 `bin/$(Configuration)/$(TargetFramework)`。删过 `obj`/`bin` 后直接构建主程序工程可能因缺文件失败——先构建整个解决方案。
4. **不要提交 `Build/CommonAssemblyInfo.cs`**：它由 CI 生成，本地提交会让版本号被钉死。
5. **`CharaSimResource` 是 submodule**：主库只记录指针（当前指向 `toNyLInL` fork）。要改资源请到那个仓库提交并更新指针，不要在主库里改子模块内容。
6. **Windows-only**：`PostBuild` 用 `xcopy`、渲染依赖 DirectX 11 与桌面会话、`net462` 需要 .NET Framework 4.6.2 targeting pack。不要在无桌面环境下声称"已运行验证"。
7. **net6 只能编译不能运行**：本机只有 .NET 8/10 运行时；net6 产物要跑需另装运行时。
8. **配置项注册是反射扫描**：`ConfigSectionBase<>` 的泛型基类判断在 `RegisterAllSection` 里，绕过基类直接继承 `ConfigurationSection` 会导致 section 永远不注册、`Setting.config` 里读不到值。
9. **`Wz_Node.Value` 类型是可空语义的**：解析新属性一律 `node.GetValueEx<T>()`（带默认值），不要直接强转；字符串节点可能返回 `null`，也可能返回 `Wz_Uol` 需要 `ResolveUol()`。
10. **`MainForm.cs`（约 13 万字符）+ `MainForm.Designer.cs`（约 18 万字符）是单文件巨兽**：改动要按方法定位，禁止整文件重排/格式化，否则 diff 无法评审。

---

## 7. 变更流程

- **提交信息**：`<模块>: <摘要> (#issue)`，模块用项目简写。历史高频前缀：`WzLib`、`MapRender`、`CharaSim`、`MainForm`、`Common`、`Updater`、`chore`。示例：`WzLib: support KMST1206.`、`MapRender: fix back tiling distance, #395`。
- **变更日志**：面向用户的行为变化写进 `UpdateLogs/dev.md` **顶部**，格式为

  ```markdown
  ## 2026.3.1
  ### WzLib
  - 支持 KMSTxxxx 新增的 xxx 属性。
  ```

  模块分类沿用 `通用 / WzLib / CharaSim / MapRender / Avatar / Patcher / Network`，一条一行、只写行为变化。
- **计划文档**：待做/进行中的方案写在 `docs/plan/`（约定见 `docs/plan/README.md`），**不进 `UpdateLogs/`**。计划里的代码引用用相对链接带行号，便于复查。
- **不要提交**：`bin/`、`obj/`、`Lib/`、`error.log`、`Setting.config`、`packages/`。
- **不要动**：`.gitattributes`（换行策略会让全仓库产生巨型 diff）、`azure-pipelines.yml` 的发布/上传逻辑、`References/` 二进制、submodule 指针（除非任务就是升级子模块）。

---

## 8. 交付门槛

任改动完成前逐条自查：

- [ ] `dotnet build .\WzComparerR2\WzComparerR2.csproj -f net8.0-windows -c Debug` 通过，且**未新增警告**。
- [ ] 触及 `WzComparerR2.WzLib` 或 `WzComparerR2.Common` 的公共代码时，额外单独编 `net462` 与 `net6.0`（`-f` 指定），确认条件编译没编歪。
- [ ] 触及插件工程时，用解决方案构建，确认 `Plugin\<插件名>\` 里 dll / `*.deps.json` / `*.runtimeconfig.json` 齐全。
- [ ] 涉及 UI / 渲染 / 解析的改动，写明**人工验证步骤**（打开哪个 wz、操作、期望结果）。
- [ ] diff 干净：无 bin/obj、无无关格式化、无乱码字符、无新增未使用文件。
- [ ] 用户可见变更已写入 `UpdateLogs/dev.md`。

---

## 9. 子代理分工（多角色）

### 9.1 总则

- 主代理（你）负责：拆解任务 → 分配互不重叠的写路径 → 评审 → 全量构建 → 交付说明。
- 子代理按**单一模块、单一写者**并行；**同一个文件在同一时刻只能有一个代理写**。路径有交集就串行。
- 子代理只读上下文可跨模块，写权限只在自己的路径集合内。
- 子代理**不执行** `git commit/push/checkout/reset`、不改 submodule、不改 `.gitattributes` 与 CI 配置。
- 子代理必须回报：改动文件清单、构建结果、人工验证步骤、风险点。

### 9.2 角色表

| 角色 | 可写路径 | 主要只读上下文 | 典型任务 |
| --- | --- | --- | --- |
| `wzlib-parser` | `WzComparerR2.WzLib/**` | `Wz_Image.cs`、`Wz_File.cs`、`Compatibility/*`、`Cryptography/*` | 新版本 wz 格式/属性支持、解析性能、字节序与加密修正 |
| `charasim-model` | `WzComparerR2.Common/CharaSim/**`、`WzComparerR2.Common/StringLinker.cs`、`WzComparerR2/CharaSim/**` | `WzLib/**`（只读） | 新增装备/道具/技能属性映射、公式、套装/潜能数据 |
| `charasim-ui` | `WzComparerR2/CharaSimControl/**` | 上面两个角色的产出 | Tooltip 渲染、装备模拟界面、导出图片 |
| `maprender` | `WzComparerR2.MapRender/**` | `WzLib/**`、`Common/Rendering/**` | 地图场景渲染、粒子、光效、性能 |
| `ui-shell` | `WzComparerR2/*.cs`（`MainForm*`、`Frm*`、`Comparer/`、`Patcher/`、`Config/`）、`WzComparerR2.PluginBase/**` | 全部（只读） | 主窗体功能、比对器、补丁器、插件契约调整 |
| `plugins` | `WzComparerR2.Avatar/**`、`WzComparerR2.Network/**`、`WzComparerR2.LuaConsole/**`、`WzComparerR2.Updater/**` | `PluginBase/**` | 纸娃娃、聊天、Lua 控制台、更新器 |
| `wz-export` | `WzComparerR2.Export/**`、`docs/plan/spec/**` | `WzLib/**`（只读）、`Common/Animation/Frame.cs`（只读，语义对齐） | 导出器命令、分组/命名规则、中间格式 schema、无样本回归脚本 |
| `build-ci` | `*.sln`、`Build/**`、`azure-pipelines.yml`、`UpdateLogs/**`、`AGENTS.md` | 全部（只读） | 构建配置、打包、变更日志 |

> 注意 `WzComparerR2.Common` 根命名空间是 `WzComparerR2`，跨项目移动类型会改变其命名空间可见性——移动 `Common/CharaSim` 与 `WzComparerR2/CharaSim` 之间的类型属于**破坏性变更**，必须由主代理裁决。

### 9.3 分派规则

1. 拆成"每个角色 ≤ 1 个可独立构建的产出"的任务；跨角色的接口变更（公共类型签名、`PluginEntry` 契约、配置 section 名）由主代理先定稿再分发。
2. 子代理提示词必须自带：任务目标、可写路径白名单、必读文件、验收命令、禁止事项。
3. 主代理汇总后**统一跑一次全量解决方案构建**，再检查是否互相覆盖（尤其 `MainForm.cs`、`Wz_Node.cs`、`MapData.cs` 这类热点文件）。
4. 任何子代理报告"无法用构建验证"时，主代理必须要求它给出人工验证步骤，不允许以"应该没问题"结案。

### 9.4 子代理提示词模板

```text
你是 <角色名>，在仓库 E:\FullStackProj\WzComparerR2 工作。

任务：<一句话目标 + 明确的完成定义>

可写路径（只能改这些）：
- <path 1>
- <path 2>

必读上下文：
- AGENTS.md
- <模块 AGENTS.md>
- <关键源文件>

约束：
- 保持 UTF-8 with BOM、Allman 风格、Nullable disable，不引入新依赖/新框架。
- 不改 .gitattributes / azure-pipelines.yml / submodule；不执行 git 写操作。
- 不新增编译警告；不做任务之外的格式化或重构。

验收：
- 命令：dotnet build .\WzComparerR2\WzComparerR2.csproj -f net8.0-windows -c Debug（按模块替换工程路径与 TFM）
- 通过标准：0 error，且警告数不高于改动前。

回报格式：
1) 改动文件清单（含一句话说明）
2) 构建结果（命令 + 结果）
3) 人工验证步骤（打开哪个 wz / 操作 / 期望）
4) 风险点与未覆盖场景
```

---

## 10. 明确不要做的事

- 不要"现代化"这套代码：不迁移到 WPF/Avalonia/Web、不引入 MVVM/DI/EF、不把 WinForms 控件换成别的库。
- 不要改写 `Wz_Crypto` 里的密钥表与版本常量、不要重排 `Wz_Image` 的解析分支——这些是逐字节对照客户端行为得出的。
- 不要在没有 wz 样本的情况下"顺手修"格式解析逻辑；宁可留 TODO 并报告。
- 不要为了通过构建而注释掉功能、删条件编译分支或跳过 `PostBuild` 拷贝。
- 不要在中文文案里混入半角标点或翻译腔，保持现有风格（如"没有选择一个map的img"这种原文就保留，不要顺手改文案）。
