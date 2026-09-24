using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
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

        public bool IsInstalled(TsnVoiceCatalog catalog,
            TsnVoiceCatalogEntry voice, TsnVoiceCatalogVersion version) {
            return File.Exists(catalog.GetInstallPath(voice, version, VoiceRoot));
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
