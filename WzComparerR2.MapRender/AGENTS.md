# AGENTS.md — WzComparerR2.MapRender

地图渲染插件（MonoGame + DirectX 11）。上级约定见仓库根 `AGENTS.md`。

## 1. 两个版本，只编一个

- **V2（当前实现）**：`FrmMapRender2*.cs` + `MapData.cs` + `Patches2/` + `MapScene.cs`。
- **V1（历史实现）**：`FrmMapRender.cs`、`Patches/`、`RenderAnimate.cs`、`RenderArgs.cs`、`ITooltip.cs`、`Camera.cs` 的部分分支——**整文件/整段包在 `#if MapRenderV1` 里，默认不参与编译**（`csproj` 未定义该常量）。
- 结论：**不要为了改 V2 去动 V1**；也不要"清理"V1（复活只需定义 `MapRenderV1`）。任何删除 V1 的动作需主人确认。

## 2. 结构地图

| 文件 | 职责 |
| --- | --- |
| `Entry.cs` | 插件入口：`OnLoad` 里加 Ribbon 按钮 → 从 `Context.SelectedNode1` 取 `Wz_Image` → 校验是 Map.wz → 起 STA 线程跑 `FrmMapRender2` |
| `FrmMapRender2.cs` | MonoGame `Game` 主循环、窗口、输入、UI 集成 |
| `FrmMapRender2.SceneManager.cs` | **状态机 + 协程**：`Starting→Loading→Entering→Running→Exiting`，文件里有转移表注释 |
| `FrmMapRender2.SceneRendering.cs` | 分阶段绘制（背景/层/前景/特效/光照/工具提示） |
| `MapData.cs` | 从 maps img 解析出地图数据：`VRect`、`Bgm`、`FieldLimit`、`Scene`、`MiniMap`、`MapEvents`、`Tooltips` |
| `MapScene.cs` | 场景树：`Back` / `Layers[0..7]{Obj,Reactor,Tile,Foothold}` / `Fly{Portal,LadderRope,Sky,SkyWhale,IlluminantCluster}` / `Front` / `Effect` |
| `Patches2/*Item.cs` | 场景数据对象（`BackItem`、`ObjItem`、`TileItem`、`LifeItem`、`ReactorItem`、`PortalItem`、`ParticleItem`、`TooltipItem`、`SkyWhaleItem`…），基类 `SceneItem`（`Name`/`Index`/`Tags`） |
| `ResourceLoader.cs` | wz → 纹理/动画/字体等资源装载与缓存（含 origin、z、帧数据） |
| `TextureLoader.cs` / `TextureAtlas.cs` / `MeshBatcher.cs` / `MsSpriteRenderer.cs` / `MsCustomSprite.cs` | 纹理与网格合批渲染 |
| `LightRenderer.cs` / `MapLight.cs` / `RenderEnv.cs` | 环境光与光照渲染 |
| `ParticleSystem.cs` / `ParticleEmitter.cs` / `ParticleDesc*.cs` | 粒子系统 |
| `Camera.cs` / `InputState.cs` / `RenderFrame.cs` | 相机、键鼠输入、每帧参数 |
| `Music.cs` | BGM（ManagedBass） |
| `UI/` | EmptyKeys.UserInterface XAML UI + `IMEHelper` 输入法 |
| `Config/MapRenderConfig.cs` | 本插件的配置 section（`[SectionName]` + `ConfigSectionBase<T>`） |
| `Effects/` | `.fx` 与预编译 `.mgfxo`、原生资源（`EmbeddedResource`） |

## 3. 关键机制

- **加载是协程驱动的状态机**：`LoadMap(Wz_Image)` 只把请求投递到 `mapImgLoading`，真正换场景发生在 `SceneManagerState` 转移里（见文件顶部转移表）。**不要在渲染循环或事件回调里直接换 `MapScene`**。
- 协程用 `Coroutine.cs` 的 `IEnumerator` 迭代器（`IE`），不是 `async/await`。保持这种写法，别混用。
- `MapData` 解析要**穷尽容错**：字段缺失给默认值、非法值忽略；一张畸形地图最多渲染不对，不能抛异常炸掉渲染器。
- 资源在加载期一次性装好（`ResourceLoader`），渲染期只读；**每帧访问 `Wz_Node` 是被禁止的**（I/O + 解压会卡帧）。
- 地形与层级 z 排序依赖 `Layer` 顺序 + `Foothold` 挂载关系，改动排序等于全局视觉回归。

## 4. 性能规则

- 每帧不分配：`Vector2`/`Rectangle` 是结构体没问题，但别 `new` 列表、别做 LINQ、别拼字符串。
- 合批优先：`MeshBatcher` + `TextureAtlas`；同纹理连续绘制，减少 `SpriteBatch` 状态切换。
- 粒子：受配置与 `ParticleSystem` 上限约束，新增粒子类型要评估数量。
- 光照：`LightRenderer` 的渲染目标开销大，调试时可用配置关掉再定位问题。

## 5. 验证

```powershell
dotnet build .\WzComparerR2.sln -c Debug -p:Platform="Any CPU"     # 必须走解决方案，插件才会被拷进 Plugin/
```

人工验证（写进交付说明）：

- 打开 `Map.wz` → 选中某 `*.img`（如 `100000000.img`）→ Modules → **MapRenderV2**。
- 期望：地图能进入 `Running` 状态（无异常弹窗）、背景/地形/物件/BGM 正常、可以移动相机、切换地图不崩。
- 涉及光影/粒子的改动，附**改动前后截图**对比（这是唯一能证明视觉一致的手段）。

## 6. 别做

- 不引入 MonoGame Content Pipeline（`Content.mgcb`）；纹理一律从 wz 内存解码。
- 不把 `MapData` 的解析挪进渲染循环，也不为了"实时刷新"每帧重读节点。
- 不重排 `FrmMapRender2*.cs`（单文件 5 万字符以上），按方法改。
- 不在 `MapRender` 里给别的地图功能开洞（比如顺手加 CharaSim 逻辑）；每个插件只管自己。
