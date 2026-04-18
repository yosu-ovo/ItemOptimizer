# ItemOptimizer

Barotrauma Lua/CSharp mod that reduces unnecessary per-frame work in large submarines. Requires [Lua For Barotrauma](https://github.com/evilfactory/LuaCsForBarotrauma).

Typical gains are 2-5ms/frame depending on submarine size, mod count, and entity count. All features can be individually toggled in the in-game settings panel (ESC menu). Supports 12 languages. This mod should not affect normal gameplay.

## Features

### Core Optimization

- **Idle Item Skip** — skip updates for items in lockers and containers, significant performance savings
- **Ground Item Throttle** — reduce update frequency for items on the ground (wired items exempted)
- **Zone Dispatch** — dynamic loading for wrecks, ruins, outposts etc. Distant zones are frozen with tier-based LOD (Active > Nearby > Passive > Dormant > Unloaded)
- **Wire Skip** — skip wire item updates; signal propagation unaffected, but wires become invisible (still operable in wiring panels)
- **HasStatusTag Cache** — cache per-frame StatusTag lookups via transpiler to avoid repeated list scans

### Component Rewrites (full logic replacement)

- **Motion Sensor Rewrite** — optimized hull-based spatial detection with configurable interval
- **Water Detector Rewrite** — streamlined water level checks with configurable interval
- **Relay Rewrite** — minimal signal passthrough
- **Power Transfer Rewrite** — optimized junction box and power transfer update logic
- **Power Container Rewrite** — optimized battery and supercapacitor update logic

### Signal Graph Accelerator

- Compiles wiring connections into a node graph and evaluates signal chains in batch
- Three modes: Off / Accelerate / Aggressive (expanded compilation including relay passthrough + delay)

### Character Optimization

- **Animation LOD** — simplified animation for distant characters, skip animation for invisible ones
- **AI Stagger Update** — spread enemy AI updates across frame groups (configurable group count)

### Client Optimization

- **Interaction Label Limit** — cap on-screen interaction labels when pressing ALT (configurable, default: 50)
- **ButtonTerminal Optimization** — eliminate nested query overhead in button terminal
- **Pump Optimization** — cache pump component reference, skip per-frame lookups
- **Hull Spatial Index** — accelerate character-hull spatial queries

### Network Sync Fixes (client-side)

- **Ladder Sync Fix** — fix ladder position desync, eliminates rubber-banding
- **Platform Sync Fix** — fix platform drop-through position desync
- **Server Dedup Optimization** — HashSet-based O(1) deduplication for server position update queue

### Entity Parallelism

- **Misc Parallel** — parallelize Hull, Structure, Gap, and Power network updates

### Server-side

- Server performance metrics collection and relay to clients
- Server performance overlay (per-system timing breakdown)

### Mod Item Management

- **Per-mod throttle profiles** — automatically detects installed mods and assigns activity-tier-based throttle values (Critical/Active/Moderate/Static). Each mod has an intensity slider (0-100%) that interpolates between base and max skip frames.
- **Global intensity slider** — adjust all mod throttle intensities at once
- **Item Rules** — user-defined per-item rules (Skip or Throttle with configurable skip frames and conditions)
- **Whitelist** — exempt specific items from all throttling

### Diagnostics & Developer Tools

- **HUD Stats Overlay** — real-time display of skip counts and timing
- **Impact Bars** — animated performance impact visualization per feature, with smooth retained-mode updates
- **Server Perf Overlay** — server-side per-system timing breakdown with bar chart
- **Spike Detector** — detect items causing per-frame spikes (configurable threshold)
- **Sync Recording** — allow clients to request server sync snapshots (iosync command)
- **Perf Profiler** — record per-item timing to CSV for offline analysis
- **Signal Graph Info** — show signal graph accelerator diagnostics

## Settings Panel

Press ESC in-game and click "Item Optimizer". The panel is organized into sections:

1. **HUD Overlay** / **Impact Bars** — quick toggles at the top
2. **Core Optimization** — idle item skip, ground throttle, zone dispatch, signal graph
3. **Sensors** — motion sensor and water detector rewrites with configurable intervals
4. **Client Optimization** — interaction labels, button terminal, pump
5. **Advanced** — wire skip, misc parallel, hull spatial index
6. **Circuit (Experimental)** — relay, power transfer, power container rewrites, HasStatusTag cache
7. **Character Optimization** — animation LOD, AI stagger
8. **Network Sync Fixes** — ladder fix, platform fix, server dedup
9. **Developer Tools** — spike detector, sync recording, server perf overlay, profiler, signal graph info
10. **Mod Control Panel** — per-mod intensity sliders, tier configuration, one-click optimize
11. **Item Rules** (collapsible) — per-item rule editor with autocomplete
12. **Whitelist** (collapsible) — item whitelist with autocomplete
13. **Diagnostics** — live skip/timing statistics with estimated savings

Config is saved to `ItemOptimizer_config.xml`. Mod profiles are saved separately.

## Localization

Supports 12 languages: English, 简体中文, 繁體中文, 日本語, 한국어, Deutsch, Español, Français, Português (BR), Русский, Polski, Türkçe. Language is auto-detected from game settings. Translation files are in `Localization/*.json`.

## Potential Mod Conflicts

This mod uses Harmony to patch the following vanilla methods. **Any mod that patches the same methods may conflict:**

| Method | Patch Type | Feature |
|--------|-----------|---------|
| `MapEntity.UpdateAll` | Prefix (takeover) | UpdateAll rewrite — idle item skip, ground throttle, mod opt, zone dispatch, misc parallel |
| `Item.Update` | Transpiler | Item update instrumentation |
| `MotionSensor.Update` | Prefix | Motion sensor rewrite |
| `WaterDetector.Update` | Prefix | Water detector rewrite |
| `RelayComponent.Update` | Prefix | Relay rewrite |
| `PowerTransfer.Update` | Prefix | Power transfer rewrite |
| `PowerContainer.Update` | Prefix | Power container rewrite |
| `ButtonTerminal.Update` | Prefix | ButtonTerminal optimization |
| `Pump.Update` | Transpiler | Pump optimization |
| `PropertyConditional.Matches` | Transpiler | HasStatusTag cache |
| `Character.UpdateAnimAll` | Prefix | Animation LOD |
| `EnemyAIController.Update` | Prefix | AI stagger update |
| `Character.Update` | Prefix | Character zone skip |
| `Ragdoll.UpdateNetPlayerPositionProjSpecific` | Prefix + Postfix | Ladder/platform sync fix |
| `Connection.SendSignalIntoConnection` | Prefix | Signal graph accelerator |
| `Wire.Connect / TryConnect / RemoveConnection / ClearConnections` | Postfix | Signal graph invalidation |
| `Connection.DisconnectWire` | Postfix | Signal graph invalidation |
| `InteractionLabelManager.RefreshInteractablesInRange` | Postfix | Interaction label limit |
| `Gap.SimulateWaterFlowFromOutsideToConnectedHulls` | Prefix | Gap safety |
| `Gap.RefreshOutsideCollider` | Prefix | Gap safety |
| `GUI.TogglePauseMenu` | Postfix | Settings button injection |
| `GUI.Draw` | Postfix | HUD overlays |
| `GameServer.ClientWriteIngame` | Transpiler | Server HashSet dedup |
| `GameSession.StartRound / EndRound` | Postfix | NativeRuntime lifecycle |
| `ServerPeer.Update` | Prefix + Postfix | Server performance tracking |

Most critically, mods that completely replace `MapEntity.UpdateAll` will directly conflict with the UpdateAll takeover system.

---

# ItemOptimizer

Barotrauma Lua/CSharp mod，减少大型潜艇中不必要的逐帧计算。依赖 [Lua For Barotrauma](https://github.com/evilfactory/LuaCsForBarotrauma)。

典型增益为 2-5ms/帧，取决于潜艇大小、mod 数量和实体数量。所有功能均可在游戏内设置面板中单独开关（ESC 菜单），支持 12 种语言。不影响正常游戏流程。

## 功能

### 核心优化

- **闲置物品跳过** — 储物柜和箱子中的物品跳过更新，节省大量性能
- **地面物品节流** — 降低地面散落物品的更新频率（有线连接的物品豁免）
- **区域调度** — 沉船、遗迹、站点等动态加载，较远区域按分级 LOD 冻结（活跃 > 邻近 > 被动 > 休眠 > 卸载）
- **电线跳过** — 跳过电线物品更新，信号传输不受影响，但电线不可见（接线面板中仍可操作）
- **状态标签缓存** — 通过 Transpiler 缓存每帧的状态标签查询

### 组件重写（完全替换原版逻辑）

- **运动传感器重写** — 基于 Hull 的优化空间检测，可配置间隔
- **水位传感器重写** — 精简的水位检查，可配置间隔
- **中继器重写** — 最小信号传递逻辑
- **配电盘重写** — 优化接线箱等电力传输更新逻辑
- **电池重写** — 优化电池/蓄电器更新逻辑

### 信号图加速器

- 编译接线连接为节点图，批量计算信号链
- 三种模式：关闭 / 加速 / 激进（扩大编译范围，含继电器信号直通 + 延迟器）

### 角色优化

- **动画简化** — 远处角色使用简化动画，不可见角色跳过动画计算
- **AI 分组更新** — 怪物 AI 分组轮流更新（可配置分组数）

### 客户端优化

- **交互标签优化** — 限制按 ALT 时显示的交互标签数量（可配置，默认 50）
- **按钮终端优化** — 消除按钮终端每帧的嵌套查询开销
- **泵优化** — 缓存泵的组件引用，跳过每帧查找
- **空间加速索引** — 加速角色与舱室的空间查询

### 网络同步修复（仅客户端）

- **梯子同步修复** — 修复梯子上的位置不同步问题，消除橡皮筋效应
- **平台同步修复** — 修复平台穿越时的位置不同步问题
- **服务器去重优化** — 基于 HashSet 的 O(1) 服务器位置更新队列去重

### 实体并行化

- **杂项并行** — 并行处理舱室/结构/缺口/电力更新

### 服务端

- 服务器性能指标收集和中继至客户端
- 服务端性能叠加层（各系统耗时分解）

### Mod 物品管理

- **逐 Mod 节流配置** — 自动检测已安装 mod，按活跃度分层（高活跃/活跃/普通/静态）分配节流值，每个 mod 有强度滑条（0-100%）
- **全局强度滑条** — 一键调整所有 mod 的节流强度
- **物品规则** — 用户自定义的逐物品规则（跳过或节流，可配置跳帧数和条件）
- **白名单** — 将特定物品从所有节流中豁免

### 诊断与开发者工具

- **HUD 统计叠加层** — 实时显示跳过次数和耗时
- **性能条** — 每个功能的动画性能影响可视化，平滑的 retained-mode 更新
- **服务端性能叠加层** — 服务端各系统耗时条形图
- **卡顿检测** — 检测单帧耗时过高的物品（可配置阈值）
- **同步录制** — 允许客户端请求服务端同步数据（iosync 命令）
- **性能分析器** — 录制逐物品计时数据保存为 CSV
- **信号图诊断** — 显示信号图加速器状态

## 设置面板

游戏内按 ESC，点击「物品优化」按钮。面板按区域组织：

1. **HUD 叠加层** / **性能条** — 顶部快速开关
2. **核心优化** — 闲置物品跳过、地面节流、区域调度、信号图
3. **传感器** — 运动传感器和水位传感器重写，可配置间隔
4. **客户端优化** — 交互标签、按钮终端、泵
5. **进阶选项** — 电线跳过、杂项并行、空间索引
6. **电路系统（实验性）** — 中继器、配电盘、电池重写，状态标签缓存
7. **角色优化** — 动画简化、AI 分组更新
8. **网络同步修复** — 梯子修复、平台修复、服务器去重
9. **开发者工具** — 卡顿检测、同步录制、服务端性能叠加层、分析器、信号图诊断
10. **Mod 控制面板** — 逐 Mod 强度滑条、分层配置、一键推荐优化
11. **物品规则**（可折叠）— 逐物品规则编辑器，带自动补全
12. **白名单**（可折叠）— 物品白名单，带自动补全
13. **诊断与统计** — 实时跳过/耗时统计及预计节省

配置保存至 `ItemOptimizer_config.xml`。Mod 配置文件单独保存。

## 本地化

支持 12 种语言：English、简体中文、繁體中文、日本語、한국어、Deutsch、Español、Français、Português (BR)、Русский、Polski、Türkçe。语言根据游戏设置自动检测。翻译文件位于 `Localization/*.json`。

## 潜在 Mod 冲突

本 mod 使用 Harmony 修补以下原版方法。**任何修补相同方法的 mod 都可能冲突：**

| 方法 | 补丁类型 | 对应功能 |
|------|---------|---------|
| `MapEntity.UpdateAll` | Prefix（接管） | UpdateAll 重写 — 闲置跳过、地面节流、Mod 优化、区域调度、实体并行 |
| `Item.Update` | Transpiler | 物品更新插桩 |
| `MotionSensor.Update` | Prefix | 运动传感器重写 |
| `WaterDetector.Update` | Prefix | 水位传感器重写 |
| `RelayComponent.Update` | Prefix | 中继器重写 |
| `PowerTransfer.Update` | Prefix | 配电盘重写 |
| `PowerContainer.Update` | Prefix | 电池重写 |
| `ButtonTerminal.Update` | Prefix | 按钮终端优化 |
| `Pump.Update` | Transpiler | 泵优化 |
| `PropertyConditional.Matches` | Transpiler | 状态标签缓存 |
| `Character.UpdateAnimAll` | Prefix | 动画简化 |
| `EnemyAIController.Update` | Prefix | AI 分组更新 |
| `Character.Update` | Prefix | 角色区域跳过 |
| `Ragdoll.UpdateNetPlayerPositionProjSpecific` | Prefix + Postfix | 梯子/平台同步修复 |
| `Connection.SendSignalIntoConnection` | Prefix | 信号图加速器 |
| `Wire.Connect / TryConnect / RemoveConnection / ClearConnections` | Postfix | 信号图失效 |
| `Connection.DisconnectWire` | Postfix | 信号图失效 |
| `InteractionLabelManager.RefreshInteractablesInRange` | Postfix | 交互标签限制 |
| `Gap.SimulateWaterFlowFromOutsideToConnectedHulls` | Prefix | Gap 安全 |
| `Gap.RefreshOutsideCollider` | Prefix | Gap 安全 |
| `GUI.TogglePauseMenu` | Postfix | 设置按钮注入 |
| `GUI.Draw` | Postfix | HUD 叠加层 |
| `GameServer.ClientWriteIngame` | Transpiler | 服务端 HashSet 去重 |
| `GameSession.StartRound / EndRound` | Postfix | NativeRuntime 生命周期 |
| `ServerPeer.Update` | Prefix + Postfix | 服务端性能追踪 |

最关键的冲突点：完全替换 `MapEntity.UpdateAll` 的 mod 会与 UpdateAll 接管系统直接冲突。
