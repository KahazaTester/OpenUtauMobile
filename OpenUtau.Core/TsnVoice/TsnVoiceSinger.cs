using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 歌手：单个 .tsnvoice 文件即一个歌手。
    /// 模型仅在渲染时加载，平时只保留包头信息，适配移动端内存。
    /// </summary>
    public class TsnVoiceSinger : USinger {
        readonly string filePath;
        readonly TsnVoiceRecord record;
        readonly List<string> errors = new List<string>();
        readonly List<USubbank> subbanks = new List<USubbank>();
        byte[] avatarData;

        public TsnVoiceSinger(TsnVoiceRecord record) {
            this.record = record;
            this.filePath = record.SourcePath;
            found = true;
            loaded = true;
        }

        public TsnVoiceRecord Record => record;

        public override byte[] AvatarData => avatarData;

        /// <summary>
        /// 按目录匹配立绘；失败时保持无头像，不影响合成。
        /// </summary>
        public override void EnsureAvatarLoaded() {
            if (avatarData != null) {
                return;
            }
            try {
                TsnVoiceCatalogEntry entry =
                    TsnVoiceCatalog.Instance.FindByFile(filePath);
                avatarData = TsnVoiceCatalog.Instance.GetPortraitBytes(entry);
            } catch (Exception e) {
                Log.Warning(e, "无法加载 TsnVoice 立绘 {File}", filePath);
            }
        }

        public override string Id => "tsnvoice:" + record.Id;

        public override string Name => record.Name;

        public override Dictionary<string, string> LocalizedNames =>
            new Dictionary<string, string>();

        public override USingerType SingerType => TsnVoiceSingerType.Value;

        public override string BasePath {
            get {
                try {
                    return Path.GetDirectoryName(filePath) ?? string.Empty;
                } catch {
                    return string.Empty;
                }
            }
        }

        public override string Location => filePath;

        public override string Version => string.Empty;

        public override string OtherInfo => record.Description;

        public override IList<string> Errors => errors;

        public override Encoding TextFileEncoding => Encoding.UTF8;

        public override IList<USubbank> Subbanks => subbanks;

        /// <summary>
        /// 默认音素器按语音主语言选择。
        /// </summary>
        public override string DefaultPhonemizer {
            get {
                string primary = PrimaryLanguage();
                if (primary == "zh_CN" || primary == "zh_TW") {
                    return typeof(TsnVoiceMandarinPhonemizer).FullName ?? string.Empty;
                }
                if (primary == "en_US") {
                    return typeof(TsnVoiceEnglishPhonemizer).FullName ?? string.Empty;
                }
                if (primary == "ko_KR") {
                    return typeof(TsnVoiceKoreanPhonemizer).FullName ?? string.Empty;
                }
                return typeof(TsnVoiceJapanesePhonemizer).FullName ?? string.Empty;
            }
        }

        public string PrimaryLanguage() {
            string[] languages = record.Languages.Split(
                new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (languages.Length == 0) {
                return TsnVoiceParameters.DefaultLanguage;
            }
            string first = languages[0].Trim();
            return TsnVoiceParameters.IsSupportedLanguage(first)
                ? first
                : TsnVoiceParameters.DefaultLanguage;
        }

        public override bool TryGetOto(string phoneme, out UOto oto) {
            oto = UOto.OfDummy(phoneme);
            return true;
        }

        /// <summary>
        /// 打开语音包（解析容器与配置，不创建 ONNX 会话）。
        /// 会话由推理层按需缓存，见 TsnVoiceInference。
        /// </summary>
        public TsnVoicePackage OpenPackage() {
            try {
                return TsnVoicePackage.Load(filePath);
            } catch (Exception e) {
                Log.Error(e, "无法打开 TsnVoice 语音包 {File}", filePath);
                throw;
            }
        }
    }
}
