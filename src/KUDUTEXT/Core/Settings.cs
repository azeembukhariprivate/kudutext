using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Kudutext.Core
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        private const string KeyPath = @"Software\KUDUTEXT";

        private static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(name) as string ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ?? string.Empty);
                }
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

        private static int GetInt(string name, int fallback) => int.TryParse(Get(name, fallback.ToString()), out var v) ? v : fallback;

        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        /// <summary>The speech model: "fast", "good" or "best".</summary>
        public static string Model
        {
            get => Get("Model", SpeechModels.Default.Id);
            set => Set("Model", value);
        }

        /// <summary>The language spoken: "auto" (found by itself) or a code such as "en", "ml", "hi".</summary>
        public static string Language
        {
            get => Get("Language", "auto");
            set => Set("Language", value);
        }

        /// <summary>Turn the speech into English text instead of writing it in its own language.</summary>
        public static bool Translate
        {
            get => GetBool("Translate", false);
            set => SetBool("Translate", value);
        }

        /// <summary>What to save: any of "srt", "vtt", "txt", comma-separated.</summary>
        public static List<string> Formats
        {
            get
            {
                var list = Get("Formats", "srt,txt").Split(',').Select(f => f.Trim().ToLowerInvariant()).Where(SubtitleFiles.Known.Contains).Distinct().ToList();
                return list.Count == 0 ? new List<string> { "srt" } : list;
            }
            set => Set("Formats", string.Join(",", value));
        }

        /// <summary>Where the text files go: empty means next to the video or recording.</summary>
        public static string OutputFolder
        {
            get => Get("OutputFolder", string.Empty);
            set => Set("OutputFolder", value);
        }
    }
}
