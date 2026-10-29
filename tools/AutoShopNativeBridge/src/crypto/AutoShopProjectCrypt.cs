// AutoShop .hcp 主程序 blob 解密器（UCodeCrypt 流密码）。
// 逆向来源：InterfaceMgr.dll CUCodeCrypt::EncryptArithmetic / DecryptArithmetic
//   （InterfaceMgr.dll RVA 0x545F0 / 0x54640，ImageBase 0x10000000；
//    AutoShop V4.12.0.2，x86）。全部接口为逆向所得，无汇川厂商背书，版本锁定。
// 密钥表：InterfaceMgr.dll .rdata RVA 0xB6620（VA 0x100B6620）处 11 字节 ASCII "SclSoftware"。
//
// 算法（从反汇编还原，2026-10-28 三路独立实证——A1 模板 / 连续式立包机 / LoginPassowrd.dat）：
//   EncryptArithmetic: ct[i] = ((pt[i] + key[(i+1) % 11]) & 0xFF) ^ (i & 0xFF 当 i 为偶数)
//   DecryptArithmetic: pt[i] = ((ct[i] ^ (i & 0xFF 当 i 为偶数)) - key[(i+1) % 11]) & 0xFF
//   （厂商循环：esi 从 0 起每字节 +1，到 0xB 归零；XOR 条件 = test cl,1 / jne 跳过，即仅偶数下标）
//
// 明文形态：Main blob 解密后 = FF FE (UTF-16LE BOM) + UTF-16LE XML 工程源内容
//   （<?xml version="1.0" encoding="UTF-16"?><project>...）。
//   P3 观察到的 "SlotaecSfwr" 奇数位循环 = UTF-16LE ASCII 文本高字节 0x00 经加密后的形态
//   （非独立"交错层"）；"循环挂起区" = CJK 汉字（非零高字节）。
//
// 安全纪律：纯内存只读——无文件写、无注册表、无网络、无 PLC 动作；不加载任何厂商 DLL。
// 解密用途仅限离线诊断/工程读取（变量导出/符号浏览），不作写入口。

using System;
using System.Text;

namespace PlcMcp.AutoShopNativeBridge.Crypto
{
    /// <summary>
    /// AutoShop .hcp 主程序 blob（工程源内容）的 UCodeCrypt 解密器（离线、只读、无网络）。
    /// </summary>
    public static class AutoShopProjectCrypt
    {
        /// <summary>11 字节密钥表（InterfaceMgr.dll RVA 0xB6620，"SclSoftware"）。</summary>
        public static readonly byte[] KeyTable =
            { (byte)'S', (byte)'c', (byte)'l', (byte)'S', (byte)'o', (byte)'f',
              (byte)'t', (byte)'w', (byte)'a', (byte)'r', (byte)'e' };

        private const int KeyLength = 11;

        /// <summary>已知明文锚点（LoginPassowrd.dat 记录，Template A1 实测）。</summary>
        public static readonly byte[] KnownAnswerCiphertext =
            { 0xB0, 0xDB, 0xB5, 0xD4, 0xA7, 0xA4, 0x87, 0xA6, 0xD7, 0xD5, 0xCD, 0xDC, 0x7A, 0xD7, 0x9B, 0x31 };
        public static readonly string KnownAnswerPlaintext = "Mode=0\nEmpty\n\x84&\xcb";

        /// <summary>
        /// 解密整个 blob（DecryptArithmetic，索引从 0 起）。返回与输入等长的明文。
        /// 对 Main blob：结果 = FF FE BOM + UTF-16LE XML。
        /// </summary>
        public static byte[] DecryptBlob(ReadOnlySpan<byte> blob)
        {
            var dst = new byte[blob.Length];
            for (int i = 0; i < blob.Length; i++)
            {
                int b = blob[i];
                b ^= (i & 1) == 0 ? (i & 0xFF) : 0;   // 还原偶数位 XOR（先于减法）
                dst[i] = (byte)((b - KeyTable[(i + 1) % KeyLength]) & 0xFF);
            }
            return dst;
        }

