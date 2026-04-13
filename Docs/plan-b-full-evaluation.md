# 完全接管 UpdateAll 方案深度评估 (EP 模式)

> 基于 EP fork 的实际实现 + 我们的冻结/降频 + 截图中的性能分布
> 评估以 Harmony prefix (return false) 完全替换 MapEntity.UpdateAll 的可行性

---

## 1. 截图中的帧预算 (11.08ms Update)

```
Update 11.08ms
├── Character       1.82ms  16.4%
├── MapEntity       3.95ms  35.6%  ← 我们要接管这里
│   ├── Items       3.44ms
│   └── Misc        0.31ms  (Hull + Structure + Gap + Power)
├── Physics         1.38ms  12.5%
├── Ragdolls        1.93ms  17.4%
├── GameSession     0.15ms
├── Level           0.09ms
├── StatusEffects   0.07ms
├── Power           0.08ms
├── Submarine       0.01ms
└── Particles       0.00ms
```

MapEntity (3.95ms) 是最大单项，但不是唯一的优化目标。
Physics (1.38ms) 和 Ragdolls (1.93ms) 合计 3.31ms — 和 MapEntity 差不多。

---

## 2. EP 做了什么

### EP 的 MapEntity.UpdateAll 改造

```csharp
// EP: 先快照
var hullList = Hull.HullList.ToList();
var structureList = Structure.WallList.ToList();
var shuffledGaps = gapList.OrderBy(g => Rand.Int(...));
var itemList = Item.ItemList.ToList();

// EP: 四路并行
Parallel.Invoke(parallelOptions,
    () => { foreach (hull in hullList) hull.Update(); },          // Hull 串行 (一个线程)
    () => { Parallel.ForEach(structureList, s => s.Update()); },  // Structure 各自并行
    () => { Parallel.ForEach(shuffledGaps, g => { g.Reset(); g.Update(); }); },  // Gap 各自并行
    () => { Powered.UpdatePower(deltaTime); }                     // Power 一个线程
);

// EP: 排水主线程待办队列
SingleThreadWorker.GlobalWorker.RunActions();

// EP: Items 仍然串行!!
foreach (Item item in itemList) item.Update(dt, cam);
```

### EP 还做了什么 (GameScreen.Update)

```
Parallel.ForEach(physicsBodies, body => body.Update());        // 物理体更新并行
...
Parallel.ForEach(physicsBodies, body => body.SetPrevTransform()); // 变换记录并行
...
Farseer Contact 碰撞检测: Parallel.ForEach + spinlock
Farseer 约束求解: Parallel.For / ThreadPool
```

### EP 尝试了但回退了什么

- Character.UpdateAll 并行 → **回退**，原因: "large-scale performance impact"
- Ragdoll.UpdateAll 并行 → **回退**
- Level.Update + Hull ThreadLocal → **回退**

---

## 3. 我们的方案: 完全接管 UpdateAll

### 3.1 Harmony Prefix 替换

```csharp
[HarmonyPrefix]
static bool UpdateAllPrefix(float deltaTime, Camera cam)
{
    // ═══ Phase 1: 准备 ═══
    IncrementTick();                    // 反射写 mapEntityUpdateTick
    int tick = GetTick();
    int interval = MapEntity.MapEntityUpdateInterval;
    bool isUpdateFrame = (tick % interval == 0);
    float scaledDt = deltaTime * interval;
    
    // ═══ Phase 2: Misc 并行 (EP 模式) ═══
    // 只在 update frame 执行
    if (isUpdateFrame)
    {
        Parallel.Invoke(
            () => { foreach (var hull in Hull.HullList) hull.Update(scaledDt, cam); },
            () => { foreach (var s in Structure.WallList) s.Update(scaledDt, cam); },
            () => { ... Gap 更新 ... },
            () => { Powered.UpdatePower(scaledDt); }
        );
        SingleThreadWorker.GlobalWorker.RunActions();
    }
    else
    {
        // 非 update frame 也要处理 Gap
        foreach (var gap in Gap.GapList) gap.ResetWaterFlowThisFrame();
        foreach (var gap in shuffledGaps) gap.Update(deltaTime, cam);
    }
    
    // ═══ Phase 3: Items (我们的核心优化) ═══
    Item.UpdatePendingConditionUpdates(deltaTime);
    if (isUpdateFrame)
    {
        DispatchItemUpdates(scaledDt, cam);
    }
    
    // ═══ Phase 4: Priority Items ═══
    foreach (var item in LuaCsSetup.Instance.Game.UpdatePriorityItems)
    {
        if (!item.Removed) item.Update(deltaTime, cam);
    }
    
    // ═══ Phase 5: 收尾 ═══
    if (isUpdateFrame)
    {
        UpdateAllProjSpecific(scaledDt);     // 需要反射调用 (partial method)
        Entity.Spawner?.Update();
    }
    
    return false;   // 完全跳过原方法
}
```

