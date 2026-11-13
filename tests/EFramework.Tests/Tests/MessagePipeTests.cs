/*
 * ================================================
 * Describe:      Frame codec conformance tests (12-byte header + payload).
 * CreationTime:  2026-11-13 13:55:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Text;
using EFramework.Tests.Harness;
using EasyFramework.Systems.Net;

namespace EFramework.Tests.Tests
{
    /// <summary>
    /// 帧编解码测试：验证与客户端 <c>MessagePipe</c> 的 12 字节头部布局逐字节一致。
    /// <para>Frame codec tests: verify the 12-byte header layout matches the client's <c>MessagePipe</c>
    /// byte for byte.</para>
    /// </summary>
    internal static class MessagePipeTests
    {
        #region Methods

        public static void Run(TestRunner runner)
        {
            TestRunner.Section("MessagePipe (frame codec)");

            runner.Run("Encode then decode round-trips opcode/rpcId/payload", () =>
            {
                byte[] payload = Encoding.UTF8.GetBytes("hello-ef-net");

                byte[] frame = MessagePipe.Encode(1001, 42, payload);

                Assert.Equal(MessagePipe.HeaderSize + payload.Length, frame.Length, "frame length");

                bool decoded = MessagePipe.TryDecode(frame, out int opcode, out int rpcId, out int offset, out int length);
                Assert.True(decoded, "decode should succeed");
                Assert.Equal(1001, opcode, "opcode");
                Assert.Equal(42, rpcId, "rpcId");
                Assert.Equal(MessagePipe.HeaderSize, offset, "payload offset");
                Assert.Equal(payload.Length, length, "payload length");
            });

            runner.Run("Header is little-endian int32 trio", () =>
            {
                byte[] frame = MessagePipe.Encode(0x01020304, 0x05060708, null);

                Assert.Equal(0x04, frame[0], "opcode byte 0");
                Assert.Equal(0x03, frame[1], "opcode byte 1");
                Assert.Equal(0x02, frame[2], "opcode byte 2");
                Assert.Equal(0x01, frame[3], "opcode byte 3");
                Assert.Equal(0x08, frame[4], "rpcId byte 0");
                Assert.Equal(0x00, frame[8] + frame[9] + frame[10] + frame[11], "payload length is zero");
            });

            runner.Run("Decode rejects a short frame", () =>
            {
                Assert.True(!MessagePipe.TryDecode(new byte[5], out _, out _, out _, out _), "5 bytes must be rejected");
                Assert.True(!MessagePipe.TryDecode(null, out _, out _, out _, out _), "null must be rejected");
            });

            runner.Run("Decode rejects illegal payload length", () =>
            {
                byte[] negative = MessagePipe.Encode(1, 0, null);
                negative[8] = 0xFF;
                negative[9] = 0xFF;
                negative[10] = 0xFF;
                negative[11] = 0xFF;
                Assert.True(!MessagePipe.TryDecode(negative, out _, out _, out _, out _), "negative length must be rejected");

                byte[] truncated = MessagePipe.Encode(1, 0, new byte[8]);
                Assert.True(!MessagePipe.TryDecode(truncated[..12], out _, out _, out _, out _),
                    "declared length beyond the buffer must be rejected");
            });

            runner.Run("TryDecodePacket copies the payload out of the frame", () =>
            {
                byte[] payload = Encoding.UTF8.GetBytes("original");
                byte[] frame = MessagePipe.Encode(1002, 7, payload);

                bool decoded = MessagePipe.TryDecodePacket(9, frame, out NetPacket packet);
                Assert.True(decoded, "decode should succeed");
                Assert.Equal(9, packet.SessionId, "session id");
                Assert.Equal(1002, packet.Opcode, "opcode");
                Assert.Equal(7, packet.RpcId, "rpcId");
                Assert.Equal(payload.Length, packet.PayloadLength, "payload length");

                frame[MessagePipe.HeaderSize] = (byte)'X';
                Assert.Equal((byte)'o', packet.Payload[0], "payload must be an independent copy");
            });

            runner.Run("Empty payload decodes to a zero-length packet", () =>
            {
                byte[] frame = MessagePipe.Encode(NetOpcode.Ping, 0, null);
                Assert.True(MessagePipe.TryDecodePacket(0, frame, out NetPacket packet), "decode should succeed");
                Assert.Equal(0, packet.PayloadLength, "payload length");
                Assert.True(packet.Payload == null, "payload stays null for an empty frame");
            });

            runner.Run("Decode rejects a payload length that exceeds the message slice", () =>
            {
                // 复用大缓冲：真实消息 20B，但帧头声称有 100B 载荷，且这个声称在缓冲容量内「放得下」
                byte[] frame = MessagePipe.Encode(1001, 0, new byte[8]);
                var buffer = new byte[256];
                Buffer.BlockCopy(frame, 0, buffer, 0, frame.Length);

                buffer[8] = 100;
                buffer[9] = 0;
                buffer[10] = 0;
                buffer[11] = 0;

                Assert.True(!MessagePipe.TryDecode(buffer, 0, frame.Length, out _, out _, out _, out _),
                    "declared payload beyond the message slice must be rejected");
                Assert.True(!MessagePipe.TryDecodePacket(1, buffer, 0, frame.Length, out _),
                    "packet decode must reject it too");

                // 旧重载按「整块缓冲视为一条消息」校验，因此同一份数据是合法的 —— 这正是复用缓冲的会话
                // 必须显式传消息长度的原因（否则会读上一条消息的残留字节）
                Assert.True(MessagePipe.TryDecode(buffer, out _, out _, out _, out _),
                    "the whole-buffer overload cannot tell the difference: that is why the slice overload exists");
            });

            runner.Run("Encode into a destination buffer reports overflow instead of throwing", () =>
            {
                var payload = new byte[4];
                var tooSmall = new byte[MessagePipe.HeaderSize];

                Assert.True(!MessagePipe.Encode(1, 0, payload, tooSmall, out int rejected), "header-only buffer is too small");
                Assert.Equal(0, rejected, "nothing is reported as written on failure");

                var exact = new byte[MessagePipe.HeaderSize + payload.Length];
                Assert.True(MessagePipe.Encode(1, 7, payload, exact, out int written), "exact fit must succeed");
                Assert.Equal(MessagePipe.HeaderSize + payload.Length, written, "written length");

                Assert.True(MessagePipe.TryDecode(exact, out int opcode, out int rpcId, out _, out int length),
                    "the encoded frame must decode back");
                Assert.Equal(1, opcode, "opcode");
                Assert.Equal(7, rpcId, "rpcId");
                Assert.Equal(payload.Length, length, "payload length");
            });
        }

        #endregion
    }
}
