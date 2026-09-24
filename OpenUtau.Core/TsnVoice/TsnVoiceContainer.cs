using System;
using System.Collections.Generic;
using System.IO;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// .tsnvoice 文件头，对应原生 VoiceHeader。
    /// </summary>
    public class TsnVoiceHeader {
        public uint Major;
        public uint Minor;
        public byte EncryptionMode;
        public byte VoiceFormat;
        public ulong EncodedSize;
    }

    /// <summary>
    /// 解码后的语音包载荷。
    /// </summary>
    public class TsnVoiceDecoded {
        public TsnVoiceHeader Header = new TsnVoiceHeader();
        public byte[] Payload = Array.Empty<byte>();
        public List<ulong> BlockLengths = new List<ulong>();
        public long PhysicalSize;
    }

    /// <summary>
    /// .tsnvoice 容器解析，托管移植自原生 container.cpp。
    /// 支持 voice format 0/1/2/5 的 legacy 块编码；encryption_mode=1 需要
    /// 可选的 libSodium 后端，移动端不支持，报明确错误。
    /// </summary>
    public static class TsnVoiceContainer {
        public const ulong MaxBlockBytes = 256UL * 1024 * 1024;
        public const ulong MaxLogicalBytes = 4UL * 1024 * 1024 * 1024;

        public static bool IsSupportedVoiceFormat(byte value) {
            return value == 0 || value == 1 || value == 2 || value == 5;
        }

        public static bool SupportsSingingScore(byte voiceFormat) {
            return voiceFormat == 0 || voiceFormat == 5;
        }

        static uint ReadU32LE(byte[] data, int offset) {
            return (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));
        }

        static byte RotateLeft(byte value, int amount) {
            return (byte)((value << amount) | (value >> (8 - amount)));
        }

        static byte RotateRight(byte value, int amount) {
            return (byte)((value >> amount) | (value << (8 - amount)));
        }

        /// <summary>
        /// 单个 legacy 块解码：偶/奇交织还原后做旋转与取反。
        /// </summary>
        public static byte[] DecodeLegacyBlock(byte[] encoded) {
            byte[] mixed = new byte[encoded.Length];
            int evenCount = (encoded.Length + 1) / 2;
            for (int i = 0; i < evenCount; i++) {
                mixed[i * 2] = encoded[i];
            }
            for (int i = 0; i < encoded.Length / 2; i++) {
                mixed[i * 2 + 1] = encoded[encoded.Length - 1 - i];
            }
            for (int i = 0; i < mixed.Length; i++) {
                byte value = mixed[i];
                if (i % 3 == 1) {
                    value = RotateRight(value, 1);
                } else if (i % 3 == 2) {
                    value = RotateLeft(value, 1);
                }
                value = (byte)~RotateLeft(value, 4);
                mixed[i] = value;
            }
            return mixed;
        }

        /// <summary>
        /// 解码词典文件（legacy 块序列），对应原生 decode_legacy_dictionary。
        /// </summary>
        public static byte[] DecodeLegacyDictionary(string path) {
            byte[] data;
            try {
                data = File.ReadAllBytes(path);
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError,
                    "无法打开歌手词典：" + path, e);
            }
            try {
                return DecodeLegacyDictionaryBytes(data);
            } catch (TsnVoiceException e) {
                throw new TsnVoiceException(e.Status, e.Message + "：" + path, e);
            }
        }

        /// <summary>
        /// 从内存解码词典载荷（内嵌资源用）。
        /// </summary>
        public static byte[] DecodeLegacyDictionaryBytes(byte[] data) {
            List<byte> result = new List<byte>();
            int position = 0;
            while (true) {
                if (position == data.Length) {
                    break;
                }
                if (position + 4 > data.Length) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "词典块截断");
                }
                byte[] lengthBytes = new byte[4];
                Array.Copy(data, position, lengthBytes, 0, 4);
                position += 4;
                uint blockSize = ReadU32LE(lengthBytes, 0);
                if (blockSize > MaxBlockBytes) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "词典块过大");
                }
                if (position + (int)blockSize > data.Length) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "词典载荷截断");
                }
                byte[] encoded = new byte[blockSize];
                Array.Copy(data, position, encoded, 0, (int)blockSize);
                position += (int)blockSize;
                result.AddRange(DecodeLegacyBlock(encoded));
            }
            if (result.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "词典为空");
            }
            return result.ToArray();
        }

        static int ReadExact(Stream stream, byte[] buffer, int offset, int count) {
            int total = 0;
            while (total < count) {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read == 0) {
                    break;
                }
                total += read;
            }
            return total;
        }

        /// <summary>
        /// 解码整个 .tsnvoice 文件。
        /// </summary>
        public static TsnVoiceDecoded DecodeVoiceFile(string path) {
            FileInfo info;
            try {
                info = new FileInfo(path);
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError, "无法访问语音文件：" + path, e);
            }
            TsnVoiceDecoded decoded = new TsnVoiceDecoded();
            decoded.PhysicalSize = info.Length;
            List<byte> payload = new List<byte>();
            try {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read)) {
                    byte[] version = new byte[8];
                    if (ReadExact(stream, version, 0, 8) != 8) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音文件头截断：" + path);
                    }
                    decoded.Header.Major = ReadU32LE(version, 0);
                    decoded.Header.Minor = ReadU32LE(version, 4);
                    if (decoded.Header.Major >= 2) {
                        int mode = stream.ReadByte();
                        if (mode < 0) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音文件头截断：" + path);
                        }
                        decoded.Header.EncryptionMode = (byte)mode;
                    }
                    int format = stream.ReadByte();
                    if (format < 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音文件头截断：" + path);
                    }
                    decoded.Header.VoiceFormat = (byte)format;
                    decoded.Header.EncodedSize = decoded.Header.Major >= 2 ? 10UL : 9UL;
                    if (!IsSupportedVoiceFormat(decoded.Header.VoiceFormat)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "不支持的 voice format：" + decoded.Header.VoiceFormat);
                    }
                    if (decoded.Header.EncryptionMode > 1) {
                        throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                            "不支持的语音加密模式：" + decoded.Header.EncryptionMode);
                    }
                    if (decoded.Header.EncryptionMode == 1) {
                        throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                            "该语音包需要 libSodium 解密后端，移动端不受支持：" + path);
                    }
                    byte[] lengthBytes = new byte[4];
                    while (true) {
                        int read = ReadExact(stream, lengthBytes, 0, 4);
                        if (read == 0) {
                            break;
                        }
                        if (read != 4) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音块长度截断：" + path);
                        }
                        uint blockSize = ReadU32LE(lengthBytes, 0);
                        if (blockSize > MaxBlockBytes) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音块超出大小限制：" + path);
                        }
                        if (blockSize > MaxLogicalBytes - (ulong)payload.Count) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "解码后语音超出大小限制：" + path);
                        }
                        byte[] encoded = new byte[blockSize];
                        if (ReadExact(stream, encoded, 0, (int)blockSize) != (int)blockSize) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音块载荷截断：" + path);
                        }
                        payload.AddRange(DecodeLegacyBlock(encoded));
                        decoded.BlockLengths.Add(blockSize);
                    }
                }
            } catch (TsnVoiceException) {
                throw;
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.IoError, "无法读取语音文件：" + path, e);
            }
            if (decoded.BlockLengths.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音文件没有数据块：" + path);
            }
            decoded.Payload = payload.ToArray();
            return decoded;
        }
    }
}
