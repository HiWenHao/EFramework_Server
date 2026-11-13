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
        }

        #endregion
    }
}
