# Barotrauma Item 系统技术文档 — ItemOptimizer 开发参考

> 此文档记录潜渊症 Item 系统的内部机制、线程安全陷阱、踩坑记录，
> 供 ItemOptimizer mod 开发时快速参考，避免重复踩坑。

---

## 1. Item 核心数据结构

### 1.1 Item 实例字段

| 字段 | 类型 | 线程安全 | 说明 |
|------|------|---------|------|
| `tags` (line 92) | `HashSet<Identifier>` | **不安全** | 运行时实例标签，`AddTag`/`RemoveTag` 直接修改，无锁 |
| `body` (line 150) | `PhysicsBody` | 不安全 | 物理体，Farseer 不可并发访问 |
| `IsActive` (line 2418) | `bool` (公开字段) | 原子读写 | Update 入口检查，false 时跳过 |
| `Connections` (line 1014) | `List<Connection>` | 不安全 | 来自 ConnectionPanel 组件，可为 null |
| `updateableComponents` | `List<ItemComponent>` | 不安全 | Update 中遍历的组件列表 |

### 1.2 关键静态字段

| 字段 | 类型 | 位置 | 说明 |
|------|------|------|------|
| `Item.ItemList` | `List<Item>` | Item.cs:36 | 全局物品列表，构造时自动加入 |
| `StatusEffect.DurationList` | `List<DurationListElement>` | StatusEffect.cs:611 | **全局共享**，所有持续效果都在这里，Apply 时写入 |
| `MapEntity.MapEntityUpdateInterval` | `int` | MapEntity.cs:22 | 帧跳间隔，deltaTime 会乘以此值补偿 |

### 1.3 ItemPrefab.Tags vs Item.tags

```
Item.HasTag(tag):
  return tags.Contains(tag)              ← HashSet<Identifier> (不安全!)
      || base.Prefab.Tags.Contains(tag)  ← ImmutableHashSet<Identifier> (安全)
```

- `Prefab.Tags` (ItemPrefab.cs:708) = `ImmutableHashSet<Identifier>` — XML 定义的标签，不可变，线程安全
- `Item.tags` (Item.cs:92) = `HashSet<Identifier>` — 运行时动态标签，`AddTag`/`RemoveTag` 直接修改

**踩坑记录**: 任何在工作线程上调用 `Item.HasTag()` 的代码都会崩溃，因为先查 HashSet。
**解决方案**: ThreadSafetyPatches 中给 `Item.HasTag(Identifier)` 加 Harmony prefix，工作线程上只查 `Prefab.Tags`。

---

## 2. MapEntity.UpdateAll 更新流程

**文件**: MapEntity.cs:642

```
UpdateAll(deltaTime, cam):
  1. mapEntityUpdateTick++
  2. if (tick % MapEntityUpdateInterval != 0) return  // 帧跳
  3. 实际 deltaTime = deltaTime * MapEntityUpdateInterval  // 补偿
  4. 遍历 Hull.HullList → hull.Update(...)
  5. 遍历 Structure.WallList → structure.Update(...)
  6. 遍历 Item.ItemList → item.Update(...)  // ← 这里是我们 patch 的地方
  7. UpdateAllProjSpecific()
  8. Spawner?.Update()  // 排空生成/移除队列
```

**ItemOptimizer 并行策略**:
- Prefix: 分类所有 Item，safe 的交给工作线程，unsafe 的留在主线程
- Postfix: `Task.WaitAll()` 等待工作线程完成，刷新延迟队列
- `Enabled` 标志控制是否激活并行，必须在 `OnLoadCompleted()` 后才设为 true

---

## 3. 线程安全分析

### 3.1 不安全的共享可变状态

