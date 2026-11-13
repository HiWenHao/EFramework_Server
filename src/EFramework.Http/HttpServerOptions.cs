/*
 * ================================================
 * Describe:      HTTP host options (bind address, port).
 * CreationTime:  2026-11-13 14:40:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;

namespace EasyFramework.Http
{
    /// <summary>
    /// HTTP 宿主参数：监听地址与端口。
    /// <para>HTTP host options: listen address and port.</para>
    /// </summary>
    public sealed class HttpServerOptions
    {
        #region Fields

        /// <summary>
        /// 默认监听端口：与客户端 <c>ProjectConfig.InnerUrl</c> 的 <c>http://127.0.0.1:8080</c> 对齐
        /// <para>Default listen port, matching the client's <c>ProjectConfig.InnerUrl</c></para>
        /// </summary>
        public const int DefaultPort = 8080;

        #endregion

        #region Properties

        /// <summary>
        /// 绑定地址（须为 IP 字面量；<c>0.0.0.0</c> 表示全部网卡）
        /// <para>Bind address (an IP literal; <c>0.0.0.0</c> means all interfaces)</para>
        /// </summary>
        public string BindAddress { get; set; } = "0.0.0.0";

        /// <summary>
        /// 监听端口（0 表示由系统分配）
        /// <para>Listen port (0 lets the OS assign one)</para>
        /// </summary>
        public int Port { get; set; } = DefaultPort;

        #endregion

        #region Methods

        /// <summary>
        /// 校验参数合法性。
        /// <para>Validates the options.</para>
        /// </summary>
        public void Validate()
        {
            if (Port is < 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be within 0..65535.");
            if (string.IsNullOrWhiteSpace(BindAddress))
                throw new ArgumentException("Bind address must not be empty.", nameof(BindAddress));
        }

        #endregion
    }
}
