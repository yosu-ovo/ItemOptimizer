# 方案 B 深度评估: 在 MapEntity.UpdateAll 层接管循环

> 核心思路: 不 patch `Item.Update`，改为用 prefix 拦截 `MapEntity.UpdateAll`
> 中的 Item 遍历循环，手动分发 safe/unsafe 物品到 worker/main。
> 这样 Item.Update 上零 Harmony patch = 零每物品分发税。

---

## 1. 原版 UpdateAll 的 Item 循环 (line 692-713)

```csharp
// MapEntity.cs line 692
if (mapEntityUpdateTick % MapEntityUpdateInterval == 0)    // ← private static int
{
    Item lastUpdatedItem = null;
    try
    {
        foreach (Item item in Item.ItemList)               // ← 公开 static List<Item>
        {
            if (LuaCsSetup.Instance.Game.UpdatePriorityItems.Contains(item))
                { continue; }
            lastUpdatedItem = item;
            item.Update(deltaTime * MapEntityUpdateInterval, cam);   // ← 这是我们要接管的
        }
    }
    catch (InvalidOperationException e) { ... }
}

// Line 715-720: priority items (每帧都跑，不受 interval 限制)
foreach (var item in LuaCsSetup.Instance.Game.UpdatePriorityItems)
{
    if (item.Removed) continue;
    item.Update(deltaTime, cam);
}
```

**关键观察**:
- `Item.ItemList` 是公开的 — 可直接访问
- `mapEntityUpdateTick` 是 **private static** — 需要反射
- `MapEntityUpdateInterval` 是 public static — 可直接读
- `UpdatePriorityItems` 是 LuaCs 的公开 HashSet — 可访问
- 循环本身**没有复杂的控制流** — 就是 foreach + skip priority + try/catch

---

## 2. 方案 B 的实现方式

### 2.1 方式一: Prefix 拦截整个 UpdateAll (return false)

```csharp
[HarmonyPrefix]
static bool UpdateAllPrefix(float deltaTime, Camera cam)
{
    // 1. 复刻原版逻辑: tick++, Hull, Structure, Gap, Powered...
    // 2. 到 Item 循环时，手动分发:
    //    - unsafe items → 当前线程直接 item.Update()
    //    - safe items → Task.Run worker
    //    - Task.WaitAll
    // 3. 继续: PriorityItems, UpdateAllProjSpecific, Spawner
    return false;  // 跳过原方法
}
```

**问题**:
- 需要**完整复刻** UpdateAll 的逻辑 (~90 行)，包括 Hull、Structure、Gap、Powered、PriorityItems、Spawner
- `mapEntityUpdateTick` 是 private → 必须用反射读/写
- `#if CLIENT` 块 (Stopwatch, PerformanceCounter) 在 mod 的 CSharp 里不存在这些预编译符号
- 如果原版升级 UpdateAll (加新逻辑、改顺序)，我们的复刻就过时了 → **维护噩梦**
- `Gap.GapList.OrderBy(g => Rand.Int(...))` 这种随机化也要复刻

**结论: 过于脆弱，不推荐。**

### 2.2 方式二: 只劫持 Item 循环部分 (更精细)

不拦截整个 UpdateAll，而是:

```
UpdateAll prefix:
  - 反射读 mapEntityUpdateTick
  - 判断本帧是否执行 Item 循环 (tick % interval == 0)
  - 如果是: 设置 _interceptActive = true，预分类 safe/unsafe

(原版 UpdateAll 正常执行 Hull/Structure/Gap/Powered...)

Item.Update prefix (极简):
  if (_interceptActive && IsWorkerItem(__instance))
      return false;  // worker 会处理
  return true;       // main 正常执行

UpdateAll postfix:
  if (_interceptActive)
      等待 worker 完成
      _interceptActive = false
```

但这**又回到了在 Item.Update 上挂 prefix** — 虽然 prefix 体很小（一个 if + HashSet.Contains），Harmony 分发开销仍然是 ~0.5μs × 2586 = 1.3ms。

比当前方案好（少了 postfix 的 1.3ms），但仍有 1.3ms 固定税。

### 2.3 方式三: Transpiler 修改 Item 循环的 IL

用 Harmony **Transpiler** 直接修改 `UpdateAll` 的 IL，把原版的:

```csharp
foreach (Item item in Item.ItemList)
    item.Update(dt, cam);
```

替换为:

```csharp
ParallelHelper.DispatchItemUpdates(Item.ItemList, dt, cam);
```

**这是唯一能做到零每物品开销的方式。**

实现路径:
1. 找到 IL 中 `Item.ItemList` 的 `callvirt get_ItemList` 指令
2. 找到对应的 `foreach` 循环 (enumerator pattern 或 List.Enumerator)
3. 用 Transpiler 把整个循环替换为一个 `call ParallelHelper.DispatchItemUpdates`
4. `DispatchItemUpdates` 内部做分类 + worker 分发

---

## 3. Transpiler 方案的详细设计

### 3.1 IL 结构分析