### 3.2 DispatchItemUpdates: 冻结 + 半多线程

```csharp
static void DispatchItemUpdates(float dt, Camera cam)
{
    var priorityItems = LuaCsSetup.Instance.Game.UpdatePriorityItems;
    
    // ── 单趟遍历: 分类 + 冻结 ──
    var mainItems = _mainBuffer;      // 复用 List，避免 GC
    var workerItems = _workerBuffer;
    mainItems.Clear();
    workerItems.Clear();
    
    foreach (Item item in Item.ItemList)
    {
        if (priorityItems.Contains(item)) continue;
        
        // 冻结降频 (替代 ItemUpdatePatch.Prefix)
        if (ShouldFreeze(item)) { Stats.ColdStorageSkips++; continue; }
        if (ShouldThrottle(item)) { Stats.GroundItemSkips++; continue; }
        
        // 分类
        if (IsSafeForWorker(item))
            workerItems.Add(item);
        else
            mainItems.Add(item);
    }
    
    // ── 并行执行 ──
    Task workerTask = null;
    if (workerItems.Count > 0)
    {
        var snapshot = new List<Item>(workerItems);    // 快照给 worker
        workerTask = Task.Run(() => {
            IsWorkerThread = true;
            foreach (var item in snapshot)
            {
                try { item.Update(dt, cam); }
                catch (Exception e) { WorkerCrashLog.RecordCrash(item, e, 0); }
            }
            IsWorkerThread = false;
        });
    }
    
    // Main thread 处理 unsafe items (同时 worker 在跑)
    foreach (var item in mainItems)
        item.Update(dt, cam);
    
    // 等待 worker
    workerTask?.Wait();
    
    // 排水
    ThreadSafetyPatches.FlushDeferredSounds();
}
```

**关键区别**: `item.Update(dt, cam)` 是直接调用 → **零 Harmony per-item 开销**。

---

## 4. 需要解决的技术问题

### 4.1 mapEntityUpdateTick (private static)

```csharp
// 一次性反射，启动时缓存
private static readonly AccessTools.FieldRef<int> _tickRef = 
    AccessTools.StaticFieldRefAccess<int>(
        typeof(MapEntity).GetField("mapEntityUpdateTick", 
            BindingFlags.NonPublic | BindingFlags.Static));

static void IncrementTick() => _tickRef() = _tickRef() + 1;
static int GetTick() => _tickRef();
```

**风险: 极低** — 字段名不太可能变。

### 4.2 UpdateAllProjSpecific (partial method)

这是个 `static partial void`，编译后变成普通的 static method。反射调用:

```csharp
private static readonly MethodInfo _updateProjSpecific = 
    AccessTools.Method(typeof(MapEntity), "UpdateAllProjSpecific");

// 调用
_updateProjSpecific?.Invoke(null, new object[] { scaledDt });
```

**风险: 低** — Client 端有实现 (更新 sprite states)，Server 端为空方法。

### 4.3 #if CLIENT PerformanceCounter

我们的 mod 编译时没有 `CLIENT` 预编译符号。解决方式:

```csharp
// 运行时检测
private static readonly Type _perfCounterType = 
    AccessTools.TypeByName("Barotrauma.PerformanceCounter");
private static readonly MethodInfo _addTicks = 
    _perfCounterType?.GetMethod("AddElapsedTicks");

// 在关键位置调用
if (_addTicks != null)
    _addTicks.Invoke(GameMain.PerformanceCounter, new object[] { "Update:MapEntity:Misc", sw.ElapsedTicks });
```

或者直接忽略 — PerformanceCounter 只是调试用，不影响功能。

### 4.4 Gap 随机化 + Rand 线程安全