        /// <summary>
        /// 加密（EncryptArithmetic，索引从 0 起；DecryptBlob 的精确逆）。
        /// 提供它仅为往返自检与未来的合成验证；本桥不产出工程写路径。
        /// </summary>
        public static byte[] EncryptBlob(ReadOnlySpan<byte> plain)
        {
            var dst = new byte[plain.Length];
            for (int i = 0; i < plain.Length; i++)
            {
                int b = (plain[i] + KeyTable[(i + 1) % KeyLength]) & 0xFF;
                if ((i & 1) == 0)
                {
                    b ^= i & 0xFF;
                }
                dst[i] = (byte)b;
            }
            return dst;
        }

        /// <summary>
        /// 解密 Main blob 并按 UTF-16LE 解码为 XML 文本；带结构校验（不抛异常版）。
        /// 校验 = BOM FF FE + "&lt;?xml " 前缀（明文头 2 字符经 BOM 后即 XML 声明）。
        /// </summary>
        public static bool TryDecryptProjectXml(ReadOnlySpan<byte> blob, out string? xml, out string? error)
        {
            xml = null;
            error = null;
            if (blob.Length < 8)
            {
                error = "blob too short (<8 B)";
                return false;
            }
            byte[] plain;
            try
            {
                plain = DecryptBlob(blob);
            }
            catch (Exception ex)
            {
                error = "decrypt failed: " + ex.Message;
                return false;
            }
            if (plain[0] != 0xFF || plain[1] != 0xFE)
            {
                error = $"missing UTF-16LE BOM (got {plain[0]:X2} {plain[1]:X2}) — blob 可能不是 UCodeCrypt 工程内容";
                return false;
            }
            xml = Encoding.Unicode.GetString(plain, 2, plain.Length - 2);
            if (!xml.StartsWith("<?xml ", StringComparison.Ordinal))
            {
                error = "decoded text does not start with '<?xml '";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 自检（离线已知明文锚点 + 往返）：
        /// 1) LoginPassowrd.dat 已知答案；2) 随机数据往返；3) UTF-16LE 头加密形态（"bj" prologue）。
        /// 全过返回 true。
        /// </summary>
        public static bool SelfTest()
        {
            // 1) 已知答案：真实样本密文 → "Mode=0\nEmpty\n\x84&\xcb"
            var ka = DecryptBlob(KnownAnswerCiphertext);
            if (Encoding.Latin1.GetString(ka) != KnownAnswerPlaintext)
            {
                return false;
            }
            if (EncryptBlob(ka).AsSpan().SequenceEqual(KnownAnswerCiphertext) == false)
            {
                return false;
            }

            // 2) 随机数据往返：Decrypt(Encrypt(x)) == x（双向精确逆）
            var rnd = new Random(20261028);
            for (int trial = 0; trial < 8; trial++)
            {
                var buf = new byte[1 + rnd.Next(4096)];
                rnd.NextBytes(buf);
                var rt = DecryptBlob(EncryptBlob(buf));
                if (!rt.AsSpan().SequenceEqual(buf))
                {
                    return false;
                }
                var rt2 = EncryptBlob(DecryptBlob(buf));
                if (!rt2.AsSpan().SequenceEqual(buf))
                {
                    return false;
                }
            }

            // 3) UTF-16LE 头形态：enc(FF FE) = 62 6A（"bj"，P3 的 blob prologue）
            var head = EncryptBlob(new byte[] { 0xFF, 0xFE });
            if (head[0] != 0x62 || head[1] != 0x6A)
            {
                return false;
            }

            // 4) 全零明文形态：enc(8×00) = "clQobtqa"（.LD 文件内容头实测）
            var zero8 = EncryptBlob(new byte[8]);
            var expect = new byte[] { 0x63, 0x6C, 0x51, 0x6F, 0x62, 0x74, 0x71, 0x61 };
            if (!zero8.AsSpan().SequenceEqual(expect))
            {
                return false;
            }
            return true;
        }
    }
}
