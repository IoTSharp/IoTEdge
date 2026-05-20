# IoTEdge 路线图

> 状态：✅ 已完成 ｜ 🚧 进行中 ｜ ⏳ 计划中 ｜ ⬜ 未开始 ｜ 🔁 持续维护
>
> 顺序：`[串行]` ｜ `[并行]` ｜ `[依赖: X]`

> 在 IoTSharp.SaaS 生态中，本仓库被引用为 **`IoTEdge`** 的 C# AOT 边缘基座 / Gateway 宿主。

## 0. 范围

本仓库提供：

- 单宿主 Gateway（`IoTEdge`）：bootstrap、注册、心跳、采集配置同步、采集执行、回传
- C# AOT 边缘基座（被 SaaS 端 `IoTSharp.CodeGen.CSharpAot` 作为生成目标使用）
- BasicRuntime 宿主接口（C# 版），用于承载 IoTEmbedded BASIC 风格脚本
- 采集、合成、上传逻辑优先由 BASIC 脚本承载；C# 侧提供宿主、可注入能力和 AOT/非 AOT 边界

不承载：多租户、Copilot、计费、License。

## 1. 里程碑

| 里程碑 | 状态 | 描述 |
| --- | --- | --- |
| G0 | ✅ | 单宿主收口（替代 Api/Worker/Web 拆分） |
| G1 | 🚧 | bootstrap / 注册 / 心跳 / 配置同步 / 回传 完整链路稳定 |
| G2 | ⏳ | C# AOT 发布稳定（裁剪、启动时间、体积） |
| G3 | 🚧 | BasicRuntime 宿主 v1 + 可注入扩展 |
| G4 | ⏳ | 多协议插件接入（与 IoTEmbedded 能力对齐的网关子集） |
| G5 | ⏳ | 远程诊断与现场可观测 |

## 2. Phase A — 链路稳定　🚧

| 编号 | 状态 | 顺序 | 任务 |
| --- | --- | --- | --- |
| A1 | ✅ | — | 单宿主架构收口 |
| A2 | 🚧 | [串行] | bootstrap.json 校验 / 兼容性 |
| A3 | 🚧 | [并行 ‖ A2] | 注册 / 心跳 / 能力上报错误恢复 |
| A4 | 🚧 | [依赖: A2] | 采集配置同步：版本号、增量、回滚 |
| A5 | ⏳ | [依赖: A4] | 采集执行：脚本驱动轮询入口、Modbus 等基础协议稳定性 |
| A6 | ⏳ | [依赖: A5] | 回传链路：脚本驱动上传入口、批量、压缩、断网缓存 |

## 3. Phase B — C# AOT 基座　⏳

| 编号 | 状态 | 顺序 | 任务 |
| --- | --- | --- | --- |
| B1 | ⏳ | [依赖: A1] | 全程 trim/AOT 兼容（消除 IL2026/IL3050 等警告） |
| B2 | ⏳ | [并行 ‖ B1] | 启动时间与内存基线 |
| B3 | ⏳ | [并行 ‖ B1] | 单文件发布与体积优化 |
| B4 | ⏳ | [依赖: B1] | 与 `IoTSharp.CodeGen.CSharpAot` 的契约固化 |

## 4. Phase C — BasicRuntime 宿主　⏳

| 编号 | 状态 | 顺序 | 任务 |
| --- | --- | --- | --- |
| C1 | 🚧 | [依赖: B1] | BasicRuntime 接口注册表 / 扩展注入 / 脚本入口 |
| C2 | ⏳ | [依赖: C1] | 脚本加载、签名校验、版本槽 |
| C3 | ⏳ | [依赖: C1] | 与 `external/IoTEmbedded` 接口签名对齐 |
| C4 | ⏳ | [依赖: C2] | 沙箱与运行预算 |

## 5. Phase D — 协议与诊断　⏳

| 编号 | 状态 | 顺序 | 任务 |
| --- | --- | --- | --- |
| D1 | ⏳ | [并行] | OPC UA / DLT645 等协议适配 |
| D2 | ⏳ | [并行] | 本地诊断页增强：实时帧、错误统计 |
| D3 | ⏳ | [并行] | 远程诊断通道（与 IoTSharp 平台联动） |
| D4 | ⏳ | 🔁 | 与上游 IoTSharp 平台 Edge API 的兼容矩阵维护 |

## 6. 接口稳定性公约

- 与 `IoTSharp.SaaS` 端 `IoTSharp.CodeGen.CSharpAot` 之间的接口为公开契约；破坏性变更需 6 个月废弃期。
- 与 `external/IoTEmbedded` 共同维护 BasicRuntime 接口签名表；`external/IoTEdge.Linux` 仅作为 Pixiu 历史迁移来源。
- 本仓库不感知租户 / 计费 / License。
