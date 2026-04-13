# ItemOptimizer Harmony Patch 开销全景图

> 此文档记录 ItemOptimizer 对潜渊症物品系统挂载的所有 Harmony patch，
> 以及每个 patch 的预期开销，供性能调优时快速定位瓶颈。

---

## 1. Harmony 分发机制与单次开销

每个 Harmony patch 的调用链:

```
原始调用 → SyncProxy (MonoMod v6 detour 分发器)
         → DMD (Dynamic Method Definition, Harmony 的 trampoline)
           → prefix 逻辑
           → 原方法 (如果 prefix 返回 true)
           → postfix 逻辑
         → 返回
```

**实测单次开销**: ~0.5–1.0μs (含 SyncProxy→DMD 分发 + prefix/postfix 函数调用)

- 一对 prefix+postfix 约 1.0–2.0μs
- 纯 prefix (返回 false 跳过原方法) 约 0.5–1.0μs
- 即使 prefix 第一行就 `return true`，分发开销仍然存在
- 此开销**不可消除**，除非 unpatch

---

## 2. 完整 Patch 清单

### 2.1 默认场景下的物品数量参考

| 指标 | 典型值 | 说明 |
|------|--------|------|
| `Item.ItemList.Count` | 2500–4000 | 全局物品实例 |
| 每帧活跃更新物品 | ~2586 | `IsActive && !IsLayerHidden && !IsInRemoveQueue` |
| `MapEntityUpdateInterval` | 1 (默认) | >1 时帧跳，物品不每帧更新 |

---

### 2.2 A 组 — 核心优化 Patch (ItemOptimizerPlugin.ApplyPatches)

**挂载策略**: 条件挂载 — 对应功能关闭时 unpatch，零开销

| # | 目标方法 | 类型 | 挂载条件 | 每帧调用 | 单次开销 | 每帧合计 |
|---|---------|------|---------|---------|---------|---------|
| A1 | `Item.Update` | prefix | ColdStorage∨GroundItem∨Rules∨ModOpt | ~2586 | ~0.5–1μs | **1.3–2.6ms** |
| A2 | `CustomInterface.Update` | prefix | EnableCustomInterfaceThrottle | ~50–200 | ~0.5μs | ~0.03–0.1ms |
| A3 | `MotionSensor.Update` | prefix | EnableMotionSensorThrottle | ~30–100 | ~0.5μs | ~0.02–0.05ms |
| A4 | `Wearable.Update` | prefix | EnableWearableThrottle | ~10–30 | ~0.5μs | ~0.005–0.015ms |
| A5 | `WaterDetector.Update` | prefix+postfix | EnableWaterDetectorThrottle | ~30–100 | ~1μs | ~0.03–0.1ms |
| A6 | `Door.Update` | prefix+postfix | EnableDoorThrottle | ~50–200 | ~1μs | ~0.05–0.2ms |
| A7 | `PropertyConditional.Matches` | prefix | EnableHasStatusTagCache | ~1000–10000 | ~0.5μs | **0.5–5ms** |
| A8 | `CharacterHealth.ApplyAffliction` | prefix | EnableAfflictionDedup | ~10–200 | ~0.5μs | ~0.005–0.1ms |
| A9 | `MapEntity.UpdateAll` | postfix | **始终挂载** (Stats.EndFrame) | 1 | ~1μs | **可忽略** |

**A 组合计 (默认全开)**: **~2.0–8.1ms/帧**
**A 组合计 (全关闭, 仅 A9)**: **~0.001ms/帧**

> **关键洞察**: A1 (`Item.Update` prefix) 和 A7 (`PropertyConditional.Matches` prefix) 是开销主力。
> A1 每帧 2586 次 × ~0.7μs ≈ 1.8ms。A7 在有大量 StatusEffect 条件检查的场景下可达 5ms+。
> 但 A7 的收益也最大 — HasStatusTagCache 将 O(conditionals × DurationList) 降为 O(1)。

---

### 2.3 B 组 — 客户端 UI Patch (InitializeClient)

**挂载策略**: 始终挂载 (runtime guard: 函数体内检查 config flag)

| # | 目标方法 | 类型 | 每帧调用 | 每帧合计 |
|---|---------|------|---------|---------|
| B1 | `GUI.TogglePauseMenu` | postfix | 0 (按 Esc 时) | **0ms** |
| B2 | `GUI.Draw` | postfix | 1 | **可忽略** |
| B3 | `StatusHUD.Update` | prefix+postfix | 0–1 | **可忽略** |
| B4 | `StatusHUD.DrawThermalOverlay` | prefix | 0–1 | **可忽略** |

**B 组合计**: **~0ms** (调用次数极低)

---

### 2.4 C 组 — PerfProfiler (PerfProfiler.RegisterPatches)

