using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 歌手类型与渲染器标识。
    /// 与 DiffSinger、Vogen 等引擎一致，作为一等引擎注册于 Core。
    /// </summary>
    public static class TsnVoiceSingerType {
        /// <summary>渲染器注册名。</summary>
        public const string RendererName = "TSNVOICE";

        /// <summary>.tsnvoice 文件扩展名。</summary>
        public const string FileExtension = ".tsnvoice";

        /// <summary>TsnVoice 歌手类型值。</summary>
        public static USingerType Value => USingerType.TsnVoice;

        /// <summary>判断歌手是否为 TsnVoice 歌手。</summary>
        public static bool IsTsnVoice(USinger singer) {
            return singer != null && singer.Found && singer.SingerType == Value;
        }
    }
}
