# AGENTS.md — WzComparerR2（主程序）

WinForms 主程序：插件宿主 + 全部主界面 + 比对器 + 补丁器 + CharaSim 展示。上级约定见仓库根 `AGENTS.md`。

## 1. 启动链（改启动相关代码前必读）

```
Program.Main
 ├─ Application.EnableVisualStyles / SetCompatibleTextRenderingDefault
 ├─ AppDomain.UnhandledException → 写 exe 目录 error.log
 ├─ AppDomain.AssemblyResolve → 从已加载插件程序集 / Lib 目录兜底
 ├─ SetDllDirectory(Lib\{x86|x64|ARM64}) + 预加载其中所有 dll
 ├─ [net6+] Encoding.RegisterProvider(CodePagesEncodingProvider) + Dotnet6Patch.Patch()（Harmony）
 └─ StartMainForm
      ├─ new MainForm()（MainForm 即 PluginContextProvider）
      ├─ LoadPlugins()：扫描 Plugin/WzComparerR2.*.dll → net6+ 走 PluginLoadContext，net462 走 Assembly.Load
      ├─ ConfigManager.ConfigFile（首次访问 exe 同目录 Setting.config）
      ├─ frm.PluginOnLoad() + PluginManager.PluginOnLoad()
      └─ Application.Run(frm)
```

`Program.ApplicationVersion` 从 `AssemblyInformationalVersion`（CI 写入 `Build/CommonAssemblyInfo.cs`）取，缺失时回退 `FileVersion`。

## 2. 文件地图

| 文件 / 目录 | 职责 | 体量提醒 |
| --- | --- | --- |
| `Program.cs` | 入口、DLL 目录、程序集解析、启动顺序 | — |
| `MainForm.cs` + `MainForm.Designer.cs` | 主窗体：Ribbon（`Tools` / `Modules` / `Help` 三个 tab，按 `Tag` 匹配）、wz 树（`AdvTree`）、`SuperTabControl` 多标签、三路选中节点、拖放打开、QuickView 入口 | **13 万 + 18 万字符，单文件巨兽** |
| `PluginLoadContext.cs` | net6+ 可卸载 ALC，插件依赖从 `Lib` 解析 | — |
| `Dotnet6Patches.cs` | Harmony 补丁，修 DotNetBar（`ComboBoxEx`、`AdvTree` 等）在 .NET 6+ 的行为差异，按**混淆后的方法名**挂载 | 仅 `NET6_0_OR_GREATER` 编译 |
| `Dotnet4Polyfill.cs` | net462 缺失 API 的补丁 | — |
| `Comparer/` | `WzFileComparer`（逐节点差异，`WzVirtualNode` 构造结果树）、`WzPngComparison`（像素级）、`EasyComparer`、`DifferenceType` | — |
| `Patcher/` | `WzPatcher`（增量补丁生成/应用）、`PatcherSetting(Collection)`、`PatchPartContext`、`ReversePatcherBuilder` | — |
| `CharaSim/` | `Character`、`CharaEquip`、`CharaProp`、`CharaSimLoader`（全局缓存 SetItem / ExclusiveEquip 等） | — |
| `CharaSimControl/` | Tooltip 渲染族（`GearTooltipRender(2)`、`ItemTooltipRender(2)`、`SkillTooltipRender(2)`、`MobTooltipRenderer`、`NpcTooltipRenderer`、`RecipeTooltipRender`、`SetItemTooltipRender`、`FamiliarTooltipRender`、`DamageSkinTooltipRender`）、自绘控件 `AControl` 家族、`CharaSimControlGroup` | 30 文件 |
| `Config/` | 主程序自己的配置 section：`WcR2Config`、`CharaSimConfig`、`CharaSim*Config`、`ImageHandlerConfig`、`CustomCSSConfig` | 与 `Common/Config` 框架配套 |
| `FrmOptions` / `FrmQuickViewSetting` / `FrmGifSetting` / `FrmGifClipOptions` / `FrmGifMaker` / `FrmCustomCSS` / `FrmUpdater` / `FrmAbout` / `FrmPatcher` | 各对话框（`*.cs` + `*.Designer.cs` + `*.resx` 三件套） | 改 UI 三件套一起改 |
| `DBConnection.cs` | 从 wz 生成 `DataSet`（技能表等导出数据） | — |
| `PictureBoxEx.cs`、`ImageDragHandler.cs` | PNG 预览与拖拽保存 | — |
| `Updater.cs`、`FrmUpdater.cs` | 检查/执行更新（配合嵌入的 `WzComparerR2.Updater`） | — |

