== ItemOptimizer 归档功能 / Archived Features ==

这些功能已从编译范围中移除，因为：
- 优化效果极低或可能造成负面效果
- 已被更好的系统取代（Rewrite 取代 Throttle）

These features have been removed from compilation because:
- Minimal optimization benefit or may cause negative side effects
- Superseded by better systems (Rewrite replaced Throttle)

归档文件 / Archived files:
  WearablePatch.cs          - 穿戴物降频 (低效果, 可能负面)
  DoorPatch.cs              - 舱门降频 (可能造成门卡顿)
  StatusHUDPatch.cs         - 状态HUD降频 (低效果, 影响体验)
  AfflictionDedupPatch.cs   - 症状去重 (低效果)
  CustomInterfacePatch.cs   - 自定义面板节流 (已被闲置物品跳过覆盖)
  MotionSensorPatch.cs      - 运动传感器旧Throttle (已被Rewrite取代)
  WaterDetectorPatch.cs     - 水位传感器旧Throttle (已被Rewrite取代)
  SignalOptPatches.cs       - 客户端信号优化 (已被Rewrite取代)

如需重新启用 / To re-enable:
  将文件移回对应的 CSharp/ 子目录，并在代码中恢复注册逻辑。
  Move the file back to the corresponding CSharp/ subdirectory and restore registration logic.

  原始路径 / Original paths:
    WearablePatch.cs          → CSharp/Shared/Patches/Item/Component/
    DoorPatch.cs              → CSharp/Shared/Patches/Item/Component/
    CustomInterfacePatch.cs   → CSharp/Shared/Patches/Item/Component/
    StatusHUDPatch.cs         → CSharp/Client/Patches/
    AfflictionDedupPatch.cs   → CSharp/Shared/Patches/Character/
    MotionSensorPatch.cs      → CSharp/Shared/Patches/Item/Sensor/
    WaterDetectorPatch.cs     → CSharp/Shared/Patches/Item/Sensor/
    SignalOptPatches.cs       → CSharp/Client/Signal/
