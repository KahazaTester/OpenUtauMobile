using System;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 错误分类，对应原生 tsnv_status。
    /// </summary>
    public enum TsnVoiceStatus {
        Ok = 0,
        InvalidArgument = 1,
        IoError = 2,
        InvalidVoice = 3,
        Unsupported = 4,
        ModelError = 5,
        Cancelled = 6,
        InternalError = 7,
    }

    /// <summary>
    /// TsnVoice 异常，携带可复现的错误文本，不做静默回退。
    /// </summary>
    public class TsnVoiceException : Exception {
        public TsnVoiceStatus Status { get; }

        public TsnVoiceException(TsnVoiceStatus status, string message)
            : base(string.IsNullOrWhiteSpace(message) ? "TsnVoice error: " + status : message) {
            Status = status;
        }

        public TsnVoiceException(TsnVoiceStatus status, string message, Exception inner)
            : base(message, inner) {
            Status = status;
        }
    }
}
