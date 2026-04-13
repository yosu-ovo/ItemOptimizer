# 物品更新优化深度总结

> 基于武器库场景实测数据 (2586 物品, 600 帧, 2026-04-12)
> 三组 CSV: 无优化 / 冻结降频 / 多线程并行

---

## 1. 两条优化线路

```
  原版物品更新循环 (MapEntity.UpdateAll → Item.Update × N)
        │
        ├── 线路 A: 冻结降频 ──→ 砍掉不该跑的
        │     "这个物品真的需要每帧更新吗？"
        │     目标: 减少 N (实际调用次数)
        │
        └── 线路 B: 多线程并行 ──→ 加速必须跑的
              "既然必须跑，能不能分摊到多核？"
              目标: 减少 wall-clock (main 和 worker 并行)
```

两条线路**不是互斥的，而是串联的**: 先由冻结砍掉冗余，剩下的才交给并行。

---

## 2. 冻结降频: 当前最有效的路径

### 效果实测

| 指标 | 无优化 | 冻结降频 | 变化 |
|------|--------|---------|------|
| 帧均耗时 | 2.951ms | 1.408ms | **-52.3%** |
| p95 | 3.791ms | 1.932ms | -49.0% |
| p99 | 4.645ms | 2.772ms | -40.3% |
| max | 6.165ms | 3.527ms | -42.8% |

### 冻结的工作方式

冻结**不会完全消灭任何物品** — 618 个 identifier 全部存活。它的效果是**大幅降低更新频率**:
- TSM 武器: 90%+ 帧被跳过 (tsm_ksg -98.4%, tsm_ump45 -97.4%)
- Vanilla 核心: 几乎不受影响 (junctionbox 仅 -1.3%)

### 冻结后的耗时分布

```
冻结后 1.408ms/帧:
  ┌────────────────────┐
  │ Vanilla 46.3%      │  0.652ms  ← 冻不动
  │ TSM 残余 22.6%     │  0.318ms  ← 已大幅降频
  │ DDA 武器 11.9%     │  0.167ms  ← 部分冻不动
  │ TSM 任务 5.7%      │  0.080ms
  │ 其他 mod 13.5%     │  0.191ms
  └────────────────────┘
```

### "冻不动"的 94 个物品 (冻结前后 delta < 10%)

占冻结后 28.4% 耗时 (0.400ms/帧)。这些物品**每帧都必须更新**，冻结对它们无效:

| 类型 | 代表物品 | 原因 |
|-----|---------|------|
| 电路 | junctionbox (0.093ms) | 每帧传输信号 |
| 门 | doorwbuttons (0.041ms) | 响应开关状态 |
| 基础设施 | outpostreactor, smallpump, vent | 持续运行 |
| DDA 武器 | deep_CZ75, deep_44Magnum | 非库存内、含复杂组件 |
| 工具 | screwdriver | 被角色持有/频繁使用 |

**这 94 个物品就是多线程真正应该处理的目标。**

---

## 3. 多线程并行: 当前的问题

### 效果实测

| 指标 | 无优化 | 多线程 | 变化 |
|------|--------|--------|------|
| 帧均总 CPU | 2.951ms | 3.361ms | **+14% 更差** |
| main lane | 2.951ms | 2.413ms | -18% |
| worker lane | - | 0.948ms | |
| wall-clock 理论值 | 2.951ms | 2.413ms | -18% (被 main 决定) |

### 为什么 TSM 武器变慢 +64~99%

武器库场景的特殊性:

```
                    武器库里 2586 个物品
                          │
             ┌────────────┴────────────┐
          容器/库存内                  场景中放置
          ~2000+ 个 TSM 武器          ~500 个 Vanilla + 少量 mod
             │                           │
        冻结已处理 ✓                分类: Vanilla → main
        多线程无需处理                    mod → 看安全性
             │                           │
      但 Harmony prefix             大部分 mod 也进了 main
      还是每帧跑! ✗                 (body/wired/held/unsafe)
```

TSM 武器变慢的**三层原因**:

1. **Harmony 叠加税**: 多线程在 `Item.Update` 上额外挂了 prefix+postfix → 2586×1μs = +2.6ms
2. **F 组 lock 连锁反应**: 每把武器的 Update 内部调用 StatusEffect.Apply / PropertyConditional.Matches → ThreadSafety 的 lock prefix/postfix 被连带触发多次
3. **白跑**: 武器在冻结后本来就跳过了，但 Harmony 入口开销在跳过判断**之前**就发生了

### main/worker 严重不平衡

```
main:   ████████████████████████ 2.413ms (85% 物品, 瓶颈)
worker: █████████               0.948ms (15% 物品, 空转等待)
```

worker 只处理了 0.948ms 的工作，远小于 main。并行化的理论收益 = worker 省下的时间 ≈ 0.948ms，但 Harmony 叠加税 = 2.6ms → **净亏损 ~1.6ms**。

---

## 4. 冻结后还能并行什么？

### 4.1 当前分类规则的瓶颈

```python
IsSafeForWorker(item):
  if Vanilla          → main  # 一刀切禁止，杀死了 46.3% 的可能性
  if 冷存储           → skip  # 已被冻结处理
  if 地面+Holdable    → main  # 已被降频处理
  if 角色持有         → main  # 正确 — 写角色状态
  if body.Enabled     → main  # 物理体活跃
  if 有接线           → main  # 信号相关
  if Unsafe component → main  # Door/Pump/Reactor 等
```

