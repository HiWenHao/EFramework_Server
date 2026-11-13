/*
 * ================================================
 * Describe:      KCP tuning knobs for the server side (wire-relevant subset).
 * Author:        EFramework.Server
 * CreationTime:  2026-11-13 12:50:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;

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
    /// <para>用完请 <see cref="Validate"/>；框架会在宿主构造期校验并持有 <see cref="Clone"/> 的副本。</para>
    /// <para>Call <see cref="Validate"/> before use; the host validates it at construction and keeps a
    /// <see cref="Clone"/>.</para>
    /// </remarks>
    public sealed class KcpSettings
    {
        #region Fields

        // 窗口在 KCP 段头里占 2 字节（uint16），超出会被静默截断成错误的窗口通告
        private const int MaxWindow = 65535;

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
        /// 默认参数（共享实例，当作模板用，不要就地修改；需要调整请 <see cref="Clone"/> 后改副本）
        /// <para>Default settings (a shared instance; treat it as a template and do not mutate it in place —
        /// clone it first)</para>
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

        /// <summary>
        /// 参数合法性校验：非法值在启动期抛出，而不是等第一个数据报到达时才在会话构造里炸。
        /// <para>Validates the values so illegal tuning fails at startup instead of blowing up inside session
        /// construction when the first datagram arrives.</para>
        /// </summary>
        /// <remarks>
        /// <para>这里只拦「会被 KCP 静默忽略或被截断」的取值，不设任意的性能上限：
        /// <c>Kcp.SetMtu</c> 对越界直接抛异常；<c>WndSize</c> 与 <c>SetDeadLink</c> 对 &lt;=0 静默保留默认值
        /// （配置写错却毫无反馈）；窗口在段头里是 uint16，超过 65535 会被截断成错误的窗口通告。</para>
        /// <para>Only values that KCP silently ignores or truncates are rejected; no arbitrary performance
        /// caps: SetMtu throws on out-of-range, WndSize/SetDeadLink silently keep defaults for &lt;=0
        /// (a typo gives no feedback at all), and the window is uint16 in the segment header, so values above
        /// 65535 truncate into a wrong window advertisement.</para>
        /// </remarks>
        public void Validate()
        {
            if (Mtu is < 50 or > 1500)
                throw new ArgumentOutOfRangeException(nameof(Mtu), Mtu, "Mtu must be within 50..1500 (the same range Kcp enforces).");

            if (SndWnd < 1 || SndWnd > MaxWindow)
                throw new ArgumentOutOfRangeException(nameof(SndWnd), SndWnd, "SndWnd must be within 1..65535 (the window is uint16 on the wire).");

            if (RcvWnd < 1 || RcvWnd > MaxWindow)
                throw new ArgumentOutOfRangeException(nameof(RcvWnd), RcvWnd, "RcvWnd must be within 1..65535 (the window is uint16 on the wire).");

            if (Interval < 1)
                throw new ArgumentOutOfRangeException(nameof(Interval), Interval, "Interval must be >= 1 ms; 0 silently falls back to 100ms inside Kcp.");

            if (FastResend < 0)
                throw new ArgumentOutOfRangeException(nameof(FastResend), FastResend, "FastResend must be >= 0 (0 disables fast retransmit).");

            if (DeadLink < 1)
                throw new ArgumentOutOfRangeException(nameof(DeadLink), DeadLink, "DeadLink must be >= 1; Kcp silently ignores values <= 0.");
        }

        /// <summary>
        /// 复制一份参数：用于把「调用方的模板」与「框架实际使用的配置」隔离。
        /// <para>Returns a copy, isolating the caller's template from the configuration the framework actually
        /// uses.</para>
        /// </summary>
        /// <remarks>
        /// <see cref="Default"/> 是进程级共享实例。若框架直接持有它，启动之后再有人改
        /// <c>KcpSettings.Default.Mtu</c>，会静默影响此后新建的每一个会话（已有会话不受影响），
        /// 这类"改了却不生效/改了却影响别处"最难排查。持有副本可以彻底断开这条隐式耦合。
        /// <para><see cref="Default"/> is a process-wide shared instance. If the framework held it directly,
        /// mutating <c>KcpSettings.Default.Mtu</c> after startup would silently affect every future session.
        /// Holding a copy breaks that implicit coupling.</para>
        /// </remarks>
        /// <returns>参数副本<para>A copy of the settings</para></returns>
        public KcpSettings Clone()
        {
            return new KcpSettings
            {
                NoDelay = NoDelay,
                Interval = Interval,
                FastResend = FastResend,
                NoCwnd = NoCwnd,
                SndWnd = SndWnd,
                RcvWnd = RcvWnd,
                Mtu = Mtu,
                DeadLink = DeadLink,
            };
        }

        #endregion
    }
}
