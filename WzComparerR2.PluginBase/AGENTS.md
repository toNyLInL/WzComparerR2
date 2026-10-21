# AGENTS.md — WzComparerR2.PluginBase

插件契约层。主程序与 4 个插件（Avatar / MapRender / Network / LuaConsole）之间的公共 ABI。上级约定见仓库根 `AGENTS.md`。

## 1. 契约

| 类型 | 作用 |
| --- | --- |
| `PluginEntry` | 插件入口抽象类。**构造函数必须接受 `PluginContext`**，`OnLoad()` / `OnUnload()` 是 `protected internal virtual`（只能 override） |
| `PluginContext` | 插件拿到的能力集合：`MainForm`、`DotNetBarManager`、`SelectedNode1/2/3`、`DefaultStringLinker`、`DefaultTooltipWindow`、`AddRibbonBar(tabName, ...)`、`AddTab(...)`，以及 `SelectedNodeNChanged` / `WzOpened` / `WzClosing` 事件 |
| `PluginContextProvider` | 主程序侧实现（`MainForm` 显式实现）。**新增 Context 成员要同步加到这里并由主程序实现** |
| `PluginManager` | 插件发现/加载/卸载、`FindWz(Wz_Type / fullPath)`、`LogError(...)` |
| `PluginInfo` / 事件参数 | 加载结果与事件载荷 |

## 2. 加载规则（改这里之前必须知道）

- 扫描路径：主程序目录下 `Plugin/**/WzComparerR2.*.dll`（递归）。
- 实例化条件：程序集里 `public`、非抽象、继承 `PluginEntry`、有 `(PluginContext)` 构造函数；构造失败的类型被**静默跳过**。
- 加载顺序：先 `frm.PluginOnLoad()`（主窗体自绘控件初始化），再 `PluginManager.PluginOnLoad()`（各插件 `OnLoad`）。
- `OnLoad` 抛异常会弹 `MessageBoxEx`（"插件初始化失败"），`OnUnload` 抛的异常被吞掉。
- 版本/作者信息来自程序集特性：`AssemblyTitle`（Name）、`AssemblyCompany`（Author）、`AssemblyInformationalVersion`（FileVersion）。

## 3. 兼容性红线

1. **只增不改**：`PluginContext` / `PluginEntry` / `PluginManager` 的既有公共成员签名、事件名、语义都不能改。插件是独立编译的 dll，改名/改签名 = 老插件全部失效。
2. 需要新能力：加新成员/新事件，并在主程序 `MainForm` 里实现掉；不要"先注释掉旧成员"。
3. 删成员前先全仓库检索插件使用点（`WzComparerR2.Avatar`、`MapRender`、`Network`、`LuaConsole`），并确认 master 上的示例插件不再引用。
4. `WcR2Plugin` 属性（`Build/Common.props`）会给插件加 `EnableDynamicLoading`；插件工程必须 `Import WcR2Plugin.targets`，否则产物不会拷进 `Plugin/`。

## 4. 验证

```powershell
dotnet build .\WzComparerR2.sln -c Debug -p:Platform="Any CPU"
```

必须走解决方案：契约改动会影响全部插件，只有全量构建才能证明没破。然后人工确认 `WzComparerR2\bin\Debug\<tfm>\Plugin\` 下 4 个插件目录齐全、启动后 Modules 里的按钮都在、点击不报错。

## 5. 别做

- 不要在 `PluginBase` 里塞业务逻辑（地图、装备、聊天都不属于这里）。
- 不要让 `PluginBase` 反向依赖某个具体插件工程。
- 不要把 `PluginContext` 的事件改成 `event EventHandler<T>` 以外的形式（老插件已按此绑定）。
