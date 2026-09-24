using System;
using System.IO;
using System.Threading.Tasks;
using OpenUtau.Core;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 歌手安装：单文件复制，与 .vogeon 安装方式对应。
    /// 安装后触发 SingersChangedNotification，由 TsnVoiceIntegration 重新合并。
    /// </summary>
    public class TsnVoiceSingerInstaller {
        public const string FileExt = ".tsnvoice";

        public static bool IsTsnVoiceFile(string filePath) {
            return string.Equals(Path.GetExtension(filePath), FileExt,
                StringComparison.OrdinalIgnoreCase);
        }

        public static void Install(string filePath) {
            string fileName = Path.GetFileName(filePath);
            string destName = Path.Combine(PathManager.Inst.SingersInstallPath, fileName);
            if (File.Exists(destName)) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(destName + " already exist!"));
                return;
            }
            // 先校验语音包合法，再复制，避免无效文件污染歌手目录。
            TsnVoiceSingerLoader.LoadSinger(filePath);
            File.Copy(filePath, destName);
            new Task(() => {
                DocManager.Inst.ExecuteCmd(new SingersChangedNotification());
                DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, "Installed " + fileName));
            }).Start(DocManager.Inst.MainScheduler);
        }
    }
}
