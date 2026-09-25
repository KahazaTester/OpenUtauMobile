using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 语音表情名索引：按安装的 .tsnvoice 文件名匹配 list.json 中
    /// voicefiles 条目的 emotions 字段（分号分隔，与矩阵行顺序对应）。
    /// 二进制内无语音侧名称（名称来自工程 XML），目录是唯一的名称来源；
    /// 仅用于展示，合成仍以矩阵行为准。
    /// </summary>
    public static class TsnVoiceEmotions {
        static readonly object emotionLock = new object();
        static Dictionary<string, string[]> byFile;

        /// <summary>按语音文件路径查找表情名，缺失返回空数组。</summary>
        public static string[] FindEmotions(string sourcePath) {
            if (string.IsNullOrEmpty(sourcePath)) {
                return Array.Empty<string>();
            }
            string fileName;
            try {
                fileName = Path.GetFileName(sourcePath);
            } catch {
                return Array.Empty<string>();
            }
            if (string.IsNullOrEmpty(fileName)) {
                return Array.Empty<string>();
            }
            Dictionary<string, string[]> index = GetIndex();
            if (index.TryGetValue(fileName, out string[] names)) {
                return names;
            }
            return Array.Empty<string>();
        }

        static Dictionary<string, string[]> GetIndex() {
            lock (emotionLock) {
                if (byFile == null) {
                    byFile = LoadIndex();
                }
                return byFile;
            }
        }

        static Dictionary<string, string[]> LoadIndex() {
            Dictionary<string, string[]> result =
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            byte[] data;
            try {
                data = TsnVoiceDictionaries.GetVoiceBytes("list.json");
            } catch (Exception e) {
                Log.Warning(e, "读取 TsnVoice 表情目录失败，表情 lanes 使用序号命名");
                return result;
            }
            try {
                using (JsonDocument document = JsonDocument.Parse(data)) {
                    if (document.RootElement.ValueKind != JsonValueKind.Array) {
                        return result;
                    }
                    foreach (JsonElement entry in document.RootElement.EnumerateArray()) {
                        if (!entry.TryGetProperty("voices", out JsonElement voices)
                            || voices.ValueKind != JsonValueKind.Object) {
                            continue;
                        }
                        if (!voices.TryGetProperty("voicefiles", out JsonElement files)
                            || files.ValueKind != JsonValueKind.Array) {
                            continue;
                        }
                        foreach (JsonElement file in files.EnumerateArray()) {
                            string name = GetString(file, "name");
                            if (name.Length == 0) {
                                continue;
                            }
                            List<string> names = new List<string>();
                            foreach (string item in GetString(file, "emotions").Split(';')) {
                                string trimmed = item.Trim();
                                if (trimmed.Length > 0) {
                                    names.Add(trimmed);
                                }
                            }
                            if (names.Count == 0) {
                                continue;
                            }
                            if (result.TryGetValue(name, out string[] existing)) {
                                if (!SameNames(existing, names.ToArray())) {
                                    Log.Warning(
                                        "语音文件 {File} 在目录中有多组不同表情名，采用首组",
                                        name);
                                }
                                continue;
                            }
                            result[name] = names.ToArray();
                        }
                    }
                }
            } catch (Exception e) {
                Log.Warning(e, "解析 TsnVoice 表情目录失败，表情 lanes 使用序号命名");
                result.Clear();
            }
            Log.Information("TsnVoice 表情目录：{Count} 个语音文件", result.Count);
            return result;
        }

        static bool SameNames(string[] left, string[] right) {
            if (left.Length != right.Length) {
                return false;
            }
            for (int i = 0; i < left.Length; i++) {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) {
                    return false;
                }
            }
            return true;
        }

        static string GetString(JsonElement element, string name) {
            if (element.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String) {
                return value.GetString() ?? string.Empty;
            }
            return string.Empty;
        }
    }
}
