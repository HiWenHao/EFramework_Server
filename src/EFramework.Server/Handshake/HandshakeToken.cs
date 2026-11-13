/*
 * ================================================
 * Describe:      Handshake token format: accountId:expiresAt:hmac (HMAC-SHA256, hex).
 * CreationTime:  2026-11-13 15:06:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EasyFramework.Server.Handshake
{
    /// <summary>
    /// 握手票据：`accountId:expiresAtUnix:hex(HMAC-SHA256(secret, "accountId|expiresAtUnix"))`。
    /// <para>Handshake ticket: accountId, absolute expiry and an HMAC-SHA256 signature over both.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么是 HMAC 而不是"客户端发密码"</b>：服务端不需要保存明文口令，客户端也无法伪造票据
    /// ——它必须先从登录服拿到签名结果。签名密钥只存在服务端与登录服，客户端永远不持有。</para>
    /// <para><b>Why HMAC instead of sending a password</b>: the server stores no plaintext secret and the
    /// client cannot forge a ticket — it must obtain the signature from the login service first. The signing
    /// key never ships with the client.</para>
    /// <para><b>为什么带过期时间</b>：签名本身永久有效，一旦票据泄露就等于永久授权。带上绝对过期时间，
    /// 泄露窗口就被限制在有效期内（UDP 明文，票据确实会被人抓包看到）。</para>
    /// <para><b>Why an expiry</b>: a signature alone never expires, so a leaked ticket would be a permanent
    /// grant. An absolute expiry bounds the damage — and UDP is plaintext, so tickets do get sniffed.</para>
    /// <para><b>为什么 accountId 不能含分隔符</b>：格式靠分隔符切分，允许 ':' 会让两种不同的
    /// (accountId, expiresAt) 组合产生同一个字符串，签名就失去了唯一对应关系。</para>
    /// <para><b>Why accountId cannot contain the separator</b>: parsing is separator-based; allowing ':' would
    /// let two different (accountId, expiresAt) pairs produce the same string and break the signature binding.</para>
    /// </remarks>
    public readonly struct HandshakeToken
    {
        #region Fields

        /// <summary>
        /// 票据最大字节数（超长直接拒绝，避免解析时无谓分配）
        /// <para>Maximum token size; longer input is rejected before parsing</para>
        /// </summary>
        public const int MaxTokenLength = 512;

        /// <summary>
        /// 账号 Id 最大长度
        /// <para>Maximum account id length</para>
        /// </summary>
        public const int MaxAccountIdLength = 64;

        // SHA-256 摘要长度
        private const int SignatureByteLength = 32;
        private const int SignatureHexLength = SignatureByteLength * 2;

        // 字段分隔符 / 参与签名的连接符（两者必须不同，否则存在歧义）
        private const char FieldSeparator = ':';
        private const char SignedSeparator = '|';

        #endregion

        #region Properties

        /// <summary>
        /// 账号 Id（票据通过校验后可信）
        /// <para>Account id (trustworthy only after verification)</para>
        /// </summary>
        public string AccountId { get; }

        /// <summary>
        /// 绝对过期时刻（Unix 秒）
        /// <para>Absolute expiry (Unix seconds)</para>
        /// </summary>
        public long ExpiresAtUnix { get; }

        /// <summary>
        /// 签名（小写十六进制）
        /// <para>Signature (lower-case hex)</para>
        /// </summary>
        public string Signature { get; }

        #endregion

        #region Methods

        private HandshakeToken(string accountId, long expiresAtUnix, string signature)
        {
            AccountId = accountId;
            ExpiresAtUnix = expiresAtUnix;
            Signature = signature;
        }

        /// <summary>
        /// 签发一张票据（由登录服或服务端自己的测试工具调用；密钥不能出现在客户端）。
        /// <para>Issue a ticket (called by the login service or a server-side tool; the key must never ship
        /// with the client).</para>
        /// </summary>
        /// <param name="accountId">账号 Id（1~64 字符，不可含 ':' 或控制字符）<para>Account id (1-64 chars, no ':' or control characters)</para></param>
        /// <param name="lifetime">有效期（必须为正）<para>Validity period (must be positive)</para></param>
        /// <param name="secret">签名密钥（不能为空）<para>Signing key (must not be empty)</para></param>
        /// <returns>票据字符串<para>The token string</para></returns>
        public static string Create(string accountId, TimeSpan lifetime, byte[] secret)
        {
            if (!IsValidAccountId(accountId))
                throw new ArgumentException("Account id must be 1..64 characters without ':' or control characters.", nameof(accountId));
            if (secret == null || secret.Length == 0)
                throw new ArgumentException("Signing secret must not be empty.", nameof(secret));
            if (lifetime <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Token lifetime must be positive.");

            long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)lifetime.TotalSeconds;

            return Create(accountId, expiresAt, secret);
        }

        /// <summary>
        /// 按绝对过期时刻签发票据（登录服需要精确控制过期点时使用；测试也用它对造过期票据）。
        /// <para>Issue a ticket with an absolute expiry (for a login service that controls the exact moment;
        /// tests use it to craft expired tickets).</para>
        /// </summary>
        /// <param name="accountId">账号 Id<para>Account id</para></param>
        /// <param name="expiresAtUnix">绝对过期时刻（Unix 秒）<para>Absolute expiry in Unix seconds</para></param>
        /// <param name="secret">签名密钥<para>Signing key</para></param>
        /// <returns>票据字符串<para>The token string</para></returns>
        public static string Create(string accountId, long expiresAtUnix, byte[] secret)
        {
            if (!IsValidAccountId(accountId))
                throw new ArgumentException("Account id must be 1..64 characters without ':' or control characters.", nameof(accountId));
            if (secret == null || secret.Length == 0)
                throw new ArgumentException("Signing secret must not be empty.", nameof(secret));
            if (expiresAtUnix <= 0)
                throw new ArgumentOutOfRangeException(nameof(expiresAtUnix), expiresAtUnix, "Expiry must be a positive Unix timestamp.");

            Span<byte> signature = stackalloc byte[SignatureByteLength];
            ComputeSignature(secret, accountId, expiresAtUnix, signature);

            return string.Concat(
                accountId,
                FieldSeparator.ToString(),
                expiresAtUnix.ToString(CultureInfo.InvariantCulture),
                FieldSeparator.ToString(),
                Convert.ToHexString(signature).ToLowerInvariant());
        }

        /// <summary>
        /// 从线上字节解析票据（只做格式解析，不验证签名）。
        /// <para>Parse a ticket from wire bytes (format only; the signature is not checked here).</para>
        /// </summary>
        /// <param name="utf8Token">票据的 UTF-8 字节<para>Token bytes in UTF-8</para></param>
        /// <param name="token">解析结果<para>Parsed token</para></param>
        /// <param name="error">失败原因（仅用于服务端日志，不要回给对端）<para>Failure reason (server log only; never echo to the peer)</para></param>
        /// <returns>是否解析成功<para>Whether parsing succeeded</para></returns>
        public static bool TryParse(ReadOnlySpan<byte> utf8Token, out HandshakeToken token, out string error)
        {
            token = default;
            error = null;

            if (utf8Token.Length == 0 || utf8Token.Length > MaxTokenLength)
            {
                error = "empty or oversized";
                return false;
            }

            string text = Encoding.UTF8.GetString(utf8Token);

            int firstSeparator = text.IndexOf(FieldSeparator);
            if (firstSeparator <= 0)
            {
                error = "missing account id";
                return false;
            }

            int secondSeparator = text.IndexOf(FieldSeparator, firstSeparator + 1);
            if (secondSeparator <= firstSeparator + 1)
            {
                error = "missing expiry";
                return false;
            }

            string accountId = text.Substring(0, firstSeparator);
            string expiryText = text.Substring(firstSeparator + 1, secondSeparator - firstSeparator - 1);
            string signature = text.Substring(secondSeparator + 1);

            if (!IsValidAccountId(accountId))
            {
                error = "invalid account id";
                return false;
            }

            if (!long.TryParse(expiryText, NumberStyles.None, CultureInfo.InvariantCulture, out long expiresAt))
            {
                error = "invalid expiry";
                return false;
            }

            if (signature.Length != SignatureHexLength)
            {
                error = "invalid signature length";
                return false;
            }

            token = new HandshakeToken(accountId, expiresAt, signature);
            return true;
        }

        /// <summary>
        /// 校验签名是否由该密钥签发（定长时间比较，避免按字节提前返回泄露信息）。
        /// <para>Verify the signature was produced by this key (constant-time compare, so a byte-by-byte early
        /// exit cannot leak information).</para>
        /// </summary>
        /// <param name="secret">签名密钥<para>Signing key</para></param>
        /// <returns>签名是否有效<para>Whether the signature is valid</para></returns>
        public bool Verify(byte[] secret)
        {
            if (secret == null || secret.Length == 0 || Signature == null) return false;

            Span<byte> provided = stackalloc byte[SignatureByteLength];
            if (!TryParseHex(Signature, provided)) return false;

            Span<byte> expected = stackalloc byte[SignatureByteLength];
            ComputeSignature(secret, AccountId, ExpiresAtUnix, expected);

            return CryptographicOperations.FixedTimeEquals(provided, expected);
        }

        /// <summary>
        /// 返回可读描述（不含签名，避免日志泄露票据）。
        /// <para>Readable description without the signature (never log the ticket itself).</para>
        /// </summary>
        /// <returns>描述文本<para>Description text</para></returns>
        public override string ToString()
        {
            return $"{AccountId}@{ExpiresAtUnix}";
        }

        // 签名覆盖 "accountId|expiresAt"，与字段分隔符 ':':' 区分开，避免拼接歧义
        private static void ComputeSignature(byte[] secret, string accountId, long expiresAtUnix, Span<byte> destination)
        {
            string payload = string.Concat(
                accountId,
                SignedSeparator.ToString(),
                expiresAtUnix.ToString(CultureInfo.InvariantCulture));

            int byteCount = Encoding.UTF8.GetByteCount(payload);
            byte[] payloadBytes = new byte[byteCount];
            Encoding.UTF8.GetBytes(payload, 0, payload.Length, payloadBytes, 0);

            Span<byte> digest = stackalloc byte[SignatureByteLength];
            HMACSHA256.HashData(secret, payloadBytes, digest);
            digest.CopyTo(destination);
        }

        // 账号 Id 规则：长度受限、不含分隔符、不含控制字符
        private static bool IsValidAccountId(string accountId)
        {
            if (string.IsNullOrEmpty(accountId) || accountId.Length > MaxAccountIdLength) return false;

            for (int i = 0; i < accountId.Length; i++)
            {
                char c = accountId[i];
                if (c == FieldSeparator || char.IsControl(c)) return false;
            }

            return true;
        }

        // 手写解析十六进制：Convert.FromHexString 失败会抛异常，握手路径上不想用异常做流程控制
        private static bool TryParseHex(string hex, Span<byte> destination)
        {
            if (hex == null || destination.Length * 2 != hex.Length) return false;

            for (int i = 0; i < destination.Length; i++)
            {
                int high = HexValue(hex[i * 2]);
                int low = HexValue(hex[i * 2 + 1]);
                if (high < 0 || low < 0) return false;

                destination[i] = (byte)((high << 4) | low);
            }

            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        #endregion
    }
}