| 状态 | 位置 | 危险操作 | ItemOptimizer 防护 |
|------|------|---------|-------------------|
| `Item.tags` (HashSet) | Item.cs:92 | `HasTag()` 并发读 + 主线程写 | HasTagPrefix: 工作线程重定向到 Prefab.Tags |
| `StatusEffect.DurationList` | StatusEffect.cs:611 | `Apply()` 添加, `UpdateAll()` 遍历/移除 | Monitor lock (DurationLock) |
| `EntitySpawner.spawnOrRemoveQueue` | EntitySpawner.cs:207 | `Queue<T>` 非并发 | Monitor lock (SpawnerLock) |
| `Item.Condition` setter | Item.cs (SetCondition) | 触发网络同步 | Monitor lock (ConditionLock) |
| `DelayedEffect` 延迟列表 | DelayedEffect.cs | Apply/Update 写入 | Monitor lock (DelayLock) |
| `Rand.GetRNG()` | Rand.cs | Unsynced 模式返回共享 Random | ThreadLocal<Random> 替代 |
| Sound.Play | client only | 音频系统非线程安全 | 工作线程跳过(返回 null) |

### 3.2 安全分类标准 (ThreadSafetyAnalyzer)

**Safe**: 无危险组件、无危险 SE、无 ConnectionPanel、无 PhysicsBody
**Conditional**: 有 ConnectionPanel 或 Body，但无其他危险 → 运行时检查 body.Enabled 和 Wires
**Unsafe**: 有危险组件 或 SE 写入共享状态 (Duration/Spawn/Affliction/etc.)

**危险组件列表**: Door, Pump, Reactor, PowerTransfer, Turret, Fabricator, Deconstructor, Steering, Engine, OxygenGenerator, MiniMap, Sonar, DockingPort, ElectricalDischarger, Controller, TriggerComponent, Rope, EntitySpawnerComponent, StatusMonitor, Wire, StatusHUD

**永远不并行的**: 原版物品、角色持有物品、被隔离(quarantine)的物品

### 3.3 SE 类型分析

**空闲触发的** (可能在地面物品上自动触发):
`Always`, `OnNotContained`, `OnContaining`, `InWater`, `NotInWater`, `OnBroken`, `OnDamaged`, `OnFire`, `OnSpawn`

**需要角色交互的** (地面物品不会触发，可忽略):
`OnUse`, `OnSecondaryUse`, `OnActive`, `OnWearing`, `OnPicked`, `OnSuccess`, `OnFailure`, `OnEating`, `OnContained`, `OnImpact`

---

## 4. 踩坑记录

### 4.1 SpritePatch.dll HasTag 崩溃 (2026-04)

**现象**: `InvalidOperationException: Operations that change non-concurrent collections must have exclusive access`
**根因**: DDA (Deep Diving Armory) 的 SpritePatch.dll 在 `Item.Update` postfix 中调用 `Item.HasTag("weapon")`，触发 `HashSet` 并发访问
**错误方案**: 修改 Workshop DLL → Steam 每次启动覆盖回原版
**正确方案**: 在 ThreadSafetyPatches 中 patch `Item.HasTag(Identifier)`，工作线程重定向到 `Prefab.Tags.Contains()`
**教训**: 永远不要修改第三方 Workshop DLL。在自己的 mod 中用 Harmony patch 解决兼容性问题。

### 4.2 线程初始化顺序 (2026-04)

**现象**: 游戏启动时卡死/阻塞，看起来像单线程更新阻塞
**根因**: `ParallelDispatchPatch.Register()` 设 `Enabled=true`，但初始化阶段线程安全集合还没准备好
**修复**: `Register()` 设 `Enabled=false`，延迟到 `OnLoadCompleted()` 才启用
**教训**: Harmony patch 注册和功能启用要分离。注册可以早，启用必须等所有依赖就绪。

### 4.3 using Barotrauma.Sounds 编译错误 (2026-04)

**现象**: CS0234 编译错误 — `Barotrauma.Sounds` 命名空间不存在
**根因**: Sound 类型只在客户端存在，且可能在不同命名空间
**修复**: 移除 `using Barotrauma.Sounds;`，用 `AccessTools.TypeByName()` 运行时发现
**教训**: 客户端专用类型永远用运行时反射查找，不要编译时引用

### 4.4 HasTag prefix 只拦截工作线程不够 (2026-04-12)