EP 在并行 Gap 更新时直接 `Parallel.ForEach(shuffledGaps, ...)`。
但 Rand.Int() 在主线程快照阶段调用（线程安全），Gap.Update 内部可能调用 Rand — 需要 ThreadSafety patch（我们的 F1 `Rand.GetRNG` prefix 已覆盖）。

### 4.5 SingleThreadWorker

EP 用这个来延迟非线程安全的操作。我们也需要实现:

```csharp
// 简单版 — ConcurrentQueue<Action>
static class DeferredActions
{
    private static readonly ConcurrentQueue<Action> _queue = new();
    public static void Enqueue(Action a) => _queue.Enqueue(a);
    public static void Flush() { while (_queue.TryDequeue(out var a)) a(); }
}
```

Gap.cs 内部的 `RefreshOutsideCollider` 会创建/销毁物理体 → 需要延迟到并行结束后。
**问题**: 我们不能修改 Gap.cs 源码（那是原版代码）。

**解决**: Harmony patch `Gap.RefreshOutsideCollider` → 在并行期间延迟到主线程。

### 4.6 Hull 并行不安全

EP 的注释: `"basically nothing here is thread-safe so"` — Hull 更新只是放到**一个并行分支**里串行执行（不是每个 Hull 并行）。这是安全的。

---

## 5. 收益预测

### 5.1 截图数据 + 优化叠加

```
当前 MapEntity: 3.95ms
├── Items: 3.44ms
└── Misc:  0.31ms (Hull + Structure + Gap + Power 串行)

方案后 MapEntity:
├── Misc 并行 (4路):
│   原串行 0.31ms → 并行后 max(Hull, Structure, Gap, Power)
│   Hull ~0.10ms, Structure ~0.05ms, Gap ~0.10ms, Power ~0.06ms
│   → ~0.10ms (被最长的分支决定)
│   节省: ~0.21ms
│
├── Items (冻结 + 无 Harmony 税):
│   原版 3.44ms
│   - 冻结跳过: -1.7ms (约 50% 物品)
│   - 消除 Harmony 税: -1.3ms (当前 A1 prefix 开销)
│   - worker 并行: -0.2ms (可并行部分)
│   → ~0.24ms
│
└── 总计: ~0.34ms (原来 3.95ms → 节省 ~3.6ms)
```

### 5.2 如果也 patch PhysicsBody (EP 模式)

```
当前 Physics:    1.38ms → Parallel.ForEach body.Update → ~0.35ms (4核)  节省 ~1.0ms
当前 Ragdolls:   1.93ms → 不并行 (EP 尝试过回退了)
```

### 5.3 总预期收益

```
                            当前        优化后       节省
MapEntity:Items            3.44ms      ~0.24ms     -3.20ms
MapEntity:Misc             0.31ms      ~0.10ms     -0.21ms
PhysicsBody (如果做)         1.38ms      ~0.35ms     -1.03ms
───────────────────────────────────────────────────────
总 Update                  11.08ms     ~6.64ms     -4.44ms  (-40%)
```

不 patch Physics 也有 -3.41ms (MapEntity 部分)。

---

## 6. 风险评估

### 6.1 低风险

| 风险 | 原因 |
|------|------|
| mapEntityUpdateTick 反射 | 字段名稳定，读写简单 |
| UpdateAllProjSpecific 反射 | 签名不变 (float→void) |
| Item 循环直接调用 | 和原版逻辑一样，只是多了冻结+分类 |
| PerformanceCounter 缺失 | 不影响功能，调试用 |

### 6.2 中风险

| 风险 | 缓解 |
|------|------|
| Hull/Gap/Structure 并行时写共享状态 | EP 已验证可行; Hull 不做内部并行; Gap 需 SingleThreadWorker |
| Gap.RefreshOutsideCollider 物理体操作 | Harmony patch 延迟到并行后 |
| 版本更新改 UpdateAll 逻辑 | 用户说基本不更新; 有 fallback |
| Misc 并行引入的 bug | 从 EP 抄经验证代码，降低风险 |

### 6.3 高风险

| 风险 | 缓解 |
|------|------|
| worker 内 StatusEffect 写共享状态 | 延用现有 F 组 ThreadSafety patches |
| worker 内 Sound 调用 (Client) | 延用现有 Sound prefix (延迟播放) |
| 原版 try/catch InvalidOperationException 丢失 | 在 DispatchItemUpdates 内重建 |
| Item.Update 内部隐式依赖执行顺序 | 只有 Safe 分类的 mod 物品进 worker; Vanilla 全留 main |

