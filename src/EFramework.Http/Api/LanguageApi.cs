/*
 * ================================================
 * Describe:      LanguageApi 的手写实现（语言切换示例）。生成物不含业务，只映射路由。
 * CreationTime:  2026-11-13 16:05:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyFramework.Http.Api.Generated;
using EasyFramework.Server;

namespace EasyFramework.Http.Api
{
    /// <summary>
    /// 语言切换接口实现：校验语言代码，返回各语言的可用标记。
    /// <para>Language implementation: validate the language code and return per-language availability flags.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>语义待确认</b>：响应里的 <c>bool[]</c> 按固定顺序对应 10 种语言（<see cref="SupportedLanguages"/>），
    /// 当前实现一律返回「全部可用」。真实含义（语言包是否已下载、是否开放、是否完成翻译……）由业务决定，
    /// 只需要改这一个方法 —— 契约与两侧类型不用动。</para>
    /// <para><b>Semantics pending</b>: the bool[] maps to the 10 languages in fixed order; the stub reports all
    /// available. Whatever it really means (pack downloaded, unlocked, translated...) only changes this method.</para>
    /// </remarks>
    public sealed class LanguageApi : ILanguageApi
    {
        #region Fields

        // 业务码沿用示例约定：200 成功
        private const int CodeOk = 200;
        private const int CodeBadRequest = 400;

        // 固定顺序：响应里的 bool[] 与它一一对应（换顺序等于换协议，两侧必须一起改）
        private static readonly string[] SupportedLanguages =
        {
            "en", "zh", "de", "es", "fr", "id", "it", "pt", "tr", "vi",
        };

        private readonly IServerLogger _logger;

        #endregion

        #region Methods

        /// <summary>
        /// 构造语言接口实现。
        /// <para>Create the language implementation.</para>
        /// </summary>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public LanguageApi(IServerLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 上报当前语言，返回各语言可用标记。
        /// <para>Report the current language and get the availability flags.</para>
        /// </summary>
        /// <param name="request">语言切换请求<para>Language request</para></param>
        /// <param name="cancellationToken">取消标记<para>Cancellation token</para></param>
        /// <returns>结果（业务码在 body 里，HTTP 始终 200）<para>Result (business code in the body; HTTP stays 200)</para></returns>
        public Task<SetLanguageRes> SetLanguageAsync(SetLanguageReq request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrEmpty(request.CurrentLanguage))
            {
                return Task.FromResult(Fail(CodeBadRequest, "currentlanguage is required"));
            }

            // Array.IndexOf 而不是 LINQ Contains：仓库禁用 System.Linq
            if (Array.IndexOf(SupportedLanguages, request.CurrentLanguage) < 0)
            {
                return Task.FromResult(Fail(CodeBadRequest,
                    $"unsupported language '{request.CurrentLanguage}', expected one of: " +
                    string.Join("/", SupportedLanguages)));
            }

            if (_logger?.IsEnabled(LogLevel.Info) == true)
            {
                _logger.Info($"[HTTP] language switch: {request.CurrentLanguage}");
            }

            // 语义待确认：先按「全部可用」返回，顺序与 SupportedLanguages 一致
            var flags = new List<bool>(SupportedLanguages.Length);
            for (int i = 0; i < SupportedLanguages.Length; i++) flags.Add(true);

            return Task.FromResult(new SetLanguageRes
            {
                Code = CodeOk,
                Msg = "success",
                Data = flags,
            });
        }

        private static SetLanguageRes Fail(int code, string message)
        {
            return new SetLanguageRes
            {
                Code = code,
                Msg = message,
                Data = new List<bool>(),
            };
        }

        #endregion
    }
}