## 3. 插件契约的使用方式

- 新增模块应当做成**独立插件工程**（参考 `WzComparerR2.MapRender`），不要往 `MainForm` 里加面板。
- 插件在 `OnLoad()` 里：`Context.AddRibbonBar("Modules", "名字")` 挂按钮、`Context.AddTab(...)` 加标签页、`ConfigManager.RegisterAllSection(assembly)` 注册配置、订阅 `SelectedNode1Changed` / `WzOpened` / `WzClosing`。
- 主程序侧需要新增的只有"挂载点"（事件、Context 成员）。**改 `PluginContext` / `PluginEntry` 的既有成员签名 = 破坏全部插件的二进制兼容**，只能新增、不能改签名或删成员。
- 内存：net6+ 的 ALC 是可卸载的，**不要在静态字段里长期持有插件类型/实例**，否则 ALC 泄漏。

## 4. 改动规则

- **禁止整体重排/格式化 `MainForm.cs`、`MainForm.Designer.cs`、`FrmPatcher.cs`**。按方法定位改动，diff 里只应出现目标方法。
- 改布局：`.cs` / `.Designer.cs` / `.resx` 三件套一起改，控件名保持稳定（`PluginContext` 是按名字 `Find("superTabControl1")`、`Controls["ribbonControl1"]` 找控件的，改名字会让插件挂不上 UI）。
- 错误处理沿用现状：`try/catch` + `PluginManager.LogError("模块名", ex, "...")`，或写 `error.log`；不要引入新的日志/异常框架。
- 线程：MonoGame/渲染相关在 STA 后台线程起窗体；WinForms UI 操作留在 UI 线程。不要用 `async void` 改造事件处理器。
- net462 与 net6+ 的差异必须两边都能编：分支用 `#if NET6_0_OR_GREATER`，不要"顺手删掉" frame 分支。

## 5. 验证

```powershell
dotnet build .\WzComparerR2\WzComparerR2.csproj -f net8.0-windows -c Debug
```

涉及插件时用解决方案构建（保证 `Plugin/`、`Lib/` 拷贝正确）：

```powershell
dotnet build .\WzComparerR2.sln -c Debug -p:Platform="Any CPU"
```

人工验证清单（按改动挑相关项，写进交付说明）：

- 打开 `Data/` 下任意 wz → 树能展开、`String.wz` 名字能显示。
- 选中 `Item.wz` 某 img → 右键装备模拟 / QuickView → tooltip 内容与客户端一致。
- 对比两个 img（Tools → Comparer）→ 差异树、PNG 差异高亮正常。
- 导出 GIF/APNG → 帧数、透明度、尺寸正确。
- 插件入口（Modules → MapRenderV2 / Avatar / LuaConsole / Network）能打开且不报错。

## 6. 别做

- 不要删除 `Dotnet6Patches.cs` 里的补丁或改写其中的混淆标识符。
- 不要把 `Updater` 的逻辑内联回主程序（它必须能被单独执行并替换 exe）。
- 不要在没有 wz 样本时改树节点排序、比较算法、导出编码参数——这些行为都是按客户端结果对齐过的。
- 不要给 `MainForm` 加新的 `public static` 全局状态；需要共享就放 `PluginContext` 或已有 config。
