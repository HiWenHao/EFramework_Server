/*
 * ================================================
 * Describe:      KCP tuning knobs for the server side (wire-relevant subset).
 * Author:        EFramework.Server
 * CreationTime:  2026-11-13 12:50:00
 * ScriptVersion: 0.1
 * ===============================================
 */

namespace EasyFramework.Systems.Net
{
    /// <summary>
    /// 服务端 KCP 调参数：只保留影响「线上行为」的旋钮，客户端生命周期策略（重连/心跳）不属于服务端职责。
    /// <para>Server-side KCP tuning: only the wire-relevant knobs; client lifecycle policy (reconnect,
    /// heartbeat) is not a server concern.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>必须与客户端 <c>NetworkSettings</c> 的默认值保持一致</b>：MTU 决定分片大小，窗口决定
    /// 单次可发的段数，两侧不一致会表现为「大消息卡死」或「带宽跑不满」。默认值与客户端完全相同
    /// （Mtu=512、WndSize=256/256、NoDelay(1,10,2,1)）。</para>
    /// <para><b>Must match the client's <c>NetworkSettings</c> defaults</b>: MTU drives fragmentation and
    /// the window drives in-flight segments; a mismatch shows up as a stalled large message or poor
    /// throughput. Defaults mirror the client exactly.</para>
    /// </remarks>
    public sealed class KcpSettings
    {
        #region Fields

        /// <summary>
        /// 启用 nodelay（以激进 RTO 换低延迟）
        /// <para>Enable nodelay (aggressive RTO for lower latency)</para>
        /// </summary>
        public bool NoDelay { get; set; } = true;

        /// <summary>
        /// 内部刷新间隔(ms)：同时决定服务端的「回包延迟上限」
        /// <para>Internal flush interval in ms; also the upper bound of reply latency on the server</para>
        /// </summary>
        public int Interval { get; set; } = 10;

        /// <summary>
        /// 触发快速重传的冗余 ACK 次数（0=关闭快重传）
        /// <para>Duplicate acks that trigger a fast retransmit (0 = off)</para>
        /// </summary>
        public int FastResend { get; set; } = 2;

        /// <summary>
        /// 关闭拥塞控制（追最低延迟，弱网下更容易丢包）
        /// <para>Disable congestion control (lowest latency, more loss on weak networks)</para>
        /// </summary>
        public bool NoCwnd { get; set; } = true;

        /// <summary>
        /// 发送窗口（段数）
        /// <para>Send window in segments</para>
        /// </summary>
        public int SndWnd { get; set; } = 256;

        /// <summary>
        /// 接收窗口（段数）
        /// <para>Receive window in segments</para>
        /// </summary>
        public int RcvWnd { get; set; } = 256;

        /// <summary>
        /// MTU 字节数（移动弱网 470~512，避免 IP 层分片）
        /// <para>MTU in bytes (470~512 on mobile weak networks to avoid IP fragmentation)</para>
        /// </summary>
        public int Mtu { get; set; } = 512;

        /// <summary>
        /// 重传次数超过该值判定链路死亡
        /// <para>Retransmit limit that marks the link as dead</para>
        /// </summary>
        public int DeadLink { get; set; } = 20;

        #endregion

        #region Properties

        /// <summary>
        /// 默认参数实例（值与客户端 <c>NetworkSettings.Default</c> 对齐）
        /// <para>Default instance (values aligned with the client's <c>NetworkSettings.Default</c>)</para>
        /// </summary>
        public static KcpSettings Default { get; } = new KcpSettings();

        #endregion

        #region Methods

        /// <summary>
        /// 把参数应用到 KCP 实例（每个会话各自持有一个 KCP 实例）
        /// <para>Apply the parameters to a KCP instance (one instance per session)</para>
        /// </summary>
        /// <param name="kcp">目标 KCP 实例<para>Target KCP instance</para></param>
        public void ApplyTo(Kcp kcp)
        {
            kcp.NoDelay(NoDelay ? 1 : 0, Interval, FastResend, NoCwnd ? 1 : 0);
            kcp.WndSize(SndWnd, RcvWnd);
            kcp.SetMtu(Mtu);
            kcp.SetDeadLink(DeadLink);
        }

        #endregion
    }
}
