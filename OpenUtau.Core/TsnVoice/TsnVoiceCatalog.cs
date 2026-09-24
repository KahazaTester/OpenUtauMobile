using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 语音目录条目（catalog.json）。
    /// </summary>
    public class TsnVoiceCatalogEntry {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Language = string.Empty;
        public string Portrait = string.Empty;
        public string Background = string.Empty;
        public List<TsnVoiceCatalogVersion> Versions = new List<TsnVoiceCatalogVersion>();
    }

    /// <summary>
    /// 目录版本条目：version 为规范化的 X.Y.Z（排序与兼容用），
    /// label 为原始版本字符串（展示用）。
    /// </summary>
    public class TsnVoiceCatalogVersion {
        public string Version = string.Empty;
        public string Label = string.Empty;
        public string FileName = string.Empty;
        public string Md5 = string.Empty;

        public override string ToString() {
            return string.IsNullOrEmpty(Label) || Label == Version ? Version : Label;
        }
    }

    /// <summary>
    /// 内嵌语音目录：官方名称、语言与立绘查询。
    /// 与参考实现的目录匹配规则一致：
    /// 先按文件名（去扩展名）匹配，再按所在目录名向上两级匹配。
    /// </summary>
    public class TsnVoiceCatalog {
        readonly Dictionary<string, TsnVoiceCatalogEntry> byId =
            new Dictionary<string, TsnVoiceCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        static readonly object catalogLock = new object();
        static TsnVoiceCatalog instance;

        public static TsnVoiceCatalog Instance {
            get {
                lock (catalogLock) {
                    if (instance == null) {
                        instance = Load();
                    }
                    return instance;
                }
            }
        }

        public int Count => byId.Count;

        public List<TsnVoiceCatalogEntry> Voices {
            get {
                return new List<TsnVoiceCatalogEntry>(byId.Values);
            }
        }

        public string DownloadBaseUrl { get; private set; } = string.Empty;

        /// <summary>
        /// 构造下载地址：{baseUrl}{voiceId}/{version}/{fileName}。
        /// </summary>
        public Uri GetDownloadUri(TsnVoiceCatalogEntry voice, TsnVoiceCatalogVersion version) {
            string baseUrl = DownloadBaseUrl;
            if (!baseUrl.EndsWith("/")) {
                baseUrl += "/";
            }
            return new Uri(baseUrl + voice.Id + "/" + version.Version + "/"
                + version.FileName, UriKind.Absolute);
        }

        /// <summary>
        /// 本地安装路径：{voiceRoot}/{voiceId}/{version}/{fileName}。
        /// 版本目录隔离保证各版本互不覆盖。
        /// </summary>
        public string GetInstallPath(TsnVoiceCatalogEntry voice,
            TsnVoiceCatalogVersion version, string voiceRoot) {
            return Path.Combine(voiceRoot, voice.Id, version.Version, version.FileName);
        }

        public TsnVoiceCatalogEntry FindById(string id) {
            if (id != null && byId.TryGetValue(id, out TsnVoiceCatalogEntry entry)) {
                return entry;
            }
            return null;
        }

        /// <summary>
        /// 按语音文件路径匹配目录条目。
        /// </summary>
        public TsnVoiceCatalogEntry FindByFile(string sourcePath) {
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            TsnVoiceCatalogEntry entry = FindById(stem);
            if (entry != null) {
                return entry;
            }
            try {
                DirectoryInfo directory = new FileInfo(sourcePath).Directory;
                for (int depth = 0; depth < 2 && directory != null; depth++) {
                    entry = FindById(directory.Name);
                    if (entry != null) {
                        return entry;
                    }
                    directory = directory.Parent;
                }
            } catch {
            }
            return null;
        }

        /// <summary>
        /// 读取立绘字节（内嵌资源或 DataPath 覆盖）。
        /// </summary>
        public byte[] GetPortraitBytes(TsnVoiceCatalogEntry entry) {
            if (entry == null || string.IsNullOrEmpty(entry.Portrait)) {
                return null;
            }
            string relative = entry.Portrait.Replace('\\', '/').Trim('/');
            if (relative.Length == 0 || relative.Contains("..")) {
                return null;
            }
            try {
                return TsnVoiceDictionaries.GetVoiceBytes(relative.Split('/'));
            } catch {
                return null;
            }
        }

        static TsnVoiceCatalog Load() {
            TsnVoiceCatalog catalog = new TsnVoiceCatalog();
            string json;
            try {
                json = TsnVoiceDictionaries.GetVoiceText("catalog.json");
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError,
                    "无法读取内嵌语音目录", e);
            }
            try {
                using (JsonDocument document = JsonDocument.Parse(json)) {
                    JsonElement root = document.RootElement;
                    catalog.DownloadBaseUrl = GetString(root, "downloadBaseUrl");
                    if (!root.TryGetProperty("voices", out JsonElement voices)
                        || voices.ValueKind != JsonValueKind.Array) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "语音目录缺少 voices");
                    }
                    foreach (JsonElement voice in voices.EnumerateArray()) {
                        TsnVoiceCatalogEntry entry = new TsnVoiceCatalogEntry();
                        entry.Id = GetString(voice, "id");
                        entry.Name = GetString(voice, "name");
                        entry.Language = GetString(voice, "language");
                        entry.Portrait = GetString(voice, "portrait");
                        entry.Background = GetString(voice, "background");
                        if (entry.Id.Length == 0 || entry.Name.Length == 0) {
                            continue;
                        }
                        if (voice.TryGetProperty("versions", out JsonElement versions)
                            && versions.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement version in versions.EnumerateArray()) {
                                TsnVoiceCatalogVersion item = new TsnVoiceCatalogVersion();
                                item.Version = GetString(version, "version");
                                item.Label = GetString(version, "label");
                                item.FileName = GetString(version, "fileName");
                                item.Md5 = GetString(version, "md5").ToLowerInvariant();
                                if (item.Version.Length == 0 || item.FileName.Length == 0
                                    || item.Md5.Length != 32) {
                                    continue;
                                }
                                if (item.Label.Length == 0) {
                                    item.Label = item.Version;
                                }
                                entry.Versions.Add(item);
                            }
                        }
                        catalog.byId[entry.Id] = entry;
                    }
                }
            } catch (TsnVoiceException) {
                throw;
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音目录解析失败", e);
            }
            return catalog;
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