| # | 目标方法 | 类型 | 挂载策略 | 每帧调用 | 每帧合计 |
|---|---------|------|---------|---------|---------|
| C1 | `MapEntity.UpdateAll` | prefix+postfix | **始终挂载** (驱动SpikeDetector) | 1 | **可忽略** |
| C2 | `Item.Update` | prefix+postfix | **动态** — 仅 profiling 时挂载 | ~2586×2 | **2.6–5.2ms (仅 profiling 时)** |

**C 组合计 (正常运行)**: **~0ms**
**C 组合计 (profiling 时)**: **2.6–5.2ms**

---

### 2.5 D 组 — SpikeDetector (SpikeDetector.SetEnabled)

| # | 目标方法 | 类型 | 挂载策略 | 每帧调用 | 每帧合计 |
|---|---------|------|---------|---------|---------|
| D1 | `Item.Update` | prefix+postfix | **动态** — 仅 SpikeDetector 开启时 | ~2586×2 | **2.6–5.2ms (仅开启时)** |

**D 组合计 (默认关闭)**: **0ms**
**D 组合计 (开启时)**: **2.6–5.2ms**

---

### 2.6 E 组 — ParallelDispatch (ParallelDispatchPatch.Register)

**挂载策略**: 条件挂载 — 只在 `EnableParallelDispatch=true` 时注册

| # | 目标方法 | 类型 | 每帧调用 | 单次开销 | 每帧合计 |
|---|---------|------|---------|---------|---------|
| E1 | `MapEntity.UpdateAll` | prefix+postfix | 1 | ~1μs | **可忽略** |
| E2 | `Item.Update` | prefix+postfix | ~2586×2 | ~0.5μs each | **2.6–5.2ms** |

**E 组合计 (关闭)**: **0ms**
**E 组合计 (开启)**: **~2.6–5.2ms**

> **教训 (4.5)**: E2 是并行调度的主要痛点。141 个物品并行节省 ~1.8ms，但 E2 在全部 2586 个物品上增加 ~2.5ms → 净亏损。

---

### 2.7 F 组 — ThreadSafety Lock Patch (ThreadSafetyPatches.RegisterPatches)

**挂载策略**: 跟随 E 组，`EnableParallelDispatch=true` 时整组注册

| # | 目标方法 | 类型 | 锁对象 | 每帧调用 | 每帧合计 |
|---|---------|------|--------|---------|---------|
| F1 | `Rand.GetRNG` | prefix | ThreadLocal 替代 | ~1000+ | ~0.5–1ms |
| F2 | `StatusEffect.Apply` (2重载) | prefix+postfix×2 | DurationLock | ~500–5000 | ~0.5–5ms |
| F3 | `StatusEffect.UpdateAll` | prefix+postfix | DurationLock | 1 | 可忽略 |
| F4 | `StatusEffect.StopAll` | prefix+postfix | Dual lock | ~0 | 可忽略 |
| F5 | `PropertyConditional.Matches` | prefix+postfix | DurationLock | ~1000–10000 | **1–10ms** |
| F6 | `CharacterHealth.GetPredictedStrength` | prefix+postfix | DurationLock | ~10–100 | ~0.01–0.1ms |
| F7 | `AbilityConditionHasStatusTag.MatchesConditionSpecific` | prefix+postfix | Dual lock | ~10–100 | ~0.01–0.1ms |
| F8 | `DelayedEffect.Apply` (2重载) | prefix+postfix×2 | DelayLock | ~10–500 | ~0.01–0.5ms |
| F9 | `DelayedEffect.Update` | prefix+postfix | DelayLock | 1 | 可忽略 |
| F10 | `EntitySpawner.*Queue` (所有重载) | prefix+postfix×n | SpawnerLock | ~0–10 | 可忽略 |
| F11 | `Item.SetCondition` | prefix+postfix | ConditionLock | ~100–1000 | ~0.1–1ms |
| F12 | `Item.SendPendingNetworkUpdates` | prefix+postfix | ConditionLock | 1 | 可忽略 |
| F13 | `Item.UpdatePendingConditionUpdates` | prefix+postfix | ConditionLock | 1 | 可忽略 |
| F14 | `Item.HasTag(Identifier)` | prefix | 重定向到 Prefab.Tags | ~1000–10000 | **0.5–5ms** |
| F15 | `Sound.Play` (所有重载) | prefix | 工作线程跳过 | ~0–50 | 可忽略 |

**F 组合计 (关闭)**: **0ms**
**F 组合计 (开启)**: **~2.6–22ms** (变化极大，取决于场景 SE 数量)

> **注意**: F5 和 A7 叠加在同一目标 `PropertyConditional.Matches` 上。
> 当并行+HasStatusTagCache 同时开启时，此方法有 2 个 prefix + 1 个 postfix = 每次调用 ~1.5μs。

---

## 3. 配置场景开销汇总

### 场景 1: 默认配置 (所有基础优化开启, 并行关闭, SpikeDetector 关闭)

