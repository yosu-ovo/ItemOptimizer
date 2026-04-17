using System.Collections.Generic;
using Barotrauma;

namespace ItemOptimizerMod
{
    static class Localization
    {
        private static readonly Dictionary<string, string> ZH = new()
        {
            // General
            ["mod_name"]              = "ItemOptimizer",

            // ESC menu
            ["btn_settings"]          = "物品优化",
            ["btn_close"]             = "关闭",
            ["btn_save"]              = "保存",
            ["btn_reload"]            = "重载",

            // Panel
            ["panel_title"]           = "ItemOptimizer 设置",
            ["section_core_opt"]      = "── 核心优化 ──",
            ["section_item_rules"]    = "── 物品规则 ──",
            ["section_mod_management"]= "── Mod 物品管理 ──",
            ["section_diagnostics"]   = "── 诊断与统计 ──",

            // ── Core Optimization strategies ──
            ["strategy_cold_storage"]          = "闲置物品跳过",
            ["strategy_cold_storage_desc"]     = "储物柜和箱子中的物品跳过更新，节省大量性能",
            ["strategy_ground_item"]           = "地面物品节流",
            ["strategy_ground_item_desc"]      = "降低地面散落物品的更新频率",
            ["strategy_ci_throttle"]           = "自定义面板降频",
            ["strategy_ci_throttle_desc"]      = "减少未使用的按钮面板更新频率",
            ["strategy_motion"]                = "运动传感器降频",
            ["strategy_motion_desc"]           = "减少运动传感器的检测频率",
            ["strategy_wearable"]              = "穿戴物降频",
            ["strategy_wearable_desc"]         = "减少非本地玩家穿戴装备的更新频率",
            ["strategy_water_det"]             = "水位传感器降频",
            ["strategy_water_det_desc"]        = "减少水位传感器的信号发送频率",
            ["strategy_door"]                  = "舱门降频",
            ["strategy_door_desc"]             = "减少舱门状态更新频率。多人游戏中跳帧过高可能导致开关延迟或回弹，建议设为2或关闭",
            ["strategy_door_desync"]           = "防回弹保护（服务端+客户端）",
            ["strategy_door_desync_desc"]      = "检测门状态变更并立即同步物理碰撞体，防止多人游戏中因降频导致的回弹",
            ["strategy_hst_cache"]             = "状态标签缓存",
            ["strategy_hst_cache_desc"]        = "缓存物品状态标签查询结果，避免重复扫描",
            ["strategy_statushud"]             = "状态HUD降频",
            ["strategy_statushud_desc"]        = "减少健康检查扫描仪的扫描频率和热成像绘制",
            ["strategy_affliction"]            = "症状去重",
            ["strategy_affliction_desc"]       = "跳过已达上限的症状效果重复计算",
            ["strategy_wire_skip"]             = "电线跳过",
            ["strategy_wire_skip_desc"]        = "跳过电线物品的更新。信号传输不受影响，但电线将不可见。打开接线面板时仍可操作",
            ["stats_wire_skip"]                = "电线跳过",
            ["strategy_motion_rewrite"]        = "运动传感器重写",
            ["strategy_motion_rewrite_desc"]   = "使用优化算法完全替换运动传感器逻辑，性能大幅提升",
            ["strategy_water_det_rewrite"]     = "水位传感器重写",
            ["strategy_water_det_rewrite_desc"]= "使用优化算法完全替换水位传感器逻辑，性能大幅提升",

            // Sensor mode dropdown
            ["sensor_off"]                     = "关闭",
            ["sensor_throttle"]                = "降频",
            ["sensor_rewrite"]                 = "重写",
            ["strategy_motion_sensor"]         = "运动传感器",
            ["strategy_motion_sensor_desc"]    = "运动传感器优化模式：关闭/降频/重写",
            ["strategy_water_detector_mode"]   = "水位传感器",
            ["strategy_water_detector_mode_desc"] = "水位传感器优化模式：关闭/降频/重写",
            ["skip_frames_label"]              = "间隔",

            // HUD held item
            ["hud_held_item"]                  = "── 手持物品 ──",
            ["hud_item_id"]                    = "ID",
            ["hud_item_name"]                  = "名称",
            ["hud_item_mod"]                   = "Mod",
            ["hud_item_status"]                = "优化状态",
            ["hud_whitelisted"]                = "已白名单",
            ["hud_cold_storage"]               = "冷存储",
            ["hud_no_opt"]                     = "无优化",
            ["btn_hud_whitelist"]              = "+ 白名单",
            ["strategy_relay_rewrite"]         = "中继器重写",
            ["strategy_relay_rewrite_desc"]    = "使用优化算法完全替换中继器逻辑",
            ["strategy_power_transfer_rewrite"]      = "配电盘重写",
            ["strategy_power_transfer_rewrite_desc"] = "优化配电盘（接线盒等）更新逻辑",
            ["strategy_power_container_rewrite"]      = "电池重写",
            ["strategy_power_container_rewrite_desc"] = "优化电池/蓄电器更新逻辑",

            // Risk labels
            ["risk_safe"]             = "安全",
            ["risk_warning"]          = "⚠ 注意",
            ["risk_experimental"]     = "⚠ 实验",

            // ── Character Optimization ──
            ["section_character_opt"]      = "── 角色优化 ──",
            ["strategy_anim_lod"]          = "动画简化",
            ["strategy_anim_lod_desc"]     = "远处角色使用简化动画，不可见角色跳过动画计算",
            ["strategy_char_stagger"]      = "AI分组更新",
            ["strategy_char_stagger_desc"] = "怪物AI分组轮流更新，摊平CPU负载。分组数越多越平但AI反应越慢",
            ["stagger_groups_label"]       = "分组",

            // ── Network Sync ──
            ["section_network_sync"]       = "── 网络同步修复 ──",
            ["strategy_ladder_fix"]        = "梯子同步修复",
            ["strategy_ladder_fix_desc"]   = "修复梯子上的位置不同步问题，消除橡皮筋效应",
            ["strategy_platform_fix"]      = "平台同步修复",
            ["strategy_platform_fix_desc"] = "修复平台穿越时的位置不同步问题",
            ["strategy_server_dedup"]      = "服务器去重优化",
            ["strategy_server_dedup_desc"] = "优化服务器位置更新队列的查重效率",

            // ── Advanced ──
            ["section_advanced"]           = "── 进阶选项 ──",
            ["strategy_misc_parallel"]     = "杂项并行",
            ["strategy_misc_parallel_desc"]= "并行处理舱室/结构/缺口/电力更新",
            ["strategy_proxy"]             = "代理物品系统",
            ["strategy_proxy_desc"]        = "为特殊物品提供批量计算架构",
            ["strategy_signal_graph"]      = "信号图加速",
            ["strategy_signal_graph_desc"] = "编译电路为优化格式加速信号处理",
            ["signal_graph_off"]           = "关闭",
            ["signal_graph_accel"]         = "加速模式",
            ["signal_graph_aggressive"]    = "\u26a0 激进模式",
            ["signal_graph_aggressive_desc"] = "扩大编译范围：含继电器信号直通+延迟器。含连接StatusEffect的继电器可能受影响",
            ["strategy_hull_spatial"]      = "空间加速索引",
            ["strategy_hull_spatial_desc"] = "加速角色与舱室的空间查询",
            ["strategy_spike_detector"]    = "卡顿检测",
            ["strategy_spike_detector_desc"]= "检测单帧耗时过高的物品，用于诊断性能问题",
            ["spike_threshold_label"]      = "阈值(ms)",

            // ── Developer Tools ──
            ["section_dev_tools"]          = "── 开发者工具 ──",
            ["strategy_allow_sync"]        = "同步录制",
            ["strategy_allow_sync_desc"]   = "允许客户端请求服务端同步数据(iosync命令)，有少量网络开销",
            ["dev_serverperf"]             = "服务端性能叠加层",
            ["dev_serverperf_desc"]        = "显示服务端各系统耗时分解（需要多人游戏连接）",
            ["dev_iorecord"]               = "录制性能数据",
            ["dev_iorecord_desc"]          = "录制1200帧逐物品计时数据，保存为CSV。等待约20秒完成录制",
            ["dev_iosgraph"]               = "信号图诊断",
            ["dev_iosgraph_desc"]          = "显示信号图加速器状态：编译节点数、寄存器数、加速模式。打开控制台查看结果",

            // ── Client Optimization ──
            ["section_client_opt"]              = "── 客户端优化 ──",
            ["strategy_interaction_label"]      = "交互标签优化",
            ["strategy_interaction_label_desc"] = "限制同时显示的交互标签数量，解决大量物品时的卡顿",
            ["interaction_label_max_label"]     = "最大数量",
            ["strategy_relay_opt"]              = "继电器优化",
            ["strategy_relay_opt_desc"]         = "消除继电器每帧的内存分配开销",
            ["strategy_motion_sensor_opt"]      = "运动传感器优化",
            ["strategy_motion_sensor_opt_desc"] = "消除运动传感器每帧的内存分配开销",
            ["strategy_water_detector_opt"]     = "水位传感器优化",
            ["strategy_water_detector_opt_desc"]= "缓存水位传感器的连接引用，跳过重复查找",
            ["strategy_button_terminal_opt"]    = "按钮终端优化",
            ["strategy_button_terminal_opt_desc"]= "消除按钮终端每帧的嵌套查询开销",
            ["strategy_pump_opt"]               = "泵优化",
            ["strategy_pump_opt_desc"]          = "缓存泵的组件引用，跳过每帧的组件查找",

            // ── Server ──
            ["section_server"]            = "服务器",
            ["server_good"]               = "良好",
            ["server_warning"]            = "警告",
            ["server_critical"]           = "危险",
            ["server_clients_entities"]   = "  客户端: {0}  实体: {1}",
            ["server_queues"]             = "  位置队列: {0:F1}  事件队列: {1:F1}",
            ["server_skipped"]            = "  跳过物品: {0}",
            ["server_metric_interval"]    = "指标间隔",

            // Item rules
            ["rule_add"]              = "+ 添加规则",
            ["rule_remove"]           = "×",
            ["rule_identifier"]       = "物品标识符",
            ["action_skip"]           = "跳过更新",
            ["action_throttle"]       = "降频",
            ["cond_always"]           = "始终",
            ["cond_cold_storage"]     = "储物柜中",
            ["cond_not_active_use"]   = "未使用时",

            // Whitelist
            ["section_whitelist"]     = "── 物品白名单 ──",
            ["whitelist_desc"]        = "白名单中的物品在任何情况下都不会被降频，适用于需要实时响应的关键物品（如燃料棒、爆炸物等）",
            ["whitelist_add"]         = "输入物品标识符...",
            ["whitelist_add_btn"]     = "+ 添加",
            ["whitelist_remove"]      = "移除",
            ["whitelist_empty"]       = "白名单为空",

            // Overlay
            ["overlay_toggle"]        = "HUD 统计叠加层",
            ["overlay_waiting"]       = "等待性能数据...",

            // Stats
            ["stats_format"]          = "{0}: ~{1:F0}/帧",
            ["stats_saved"]           = "预计节省: ~{0:F2}ms/帧",
            ["stats_item_rules"]      = "物品规则",
            ["stats_mod_opt"]         = "Mod优化",
            ["stats_ground_item"]     = "地面物品",
            ["stats_water_det"]       = "水位传感器",
            ["stats_door"]            = "舱门",
            ["stats_hst_cache"]       = "标签缓存命中",
            ["stats_statushud"]       = "状态HUD",
            ["stats_affliction"]      = "症状去重",
            ["stats_ladder_fix"]      = "梯子修正",
            ["stats_platform_fix"]    = "平台修正",
            ["stats_proxy_items"]     = "代理物品",
            ["stats_anim_lod"]        = "动画简化",
            ["stats_char_stagger"]    = "AI分组",
            ["stats_signal_graph_skip"]    = "信号图跳过",
            ["stats_signal_graph_tick"]    = "信号图耗时",

            // Mod control
            ["section_mod_control"]   = "── Mod 控制面板 ──",
            ["btn_optimize_all"]      = "一键推荐优化",
            ["btn_clear_all_mod"]     = "清除所有Mod规则",
            ["confirm_clear_all"]     = "确定要清除所有 Mod 规则吗？此操作不可撤销。",
            ["confirm_optimize_all"]  = "确定要对所有 Mod 应用推荐优化吗？",
            ["btn_confirm"]           = "确定",
            ["btn_cancel"]            = "取消",
            ["mods_optimized_summary"]= "已优化 {0}/{1} mods",
            ["tier_critical"]         = "高活跃",
            ["tier_active"]           = "活跃",
            ["tier_moderate"]         = "普通",
            ["tier_static"]           = "静态",
            ["tier_skip_label"]       = "间隔:",
            ["tier_status"]           = "{0}/{1}",
            ["btn_apply_tier"]        = "应用",
            ["btn_optimize_mod"]      = "推荐优化",
            ["btn_show_detail"]       = "显示详情 ▼",
            ["btn_hide_detail"]       = "隐藏详情 ▲",
            ["btn_remove_rule"]       = "移除",
            ["mod_not_optimized"]     = "未优化",
            ["mod_items_count"]       = "({0} 物品)",
            ["mod_batch_add"]         = "批量添加",
            ["mod_no_rule"]           = "无规则",
            ["mod_rule_summary"]      = "Rule: {0}/{1}",
            ["mod_add_rule"]          = "添加规则",
            ["rule_added"]            = "已添加",
            ["rule_removed"]          = "已移除",
            ["mod_pattern_statushud"] = "StatusHUD",
            ["mod_pattern_affliction"]= "Affliction",
            ["mod_pattern_conditional"]= "Conditional",
            ["mod_pattern_multi_se"]  = "MultiSE",

            // Intensity slider
            ["intensity_label"]       = "强度",
            ["intensity_preview"]     = "效果预览: 高活跃→{0} | 活跃→{1} | 普通→{2} | 静态→{3}",
            ["global_intensity_label"]= "全局强度",

            // Proxy system
            ["section_proxy"]         = "── 代理系统 ──",
            ["proxy_batch"]           = "批量计算",
            ["proxy_sync"]            = "同步回写",
            ["proxy_physics"]         = "物理维持",
            ["proxy_items_count"]     = "代理: ~{0:F0} / 总物品: {1}",

            // Toast
            ["config_saved"]          = "配置已保存",
            ["config_reloaded"]       = "配置已重载",

            // Dispatch overlay
            ["section_threads"]       = "── 线程分布 ──",
            ["parallel_main"]         = "主线程",
            ["dispatch_total"]        = "总调度: {0:F1}ms (开销: {1:F1}ms)",
        };

