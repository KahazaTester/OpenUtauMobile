using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 扫描到的可用语音记录，对应原生 VoiceRecord。
    /// </summary>
    public class TsnVoiceRecord {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public string SourcePath = string.Empty;
        public string Languages = string.Empty;
        public byte VoiceFormat;
    }

    /// <summary>
    /// 语音仓库：递归扫描语音目录，仅收录可唱语音包（format 0/5）。
    /// 托管移植自原生 repository.cpp，ID 与命名规则与官方加载器一致。
    /// </summary>
    public static class TsnVoiceRepository {
        static string MakeBaseId(string path) {
            StringBuilder result = new StringBuilder(
                Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
            for (int i = 0; i < result.Length; i++) {
                char value = result[i];
                bool ok = (value >= 'a' && value <= 'z')
                    || (value >= '0' && value <= '9')
                    || value == '-' || value == '_' || value == '.';
                if (!ok) {
                    result[i] = '-';
                }
            }
            string collapsed = result.ToString();
            while (collapsed.Contains("--")) {
                collapsed = collapsed.Replace("--", "-");
            }
            if (collapsed.Length == 0) {
                collapsed = "voice";
            }
            return collapsed;
        }

        static uint Fnv1a(string text) {
            uint value = 2166136261U;
            foreach (byte b in Encoding.UTF8.GetBytes(text)) {
                value ^= b;
                value *= 16777619U;
            }
            return value;
        }

        static string ChooseName(TsnVoicePackage package, TsnVoiceCatalog catalog) {
            if (catalog != null) {
                TsnVoiceCatalogEntry entry = catalog.FindByFile(package.SourcePath);
                if (entry != null && entry.Name.Length > 0) {
                    return entry.Name;
                }
            }
            foreach (string key in new string[] {
                "VOICE_NAME", "SINGER_NAME", "PRODUCT_NAME", "NAME",
            }) {
                if (package.Config.TryGetValue(key, out string name) && name.Length > 0) {
                    return name;
                }
            }
            return Path.GetFileNameWithoutExtension(package.SourcePath);
        }

        static List<string> EnumerateFiles(List<string> roots,
            List<string> diagnostics, SearchOption searchOption) {
            List<string> result = new List<string>();
            foreach (string root in roots) {
                try {
                    if (File.Exists(root)) {
                        if (string.Equals(Path.GetExtension(root), TsnVoiceSingerType.FileExtension,
                            StringComparison.OrdinalIgnoreCase)) {
                            result.Add(root);
                        }
                        continue;
                    }
                    if (!Directory.Exists(root)) {
                        diagnostics.Add("语音目录不可读：" + root);
                        continue;
                    }
                    foreach (string file in Directory.EnumerateFiles(
                        root, "*" + TsnVoiceSingerType.FileExtension, searchOption)) {
                        result.Add(file);
                    }
                } catch (Exception e) {
                    diagnostics.Add("语音目录扫描不完整（" + root + "）：" + e.Message);
                }
            }
            result.Sort(StringComparer.Ordinal);
            List<string> unique = new List<string>();
            string previous = null;
            foreach (string file in result) {
                if (previous == null || !string.Equals(previous, file, StringComparison.Ordinal)) {
                    unique.Add(file);
                }
                previous = file;
            }
            return unique;
        }

        /// <summary>
        /// 扫描给定目录，返回可用语音与诊断信息。
        /// 无效语音仅记录诊断，不中断整体扫描。
        /// </summary>
        public static void Scan(
            List<string> roots, out List<TsnVoiceRecord> voices,
            out List<string> diagnostics,
            SearchOption searchOption = SearchOption.AllDirectories) {
            voices = new List<TsnVoiceRecord>();
            diagnostics = new List<string>();
            TsnVoiceCatalog catalog = null;
            try {
                catalog = TsnVoiceCatalog.Instance;
            } catch (Exception e) {
                diagnostics.Add("语音目录加载失败，使用包内名称：" + e.Message);
            }
            Dictionary<string, int> occurrences =
                new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in EnumerateFiles(roots, diagnostics, searchOption)) {
                try {
                    TsnVoicePackage package = TsnVoicePackage.Load(path);
                    if (!package.SupportsSingingScore()) {
                        diagnostics.Add("跳过 Talker/TTS 语音包：" + path);
                        continue;
                    }
                    string languages = package.LanguagesCsv();
                    string description = "TsnVoice singing voice; languages: " + languages
                        + "; format: " + package.Header.VoiceFormat;
                    string baseId = MakeBaseId(path);
                    if (!occurrences.TryGetValue(baseId, out int count)) {
                        count = 0;
                    }
                    occurrences[baseId] = count + 1;
                    string id = count == 0
                        ? baseId
                        : baseId + "-" + Fnv1a(path).ToString("x8");
                    TsnVoiceRecord record = new TsnVoiceRecord();
                    record.Id = id;
                    record.Name = ChooseName(package, catalog);
                    record.Description = description;
                    record.SourcePath = Path.GetFullPath(path);
                    record.Languages = languages;
                    record.VoiceFormat = package.Header.VoiceFormat;
                    voices.Add(record);
                } catch (Exception e) {
                    diagnostics.Add("跳过无效语音 '" + path + "'：" + e.Message);
                }
            }
        }
    }
}