"角色持有 → main" 这条规则**在武器库场景不成立**——大部分武器是静止在容器里的，只有极少数被角色装备。但冻结已经把容器内的武器处理掉了，所以这条规则实际影响很小。

**真正的问题是 `Vanilla → main` 这条一刀切规则。**

### 4.2 冻结后的可并行候选

把 94 个 "冻不动" 的物品按当前分类规则过滤:

| 候选类型 | 物品 | 冻结后耗时 | 能否并行 | 原因 |
|---------|------|-----------|---------|------|
| Vanilla 电路 | junctionbox | 0.093ms | ✗ 当前禁止 | 信号传输，写共享状态 |
| Vanilla 门 | doorwbuttons | 0.041ms | ✗ 当前禁止 | Door 是 Unsafe component |
| Vanilla 反应堆 | outpostreactor | 0.033ms | ✗ 当前禁止 | Reactor 是 Unsafe |
| Vanilla 通风 | vent | 0.029ms | ✗ 当前禁止 | 可能接线 |
| DDA 武器 | deep_CZ75 | 0.025ms | **可能** ✓ | mod 物品，无物理体，不接线 |
| DDA 武器 | deep_44Magnum | 0.017ms | **可能** ✓ | 同上 |
| 中医 mod | antibloodloss1 | 0.006ms | **可能** ✓ | mod 物品 |

### 4.3 残酷的现实

冻结后:
- **Vanilla 占 46.3%** → 全部被 `Vanilla → main` 规则锁死
- **mod 中 "冻不动" 的** → 大约只有 DDA 武器等少量物品，总计 ~0.1–0.2ms

即使把所有可安全并行的 mod 物品都推到 worker，最多只能卸载 **~0.1–0.2ms** 给 worker。

```
冻结后 1.408ms 的构成:
  Vanilla (冻不动, 禁入 worker)    0.652ms  46%  ← 无法触及
  Mod (已被冻结大幅降频)            0.318ms  23%  ← 已经很低了
  Mod (冻不动, 可能可以并行)        0.200ms  14%  ← 多线程的全部收益上限
  Mod (冻不动, unsafe/held/wired)  0.238ms  17%  ← 无法触及
```

**多线程在冻结后的理论收益上限: ~0.2ms** — 远不值得为此付出 2.6ms 的 Harmony 税。

---

## 5. 如果要让多线程有意义

### 方案 A: 允许部分 Vanilla 物品并行 (风险高)

Vanilla 占冻结后 46.3%。如果能安全地把部分 Vanilla 物品推到 worker:

| 候选 | 耗时 | 可行性 |
|------|------|--------|
| junctionbox | 0.093ms | **极危险** — 信号传输、读写共享 Connection 状态 |
| doorwbuttons | 0.041ms | **不可行** — Door 组件操作物理/GUI 状态 |
| vent | 0.029ms | **可能** — 如果无接线，逻辑简单 |
| delaycomponent | 0.028ms | **可能** — 纯逻辑，但参与信号链 |
| lightfluorescentl01 | 0.016ms | **可能** — 灯光状态简单 |

即使成功把 vent + delay + light 并行化，收益也只有 ~0.07ms。

### 方案 B: 消除 Harmony 税 (根本解决)

当前架构的根本问题: 并行调度需要在 `Item.Update` 上挂 prefix+postfix → 2586 次/帧 → 2.6ms 税。

如果改为在 `MapEntity.UpdateAll` 层面直接接管循环 (不 patch Item.Update):

```
MapEntity.UpdateAll prefix:
  1. 分类物品 → safe / unsafe
  2. 拦截原方法 (return false)
  3. 手动遍历: unsafe 物品在当前线程调 Item.Update()
  4. 同时 Task.Run: safe 物品在 worker 调 Item.Update()
  5. WaitAll
```

这样 Item.Update 上 **零额外 patch**，Harmony 税降为每帧 1 次 (MapEntity.UpdateAll 的 prefix)。

**难点**: 需要反射/IL 访问 `MapEntity.UpdateAll` 的私有遍历逻辑，或完整重写该方法的遍历部分。

### 方案 C: 只在大规模 mod 场景启用

武器库场景 (2586 物品) 不适合多线程 — 大部分是容器内武器，冻结已处理。

**真正需要多线程的场景**: mid/late game 潜艇 + 大量 mod 物品活跃在场景中（不在容器内），冻结无法处理的物品数量足够大 (>500 个 mod 物品活跃) 时，并行收益才能覆盖 Harmony 税。

---

## 6. 总结与建议

| 策略 | 帧均效果 | 状态 | 建议 |
|------|---------|------|------|
| 冻结降频 | **-1.54ms (-52%)** | 稳定可靠 | **主力方案** |
| 多线程 (当前) | +0.41ms (+14%) | 净亏损 | 暂停使用 |
| 多线程 (方案 B) | 理论 -0.2ms | 未实现 | 仅在大规模 mod 场景有价值 |

**当前最优配置**: 只开冻结降频，关闭多线程。帧均 1.41ms，已接近原版天花板。

**未来方向**:
1. 收集更多场景数据 (mid/late game 潜艇) 确认多线程在哪种场景下有正收益
2. 如果要继续多线程，必须先解决方案 B (不 patch Item.Update)，否则 Harmony 税始终大于收益
3. 考虑对 Vanilla 物品做更精细的冻结 (如不接线的灯光 → 低频更新)

---

## 7. 版本记录

| 日期 | 更新 |
|------|------|
| 2026-04-12 | 初版: 三组 CSV 对比分析，两条线路确立，多线程问题定位 |
