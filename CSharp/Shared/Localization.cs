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
            ["section_strategies"]    = "── 物品更新策略 ──",
            ["section_item_rules"]    = "── 逐物品规则 ──",
            ["section_stats"]         = "── 实时统计 ──",

            // Strategies
            ["strategy_cold_storage"] = "冷存储跳过",
            ["strategy_ground_item"]  = "地面物品节流",
            ["strategy_ci_throttle"]  = "自定义接口节流",
            ["strategy_motion"]       = "运动传感器节流",
            ["strategy_wearable"]     = "可穿戴节流",
            ["strategy_water_det"]    = "水探测器节流",
            ["strategy_door"]         = "门节流",
            ["strategy_hst_cache"]    = "HasStatusTag缓存",
            ["strategy_statushud"]    = "StatusHUD节流",
            ["strategy_affliction"]   = "Affliction去重",

            ["strategy_cold_storage_desc"] = "跳过储物柜/容器中物品的更新",
            ["strategy_ground_item_desc"]  = "降低地面散落物品的更新频率（不影响物理/水流）",
            ["strategy_ci_throttle_desc"]  = "跳过未手持武器的CustomInterface更新",
            ["strategy_motion_desc"]       = "降低运动传感器的扫描频率",
            ["strategy_wearable_desc"]     = "降低非本地角色穿戴物的更新频率",
            ["strategy_water_det_desc"]    = "降低水探测器的信号发送频率",
            ["strategy_door_desc"]         = "静止状态的门（全开/全关）降低更新频率。多人游戏中跳帧过高可能导致客户端与服务器不同步（门状态延迟→人物回弹），建议多人游戏设为2或关闭",
            ["strategy_door_desync"]       = "防回弹保护（服务端+客户端）",
            ["strategy_door_desync_desc"]  = "检测门状态变更并立即同步物理碰撞体，防止多人游戏中因节流导致的回弹。服务端和客户端均需开启",
            ["strategy_hst_cache_desc"]    = "缓存HasStatusTag条件检查结果（每帧重建），避免重复扫描DurationList",
            ["strategy_statushud_desc"]    = "降低StatusHUD扫描频率并节流热成像绘制",
            ["strategy_affliction_desc"]   = "跳过已达上限的buff型Affliction重复施加",

            ["skip_frames_label"]     = "跳帧",

            // Item rules
            ["rule_add"]              = "+ 添加规则",
            ["rule_remove"]           = "×",
            ["rule_identifier"]       = "物品标识符",
            ["action_skip"]           = "跳过更新",
            ["action_throttle"]       = "降频",
            ["cond_always"]           = "始终",
            ["cond_cold_storage"]     = "冷存储中",
            ["cond_not_active_use"]   = "未主动使用",

            // Overlay
            ["overlay_toggle"]        = "HUD 统计叠加层",
            ["overlay_waiting"]       = "等待性能数据...",

            // Stats
            ["stats_format"]          = "{0}: ~{1:F0}/帧",
            ["stats_saved"]           = "预计节省: ~{0:F2}ms/帧",
            ["stats_item_rules"]      = "逐物品规则",
            ["stats_mod_opt"]         = "Mod优化",
            ["stats_ground_item"]     = "地面物品",
            ["stats_water_det"]       = "水探测器",
            ["stats_door"]            = "门",
            ["stats_hst_cache"]       = "HST缓存命中",
            ["stats_statushud"]       = "StatusHUD",
            ["stats_affliction"]      = "Affliction去重",

            // Character optimization
            ["section_character_opt"]      = "── 角色优化 ──",
            ["strategy_anim_lod"]          = "动画LOD",
            ["strategy_anim_lod_desc"]     = "简化物理角色跳过动画计算（不可见），远距离角色降半频",
            ["strategy_char_stagger"]      = "AI交错更新",
            ["strategy_char_stagger_desc"] = "怪物AI分组轮流更新，摊平CPU负载。分组数越多越平但AI反应越慢",
            ["stagger_groups_label"]       = "分组",
            ["strategy_ladder_fix"]        = "梯子反同步修复",
            ["strategy_ladder_fix_desc"]   = "修复原版梯子网络反同步bug：服务器和客户端攀爬状态不一致导致的橡皮筋效应。自动检测并修正攀爬动画状态，连续拉回时强制接受服务器判定",
            ["strategy_platform_fix"]      = "平台穿越反同步修复",
            ["strategy_platform_fix_desc"] = "修复原版平台穿越网络反同步：服务器与客户端IgnorePlatforms状态不一致导致在楼梯/平台上按S键时的回弹",
            ["stats_ladder_fix"]           = "梯子修正",
            ["stats_platform_fix"]         = "平台修正",
            ["stats_anim_lod"]             = "动画LOD",
            ["stats_char_stagger"]         = "AI交错",

            // Mod list
            ["section_mod_list"]      = "── Mod 列表 ──",
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
            ["tier_skip_label"]       = "跳帧:",
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

            // Toast
            ["config_saved"]          = "配置已保存",
            ["config_reloaded"]       = "配置已重载",

            // Parallel dispatch
            ["strategy_misc_parallel"]     = "Misc实体并行",
            ["strategy_misc_parallel_desc"]= "Hull/Structure/Gap/Power更新的4路并行执行(EP风格)。安全、无副作用",
            ["strategy_parallel"]     = "并行物品更新",
            ["strategy_parallel_desc"]= "将安全物品分配到工作线程并行执行Update，减少主线程负载。实验性功能",
            ["parallel_workers_label"]= "线程数",
            ["section_threads"]       = "── 线程分布 ──",
            ["parallel_main"]         = "主线程",
            ["parallel_worker"]       = "工作线程",
            ["parallel_saved"]        = "并行节省: ~{0:F1}ms",
            ["parallel_items"]        = "并行: ~{0:F0}  主线程: ~{1:F0}",

            // Server optimization
            ["section_server"]            = "服务器",
            ["section_network_sync"]      = "── 网络同步修复 ──",
            ["section_parallel"]          = "── 并行处理 ──",
            ["strategy_server_dedup"]     = "HashSet去重",
            ["strategy_server_dedup_desc"]= "用HashSet替代Queue.Contains的O(n)检查，优化位置更新入队",
            ["server_good"]               = "良好",
            ["server_warning"]            = "警告",
            ["server_critical"]           = "危险",
            ["server_clients_entities"]   = "  客户端: {0}  实体: {1}",
            ["server_queues"]             = "  位置队列: {0:F1}  事件队列: {1:F1}",
            ["server_skipped"]            = "  跳过物品: {0}",
            ["server_metric_interval"]    = "指标发送间隔",

            // Thread safety analysis
            ["section_thread_safety"]     = "── 线程安全分析 ──",
            ["btn_run_scan"]              = "运行预扫描",
            ["btn_rescan"]                = "重新扫描",
            ["scan_status_valid"]         = "缓存有效 (hash {0})",
            ["scan_status_none"]          = "未扫描 — 请运行预扫描以启用",
            ["scan_summary"]              = "安全: {0} | 条件: {1} | 不安全: {2}",
            ["scan_running"]              = "正在扫描...",
            ["scan_complete"]             = "扫描完成",
            ["safety_safe"]               = "安全",
            ["safety_conditional"]        = "条件",
            ["safety_unsafe"]             = "不安全",
            ["override_auto"]             = "[自动]",
            ["override_force_safe"]       = "[强制安全]",
            ["override_force_unsafe"]     = "[强制不安全]",
            ["reason_dangerous_component"]= "危险组件",
            ["reason_connection_panel"]   = "连接面板",
            ["reason_physics_body"]       = "物理体",
            ["reason_se_duration"]        = "SE持续时间",
            ["reason_se_interval"]        = "SE间隔",
            ["reason_se_affliction"]      = "SE伤害",
            ["reason_se_spawn"]           = "SE生成",
            ["reason_se_fire"]            = "SE火焰",
            ["reason_se_explosion"]       = "SE爆炸",
            ["reason_se_remove_item"]     = "SE移除物品",
            ["reason_se_external_target"] = "SE外部目标",
            ["reason_se_give_skill"]      = "SE技能",
            ["reason_se_give_talent"]     = "SE天赋",
            ["reason_se_drop_item"]       = "SE丢弃物品",
            ["reason_se_trigger_event"]   = "SE触发事件",
            ["reason_se_use_item"]        = "SE使用物品",
            ["reason_manual_override"]    = "手动覆盖",
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
            ["section_strategies"]    = "── Item Update Strategies ──",
            ["section_item_rules"]    = "── Per-Item Rules ──",
            ["section_stats"]         = "── Live Stats ──",

            // Strategies
            ["strategy_cold_storage"] = "Cold Storage Skip",
            ["strategy_ground_item"]  = "Ground Item Throttle",
            ["strategy_ci_throttle"]  = "CustomInterface Throttle",
            ["strategy_motion"]       = "MotionSensor Throttle",
            ["strategy_wearable"]     = "Wearable Throttle",
            ["strategy_water_det"]    = "WaterDetector Throttle",
            ["strategy_door"]         = "Door Throttle",
            ["strategy_hst_cache"]    = "HasStatusTag Cache",
            ["strategy_statushud"]    = "StatusHUD Throttle",
            ["strategy_affliction"]   = "Affliction Dedup",

            ["strategy_cold_storage_desc"] = "Skip update for items in lockers/containers",
            ["strategy_ground_item_desc"]  = "Throttle updates for items lying on the ground (physics/water unaffected)",
            ["strategy_ci_throttle_desc"]  = "Skip CI update for weapons not held in hand",
            ["strategy_motion_desc"]       = "Reduce motion sensor scan frequency",
            ["strategy_wearable_desc"]     = "Reduce wearable update for non-local characters",
            ["strategy_water_det_desc"]    = "Reduce water detector signal sending frequency",
            ["strategy_door_desc"]         = "Reduce update frequency for idle doors (fully open/closed). High skip frames in multiplayer may cause client-server desync (door state delay, rubber-banding). Set to 2 or disable in multiplayer.",
            ["strategy_door_desync"]       = "Anti-Desync Guard (Server+Client)",
            ["strategy_door_desync_desc"]  = "Detect door state changes and immediately sync physics body. Prevents rubber-banding from throttle desync in multiplayer. Must be enabled on both server and client.",
            ["strategy_hst_cache_desc"]    = "Cache HasStatusTag conditional results per frame, avoiding DurationList scans",
            ["strategy_statushud_desc"]    = "Reduce StatusHUD scan frequency and throttle thermal overlay drawing",
            ["strategy_affliction_desc"]   = "Skip redundant buff affliction application when already at max strength",

            ["skip_frames_label"]     = "Skip",

            // Item rules
            ["rule_add"]              = "+ Add Rule",
            ["rule_remove"]           = "x",
            ["rule_identifier"]       = "Item Identifier",
            ["action_skip"]           = "Skip Update",
            ["action_throttle"]       = "Throttle",
            ["cond_always"]           = "Always",
            ["cond_cold_storage"]     = "Cold Storage",
            ["cond_not_active_use"]   = "Not Active Use",

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
            ["stats_hst_cache"]       = "HST Cache Hits",
            ["stats_statushud"]       = "StatusHUD",
            ["stats_affliction"]      = "Affliction Dedup",

            // Character optimization
            ["section_character_opt"]      = "── Character Optimization ──",
            ["strategy_anim_lod"]          = "Animation LOD",
            ["strategy_anim_lod_desc"]     = "Skip animation for invisible (simple physics) characters, half-rate for distant ones",
            ["strategy_char_stagger"]      = "AI Stagger Update",
            ["strategy_char_stagger_desc"] = "Spread enemy AI updates across frames. More groups = smoother load but slower AI reactions",
            ["stagger_groups_label"]       = "Groups",
            ["strategy_ladder_fix"]        = "Ladder Desync Fix",
            ["strategy_ladder_fix_desc"]   = "Fix vanilla ladder networking desync: corrects climbing animation state mismatch between server and client. Detects rubber-banding loops and force-accepts server state",
            ["strategy_platform_fix"]      = "Platform Desync Fix",
            ["strategy_platform_fix_desc"] = "Fix vanilla platform drop-through desync: syncs IgnorePlatforms from server state, preventing rubber-banding when pressing S on stairs/platforms",
            ["stats_ladder_fix"]           = "Ladder Fix",
            ["stats_platform_fix"]         = "Platform Fix",
            ["stats_anim_lod"]             = "Anim LOD",
            ["stats_char_stagger"]         = "AI Stagger",

            // Mod list
            ["section_mod_list"]      = "── Mod List ──",
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

            // Toast
            ["config_saved"]          = "Config saved",
            ["config_reloaded"]       = "Config reloaded",

            // Parallel dispatch
            ["strategy_misc_parallel"]     = "Misc Entity Parallel",
            ["strategy_misc_parallel_desc"]= "4-way parallel execution for Hull/Structure/Gap/Power updates (EP-style). Safe, no side effects.",
            ["strategy_parallel"]     = "Parallel Item Update",
            ["strategy_parallel_desc"]= "Dispatch safe items to worker threads for parallel Update execution. Experimental.",
            ["parallel_workers_label"]= "Workers",
            ["section_threads"]       = "── Thread Distribution ──",
            ["parallel_main"]         = "Main",
            ["parallel_worker"]       = "Worker",
            ["parallel_saved"]        = "Parallel saved: ~{0:F1}ms",
            ["parallel_items"]        = "Parallel: ~{0:F0}  Main: ~{1:F0}",

            // Server optimization
            ["section_server"]            = "Server",
            ["section_network_sync"]      = "── Network Sync Fixes ──",
            ["section_parallel"]          = "── Parallel Processing ──",
            ["strategy_server_dedup"]     = "HashSet Dedup",
            ["strategy_server_dedup_desc"]= "Replace Queue.Contains O(n) with HashSet O(1) for position update enqueue",
            ["server_good"]               = "Good",
            ["server_warning"]            = "Warning",
            ["server_critical"]           = "Critical",
            ["server_clients_entities"]   = "  Clients: {0}  Entities: {1}",
            ["server_queues"]             = "  Pos Queue: {0:F1}  Event Queue: {1:F1}",
            ["server_skipped"]            = "  Skipped Items: {0}",
            ["server_metric_interval"]    = "Metric Interval",

            // Thread safety analysis
            ["section_thread_safety"]     = "── Thread Safety Analysis ──",
            ["btn_run_scan"]              = "Run Pre-Scan",
            ["btn_rescan"]                = "Re-scan",
            ["scan_status_valid"]         = "Cache valid (hash {0})",
            ["scan_status_none"]          = "No scan — run pre-scan to enable",
            ["scan_summary"]              = "Safe: {0} | Conditional: {1} | Unsafe: {2}",
            ["scan_running"]              = "Scanning...",
            ["scan_complete"]             = "Scan complete",
            ["safety_safe"]               = "Safe",
            ["safety_conditional"]        = "Cond",
            ["safety_unsafe"]             = "Unsafe",
            ["override_auto"]             = "[Auto]",
            ["override_force_safe"]       = "[F:Safe]",
            ["override_force_unsafe"]     = "[F:Unsafe]",
            ["reason_dangerous_component"]= "DangerousComponent",
            ["reason_connection_panel"]   = "ConnectionPanel",
            ["reason_physics_body"]       = "PhysicsBody",
            ["reason_se_duration"]        = "SE:Duration",
            ["reason_se_interval"]        = "SE:Interval",
            ["reason_se_affliction"]      = "SE:Affliction",
            ["reason_se_spawn"]           = "SE:Spawn",
            ["reason_se_fire"]            = "SE:Fire",
            ["reason_se_explosion"]       = "SE:Explosion",
            ["reason_se_remove_item"]     = "SE:RemoveItem",
            ["reason_se_external_target"] = "SE:ExternalTarget",
            ["reason_se_give_skill"]      = "SE:GiveSkill",
            ["reason_se_give_talent"]     = "SE:GiveTalent",
            ["reason_se_drop_item"]       = "SE:DropItem",
            ["reason_se_trigger_event"]   = "SE:TriggerEvent",
            ["reason_se_use_item"]        = "SE:UseItem",
            ["reason_manual_override"]    = "ManualOverride",
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