`foreach (Item item in Item.ItemList)` 在 IL 中大致是:

```il
// Item.ItemList getter
call       class [System.Collections.Generic]List`1<class Item> Item::get_ItemList()
// 获取 enumerator
callvirt   instance valuetype List`1/Enumerator<class Item> [mscorlib]List`1<class Item>::GetEnumerator()
stloc.s    enumerator

.try {
  br.s     LOOP_CHECK
  LOOP_BODY:
    ldloca.s   enumerator
    call       instance !0 List`1/Enumerator<class Item>::get_Current()
    stloc.s    item
    
    // PriorityItems check...
    // item.Update(dt, cam) call
    ldloc.s    item
    ldarg.0    // deltaTime * interval
    ldarg.1    // cam
    callvirt   instance void Item::Update(float, class Camera)
    
  LOOP_CHECK:
    ldloca.s   enumerator
    call       instance bool List`1/Enumerator<class Item>::MoveNext()
    brtrue.s   LOOP_BODY
    
  leave.s    END
}
finally {
  // enumerator.Dispose()
}
END:
```

### 3.2 Transpiler 做什么

找到从 `call Item::get_ItemList()` 到 `finally` 结束的整段 IL，替换为:

```il
ldarg.0    // deltaTime * MapEntityUpdateInterval
ldarg.1    // cam
call       void ParallelHelper::DispatchItemUpdates(float, class Camera)
```

### 3.3 DispatchItemUpdates 内部逻辑

```csharp
static class ParallelHelper
{
    static void DispatchItemUpdates(float dt, Camera cam)
    {
        var priorityItems = LuaCsSetup.Instance.Game.UpdatePriorityItems;
        
        // Phase 1: 分类 (单线程，遍历一次)
        var mainItems = new List<Item>(2048);
        var workerItems = new List<Item>(512);
        
        foreach (Item item in Item.ItemList)
        {
            if (priorityItems.Contains(item)) continue;
            
            // 冻结跳过检查 (替代 ItemUpdatePatch)
            if (ShouldSkip(item)) continue;
            
            if (IsSafeForWorker(item))
                workerItems.Add(item);
            else
                mainItems.Add(item);
        }
        
        // Phase 2: 并行执行
        Task workerTask = null;
        if (workerItems.Count > 0)
            workerTask = Task.Run(() => RunWorker(workerItems, dt, cam));
        
        // Main thread 处理 unsafe items
        foreach (var item in mainItems)
            item.Update(dt, cam);
        
        // Phase 3: 等待 worker
        workerTask?.Wait();
    }
}
```

**关键优势: `item.Update(dt, cam)` 是直接调用，没有 Harmony 中间层 → 零额外开销。**

---

## 4. 风险矩阵

| 风险 | 严重性 | 概率 | 缓解措施 |
|------|--------|------|---------|
| IL 结构在版本间变化 | 高 | 中 | Transpiler 用模式匹配而非硬编码偏移；失败时 fallback 到原版 |
| try/catch 被移除后异常处理丢失 | 中 | 确定 | 在 DispatchItemUpdates 内重建 try/catch |
| PriorityItems 逻辑遗漏 | 高 | 低 | 显式处理（已在设计中） |
| worker 内 StatusEffect 写共享状态 | 致命 | 高 | 延用现有 ThreadSafetyPatches (F组) |
| `#if CLIENT` 的 Stopwatch 丢失 | 低 | 确定 | 在 DispatchItemUpdates 内重建 PerformanceCounter |
| LuaCs 升级改 UpdateAll 签名 | 高 | 低 | Transpiler 在找不到目标 IL 时安全退出 |
| 冻结逻辑在 DispatchItemUpdates 内重复 | 中 | 确定 | 抽取共享方法，ItemUpdatePatch.ShouldSkip() |

### 4.1 最危险的风险: Transpiler 脆弱性

Harmony Transpiler 依赖目标方法的 IL 结构。如果 Barotrauma/LuaCs 更新改了 `UpdateAll`:
- 加了一行代码 → IL 偏移全变
- 改了变量声明顺序 → local index 变
- Release 编译器优化了循环 → IL 结构不同

**缓解**: Transpiler 用**语义匹配**而非偏移:
```csharp
// 找 call Item.get_ItemList 操作码
var codes = instructions.ToList();
int loopStart = codes.FindIndex(c => 
    c.opcode == OpCodes.Call && 
    c.operand is MethodInfo m && 
    m.Name == "get_ItemList");
```
如果找不到 → 放弃 patch，log 警告，回退到原版行为。

---

## 5. 开销对比

| 方案 | Item.Update 上的 Harmony hook | 每帧固定税 | 并行收益上限 |
|------|------------------------------|-----------|-------------|
| 当前 A 方案 | 1 prefix (A1) | 1.3ms | N/A (无并行) |
| 当前 A+E 方案 | 3 prefix + 2 postfix | 3.9ms | ~0.3ms |
| **方案 B (Transpiler)** | **0** | **~0.01ms** | ~0.3ms |
| 方案 B + 冻结融合 | **0** | **~0.01ms** | ~0.3ms + 冻结省下的量 |