**现象**: 加了 `HasTagPrefix`（只在 `IsWorkerThread` 时拦截）后仍然崩溃在 `HashSet.FindItemIndex`
**根因**: prefix 只在工作线程拦截，主线程仍读 HashSet。但 .NET `HashSet` 一旦经历并发读写访问会**永久损坏内部状态**——之后即使单线程读也会崩溃。竞态: 工作线程的 SE 通过 `AddTag` 写某物品的 tags，主线程的 SpritePatch postfix 恰好读同一物品 → 损坏 → 后续所有读崩溃。
**调试关键**: stack trace 中 `DMD<>` 前缀证明 Harmony patch 确实在执行；`SyncProxy` 是 MonoMod v6 的 detour 分发器，不会绕过 patch。崩溃到 `HashSet.FindItemIndex` 说明 prefix 返回了 `true`（放行原方法），即 `IsWorkerThread==false`，在主线程上。
**修复**: 将条件从 `!IsWorkerThread` 改为 `!_dispatchActive`。在整个并行帧（`_dispatchActive==true`）期间，**所有线程**的 `HasTag` 都重定向到 `Prefab.Tags`（ImmutableHashSet），完全绕过实例 HashSet。
**教训**: 并发 HashSet 损坏是永久性的——只保护一端（读或写）不够，必须在并发窗口内**完全隔离**所有读写。

### 4.5 Harmony patch 在热路径上的开销 (2026-04-12)

**现象**: 只开并行不开其他优化，Items 耗时从约 3ms 增加到 5.28ms（+2-3ms）
**根因**: ParallelDispatch 在 `Item.Update` 上挂了 prefix+postfix，每次调用都经过 SyncProxy→DMD→prefix逻辑→原方法→postfix。约 2500 个物品每个都承受此开销。
**数据**: safe=165, 实际并行 141 物品 vs 主线程 2445 物品。并行节省 ~1.8ms，但 Harmony 开销 ~2.5ms，净亏损。
**Harmony 开销估算**: 每个 patch 每次调用约 0.5-1μs（SyncProxy + DMD 分发 + prefix/postfix 调用），Item.Update 有 prefix+postfix = 每物品 ~1μs，×2500 = 2.5ms。
**教训**: Harmony patch 不是免费的。在每帧调用 2000+ 次的热路径上，即使 prefix 立即返回 true，分发开销也会累积。并行化的净收益 = 并行节省时间 - Harmony 开销 - 线程同步开销。只有 safe 物品数足够多时才有正收益。

---

## 5. Item.Update 执行流程

```
Item.Update(deltaTime, cam):
  if (!IsActive || IsLayerHidden || IsInRemoveQueue) return;
  
  1. 处理 impactQueue (物理碰撞)
  2. 更新 AITarget
  3. 应用 StatusEffects:
     - Always 类型 SE
     - OnContained / OnNotContained SE (基于 ParentInventory)
  4. 遍历 updateableComponents:
     - 评估 IsActiveConditionals
     - 调用 component.Update(deltaTime, cam)
  5. 更新 Condition 相关 (损坏/修复)
```

**并行化关键点**:
- 步骤 1 (impactQueue): 物理相关，不可并行
- 步骤 3 (SE Apply): 可能写入 DurationList，需要锁保护
- 步骤 4 (组件更新): 取决于组件类型，危险组件不可并行

---

## 6. Harmony Patch 优先级与执行顺序

```
ItemOptimizer 的 patch 优先级设计:
  - ParallelDispatchPatch (Low priority): 在默认 prefix 之后运行
    → 先让 cold storage / throttle prefix 执行完
    → 然后决定是否分配给工作线程
  - MapEntityUpdateAllPostfix (Last priority): 最后运行
    → 确保 PerfProfiler 和 ParallelDispatch 的 postfix 先完成
    → 最后做统计帧结束
```

