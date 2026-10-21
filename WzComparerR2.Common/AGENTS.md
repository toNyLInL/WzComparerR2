# AGENTS.md — WzComparerR2.Common

主程序与各插件共享的基础库。上级约定见仓库根 `AGENTS.md`。

## 1. 先记住两件事

1. **根命名空间是 `WzComparerR2`，不是 `WzComparerR2.Common`**。所以 `Common/CharaSim` 与主程序 `WzComparerR2/CharaSim` 里的类型同处一个命名空间（`WzComparerR2.CharaSim`）——在两个项目间移动类型不会改命名空间，但会改程序集归属，属于对插件的**二进制破坏性变更**。
2. **本库同时被主程序与全部插件引用**，任何公共成员签名变更都要先确认 4 个插件工程（Avatar / MapRender / Network / LuaConsole）能编过。

## 2. 目录地图

| 目录 | 内容 | 备注 |
| --- | --- | --- |
| `Config/` | `ConfigManager`、`ConfigSectionBase<T>`、`ConfigItem`、`SectionNameAttribute` | 全局配置框架，`Setting.config`（exe 同目录） |
| `CharaSim/` | 40 个纯数据模型：`Gear`、`Item`、`Mob`、`Npc`、`Skill`、`SetItem`、`Potential`、`Recipe`、`Familiar`、`DamageSkin`、`GearPropType`/`ItemPropType`/`ItemSpecType`、`SummaryParser`、`FormulaVersion` | 从 `Wz_Node` 反序列化 + 提供属性/公式，**不含 UI** |
| `Text/` | `Parser`（客户端富文本标记 → `DocElement` 列表）、`TextRenderer`、`DocumentElements` | 装备/技能说明文本的解析与排版 |
| `Rendering/` | `D2DFactory`/`D2DContext`/`D2DRenderer`/`D2DFont`、`XnaFont`/`XnaFontRenderer`、`PngEffect`、`MeshBatcher` 相关工具、`WzLibExtension`（wz → Texture2D） | Direct2D 与 XNA 两套栈并存 |
| `Animation/` | `FrameAnimator`/`FrameAnimationData`/`KeyFrame`、`SpineAnimatorV2/V4`、`MaplestoryCanvasVideoLoader` | Spine V2 与 V4 是两套不同实现，别合并 |
| `Encoders/` | `BuildInGifEncoder`、`BuildInApngEncoder`、`FFmpegEncoder`、`IndexGifEncoder`、`GifEncoderCompatibility` | 动图/截图导出 |
| `Controls/` | `AlphaForm`、`GraphicsDeviceControl`(+`GraphicsDeviceService`)、`AnimationControl`、`ProgressDialog` | WinForms ↔ MonoGame 桥接 |
| `StringLinker.cs` | String.wz 字符串表查询（含多语言回退） | 插件用 `PluginContext.DefaultStringLinker` |

## 3. 配置框架规则

- 新增配置：写一个 `[SectionName("...")]` 标注的 `ConfigSectionBase<T>` 子类，T 指向自身；属性上用 `ConfigItem`。
- 注册：在所属程序集启动时调用 `ConfigManager.RegisterAllSection(assembly)`（插件在 `OnLoad()` 里调）。**只继承 `ConfigurationSection` 而不走 `ConfigSectionBase<T>` 不会被扫到。**
- `SectionName` 缺省时取**类名**——改类名等于丢用户配置，改名必须显式补 `[SectionName]`。
- 保存：`ConfigManager.Save()` 写入 `Setting.config`。新增属性要有合理默认值，避免老配置读取时报错。

## 4. CharaSim 模型约定

- 一个 wz 属性 → 一个字段 + 一个 `XxxPropType` 枚举值 + 一处 `switch` 映射；不要用反射批量映射。
- 数值/文本展示的拼装走 `SummaryParser` / `ItemStringHelper`，不要在各个 Tooltip 渲染器里各写一份。
- 公式随版本变化的部分集中到 `FormulaVersion`，用参数控制而不是 `if (客户端版本 == ...)` 散落各处。
- 模型只做"节点 → 字段"和"字段 → 展示值"；画图、字体、颜色都在 `WzComparerR2/CharaSimControl`。

## 5. 渲染相关注意

- `Rendering/Effect/PngEffect.<MonoGame 版本>.mgfxo` **已入库**，三个 MonoGame 版本各一份（`Common.props` 里按 TFM 选版本）。`csproj` 的 `PreBuild` 只在文件缺失时调 `EffectCompiler.bat` 重新编译。不要删除、不要重复提交新版本、不要改 `.fx` 而不重新产出对应 `.mgfxo`。
- `Bitmap`（GDI+）与 `Texture2D`（XNA）互转很贵，禁止在每帧循环里来回转；批量转换请一次成型。
- `System.Drawing.Common` 版本随 TFM 不同（net8 → 8.0.11，net6 → 6.0.0，net462 → 框架自带）。不要用只在 8.0 才有的 API。
- net6+ 下 GBK 等码页需要 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`，**这行注册在主程序 `Program.Main`**。本库不要假设非 ASCII 码页可直接用。

## 6. 验证

```powershell
dotnet build .\WzComparerR2.Common\WzComparerR2.Common.csproj -f net8.0-windows -c Debug
dotnet build .\WzComparerR2.Common\WzComparerR2.Common.csproj -f net462         -c Debug
```

改了 CharaSim 字段/公式，人工验证路径形如：`打开 Item.wz → 1302000.img → 右键装备模拟 → 期望 tooltip 中的攻击力与客户端一致`；改了编码器：`QuickView 导出 GIF/APNG，检查帧数与透明度`。

## 7. 别做

- 不要在 `Common` 里 `new Form()` 或弹窗（`Controls/` 里的控件由主程序实例化）。
- 不要给 `Common` 引入 `System.Text.Json`、`Newtonsoft.Json`、日志库、DI 容器（现有 JSON 用在主程序与 Network 插件）。
- 不要把 `CharaSim` 模型改成 `record` / 加 nullable 注解 / 改成不可变对象——大量代码依赖可变字段与字段顺序。