        private static readonly Dictionary<string, string> EN = new()
        {
            // General
            ["mod_name"]              = "ItemOptimizer",

            // ESC menu
            ["btn_settings"]          = "Item Optimizer",
            ["btn_close"]             = "Close",
            ["btn_save"]              = "Save",
            ["btn_reload"]            = "Reload",

            // Panel
            ["panel_title"]           = "ItemOptimizer Settings",
            ["section_core_opt"]      = "── Core Optimization ──",
            ["section_item_rules"]    = "── Item Rules ──",
            ["section_mod_management"]= "── Mod Item Management ──",
            ["section_diagnostics"]   = "── Diagnostics & Stats ──",

            // ── Core Optimization strategies ──
            ["strategy_cold_storage"]          = "Idle Item Skip",
            ["strategy_cold_storage_desc"]     = "Skip updates for items in lockers and containers, significant performance savings",
            ["strategy_ground_item"]           = "Ground Item Throttle",
            ["strategy_ground_item_desc"]      = "Reduce update frequency for items on the ground",
            ["strategy_ci_throttle"]           = "Custom Panel Throttle",
            ["strategy_ci_throttle_desc"]      = "Reduce update frequency for unused button panels",
            ["strategy_motion"]                = "Motion Sensor Throttle",
            ["strategy_motion_desc"]           = "Reduce motion sensor scan frequency",
            ["strategy_wearable"]              = "Wearable Throttle",
            ["strategy_wearable_desc"]         = "Reduce wearable update frequency for non-local characters",
            ["strategy_water_det"]             = "Water Detector Throttle",
            ["strategy_water_det_desc"]        = "Reduce water detector signal sending frequency",
            ["strategy_door"]                  = "Door Throttle",
            ["strategy_door_desc"]             = "Reduce door state update frequency. High skip values in multiplayer may cause door delay or rubber-banding. Recommend 2 or off for MP.",
            ["strategy_door_desync"]           = "Anti-Desync Guard (Server+Client)",
            ["strategy_door_desync_desc"]      = "Detect door state changes and immediately sync physics body. Prevents rubber-banding from throttle desync in multiplayer.",
            ["strategy_hst_cache"]             = "Status Tag Cache",
            ["strategy_hst_cache_desc"]        = "Cache status tag query results to avoid repeated scans",
            ["strategy_statushud"]             = "Status HUD Throttle",
            ["strategy_statushud_desc"]        = "Reduce health scanner scan frequency and thermal overlay drawing",
            ["strategy_affliction"]            = "Affliction Dedup",
            ["strategy_affliction_desc"]       = "Skip redundant affliction application when already at max strength",
            ["strategy_wire_skip"]             = "Wire Skip",
            ["strategy_wire_skip_desc"]        = "Skip wire item updates. Signal propagation is unaffected, but wires become invisible. Wires remain operable in wiring panels.",
            ["stats_wire_skip"]                = "Wire Skip",
            ["strategy_motion_rewrite"]        = "Motion Sensor Rewrite",
            ["strategy_motion_rewrite_desc"]   = "Complete optimized replacement for motion sensor logic, major performance gain",
            ["strategy_water_det_rewrite"]     = "Water Detector Rewrite",
            ["strategy_water_det_rewrite_desc"]= "Complete optimized replacement for water detector logic, major performance gain",

            // Sensor mode dropdown
            ["sensor_off"]                     = "Off",
            ["sensor_throttle"]                = "Throttle",
            ["sensor_rewrite"]                 = "Rewrite",
            ["strategy_motion_sensor"]         = "Motion Sensor",
            ["strategy_motion_sensor_desc"]    = "Motion sensor optimization mode",
            ["strategy_water_detector_mode"]   = "Water Detector",
            ["strategy_water_detector_mode_desc"] = "Water detector optimization mode",
            ["skip_frames_label"]              = "Interval",

            // HUD held item
            ["hud_held_item"]                  = "── Held Item ──",
            ["hud_item_id"]                    = "ID",
            ["hud_item_name"]                  = "Name",
            ["hud_item_mod"]                   = "Mod",
            ["hud_item_status"]                = "Opt Status",
            ["hud_whitelisted"]                = "Whitelisted",
            ["hud_cold_storage"]               = "Cold Storage",
            ["hud_no_opt"]                     = "No optimization",
            ["btn_hud_whitelist"]              = "+ Whitelist",
            ["strategy_relay_rewrite"]         = "Relay Rewrite",
            ["strategy_relay_rewrite_desc"]    = "Complete optimized replacement for relay component logic",
            ["strategy_power_transfer_rewrite"]      = "PowerTransfer Rewrite",
            ["strategy_power_transfer_rewrite_desc"] = "Optimize junction box and power transfer update logic",
            ["strategy_power_container_rewrite"]      = "PowerContainer Rewrite",
            ["strategy_power_container_rewrite_desc"] = "Optimize battery and supercapacitor update logic",

            // Risk labels
            ["risk_safe"]             = "Safe",
            ["risk_warning"]          = "\u26a0 Caution",
            ["risk_experimental"]     = "\u26a0 Experimental",

            // ── Character Optimization ──
            ["section_character_opt"]      = "── Character Optimization ──",
            ["strategy_anim_lod"]          = "Animation LOD",
            ["strategy_anim_lod_desc"]     = "Simplified animation for distant characters, skip animation for invisible ones",
            ["strategy_char_stagger"]      = "AI Stagger Update",
            ["strategy_char_stagger_desc"] = "Spread enemy AI updates across frames. More groups = smoother load but slower AI reactions",
            ["stagger_groups_label"]       = "Groups",

            // ── Network Sync ──
            ["section_network_sync"]       = "── Network Sync Fixes ──",
            ["strategy_ladder_fix"]        = "Ladder Sync Fix",
            ["strategy_ladder_fix_desc"]   = "Fix ladder position desync, eliminates rubber-banding",
            ["strategy_platform_fix"]      = "Platform Sync Fix",
            ["strategy_platform_fix_desc"] = "Fix platform drop-through position desync",
            ["strategy_server_dedup"]      = "Server Dedup Optimization",
            ["strategy_server_dedup_desc"] = "Optimize server position update queue deduplication",

            // ── Advanced ──
            ["section_advanced"]           = "── Advanced ──",
            ["strategy_misc_parallel"]     = "Misc Entity Parallel",
            ["strategy_misc_parallel_desc"]= "Parallel processing for Hull/Structure/Gap/Power updates",
            ["strategy_proxy"]             = "Proxy Item System",
            ["strategy_proxy_desc"]        = "Batch compute architecture for special items",
            ["strategy_signal_graph"]      = "Signal Graph Accel",
            ["strategy_signal_graph_desc"] = "Compile signal circuits into optimized format for faster processing",
            ["signal_graph_off"]           = "Off",
            ["signal_graph_accel"]         = "Accelerate",
            ["signal_graph_aggressive"]    = "\u26a0 Aggressive",
            ["signal_graph_aggressive_desc"] = "Expanded compilation: relay signal passthrough + delay. May affect relays with StatusEffects on connections",
            ["strategy_hull_spatial"]      = "Hull Spatial Index",
            ["strategy_hull_spatial_desc"] = "Accelerate character-hull spatial queries",
            ["strategy_spike_detector"]    = "Spike Detector",
            ["strategy_spike_detector_desc"]= "Detect items causing per-frame spikes, for performance diagnosis",
            ["spike_threshold_label"]      = "Threshold(ms)",

            // ── Developer Tools ──
            ["section_dev_tools"]          = "── Developer Tools ──",
            ["strategy_allow_sync"]        = "Sync Recording",
            ["strategy_allow_sync_desc"]   = "Allow clients to request server sync snapshots (iosync command), minor network overhead",
            ["dev_serverperf"]             = "Server Perf Overlay",
            ["dev_serverperf_desc"]        = "Show server-side per-system timing breakdown (requires multiplayer connection)",
            ["dev_iorecord"]               = "Record Perf Data",
            ["dev_iorecord_desc"]          = "Record 1200 frames of per-item timing to CSV. Takes ~20 seconds to complete",
            ["dev_iosgraph"]               = "Signal Graph Info",
            ["dev_iosgraph_desc"]          = "Show signal graph accelerator status: compiled nodes, registers, mode. Opens console to view results",

            // ── Client Optimization ──
            ["section_client_opt"]              = "── Client Optimization ──",
            ["strategy_interaction_label"]      = "Interaction Label Opt",
            ["strategy_interaction_label_desc"] = "Cap interaction label count when pressing ALT, fixes lag with many items",
            ["interaction_label_max_label"]     = "Max Labels",
            ["strategy_relay_opt"]              = "Relay Optimization",
            ["strategy_relay_opt_desc"]         = "Eliminate per-frame memory allocation in relay component",
            ["strategy_motion_sensor_opt"]      = "MotionSensor Optimization",
            ["strategy_motion_sensor_opt_desc"] = "Eliminate per-frame memory allocation in motion sensor",
            ["strategy_water_detector_opt"]     = "WaterDetector Optimization",
            ["strategy_water_detector_opt_desc"]= "Cache water detector connection references, skip repeated lookups",
            ["strategy_button_terminal_opt"]    = "ButtonTerminal Optimization",
            ["strategy_button_terminal_opt_desc"]= "Eliminate nested query overhead in button terminal",
            ["strategy_pump_opt"]               = "Pump Optimization",
            ["strategy_pump_opt_desc"]          = "Cache pump component reference, skip per-frame lookups",

            // ── Server ──
            ["section_server"]            = "Server",
            ["server_good"]               = "Good",
            ["server_warning"]            = "Warning",
            ["server_critical"]           = "Critical",
            ["server_clients_entities"]   = "  Clients: {0}  Entities: {1}",
            ["server_queues"]             = "  Pos Queue: {0:F1}  Event Queue: {1:F1}",
            ["server_skipped"]            = "  Skipped Items: {0}",
            ["server_metric_interval"]    = "Metric Interval",

            // Item rules
            ["rule_add"]              = "+ Add Rule",
            ["rule_remove"]           = "x",
            ["rule_identifier"]       = "Item Identifier",
            ["action_skip"]           = "Skip Update",
            ["action_throttle"]       = "Throttle",
            ["cond_always"]           = "Always",
            ["cond_cold_storage"]     = "In Storage",
            ["cond_not_active_use"]   = "Not In Use",

            // Whitelist
            ["section_whitelist"]     = "── Item Whitelist ──",
            ["whitelist_desc"]        = "Whitelisted items will never be throttled under any condition. Use for critical items that need real-time response (e.g. fuel rods, explosives).",
            ["whitelist_add"]         = "Enter item identifier...",
            ["whitelist_add_btn"]     = "+ Add",
            ["whitelist_remove"]      = "Remove",
            ["whitelist_empty"]       = "Whitelist is empty",

            // Overlay
            ["overlay_toggle"]        = "HUD Stats Overlay",
            ["overlay_waiting"]       = "Waiting for perf data...",

            // Stats
            ["stats_format"]          = "{0}: ~{1:F0}/frame",
            ["stats_saved"]           = "Estimated saved: ~{0:F2}ms/frame",
            ["stats_item_rules"]      = "Item Rules",
            ["stats_mod_opt"]         = "Mod Optimization",
            ["stats_ground_item"]     = "Ground Items",
            ["stats_water_det"]       = "WaterDetector",
            ["stats_door"]            = "Door",
            ["stats_hst_cache"]       = "Tag Cache Hits",
            ["stats_statushud"]       = "StatusHUD",
            ["stats_affliction"]      = "Affliction Dedup",
            ["stats_ladder_fix"]      = "Ladder Fix",
            ["stats_platform_fix"]    = "Platform Fix",
            ["stats_proxy_items"]     = "Proxy Items",
            ["stats_anim_lod"]        = "Anim LOD",
            ["stats_char_stagger"]    = "AI Stagger",
            ["stats_signal_graph_skip"]    = "SignalGraph Skip",
            ["stats_signal_graph_tick"]    = "SignalGraph Tick",

            // Mod control
            ["section_mod_control"]   = "── Mod Control Panel ──",
            ["btn_optimize_all"]      = "Optimize All (Recommended)",
            ["btn_clear_all_mod"]     = "Clear All Mod Rules",
            ["confirm_clear_all"]     = "Clear all mod rules? This cannot be undone.",
            ["confirm_optimize_all"]  = "Apply recommended optimization to all mods?",
            ["btn_confirm"]           = "Confirm",
            ["btn_cancel"]            = "Cancel",
            ["mods_optimized_summary"]= "{0}/{1} mods optimized",
            ["tier_critical"]         = "Critical",
            ["tier_active"]           = "Active",
            ["tier_moderate"]         = "Moderate",
            ["tier_static"]           = "Static",
            ["tier_skip_label"]       = "Skip:",
            ["tier_status"]           = "{0}/{1}",
            ["btn_apply_tier"]        = "Apply",
            ["btn_optimize_mod"]      = "Optimize",
            ["btn_show_detail"]       = "Show Detail \u25bc",
            ["btn_hide_detail"]       = "Hide Detail \u25b2",
            ["btn_remove_rule"]       = "Remove",
            ["mod_not_optimized"]     = "Not optimized",
            ["mod_items_count"]       = "({0} items)",
            ["mod_batch_add"]         = "Batch Add",
            ["mod_no_rule"]           = "No Rule",
            ["mod_rule_summary"]      = "Rule: {0}/{1}",
            ["mod_add_rule"]          = "Add Rule",
            ["rule_added"]            = "Added",
            ["rule_removed"]          = "Removed",
            ["mod_pattern_statushud"] = "StatusHUD",
            ["mod_pattern_affliction"]= "Affliction",
            ["mod_pattern_conditional"]= "Conditional",
            ["mod_pattern_multi_se"]  = "MultiSE",

            // Intensity slider
            ["intensity_label"]       = "Intensity",
            ["intensity_preview"]     = "Preview: Critical\u2192{0} | Active\u2192{1} | Moderate\u2192{2} | Static\u2192{3}",
            ["global_intensity_label"]= "Global Intensity",

            // Proxy system
            ["section_proxy"]         = "── Proxy System ──",
            ["proxy_batch"]           = "BatchCompute",
            ["proxy_sync"]            = "SyncBack",
            ["proxy_physics"]         = "PhysMaint",
            ["proxy_items_count"]     = "Proxy: ~{0:F0} / Total: {1}",

            // Toast
            ["config_saved"]          = "Config saved",
            ["config_reloaded"]       = "Config reloaded",

            // Dispatch overlay
            ["section_threads"]       = "── Thread Distribution ──",
            ["parallel_main"]         = "Main",
            ["dispatch_total"]        = "Total: {0:F1}ms (overhead: {1:F1}ms)",
        };

        private static bool IsChinese
        {
            get
            {
                var lang = GameSettings.CurrentConfig.Language.Value.Value ?? string.Empty;
                return lang.Contains("Chinese");
            }
        }

        public static string T(string key)
        {
            var dict = IsChinese ? ZH : EN;
            return dict.TryGetValue(key, out var value) ? value : key;
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }
    }
}