```
  A1  Item.Update prefix           ~1.8ms
  A2  CustomInterface.Update        ~0.05ms
  A3  MotionSensor.Update           ~0.03ms
  A4  Wearable.Update               ~0.01ms
  A5  WaterDetector.Update          ~0.05ms
  A6  Door.Update                   ~0.1ms
  A7  PropertyConditional.Matches   ~2ms (场景依赖)
  A8  CharacterHealth.ApplyAffliction ~0.03ms
  A9  MapEntity.UpdateAll postfix   ~0ms
  C1  MapEntity.UpdateAll (PerfProfiler) ~0ms
  B1-4 客户端UI                     ~0ms
  ─────────────────────────────────────
  总计 Harmony 开销                 ~4.1ms/帧 (典型)
  收益 (跳过的 Item.Update)         -1.5~3ms
  ─────────────────────────────────────
  净效果                            约 +1 ~ -1ms 左右 (取决于跳过率)
```

### 场景 2: 只开冷存储+降频 (关闭其他所有)

```
  A1  Item.Update prefix           ~1.8ms
  A9  MapEntity.UpdateAll postfix   ~0ms
  C1  MapEntity.UpdateAll           ~0ms
  B1-4 客户端UI                     ~0ms
  ─────────────────────────────────────
  总计 Harmony 开销                 ~1.8ms/帧
  收益 (ColdStorage 跳过)           -0.5~1.5ms
  ─────────────────────────────────────
  净效果                            约 +0.3 ~ -0.5ms
```

### 场景 3: 全关闭 (mod 已加载但所有功能关闭)

```
  A9  MapEntity.UpdateAll postfix   ~0ms
  C1  MapEntity.UpdateAll           ~0ms
  B1-4 客户端UI                     ~0ms
  ─────────────────────────────────────
  总计 Harmony 开销                 ~0.002ms/帧
```

### 场景 4: 基础优化 + 并行调度 (实验性)

```
  A 组 (同场景1)                    ~4.1ms
  E2  Item.Update prefix+postfix    ~3.5ms  ← 这是最痛的
  F 组 (ThreadSafety locks)         ~3~15ms (极度场景依赖)
  ─────────────────────────────────────
  总计 Harmony 开销                 ~10.6~22.6ms
  收益 (并行+跳过)                  -2~5ms
  ─────────────────────────────────────
  净效果                            通常净亏损 5~18ms
```

### 场景 5: 基础优化 + SpikeDetector

```
  A 组 (同场景1)                    ~4.1ms
  D1  Item.Update prefix+postfix    ~3.5ms
  ─────────────────────────────────────
  总计 Harmony 开销                 ~7.6ms
  收益 (跳过+诊断)                  -1.5~3ms + 诊断价值
  ─────────────────────────────────────
  净效果                            约 +4.6ms (诊断工具成本)
```

---

## 4. Patch 叠加热点

某些方法上叠加了多个独立的 Harmony patch，导致开销倍增:

| 目标方法 | 最大 patch 数 | 何时叠加 | 每次调用总开销 |
|---------|-------------|---------|-------------|
| **`Item.Update`** | 4 prefix + 4 postfix | A1 + E2 + C2 + D1 全开 | ~4μs |
| **`PropertyConditional.Matches`** | 2 prefix + 1 postfix | A7 + F5 (并行+缓存) | ~1.5μs |
| **`MapEntity.UpdateAll`** | 3 prefix + 3 postfix | A9 + C1 + E1 | ~3μs (仅1次/帧) |
| **`StatusEffect.Apply`** | 1 prefix + 1 postfix | F2 (并行时) | ~1μs |

> **结论**: `Item.Update` 是最关键的热点。每增加一个 prefix 增加 ~1.3ms/帧，每增加一对 prefix+postfix 增加 ~2.6ms/帧。
> 当前架构的天花板: 如果 Item.Update 上只有 1 个 prefix (A1)，Harmony 固定税 ≈ 1.8ms。

---

## 5. 优化方向

### 已实施

1. **条件挂载/卸载**: 功能关闭时 unpatch → Harmony 分发开销归零
2. **Pre-computed flags**: 热路径 prefix 中避免读 Dictionary.Count 和 config 字段
3. **动态 attach/detach**: PerfProfiler 和 SpikeDetector 的 Item.Update patch 仅在需要时挂载
4. **AggressiveInlining**: 热路径 prefix 标注，暗示 JIT

### 未来方向

1. **IL-level patch**: 用 MonoMod 直接注入 IL 而非 Harmony prefix/postfix，省去 SyncProxy 分发开销 — 风险高
2. **批处理替代单物品 patch**: 不 patch `Item.Update`，改 patch `MapEntity.UpdateAll` 内的遍历循环，一次前缀处理所有物品 — 需要反射/IL 访问私有循环变量
3. **减少 Item.Update 上的 patch 层数**: 当前 A1+E2 叠加是主要痛点，考虑合并为一个 prefix
4. **选择性 patch**: 只 patch mod 物品的 Update (需要 IL 修改或反射 trampoline)，原版物品走原始路径

---

## 6. 版本记录

| 日期 | 更新 |
|------|------|
| 2026-04-12 | 初始版本: 完整 patch 清单、开销预算、场景分析 |
