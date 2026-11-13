/*
 * ================================================
 * Describe:      LoginApi 的手写实现（登录发握手票据 + 版本查询）。生成物不含业务，只映射路由。
 * CreationTime:  2026-11-13 15:58:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using EasyFramework.Http.Api.Generated;
using EasyFramework.Server;
using EasyFramework.Server.Handshake;

namespace EasyFramework.Http.Api
{
    /// <summary>
    /// 登录接口实现：校验账号密码 → 用服务端密钥签发 UDP 握手票据 → 客户端拿票据去连游戏服。
    /// <para>Login implementation: verify credentials, sign a UDP handshake ticket with the server key, and
    /// let the client use that ticket to join the game server.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>这个类为什么手写</b>：契约生成的只有 DTO、路径常量、接口与路由映射。业务流程（查账号、
    /// 签票、错误码）必须由人写 —— 生成器一旦开始"猜业务"，就变成了不可维护的模板引擎。</para>
    /// <para><b>Why hand-written</b>: the generator only produces DTOs, route constants, the interface and the
    /// route mapping. Business flow (look up the account, sign the ticket, map error codes) stays human-written;
    /// a generator that starts guessing business logic becomes an unmaintainable template engine.</para>
    /// <para><b>当前是演示实现</b>：只认固定账号 <c>demo/demo</c>，密码明文比较。生产必须换成真实的账号
    /// 存储 + 加盐哈希（bcrypt/argon2）+ HTTPS，并且错误原因不区分"账号不存在"与"密码错"，避免账号枚举。</para>
    /// <para><b>Demo implementation</b>: only accepts the fixed account demo/demo with plaintext comparison.
    /// Production needs a real account store, salted hashing (bcrypt/argon2), HTTPS, and an error message that
    /// does not distinguish "no such account" from "wrong password" (account enumeration).</para>
    /// </remarks>
    public sealed class LoginApi : ILoginApi
    {
        #region Fields

        // 业务结果码：0 成功；非 0 由本实现定义，客户端按码提示
        private const int CodeOk = 0;
        private const int CodeBadRequest = 1;
        private const int CodeBadCredential = 2;
        private const int CodeAuthDisabled = 3;

        // 演示账号：仅用于打通链路，生产替换为真实账号存储
        private const string DemoAccount = "demo";
        private const string DemoPassword = "demo";

        private const int TicketLifetimeSeconds = 3600;

        private readonly byte[] _signingSecret;
        private readonly NetServer _server;
        private readonly IServerLogger _logger;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        #endregion

        #region Methods

        /// <summary>
        /// 构造登录实现。
        /// <para>Create the login implementation.</para>
        /// </summary>
        /// <param name="signingSecret">票据签名密钥（null 表示服务端未启用鉴权）<para>Ticket signing key (null when auth is off)</para></param>
        /// <param name="server">游戏服实例（版本接口要读会话数）<para>Game server (the version endpoint reads the session count)</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public LoginApi(byte[] signingSecret, NetServer server, IServerLogger logger)
        {
            _signingSecret = signingSecret;
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _logger = logger;

            if (_logger?.IsEnabled(LogLevel.Warn) == true)
            {
                _logger.Warn("[HTTP] /api/login is a DEMO implementation: it only accepts " +
                             $"{DemoAccount}/{DemoPassword} and compares the password in plaintext.");
            }
        }

        /// <summary>
        /// 账号密码登录，成功后返回握手票据。
        /// <para>Credential login; returns a handshake ticket on success.</para>
        /// </summary>
        /// <param name="request">登录请求<para>Login request</para></param>
        /// <param name="cancellationToken">取消标记<para>Cancellation token</para></param>
        /// <returns>登录结果（业务码在 body 里，HTTP 始终 200）<para>Login result (business code in the body; HTTP stays 200)</para></returns>
        public Task<LoginRes> LoginAsync(LoginReq request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrEmpty(request.Account) || string.IsNullOrEmpty(request.Password))
            {
                return Task.FromResult(Fail(CodeBadRequest, "account and password are required"));
            }

            if (!string.Equals(request.Account, DemoAccount, StringComparison.Ordinal) ||
                !string.Equals(request.Password, DemoPassword, StringComparison.Ordinal))
            {
                // 不区分"账号不存在"与"密码错误"：否则攻击者能靠错误提示枚举账号
                return Task.FromResult(Fail(CodeBadCredential, "invalid account or password"));
            }

            if (_signingSecret == null || _signingSecret.Length == 0)
            {
                return Task.FromResult(Fail(CodeAuthDisabled,
                    "server auth is disabled; start it with --auth-secret to issue tickets"));
            }

            long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TicketLifetimeSeconds;
            string token = HandshakeToken.Create(request.Account, expiresAt, _signingSecret);

            if (_logger?.IsEnabled(LogLevel.Info) == true)
            {
                _logger.Info($"[HTTP] login ok: account={request.Account}, ticket expires at {expiresAt}");
            }

            return Task.FromResult(new LoginRes
            {
                Code = CodeOk,
                Message = string.Empty,
                Token = token,
                AccountId = request.Account,
                ExpiresAtUnix = expiresAt,
                ServerVersion = NetServer.Version,
            });
        }

        /// <summary>
        /// 取服务端版本与运行状态。
        /// <para>Returns the server version and runtime status.</para>
        /// </summary>
        /// <param name="cancellationToken">取消标记<para>Cancellation token</para></param>
        /// <returns>版本信息<para>Version information</para></returns>
        public async Task<VersionRes> VersionAsync(CancellationToken cancellationToken)
        {
            // 会话数只有泵线程能安全读取，所以走 PostAsync 投递取快照
            int sessions = await _server.PostAsync(server => server.Sessions.Count).ConfigureAwait(false);

            return new VersionRes
            {
                Version = NetServer.Version,
                UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                Sessions = sessions,
            };
        }

        private static LoginRes Fail(int code, string message)
        {
            return new LoginRes
            {
                Code = code,
                Message = message,
                Token = string.Empty,
                AccountId = string.Empty,
                ExpiresAtUnix = 0L,
                ServerVersion = NetServer.Version,
            };
        }

        #endregion
    }
}
