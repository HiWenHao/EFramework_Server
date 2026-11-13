# 怎么测

> 这套东西有四层测试面，**从下往上验证**：下面一层不通，上面一层必然不通。
> 每一层都有明确的"通过标准"，不要凭感觉判断。

| 层 | 测什么 | 需要什么 | 通过标准 |
|---|---|---|---|
| L1 | 协议与运行时（33 个用例） | dotnet | `ALL PASSED (33 cases)` |
| L2 | 真实 UDP 链路（命令行探针） | dotnet | `Probe PASSED` |
| L3 | Unity 客户端端到端（测试面板） | Unity | 面板上 ping/pong 有 RTT、echo 有回包 |
| L4 | 容量与延迟（压测） | dotnet | 丢包 0%、泵利用率 < 30% |

## L1 · 服务端自测（最快，10 秒）

```bash
cd D:/_Projects/CSharp/EFramework_Server
dotnet run --project tests/EFramework.Tests
```

覆盖：帧编解码边界、KCP 可靠传输、端到端收发、握手鉴权、背压与限流、管理面桥接。

输了就看第一个 FAIL 的名字——它直接指向出问题的子系统。

## L2 · 命令行探针（验证服务端自身收发）

```bash
# 终端 1：起服务端
dotnet run --project src/EFramework.App -- --port 7777 --http-port 8080

# 终端 2：探针
dotnet run --project tests/EFramework.Tests -- probe 127.0.0.1 7777
```

预期输出：

```
Probe -> 127.0.0.1:7777
  [OK] Pong     opcode=2 rtt=21ms localPort=60586
  [OK] EchoRes  opcode=1002 rpcId=1 body={"Content":"hello from probe",...}
Probe PASSED
```

### 开了鉴权时

```bash
# 服务端：给密钥 + 要一张开发票据（横幅里会打印）
dotnet run --project src/EFramework.App -- --port 7777 --auth-secret dev-secret --dev-token alice

# 复制横幅里那行，直接跑
dotnet run --project tests/EFramework.Tests -- probe 127.0.0.1 7777 "alice:1794557215:9aa6..."
```

票据三部分：`账号:过期时间戳:HMAC 签名`。签名错 → `code=3`；过期 → `code=4 token expired`。

## L3 · Unity 客户端端到端（你最需要的那层）

工程里已经放好一个零配置面板：`Assets/NetSmokeTest/NetSmokeTest.cs`。

**步骤**

1. Unity 打开 `D:/_Projects/Unity/EFramework_Package/EF_Unity`（2022.3.62f2）。
   首次打开会编译，先确认 Console **没有红色报错**——有错就先解决编译，面板不会出现。
2. 任意场景按 **Play**。面板会自动出现在左上角（`[RuntimeInitializeOnLoadMethod]` 自动挂载，不用拖组件、不用改场景）。
3. 面板填 `host=127.0.0.1`、`port=7777`（与服务端 `--port` 一致），token 留空（服务端未开鉴权时）。
4. 点 **Connect** → 点 **Ping** → 点 **Echo 1001**。

**通过标准**

```
state: connected  session=1
pong: 3  rtt last=21ms avg=24ms
last echo: {"Content":"unity 15:42:10","ServerUnixMs":...,"SessionId":1}
```

底部日志应依次出现 `handlers registered` → `connect -> ...` → `ping sent` → `echo <- rpcId=1 ...`。

**测完怎么关**

- 临时：把文件顶部 `AutoInstall` 改成 `false`；
- 彻底：删掉 `Assets/NetSmokeTest/` 整个目录（它不属于框架包，只是本地验证工具）。

> 面板只依赖协议层（原始 Handler），不需要注入序列化器——这样"能不能连通"这件事不会被业务序列化问题掩盖。

## L4 · 压测（容量与延迟）

```bash
dotnet run --project tests/EFramework.Tests -- load 127.0.0.1 7777 \
    --clients 100 --qps 10 --seconds 10 --stats-url http://127.0.0.1:8080/stats
```

判读方式与「哪些数字不能信」见 [`load-test.md`](load-test.md)。
运维只需盯一个比值：`/stats` 的 `actualTickWorkMs / actualTickIntervalMs`（泵利用率），< 30% 健康。

## 排查表（从现象倒推）

| 现象 | 最可能的原因 | 怎么确认 / 怎么修 |
|---|---|---|
| Unity 里看不到面板 | 编译失败，或不在 Editor/Development Build | 看 Console 第一条红字；确认顶部 `#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD` 生效 |
| 点 Connect 后 `state: connected` 但 Ping 无回包 | 服务端没起 / 端口不对 / 防火墙 | 服务端日志有没有 `[SESSION+]`；换端口试；本机排查先用 127.0.0.1 |
| 有 Pong 但 Echo 无回包 | 服务端 JSON 反序列化失败（字段名大小写） | 服务端日志有 `[ECHO]`？没有就是解析失败，检查 `{"Content":"..."}` |
| `handshake REJECTED code=3` | token 与服务端 `--auth-secret` 不匹配 | 用服务端 `--dev-token` 打的那张票，别自己拼 |
| `code=4 token expired` | 票据过期（默认 1 小时） | 重新用 `--dev-token` 打一张 |
| 服务端日志 `opcode X dropped: handshake pending` | 服务端开了 `RequireHandshake`，客户端没带 token | 面板 token 填上，或服务端去掉 `--auth-secret` |
| 服务端日志 `frame decode failed` | 客户端与服务端协议文件不一致 | 跑 `python tools/sync-protocol.py --check` |
| 服务端日志 `send backlog overflow` | 对端长时间不回 ACK（网络断/客户端卡住） | 属保护机制生效；确认客户端是否卡在断点或主线程 |
| 服务端日志 `receive rate limit ... hit` | 客户端单秒发太多（默认上限 512/s） | 调 `ServerOptions.MaxMessagesPerSecond`，或业务侧降频 |
| 延迟明显偏高 | `TickIntervalMs` 被系统计时器粒度吃掉 | 看 `/stats` 的 `actualTickIntervalMs`，Windows 上若 ~15.6ms 就设 `TickIntervalMs >= 16` |

## 一句话原则

**先证明下层通，再查上层。** 客户端连不上时，先在 L2 用探针确认服务端是好的；
L2 通过就说明问题在 Unity 侧（编译、场景、token、防火墙），不用再去翻服务端代码。
