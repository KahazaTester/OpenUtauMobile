using System;
using System.Collections.Generic;
using System.IO;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 歌手扫描：遍历全部歌手目录下的 .tsnvoice 文件。
    /// </summary>
    public class TsnVoiceSingerLoader {
        public static IEnumerable<USinger> FindAllSingers() {
            List<USinger> singers = new List<USinger>();
            List<string> roots = new List<string>();
            try {
                roots.AddRange(PathManager.Inst.SingersPaths);
            } catch (Exception e) {
                Log.Warning(e, "无法获取歌手目录，跳过 TsnVoice 扫描");
                return singers;
            }
            TsnVoiceRepository.Scan(roots, out List<TsnVoiceRecord> voices,
                out List<string> diagnostics,
                Preferences.Default.LoadDeepFolderSinger
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly);
            foreach (string diagnostic in diagnostics) {
                Log.Warning("TsnVoice 扫描: {Diagnostic}", diagnostic);
            }
            Log.Information("TsnVoice 扫描到 {Count} 个语音", voices.Count);
            foreach (TsnVoiceRecord record in voices) {
                try {
                    singers.Add(new TsnVoiceSinger(record));
                } catch (Exception e) {
                    Log.Error(e, "无法加载 TsnVoice 歌手 {File}", record.SourcePath);
                }
            }
            return singers;
        }

        /// <summary>
        /// 按文件加载单个歌手（安装后即时生效用）。
        /// </summary>
        public static USinger LoadSinger(string filePath) {
            TsnVoiceRepository.Scan(new List<string> { filePath },
                out List<TsnVoiceRecord> voices, out List<string> diagnostics);
            if (voices.Count == 0) {
                string detail = diagnostics.Count > 0
                    ? string.Join("; ", diagnostics)
                    : "未知原因";
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "无法加载 TsnVoice 歌手：" + filePath + "（" + detail + "）");
            }
            return new TsnVoiceSinger(voices[0]);
        }
    }
}
