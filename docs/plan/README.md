# docs/plan — 计划文档

本目录存放**待做/进行中**的计划文档，不记录已完成的行为变更（那属于 `UpdateLogs/`）。

## 约定

- 一个计划一个文件：`<kebab-case-主题>.md`，例如 `wz-to-unity-export.md`。
- 每份计划必须包含：状态、创建日期、目标/非目标、里程碑与验收标准、风险、开放问题。
- 规范/契约类产物放 `spec/`，例如 `spec/wz-export-schema.md`。
- 计划被否决或完成后：状态改为 `closed`（保留文件，便于回溯），不要直接删除。
- 文档内的代码引用请用相对链接并带行号（`../../WzComparerR2/MainForm.cs#L1777`），方便核对与后续代理复查。

## 索引

| 计划 | 状态 | 说明 |
| --- | --- | --- |
| [wz-to-unity-export.md](wz-to-unity-export.md) | 进行中 | WZ 资源导出到外部引擎（以 Unity 为例）的三段式方案与里程碑 |

## 规范

| 规范 | 说明 |
| --- | --- |
| [spec/wz-export-schema.md](spec/wz-export-schema.md) | `wzexport` 中间格式 schema 1：目录结构、meta.json / manifest.json 字段字典、命名与校验规则 |