方案 B 的核心价值不在于并行收益变大了，而在于 **Harmony 税从 3.9ms 降到 ~0ms**。

### 5.1 Transpiler 本身的开销

Transpiler 在**加载时执行一次**（修改 IL），运行时**零开销** — 修改后的 IL 直接被 JIT 编译为 native code，和原版性能一样。

唯一的运行时开销是 `DispatchItemUpdates` 内部的分类循环 (遍历 Item.ItemList 一次做 safe/unsafe 分类)。这是 ~2586 × (一次 HashSet.Contains + 几个 if) ≈ **0.1ms**。

---

## 6. 方案 B 的两个变体

### 6.1 B-Lite: 只做冻结，不做并行

Transpiler 接管 Item 循环，但**只做冻结跳过**，不分发到 worker:

```csharp
static void DispatchItemUpdates(float dt, Camera cam)
{
    foreach (Item item in Item.ItemList)
    {
        if (priorityItems.Contains(item)) continue;
        if (ShouldSkip(item)) continue;     // 冻结/降频逻辑
        item.Update(dt, cam);               // 直接调用，零 Harmony 税
    }
}
```

**效果**: 冻结降频的收益（-1.5ms），完全不付 Harmony 税（-1.3ms）→ 比当前方案多省 1.3ms。

这是**最稳妥的选择** — 逻辑简单，不涉及多线程，风险只有 Transpiler 脆弱性。

### 6.2 B-Full: 冻结 + 并行

在 B-Lite 基础上加入 worker 分发。额外风险是线程安全（需要 F 组 ThreadSafety patches 保底）。

---

## 7. 实现难度评估

| 步骤 | 难度 | 工作量 |
|------|------|--------|
| 写 Transpiler 匹配 Item 循环 IL | **高** | 需要精确理解 IL 结构 |
| 实现 DispatchItemUpdates | 中 | 逻辑清楚，从现有代码重构 |
| 处理 `mapEntityUpdateTick` (private) | 低 | `AccessTools.FieldRefAccess` 一行 |
| 处理 PriorityItems 跳过 | 低 | 直接用 LuaCsSetup.Instance 访问 |
| 重建 try/catch + lastUpdatedItem 日志 | 低 | 直接抄原版 |
| 重建 CLIENT PerformanceCounter | 低 | 可选，不影响功能 |
| Fallback 机制 (Transpiler 失败时) | 中 | 检测 + 回退到当前 prefix 方案 |
| 测试验证 | **高** | 需要对比原版行为一致性 |

**总估计**: 实现 B-Lite 需要 ~100-150 行新代码 + 大量测试。

---

## 8. 替代方案: Detour 而非 Transpiler

如果 Transpiler 太脆弱，可以用 **MonoMod Detour** 直接替换整个 `UpdateAll`:

```csharp
// 保存原方法引用
static Action<float, Camera> _originalUpdateAll;

// 用 MonoMod detour 替换
var orig = AccessTools.Method(typeof(MapEntity), "UpdateAll");
var hook = AccessTools.Method(typeof(OurPatch), "UpdateAllReplacement");
// MonoMod 的 NativeDetour 或 Hook
```

Detour 比 Transpiler 更可靠（不依赖 IL 内部结构），但需要**完整复刻 UpdateAll** 的所有逻辑（Hull、Gap、Powered 等）— 这就是方式 2.1 的问题。

**折中方案**: Detour 替换 UpdateAll，但在 Item 循环部分用我们的逻辑，其他部分**调用 original**:

```csharp
static void UpdateAllReplacement(float deltaTime, Camera cam)
{
    // 无法只调用 "UpdateAll 的前半段" — original 是整个方法
    // 必须完整复刻或用 Transpiler
}
```

不可行 — original 是原子的，不能只调用一部分。

---

## 9. 结论

| 维度 | 评价 |
|------|------|
| 性能收益 | **极高** — 消除 1.3-3.9ms/帧 的 Harmony 税 |
| 实现复杂度 | **高** — Transpiler IL 操作 |
| 维护成本 | **中-高** — 依赖目标 IL 结构，版本升级可能 break |
| 风险 | **中** — 有 fallback 机制可缓解 |
| 推荐路径 | **B-Lite 优先** — 只做冻结跳过，不做并行，最大收益/风险比 |

### 推荐实施顺序

1. **先实现 B-Lite** (Transpiler 接管 Item 循环，做冻结跳过，不做并行)
   - 预期收益: 当前冻结的 -1.5ms 收益 + 消除 1.3ms Harmony 税 = **净省 ~2.8ms**
   - 从当前的 1.408ms (冻结) 降至 ~0.1ms (理论值，只剩分类遍历开销)

2. **验证稳定后考虑 B-Full** (加入并行分发)
   - 额外收益: ~0.2-0.3ms (可并行部分)
   - 额外风险: 线程安全

3. **永远保留 fallback** (当前 prefix 方案)
   - Transpiler 失败时自动回退
