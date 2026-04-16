# ItemOptimizer

Barotrauma Lua/CSharp mod that reduces unnecessary per-frame work in large submarines. Requires [Lua For Barotrauma](https://github.com/evilfactory/LuaCsForBarotrauma).

Typical gains are 1-3ms/frame depending on submarine size, mod count, and entity count. All features can be individually toggled in the in-game settings panel (ESC menu). This mod should not affect normal gameplay.

## Features

### Core Optimization

**Item Update Strategies**
- Cold Storage Skip — freeze item updates for items inside unopened containers
- Ground Item Throttle — reduce update frequency for holdable items lying on the floor (wired items exempted)
- Component Throttle — reduce update frequency for CustomInterface, Wearable, Door
- Wire Skip — skip redundant wire signal propagation checks
- HasStatusTag Cache — cache per-frame StatusTag lookups via transpiler to avoid repeated list scans
- Affliction Dedup — skip duplicate affliction applications in the same frame
- StatusHUD Throttle — reduce scan and draw frequency for the health scanner HUD

**Component Rewrites** (full logic replacement for maximum efficiency)
- Motion Sensor Rewrite — replaces vanilla MotionSensor.Update with optimized hull-based spatial detection
- Water Detector Rewrite — replaces vanilla WaterDetector.Update with streamlined water level checks
- Relay Rewrite — replaces vanilla RelayComponent.Update with minimal signal passthrough
- Power Transfer Rewrite — replaces vanilla PowerTransfer.Update (junction boxes, etc.)
- Power Container Rewrite — replaces vanilla PowerContainer.Update (batteries, supercapacitors)

**Signal Graph Accelerator**
- Builds a node graph of wiring connections and evaluates signal chains in batch, skipping per-wire `SendSignal` overhead
- Three modes: Off / Accelerate / Aggressive

**Proxy System**
- Batch compute + sync architecture for groups of similar items

### Character Optimization
- Animation LOD — reduce animation update frequency for off-screen / distant characters
- Character Stagger — distribute enemy AI updates across frame groups (e.g. 4 groups = each AI updates every 4th frame)

### Client Optimization
- Interaction Label Limit — cap on-screen interaction labels to reduce UI overhead (default: 50)
- Relay/MotionSensor/WaterDetector/ButtonTerminal/Pump draw optimization — skip redundant client-side rendering for these components
- Hull Spatial Index — spatial pre-filtering for motion sensor proximity checks

### Network Sync Fixes (client-side)
- Ladder Desync Fix — correct climbing animation state mismatch after server reconciliation
- Platform Desync Fix — sync IgnorePlatforms from server state, fixing rubber-banding on stairs/platforms

### Entity Parallelism
- Misc Parallel — parallelize Hull, Structure, Gap, and Power network updates (safe, no side effects)

### Server-side
- HashSet dedup for server-side item update loops
- Server performance metrics collection and relay to clients

### Mod Item Management
- **Per-mod throttle profiles** — automatically detects installed mods and assigns activity-tier-based throttle values (Critical/Active/Moderate/Static). Each mod gets an intensity slider (0-100%) that interpolates between base and max skip frames.
- **Global intensity slider** — adjust all mod throttle intensities at once
- **Item Rules** — user-defined per-item rules (Skip or Throttle with configurable skip frames and conditions)
- **Whitelist** — exempt specific items from mod-based throttling

### Diagnostics
- HUD Stats Overlay — real-time display of skip counts, timing, proxy stats, signal graph metrics
- Held Item Inspector — shows optimization status of the currently held item on the HUD, with quick-whitelist button
- Spike Detector — flags items whose Update exceeds a configurable threshold (off by default, adds overhead)
- Diagnostic logging and performance profiling commands

## Settings Panel

Press ESC in-game and click the "ItemOptimizer" button. The panel is organized into sections:

1. **HUD Overlay Toggle** (top) — quick toggle for the stats overlay
2. **Core Optimization** — item throttles, caches, rewrites, character optimization
3. **Advanced** — signal graph mode, sensor mode dropdowns (Off/Throttle/Rewrite), hull spatial index, misc parallel, proxy system
4. **Client Optimization** — interaction label limit, component draw optimizations
5. **Mod Control** — per-mod intensity sliders, global intensity, tier configuration
6. **Item Rules** (collapsible) — per-item rule editor with autocomplete
7. **Whitelist** (collapsible) — item whitelist with autocomplete
8. **Diagnostics** — live skip/timing statistics

Config is saved to `ItemOptimizer_config.xml`. Mod profiles are saved separately.

## Potential Mod Conflicts

This mod uses Harmony to patch the following vanilla methods. **Any mod that patches the same methods may conflict:**

| Method | Patch Type | Feature |
|--------|-----------|---------|
| `MapEntity.UpdateAll` | Prefix (takeover) | UpdateAll rewrite — cold storage, ground throttle, mod opt, misc parallel |
| `Item.Update` | Transpiler | Item update transpiler |
| `CustomInterface.Update` | Prefix | CI throttle |
| `MotionSensor.Update` | Prefix | Motion sensor throttle / rewrite |
| `Wearable.Update` | Prefix | Wearable throttle |
| `WaterDetector.Update` | Prefix + Postfix | Water detector throttle / rewrite |
| `Door.Update` | Prefix + Postfix | Door throttle |
| `ButtonTerminal.Update` | Prefix | ButtonTerminal optimization |
| `Pump.Update` | Transpiler | Pump optimization |
| `RelayComponent.Update` | Prefix | Relay rewrite / draw optimization |
| `PowerTransfer.Update` | Prefix | Power transfer rewrite |
| `PowerContainer.Update` | Prefix | Power container rewrite |
| `PropertyConditional.Matches` | Transpiler | HasStatusTag cache |
| `CharacterHealth.ApplyAffliction` | Prefix | Affliction dedup |
| `Character.UpdateAnimAll` | Prefix | Animation LOD |
| `EnemyAIController.Update` | Prefix | Character stagger |
| `Ragdoll.UpdateNetPlayerPositionProjSpecific` | Prefix + Postfix | Ladder/platform desync fix |
| `Connection.SendSignalIntoConnection` | Prefix | Signal graph accelerator |
| `Wire.Connect / TryConnect / RemoveConnection / ClearConnections` | Postfix | Signal graph invalidation |
| `Connection.DisconnectWire` | Postfix | Signal graph invalidation |
| `InteractionLabelManager.RefreshInteractablesInRange` | Postfix | Interaction label limit |
| `MotionSensor.TriggersOn` | Prefix | Motion sensor draw optimization |
| `Gap.SimulateWaterFlowFromOutsideToConnectedHulls` | Prefix | Gap safety |
| `Gap.RefreshOutsideCollider` | Prefix | Gap safety |
| `StatusHUD.Update / DrawThermalOverlay` | Prefix + Postfix | StatusHUD throttle |
| `GUI.TogglePauseMenu` | Postfix | Settings button injection |
| `GUI.Draw` | Postfix | Stats overlay |
| `GameServer.ClientWriteIngame` | Transpiler | Server HashSet dedup |

Most critically, mods that completely replace `MapEntity.UpdateAll` will directly conflict with the UpdateAll takeover system.

---

# ItemOptimizer

Barotrauma Lua/CSharp mod，减少大型潜艇中不必要的逐帧计算。依赖 [Lua For Barotrauma](https://github.com/evilfactory/LuaCsForBarotrauma)。

典型增益为 1-3ms/帧，取决于潜艇大小、mod 数量和实体数量。所有功能均可在游戏内设置面板中单独开关（ESC 菜单），不影响正常游戏流程。

## 功能

### 核心优化

**物品更新策略**
- 冷存储跳过 — 冻结未打开容器内物品的更新
- 地面物品节流 — 降低地面可拾取物品的更新频率（有线连接的物品豁免）
- 组件节流 — 降低 CustomInterface、Wearable、Door 的更新频率
- 线路跳过 — 跳过冗余的线路信号传播检查
- HasStatusTag 缓存 — 通过 Transpiler 缓存每帧的状态标签查询
- Affliction 去重 — 跳过同一帧内的重复异常状态施加
- StatusHUD 节流 — 降低健康扫描仪 HUD 的扫描和绘制频率

**组件重写**（完全替换原版逻辑以获得最大效率）
- 运动传感器重写 — 使用基于 Hull 的空间检测替换原版 MotionSensor.Update
- 水位传感器重写 — 使用精简的水位检查替换原版 WaterDetector.Update
- 中继器重写 — 使用最小信号传递替换原版 RelayComponent.Update
- 电力传输重写 — 替换原版 PowerTransfer.Update（接线箱等）
- 电力容器重写 — 替换原版 PowerContainer.Update（电池、超级电容）

**信号图加速器**
- 构建接线连接的节点图，批量计算信号链，跳过逐线 `SendSignal` 开销
- 三种模式：关闭 / 加速 / 激进

**代理系统**
- 对同类物品进行批量计算 + 同步的架构

### 角色优化
- 动画 LOD — 降低远处/屏幕外角色的动画更新频率
- AI 交错更新 — 将敌方 AI 更新分散到多个帧组（如 4 组 = 每个 AI 每 4 帧更新一次）

### 客户端优化
- 交互标签限制 — 限制屏幕上的交互标签数量以减少 UI 开销（默认：50）
- Relay/MotionSensor/WaterDetector/ButtonTerminal/Pump 绘制优化 — 跳过这些组件的冗余客户端渲染
- Hull 空间索引 — 运动传感器近距离检测的空间预过滤

### 网络同步修复（仅客户端）
- 梯子反同步修复 — 修正服务器校正后攀爬动画状态不一致
- 平台穿越反同步修复 — 从服务器状态同步 IgnorePlatforms，修复在楼梯/平台上按 S 时的回弹

### 实体并行化
- Misc Parallel — 并行化 Hull、Structure、Gap 和电力网络更新（安全，无副作用）

### 服务端
- 服务端物品更新循环 HashSet 去重
- 服务器性能指标收集和中继至客户端

### Mod 物品管理
- **逐 Mod 节流配置** — 自动检测已安装的 mod，按活跃度分层（关键/活跃/中等/静态）分配节流值。每个 mod 有强度滑条（0-100%），在基础值和最大跳帧之间插值。
- **全局强度滑条** — 一键调整所有 mod 的节流强度
- **物品规则** — 用户自定义的逐物品规则（跳过或节流，可配置跳帧数和条件）
- **白名单** — 将特定物品从 mod 节流中豁免

### 诊断工具
- HUD 统计覆盖层 — 实时显示跳过次数、耗时、代理统计、信号图指标
- 手持物品检视器 — 在 HUD 上显示当前手持物品的优化状态，含快捷白名单按钮
- 尖峰检测器 — 标记 Update 超过阈值的物品（默认关闭，有额外开销）
- 诊断日志和性能分析命令

## 设置面板

游戏内按 ESC，点击 "ItemOptimizer" 按钮。面板按区域组织：

1. **HUD 覆盖层开关**（顶部）— 快速开关统计覆盖层
2. **核心优化** — 物品节流、缓存、重写、角色优化
3. **高级** — 信号图模式、传感器模式下拉框（关闭/节流/重写）、Hull 空间索引、实体并行、代理系统
4. **客户端优化** — 交互标签限制、组件绘制优化
5. **Mod 控制** — 逐 Mod 强度滑条、全局强度、层级配置
6. **物品规则**（可折叠）— 逐物品规则编辑器，带自动补全
7. **白名单**（可折叠）— 物品白名单，带自动补全
8. **诊断** — 实时跳过/耗时统计

配置保存至 `ItemOptimizer_config.xml`。Mod 配置文件单独保存。

## 潜在 Mod 冲突

本 mod 使用 Harmony 修补以下原版方法。**任何修补相同方法的 mod 都可能冲突：**

| 方法 | 补丁类型 | 对应功能 |
|------|---------|---------|
| `MapEntity.UpdateAll` | Prefix（接管） | UpdateAll 重写 — 冷存储、地面节流、Mod 优化、实体并行 |
| `Item.Update` | Transpiler | 物品更新 Transpiler |
| `CustomInterface.Update` | Prefix | CI 节流 |
| `MotionSensor.Update` | Prefix | 运动传感器节流/重写 |
| `Wearable.Update` | Prefix | 可穿戴节流 |
| `WaterDetector.Update` | Prefix + Postfix | 水位传感器节流/重写 |
| `Door.Update` | Prefix + Postfix | 门节流 |
| `ButtonTerminal.Update` | Prefix | 按钮终端优化 |
| `Pump.Update` | Transpiler | 泵优化 |
| `RelayComponent.Update` | Prefix | 中继器重写/绘制优化 |
| `PowerTransfer.Update` | Prefix | 电力传输重写 |
| `PowerContainer.Update` | Prefix | 电力容器重写 |
| `PropertyConditional.Matches` | Transpiler | HasStatusTag 缓存 |
| `CharacterHealth.ApplyAffliction` | Prefix | Affliction 去重 |
| `Character.UpdateAnimAll` | Prefix | 动画 LOD |
| `EnemyAIController.Update` | Prefix | AI 交错更新 |
| `Ragdoll.UpdateNetPlayerPositionProjSpecific` | Prefix + Postfix | 梯子/平台反同步修复 |
| `Connection.SendSignalIntoConnection` | Prefix | 信号图加速器 |
| `Wire.Connect / TryConnect / RemoveConnection / ClearConnections` | Postfix | 信号图失效 |
| `Connection.DisconnectWire` | Postfix | 信号图失效 |
| `InteractionLabelManager.RefreshInteractablesInRange` | Postfix | 交互标签限制 |
| `MotionSensor.TriggersOn` | Prefix | 运动传感器绘制优化 |
| `Gap.SimulateWaterFlowFromOutsideToConnectedHulls` | Prefix | Gap 安全 |
| `Gap.RefreshOutsideCollider` | Prefix | Gap 安全 |
| `StatusHUD.Update / DrawThermalOverlay` | Prefix + Postfix | StatusHUD 节流 |
| `GUI.TogglePauseMenu` | Postfix | 设置按钮注入 |
| `GUI.Draw` | Postfix | 统计覆盖层 |
| `GameServer.ClientWriteIngame` | Transpiler | 服务端 HashSet 去重 |

最关键的冲突点：完全替换 `MapEntity.UpdateAll` 的 mod 会与 UpdateAll 接管系统直接冲突。
