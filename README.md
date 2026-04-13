# ItemOptimizer

Barotrauma Lua/CSharp mod that reduces unnecessary per-frame work in large submarines. Requires [Lua For Barotrauma](https://steamcommunity.com/sharedfiles/filedetails/?id=2559634234).

**Honest disclaimer:** The optimization gains are modest — typically 1-3ms/frame depending on submarine size and entity count. This mod is designed to be as non-intrusive as possible and should not affect normal gameplay. All features can be individually toggled on/off in the in-game settings panel (ESC menu).

## Features

**Item Update Strategies**
- Cold Storage Skip — freeze item updates for items inside unopened containers
- Ground Item Throttle — reduce update frequency for holdable items lying on the floor (wired items exempted)
- Component Throttle — reduce update frequency for CustomInterface, MotionSensor, Wearable, WaterDetector, Door
- HasStatusTag Cache — cache per-frame StatusTag lookups to avoid repeated list scans
- Affliction Dedup — skip duplicate affliction applications in the same frame

**Character Optimization**
- Animation LOD — reduce animation update frequency for off-screen / distant characters
- Character Stagger — distribute AI character updates across frame groups (e.g. 4 groups = each AI updates every 4th frame)

**Network Sync Fixes** (client-side only)
- Ladder Desync Fix — correct climbing animation state mismatch after server reconciliation, with anti-rubber-banding
- Platform Desync Fix — sync IgnorePlatforms from server state, fixing rubber-banding when pressing S on stairs/platforms

**Parallel Processing** (experimental)
- Parallel Item Dispatch — offload safe item updates to worker threads
- Thread safety patches for Rand.GetRNG, connection queries, etc.

**Server-side**
- HashSet dedup for server-side item update loops
- Server performance metrics relay to clients

## Potential Mod Conflicts

This mod uses Harmony to patch the following vanilla methods. **Any mod that patches the same methods may conflict:**

| Method | Patch Type | Feature |
|--------|-----------|---------|
| `MapEntity.UpdateAll` | Prefix (takeover) | Parallel dispatch, cold storage, ground item throttle |
| `CustomInterface.Update` | Prefix | CI throttle |
| `MotionSensor.Update` | Prefix | Motion sensor throttle |
| `Wearable.Update` | Prefix | Wearable throttle |
| `WaterDetector.Update` | Prefix + Postfix | Water detector throttle |
| `Door.Update` | Prefix + Postfix | Door throttle (disabled by default) |
| `PropertyConditional.Matches` | Transpiler | HasStatusTag cache |
| `CharacterHealth.ApplyAffliction` | Prefix | Affliction dedup |
| `Ragdoll.UpdateNetPlayerPositionProjSpecific` | Prefix + Postfix | Ladder/platform desync fix |
| `AnimController.UpdateAnim` | Prefix | Animation LOD |
| `Character.Control` | Prefix | Character stagger |
| `Rand.GetRNG` | Transpiler | Thread-safe RNG |
| `GUI.TogglePauseMenu` | Postfix | Settings button injection |
| `GUI.Draw` | Postfix | Stats overlay |
| `StatusHUD.Update / DrawThermalOverlay` | Prefix/Postfix | StatusHUD throttle |

Most critically, mods that completely replace `MapEntity.UpdateAll` will directly conflict with the parallel dispatch system.

## Settings

Press ESC in-game to access the settings panel via the "ItemOptimizer" button. All strategies can be toggled individually. Config is saved to `ItemOptimizer_config.xml`.

---

# ItemOptimizer

Barotrauma Lua/CSharp mod, 减少大型潜艇中不必要的逐帧计算。依赖 [Lua For Barotrauma](https://steamcommunity.com/sharedfiles/filedetails/?id=2559634234)。

**老实说:** 优化增益有限 — 通常 1-3ms/帧, 取决于潜艇大小和实体数量。本 mod 设计原则是尽量不影响正常游戏流程，所有功能均可在游戏内设置面板中单独开关。

## 功能

**物品更新策略**
- 冷存储跳过 — 冻结未打开容器内物品的更新
- 地面物品节流 — 降低丢在地上的可拾取物品更新频率（有线连接的物品豁免）
- 组件节流 — 降低 CustomInterface、MotionSensor、Wearable、WaterDetector、Door 的更新频率
- HasStatusTag 缓存 — 缓存每帧的状态标签查询，避免重复遍历列表
- Affliction 去重 — 跳过同一帧内的重复异常状态施加

**角色优化**
- 动画 LOD — 降低远处/屏幕外角色的动画更新频率
- AI 交错更新 — 将 AI 角色更新分散到多个帧组（如4组 = 每个 AI 每4帧更新一次）

**网络同步修复**（仅客户端）
- 梯子反同步修复 — 修正服务器校正后攀爬动画状态不一致，含防橡皮筋机制
- 平台穿越反同步修复 — 从服务器状态同步 IgnorePlatforms，修复在楼梯/平台上按 S 时的回弹

**并行处理**（实验性）
- 并行物品调度 — 将安全的物品更新分配到工作线程
- 线程安全补丁：Rand.GetRNG、连接查询等

**服务端**
- 服务端物品更新循环 HashSet 去重
- 服务器性能数据中继至客户端

## 潜在 Mod 冲突

本 mod 使用 Harmony 修补以下原版方法。**任何修补相同方法的 mod 都可能冲突：**

| 方法 | 补丁类型 | 对应功能 |
|------|---------|---------|
| `MapEntity.UpdateAll` | Prefix（接管） | 并行调度、冷存储、地面物品节流 |
| `CustomInterface.Update` | Prefix | CI 节流 |
| `MotionSensor.Update` | Prefix | 运动传感器节流 |
| `Wearable.Update` | Prefix | 可穿戴节流 |
| `WaterDetector.Update` | Prefix + Postfix | 水探测器节流 |
| `Door.Update` | Prefix + Postfix | 门节流（默认关闭） |
| `PropertyConditional.Matches` | Transpiler | HasStatusTag 缓存 |
| `CharacterHealth.ApplyAffliction` | Prefix | Affliction 去重 |
| `Ragdoll.UpdateNetPlayerPositionProjSpecific` | Prefix + Postfix | 梯子/平台反同步修复 |
| `AnimController.UpdateAnim` | Prefix | 动画 LOD |
| `Character.Control` | Prefix | 角色交错 |
| `Rand.GetRNG` | Transpiler | 线程安全随机数 |
| `GUI.TogglePauseMenu` | Postfix | 设置按钮注入 |
| `GUI.Draw` | Postfix | 统计覆盖层 |
| `StatusHUD.Update / DrawThermalOverlay` | Prefix/Postfix | StatusHUD 节流 |

最关键的冲突点：完全替换 `MapEntity.UpdateAll` 的 mod 会与并行调度系统直接冲突。

## 设置

游戏内按 ESC，点击 "ItemOptimizer" 按钮打开设置面板。所有策略均可单独开关。配置保存至 `ItemOptimizer_config.xml`。
