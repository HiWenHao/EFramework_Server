/*
 * ================================================
 * Describe:      HMAC-SHA256 handshake validator (verifies tickets issued by a login service).
 * CreationTime:  2026-11-13 15:10:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Net;

namespace EasyFramework.Server.Handshake
{
    /// <summary>
    /// 生产可用的握手校验器：验证 <see cref="HandshakeToken"/> 的签名、有效期与生命周期上限。
    /// <para>Production handshake validator: verifies the ticket signature, expiry and lifetime bound.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>职责边界</b>：本类<b>只验签，不签发</b>。票据应由登录服（HTTP）在账号鉴权通过后签发，
    /// 游戏服只持有验签用的公钥/对称密钥。这样游戏服被攻破也拿不到签发能力。</para>
    /// <para><b>Responsibility</b>: verify only, never issue. Tickets are issued by the login service (HTTP)
    /// after account authentication; the game server holds only the verification key, so compromising it does
    /// not grant issuing power.</para>
    /// <para><b>为什么会话要拿到 accountId</b>：握手通过后 <see cref="HandshakeResult.Subject"/> 会被写进
    /// 会话，业务层据此知道"这条连接是谁"，不再信任客户端自报的 Id。</para>
    /// <para><b>Why the session needs accountId</b>: on success the subject is written into the session, so
    /// business code knows who the connection belongs to instead of trusting a client-claimed id.</para>
    /// </remarks>
    public sealed class HmacHandshakeValidator : IHandshakeValidator
    {
        #region Fields

        // 结果码：0 = 通过，非 0 由本类定义（客户端只负责展示 reason）
        private const int CodeMalformed = 2;
        private const int CodeBadSignature = 3;
        private const int CodeExpired = 4;
        private const int CodeLifetimeOutOfRange = 5;

        private readonly byte[] _secret;
        private readonly long _allowedClockSkewSeconds;
        private readonly long _maxLifetimeSeconds;
        private readonly IServerLogger _logger;

        #endregion

        #region Methods

        /// <summary>
        /// 构造 HMAC 校验器。
        /// <para>Create the HMAC validator.</para>
        /// </summary>
        /// <param name="secret">验签密钥（与登录服一致；不得进入客户端）<para>Verification key (shared with the login service; never shipped to clients)</para></param>
        /// <param name="allowedClockSkewSeconds">允许的时钟偏差秒数<para>Allowed clock skew in seconds</para></param>
        /// <param name="maxLifetimeSeconds">票据有效期上限（0 表示不限制）；防止签发方误发十年有效票据<para>Maximum ticket lifetime (0 disables); guards against a ten-year ticket</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public HmacHandshakeValidator(byte[] secret, int allowedClockSkewSeconds = 30, int maxLifetimeSeconds = 86400,
            IServerLogger logger = null)
        {
            if (secret == null || secret.Length == 0)
                throw new ArgumentException("Signing secret must not be empty.", nameof(secret));
            if (allowedClockSkewSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(allowedClockSkewSeconds), allowedClockSkewSeconds, "Clock skew must be >= 0.");
            if (maxLifetimeSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(maxLifetimeSeconds), maxLifetimeSeconds, "Max lifetime must be >= 0.");

            // 防御性拷贝：避免调用方之后改掉数组内容（密钥被就地清空/替换会静默失效）
            _secret = (byte[])secret.Clone();
            _allowedClockSkewSeconds = allowedClockSkewSeconds;
            _maxLifetimeSeconds = maxLifetimeSeconds;
            _logger = logger;
        }

        /// <summary>
        /// 校验票据：格式 → 签名 → 有效期 → 生命周期上限；全部通过才返回携带 accountId 的通过结论。
        /// <para>Validate the ticket: format, signature, expiry, lifetime bound; only a full pass returns an
        /// acceptance carrying the account id.</para>
        /// </summary>
        /// <param name="token">票据 UTF-8 字节<para>Ticket bytes in UTF-8</para></param>
        /// <param name="remoteEndPoint">客户端来源端点<para>Client endpoint</para></param>
        /// <returns>通过或拒绝结论<para>Acceptance or rejection verdict</para></returns>
        public HandshakeResult Validate(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint)
        {
            if (!HandshakeToken.TryParse(token, out HandshakeToken parsed, out string error))
            {
                LogDebug($"malformed token from {remoteEndPoint}: {error}");
                return HandshakeResult.Reject(CodeMalformed, "invalid token");
            }

            if (!parsed.Verify(_secret))
            {
                // 对外统一说"invalid token"：不告诉攻击者是签名错还是账号不存在
                LogDebug($"signature mismatch from {remoteEndPoint} ({parsed})");
                return HandshakeResult.Reject(CodeBadSignature, "invalid token");
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (parsed.ExpiresAtUnix + _allowedClockSkewSeconds < now)
            {
                // 过期则明确告知，客户端据此重新走登录拿新票据
                LogDebug($"expired token from {remoteEndPoint} ({parsed})");
                return HandshakeResult.Reject(CodeExpired, "token expired");
            }

            if (_maxLifetimeSeconds > 0 && parsed.ExpiresAtUnix > now + _maxLifetimeSeconds)
            {
                LogDebug($"token expiry too far ahead from {remoteEndPoint} ({parsed})");
                return HandshakeResult.Reject(CodeLifetimeOutOfRange, "token lifetime out of range");
            }

            return HandshakeResult.Accept(parsed.AccountId);
        }

        // 握手每个连接只发生一次，但日志仍先判级别：拒绝风暴时不该把 CPU 花在拼字符串上
        private void LogDebug(string message)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true) _logger.Debug($"[HANDSHAKE] {message}");
        }

        #endregion
    }
}