**其他 mod 的 Harmony patch 会在并行时运行**。任何在 `Item.Update` 上挂 postfix/prefix 的 mod:
- 如果 item 被分配到工作线程 → 那个 mod 的 patch 也在工作线程上跑
- 如果 patch 内有不安全操作 → 崩溃
- 解决: 在 ThreadSafetyPatches 中主动 patch 已知危险 API (如 HasTag)

---

## 7. 配置与初始化顺序

```
Initialize():
  1. OptimizerConfig.Load()         ← 读配置 + LoadProfile() + ThreadSafetyAnalyzer.LoadCache()
  2. Harmony 创建
  3. CacheMethodReferences()
  4. ApplyPatches()                  ← 基础 patch (cold storage, throttle 等)
  5. PerfProfiler/SpikeDetector 初始化
  6. if (EnableParallelDispatch):
       ThreadSafetyPatches.RegisterPatches()  ← Harmony 注册 (Enabled=false)
       ParallelDispatchPatch.Register()       ← Harmony 注册 (Enabled=false)
  7. InitializeClient()

OnLoadCompleted():
  if (EnableParallelDispatch):
    ParallelDispatchPatch.Enabled = true      ← 这里才真正启用!!

SetStrategyEnabled("parallel_dispatch", true):  ← GUI 切换
  ThreadSafetyPatches.RegisterPatches()
  ParallelDispatchPatch.Register()
  ParallelDispatchPatch.Enabled = true          ← GUI 时游戏已加载完，可以直接启用
```

---

## 8. 性能考量

- 每个 Harmony patch 即使不生效也有调用开销 (~0.5-1μs per call per patch，含 SyncProxy→DMD 分发)
- `Item.ItemList` 通常有 2000-4000 个物品
- `MapEntityUpdateInterval > 1` 时物品不是每帧更新
- 工作线程数量: 1-6 可配置，默认 2
- 分类在 Prefix 中每帧重新执行 (`IsSafeForWorker`)，O(N) 物品数
- Harmony hook chain 本身有开销: patch 越多，即使都不生效，每个 item.Update 调用也会慢

### 8.1 Harmony 开销预算 (2026-04-12 实测)

| 组件 | 挂在 | 每帧调用次数 | 单次开销 | 合计 |
|------|------|------------|---------|------|
| ItemUpdatePatch prefix | Item.Update | ~2586 | ~0.5-1μs | **1.3-2.6ms** |
| ParallelDispatch prefix+postfix | Item.Update | ~2586 × 2 | ~0.5μs | **2.6-5.2ms** |
| 组件级 patch (Motion,CI,Door...) | 各组件.Update | 各数百 | ~0.5μs | ~0.3-0.5ms |
| MapEntity.UpdateAll hooks | MapEntity.UpdateAll | 1 | ~1μs | **可忽略** |

**关键结论**: Item.Update 上的 patch 是开销大户。每增加一对 prefix+postfix 就增加 ~1.3ms。
冷存储+降频这些策略虽然能跳过部分 Item.Update，但 Harmony 入口开销在跳过之前就已经发生了。

### 8.2 优化措施

1. **条件挂载**: 功能关闭时 unpatch，消除 Harmony 分发开销 (SyncItemUpdatePatch/TogglePatch)
2. **Pre-computed flags**: `ItemUpdatePatch.NewFrame()` 中缓存 `HasColdStorage`/`HasGroundItem`/`HasRules`/`HasModOpt`，避免热路径上读字典 `.Count` 和 config 字段
3. **Fast exit**: 无 Rules/ModOpt 时跳过 identifier 查找
4. **AggressiveInlining**: 对热路径 prefix 标注，暗示 JIT 内联

### 8.3 基准测试方法论

BaselineBench mod (独立 mod，零 Item.Update patch):
- 只在 MapEntity.UpdateAll 上挂 prefix+postfix 计时
- 测量真正的原版 Item 更新耗时，无任何 Harmony 每物品开销
- 命令: `bb_start [frames]`, `bb_stop`
- 输出 CSV: frame, items_total, items_active, update_ms
- 测试时禁用 ItemOptimizer，只启用 BaselineBench
