using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 安装附带元数据：标识具体是哪个变体（label + md5），
    /// 同一规范版本的不同变体（如 2.0.0 与 2.0.0 Append）共享安装路径，
    /// 靠附带文件区分实际安装的是哪一个。
    /// </summary>
    public class TsnVoiceInstallMetadata {
        public string VoiceId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
    }

    /// <summary>
    /// TsnVoice 语音下载与安装，对应参考实现语音管理器的下载流程：
    /// 校验下载、MD5 校验、按版本目录安装、卸载。
    /// 安装布局 {voiceRoot}/{voiceId}/{version}/{fileName}，与参考实现的文件布局互通。
    /// </summary>
    public class TsnVoiceVoiceInstaller {
        public TsnVoiceVoiceInstaller(string voiceRoot = null) {
            VoiceRoot = Path.GetFullPath(
                voiceRoot ?? Path.Combine(PathManager.Inst.SingersPath, ".tsnvoice"));
        }

        public string VoiceRoot { get; }

        static string MetadataPath(string packagePath) {
            return packagePath + ".install.json";
        }

        static TsnVoiceInstallMetadata ReadMetadata(string packagePath) {
            string metadataPath = MetadataPath(packagePath);
            if (!File.Exists(metadataPath)) {
                return null;
            }
            try {
                string json = File.ReadAllText(metadataPath, Encoding.UTF8);
                using (JsonDocument document = JsonDocument.Parse(json)) {
                    JsonElement root = document.RootElement;
                    TsnVoiceInstallMetadata metadata = new TsnVoiceInstallMetadata();
                    metadata.VoiceId = GetField(root, "VoiceId");
                    metadata.Version = GetField(root, "Version");
                    metadata.Label = GetField(root, "Label");
                    metadata.FileName = GetField(root, "FileName");
                    metadata.Md5 = GetField(root, "Md5");
                    return metadata;
                }
            } catch {
                return null;
            }
        }

        static string GetField(JsonElement root, string name) {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String) {
                return value.GetString() ?? string.Empty;
            }
            return string.Empty;
        }

        static string EscapeJson(string value) {
            StringBuilder builder = new StringBuilder(value.Length + 2);
            foreach (char c in value) {
                switch (c) {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 0x20) {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4"));
                        } else {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        void WriteMetadata(TsnVoiceCatalog catalog, TsnVoiceCatalogEntry voice,
            TsnVoiceCatalogVersion version) {
            // 手工拼装 JSON，避免反射序列化在裁剪/AOT 下不可用。
            string json = "{\"VoiceId\":\"" + EscapeJson(voice.Id)
                + "\",\"Version\":\"" + EscapeJson(version.Version)
                + "\",\"Label\":\"" + EscapeJson(version.Label)
                + "\",\"FileName\":\"" + EscapeJson(version.FileName)
                + "\",\"Md5\":\"" + EscapeJson(version.Md5) + "\"}";
            string metadataPath = MetadataPath(
                catalog.GetInstallPath(voice, version, VoiceRoot));
            string temporary = metadataPath + ".tmp";
            File.WriteAllText(temporary, json, Encoding.UTF8);
            File.Move(temporary, metadataPath, true);
        }

        /// <summary>
        /// 是否安装了指定变体：文件存在且附带元数据的 md5 一致；
        /// 无附带文件的旧安装按文件存在判定（宽松兼容）。
        /// </summary>
        public bool IsInstalled(TsnVoiceCatalog catalog,
            TsnVoiceCatalogEntry voice, TsnVoiceCatalogVersion version) {
            string packagePath = catalog.GetInstallPath(voice, version, VoiceRoot);
            if (!File.Exists(packagePath)) {
                return false;
            }
            TsnVoiceInstallMetadata metadata = ReadMetadata(packagePath);
            if (metadata == null) {
                return true;
            }
            return string.Equals(metadata.Md5, version.Md5,
                StringComparison.OrdinalIgnoreCase);
        }

        public List<string> InstalledVersions(TsnVoiceCatalog catalog,
            TsnVoiceCatalogEntry voice) {
            List<string> result = new List<string>();
            foreach (TsnVoiceCatalogVersion version in voice.Versions) {
                if (IsInstalled(catalog, voice, version)
                    && !result.Contains(version.Version)) {
                    result.Add(version.Version);
                }
            }
            return result;
        }

        /// <summary>
        /// 已安装变体的展示名（label），按目录顺序去重。
        /// </summary>
        public List<string> InstalledLabels(TsnVoiceCatalog catalog,
            TsnVoiceCatalogEntry voice) {
            List<string> result = new List<string>();
            foreach (TsnVoiceCatalogVersion version in voice.Versions) {
                if (IsInstalled(catalog, voice, version)
                    && !result.Contains(version.Label)) {
                    result.Add(version.Label);
                }
            }
            return result;
        }

        /// <summary>
        /// 下载指定版本并校验 MD5，完成后通知歌手列表刷新。
        /// </summary>
        public async Task<string> DownloadAsync(TsnVoiceCatalog catalog,
            TsnVoiceCatalogEntry voice, TsnVoiceCatalogVersion version,
            IProgress<double> progress, CancellationToken cancellationToken) {
            string destination = catalog.GetInstallPath(voice, version, VoiceRoot);
            string directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(directory)) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError,
                    "语音目标目录不可用");
            }
            Directory.CreateDirectory(directory);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            Uri downloadUri = catalog.GetDownloadUri(voice, version);
            Log.Information("TsnVoice 下载开始：{Voice} {Version} {Url}",
                voice.Id, version.Label, downloadUri);
            try {
                long downloaded = 0;
                long? contentLength = null;
                string actualMd5;
                using (HttpClient client = CreateHttpClient()) {
                    using (HttpResponseMessage response = await client.GetAsync(
                        downloadUri, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false)) {
                        if (!response.IsSuccessStatusCode) {
                            throw new HttpRequestException(
                                "下载 " + downloadUri + " 失败：HTTP "
                                + (int)response.StatusCode + " "
                                + (response.ReasonPhrase ?? string.Empty));
                        }
                        contentLength = response.Content.Headers.ContentLength;
                        using (Stream source = await response.Content
                            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)) {
                            using (IncrementalHash hash =
                                IncrementalHash.CreateHash(HashAlgorithmName.MD5)) {
                                byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                                try {
                                    using (FileStream target = new FileStream(temporary,
                                        FileMode.Create, FileAccess.Write, FileShare.None,
                                        128 * 1024, true)) {
                                        while (true) {
                                            int count = await source.ReadAsync(
                                                buffer, 0, buffer.Length,
                                                cancellationToken).ConfigureAwait(false);
                                            if (count == 0) {
                                                break;
                                            }
                                            await target.WriteAsync(buffer, 0, count,
                                                cancellationToken).ConfigureAwait(false);
                                            hash.AppendData(buffer, 0, count);
                                            downloaded += count;
                                            if (contentLength.HasValue
                                                && contentLength.Value > 0) {
                                                progress?.Report(Math.Clamp(
                                                    (double)downloaded / contentLength.Value,
                                                    0.0, 1.0));
                                            }
                                        }
                                        await target.FlushAsync(cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                } finally {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                }
                                actualMd5 = BitConverter.ToString(hash.GetHashAndReset())
                                    .Replace("-", string.Empty).ToLowerInvariant();
                            }
                        }
                    }
                }
                if (!string.Equals(actualMd5, version.Md5,
                    StringComparison.OrdinalIgnoreCase)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "下载校验失败：期望 " + version.Md5 + "，实际 " + actualMd5);
                }
                File.Move(temporary, destination, true);
                WriteMetadata(catalog, voice, version);
                progress?.Report(1.0);
                Log.Information("TsnVoice 下载完成：{Voice} {Version} {Bytes} 字节",
                    voice.Id, version.Label, downloaded);
                DocManager.Inst.ExecuteCmd(new SingersChangedNotification());
                return destination;
            } catch (Exception e) when (!(e is TsnVoiceException)) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError,
                    "下载失败：" + e.Message, e);
            } finally {
                try {
                    File.Delete(temporary);
                } catch {
                }
            }
        }

        /// <summary>
        /// 卸载指定版本的语音包，并清理空目录。
        /// </summary>
        public void Remove(TsnVoiceCatalog catalog,
            TsnVoiceCatalogEntry voice, TsnVoiceCatalogVersion version) {
            string packagePath = catalog.GetInstallPath(voice, version, VoiceRoot);
            try {
                File.Delete(packagePath);
                File.Delete(MetadataPath(packagePath));
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError,
                    "卸载失败：" + e.Message, e);
            }
            try {
                string directory = Path.GetDirectoryName(packagePath);
                while (!string.IsNullOrEmpty(directory)
                    && directory.StartsWith(
                        Path.GetFullPath(VoiceRoot), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any()) {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            } catch {
            }
            DocManager.Inst.ExecuteCmd(new SingersChangedNotification());
        }

        static HttpClient CreateHttpClient() {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenUtauMobile-TsnVoice/1.0");
            return client;
        }
    }
}
