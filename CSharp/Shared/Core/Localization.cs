using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Barotrauma;

namespace ItemOptimizerMod
{
    static class Localization
    {
        private static Dictionary<string, string> _current;
        private static Dictionary<string, string> _fallback;

        private static readonly Dictionary<string, string> LangMap = new()
        {
            ["English"]              = "en",
            ["Simplified Chinese"]   = "zh-CN",
            ["Traditional Chinese"]  = "zh-TW",
            ["Japanese"]             = "ja",
            ["Russian"]              = "ru",
            ["German"]               = "de",
            ["Korean"]               = "ko",
            ["French"]               = "fr",
            ["Castilian Spanish"]    = "es",
            ["Latinamerican Spanish"]= "es",
            ["Brazilian Portuguese"] = "pt-BR",
            ["Polish"]               = "pl",
            ["Turkish"]              = "tr",
        };

        /// <summary>
        /// Load language files. Call once at startup, before any UI is built.
        /// </summary>
        public static void Init()
        {
            _fallback = LoadFile("en");

            string lang;
            try
            {
                lang = GameSettings.CurrentConfig.Language.Value.Value ?? "English";
            }
            catch
            {
                lang = "English";
            }

            string code = LangMap.TryGetValue(lang, out var c) ? c : "en";
            if (code == "en")
            {
                _current = _fallback;
            }
            else
            {
                _current = LoadFile(code) ?? _fallback;
            }

            int count = _current?.Count ?? 0;
            LuaCsLogger.Log($"[ItemOptimizer] Localization loaded: lang={lang} code={code} keys={count}");
        }

        public static string T(string key)
        {
            if (_current != null && _current.TryGetValue(key, out var v)) return v;
            if (_fallback != null && _fallback.TryGetValue(key, out var f)) return f;
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        private static Dictionary<string, string> LoadFile(string code)
        {
            try
            {
                string path = ModPaths.ResolveInSubDir("Localization", $"{code}.json");
                if (!File.Exists(path))
                {
                    LuaCsLogger.LogError($"[ItemOptimizer] Language file not found: {path}");
                    return null;
                }

                string json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return dict;
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError($"[ItemOptimizer] Failed to load language file '{code}': {e.Message}");
                return null;
            }
        }
    }
}