### 6.4 核心认知: EP 为什么不并行 Item

EP 明确注释: `"Item.Update() is not thread-safe and must be executed on the main thread"`

原因是 `Item.Update` 内部调用:
- `ApplyStatusEffects()` → 可修改任意实体属性、发射信号、生成粒子
- `ic.Update()` → 每个组件自己的逻辑 (Pump, Reactor, Door, 信号...)
- `Spawner?.AddItemToRemoveQueue()` → 全局队列
- `UpdateTransform()` → 写 Submarine 引用
- 物理体操作 (velocity, buoyancy)

**但我们的区别**: 我们只把经过 `ThreadSafetyAnalyzer` 筛选的 Safe mod 物品推到 worker，这些物品:
- 不接线 (无信号)
- 无活跃物理体
- 非角色持有
- Prefab 扫描确认无危险组件

这个子集比 EP 试图并行的全量物品安全得多。

---

## 7. 实施总览

### 需要的新代码

| 组件 | 行数 | 说明 |
|------|------|------|
| `UpdateAllPrefix` (return false) | ~80 | 完整替换 UpdateAll 逻辑 |
| `DispatchItemUpdates` | ~60 | 冻结 + 分类 + worker 分发 |
| `DeferredActions` (=SingleThreadWorker) | ~20 | ConcurrentQueue 延迟执行 |
| `Gap.RefreshOutsideCollider` prefix | ~15 | 并行期间延迟物理操作 |
| 反射访问缓存 | ~15 | tick, ProjSpecific, PerformanceCounter |
| Fallback 机制 | ~20 | prefix 失败时恢复原版行为 |
| **合计** | **~210** | |

### 可删除的旧代码

| 组件 | 说明 |
|------|------|
| `ParallelDispatchPatch.cs` 的 Item.Update prefix+postfix | 被 DispatchItemUpdates 取代 |
| `ItemUpdatePatch.cs` | 冻结逻辑移入 DispatchItemUpdates |
| `ItemOptimizerPlugin.MapEntityUpdateAllPostfix` (Stats) | 移入 UpdateAllPrefix 末尾 |
| `PerfProfiler.MapEntityUpdateAll` prefix+postfix | 移入 UpdateAllPrefix |

这些删除**消灭了 Item.Update 上的所有 Harmony patch** — 当前架构最大的开销源。

### 注册方式

```csharp
// 只需一个 prefix on MapEntity.UpdateAll
harmony.Patch(
    AccessTools.Method(typeof(MapEntity), "UpdateAll"),
    prefix: new HarmonyMethod(typeof(UpdateAllTakeover), nameof(Prefix)));
```

当前的 6 组、~67 个 hook → 减少到:

| 保留 | 说明 |
|------|------|
| MapEntity.UpdateAll prefix (return false) | **唯一的入口** |
| F 组 ThreadSafety patches | worker 线程保底 |
| B 组 Client UI patches | overlay/settings |
| Gap.RefreshOutsideCollider prefix | 延迟物理操作 |

Item.Update 上: **从 4-8 个 hook → 0 个**。

---

## 8. 结论

| 维度 | 评分 |
|------|------|
| 性能收益 | **极高** — MapEntity 3.95ms → ~0.34ms, 净省 ~3.6ms |
| 可行性 | **高** — EP 已验证 Misc 并行可行; 冻结是已验证逻辑; 反射访问简单 |
| 风险 | **中** — 主要风险在 worker 线程安全 (已有 F 组覆盖) 和版本兼容 (用户说不担心) |
| 维护成本 | **中** — ~210 行新代码，逻辑集中在一个文件 |
| 投入产出比 | **最高** — 一个 prefix 替代整个 patch 架构，收益/复杂度比所有方案都好 |

**这是应该做的方案。**

冻结降频的 -1.5ms 收益 + 消除 Harmony 税的 -1.3ms + Misc 并行的 -0.2ms + worker 的 -0.2ms = **总省 ~3.2ms**，从 3.95ms 降到 ~0.75ms。

如果再加 PhysicsBody 并行 (额外一个 prefix on GameScreen.Update)，还能省 ~1ms。
