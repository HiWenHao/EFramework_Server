# EFramework_Server

基于 Unity 客户端包 **`cn.efefef.net`（EF.Net）** 对齐实现的 .NET 服务端基础框架。
**启动后即可被 EF.Net 客户端直连**：同一套 KCP 算法、同一种 12 字节消息帧、同一组 Opcode，客户端零改动。

- 协议层逐字节复用客户端源码（`Kcp.cs` / `MessagePipe.cs` / `NetOpcode.cs` / `NetProtocolException.cs`），不做"重新实现"。
- 零第三方依赖（无 NuGet 包），日志 / 序列化 / 传输全部走接口，可替换。
- 单线程消息泵：所有 KCP 状态与业务处理器跑在同一线程，会话无需加锁。

## 已验证结论

```
dotnet build EFramework_Server.sln            -> 0 error / 0 warning
dotnet run --project tests/EFramework.Tests   -> ALL PASSED (17 cases)
```

覆盖：帧编解码（含非法帧拒绝）、KCP 回环（200 条变长消息顺序 + 48KB 单条大消息 + 死链判定）、
真实 UDP 回环上的 Ping/Pong、类型化 RPC、**两个客户端同 conv=1 不串会话**、握手通过与拒绝、
空闲回收、未知 Opcode 与抛异常处理器不拖垮服务端、停机通知。

进程级实测（`dotnet run --project src/EFramework.App` + 探针）：

```
Probe -> 127.0.0.1:7801
  [OK] Pong     opcode=2 rtt=56ms localPort=64969
  [OK] EchoRes  opcode=1002 rpcId=1 body={"Content":"hello from probe","ServerUnixMs":...,"SessionId":1}
Probe PASSED
```

## 快速开始

```bash
# 1. 启动服务端（默认 0.0.0.0:7777）
dotnet run --project src/EFramework.App

# 2. 换端口 / 打开 Debug 日志
dotnet run --project src/EFramework.App -- --port 7801 --verbose

# 3. 手工探针（另一个终端）：Ping/Pong + 业务回声
dotnet run --project tests/EFramework.Tests -- probe 127.0.0.1 7777

# 4. 跑全部验证用例
dotnet run --project tests/EFramework.Tests
```

## 目录结构

```
EFramework_Server/
├─ src/
│  ├─ EFramework.Net/                 # 协议契约层（netstandard2.1，可反向链接进 Unity 复用）
│  │  └─ Protocol/                    # Kcp / MessagePipe / NetOpcode / KcpSettings / [NetOpcode]
│  ├─ EFramework.Server/              # 服务端运行时（net8.0）
│  │  ├─ NetServer.cs                 # 组合根 + 生命周期（Start / StopAsync / Send / Broadcast）
│  │  ├─ ServerLoop.cs                # 定频泵：收包 -> 推进会话 -> 回收
│  │  ├─ BuiltInMessageHandlers.cs    # 内置 Ping→Pong、Handshake→HandshakeResult
│  │  ├─ Sessions/                    # NetSession（KCP 通道）+ SessionRouter（端点→会话）
│  │  ├─ Dispatch/                    # ServerDispatcher + MessageContext + MessageHandler<T>
│  │  ├─ Transport/                   # IServerTransport + UdpServerTransport
│  │  ├─ Handshake/                   # IHandshakeValidator + 默认放行实现
│  │  └─ Logging/, Serialization/      # 控制台日志 + System.Text.Json 序列化器
│  └─ EFramework.App/                 # 可执行入口 + Echo 示例业务
├─ tests/EFramework.Tests/            # 零依赖验证工程（同时是协议探针）
└─ tools/sync-protocol.py             # 从 Unity 包重新同步协议文件的脚本
```

依赖方向单向：`App → Server → Net`，`Net` 不引用任何服务端类型。

## 与 EF.Net 的协议对齐

| 项目 | 客户端 `cn.efefef.net` | 本服务端 | 说明 |
|---|---|---|---|
| 传输 | UDP（`UdpSocket`） | UDP（`UdpServerTransport`） | 一个套接字服务所有端点 |
| 可靠层 | `Kcp.cs` | 同一份源码 | 逐字节一致 |
| 帧头 | 12B：`opcode/rpcId/payloadLength`（小端 int32） | 同一份源码 | `MessagePipe` |
| conv | 客户端进程内自增（从 1 开始） | 取**首个数据报**的 conv | 见下方「关键设计」 |
| 会话身份 | — | **远端端点 ip:port** | 不按 conv 建会话 |
| 心跳 | 每 5s 发 `Ping`，携 8B 时间戳 | 回显为 `Pong` | 客户端据此算 RTT |
| 握手 | 有 Token 才发 `Handshake` | `HandshakeResult`：`[code:1][reason:UTF-8]` | 无 Token 则跳过 |
| RPC | `Call<T>` 发 rpcId≠0 | 回包时回显同一 rpcId | 客户端按 rpcId 配对 |
| 序列化 | 无内置，需注入 | 默认 `JsonMessageSerializer` | 两侧必须同构 |
| Opcode | `1~999` 框架 / `1000+` 业务 | 相同 | `NetOpcode` |
| 版本 | `package.json` 的 `0.0.1` | `0.0.1`（`Directory.Build.props`） | 服务端版本跟着包走，不另立版本线 |

### 关键设计：为什么按端点而不是 conv 建会话

客户端 `NetworkManager` 的 conv 是**进程内**自增（`_nextConv = 1`），所以每个玩家进程的第一个会话都是 `conv=1`。
若服务端按 conv 建会话，两个玩家会撞进同一条 KCP 通道 —— 表现为"两人互相收到对方的消息"。

因此服务端以 `IPEndPoint` 作为会话身份，conv 只用于**同一端点内检测通道重建**（conv 变化即重建 KCP 实例）。
这一条已由用例 *"Two clients sharing conv=1 get independent sessions"* 锁定。

另一条由此衍生的行为：**服务端会话在收到首包时才建立**（协议没有单独的 connect 包）。
客户端连上后若 5s 内不发数据，服务端还看不到它；心跳包到达即建会话。

## Unity 客户端直连（零改动）

```csharp
using System.Text;
using EasyFramework.Systems.Net;

// 1. 连接（不传 token = 跳过握手，直接就绪）
var session = NetworkManager.Instance.Connect("127.0.0.1", 7777);

// 2. 注册回包处理器（原始字节，无需序列化器）
NetworkManager.Instance.RegisterHandler(1002, packet =>
{
    string json = Encoding.UTF8.GetString(packet.Payload, 0, packet.PayloadLength);
    Debug.Log($"[EchoRes] {json}");
});

// 3. 发请求（1001 = EchoReq）
session.Send(1001, Encoding.UTF8.GetBytes("{\"Content\":\"hi\"}"));
```

需要在客户端用强类型 + `Call<T>` 时，先注入与服务端同构的序列化器：

```csharp
NetworkManager.Instance.Serializer = new YourJsonSerializer();   // 字段名保持原样，勿开 camelCase
var res = await session.Call<EchoRes>(1001, Encoding.UTF8.GetBytes("{\"Content\":\"hi\"}"));
```

带握手（服务端 `RequireHandshake = true` 时必须）：

```csharp
var session = NetworkManager.Instance.Connect("127.0.0.1", 7777, token: "your-token");
NetworkManager.Instance.RegisterHandler(NetOpcode.HandshakeResult, packet =>
    Debug.Log(packet.Payload[0] == 0 ? "accepted" : "rejected"));
```

## 新增一条业务消息（3 步）

```csharp
// 1. 定义消息（Opcode 取 1000 以上，两侧一致）
[NetOpcode(1004)]
public sealed class LoginReq { public string Account { get; set; } }

[NetOpcode(1005)]
public sealed class LoginRes { public bool Ok { get; set; } public string Token { get; set; } }

// 2. 写处理器（只写业务，回包由 context.Reply 完成）
public sealed class LoginHandler : MessageHandler<LoginReq>
{
    public override void Handle(LoginReq message, MessageContext context)
    {
        context.Reply(new LoginRes { Ok = true, Token = "..." });   // 自动取 LoginRes 的 Opcode 并回显 rpcId
    }
}

// 3. 注册（启动前）
dispatcher.Register<LoginReq, LoginHandler>(new LoginHandler(logger));
```

`MessageContext` 还提供 `Reply(opcode, payload)`（原始字节）、`Push<T>(msg)`（单向推送，不回包）、
`GetString()` / `Deserialize<T>()`。服务端主动推送：`server.Broadcast(opcode, payload)` 或 `server.Send(sessionId, ...)`。

## 配置项

| 参数 | 默认 | 说明 |
|---|---|---|
| `BindAddress` | `0.0.0.0` | `*` 全部网卡，`::` IPv6 双栈，也可填具体 IP/主机名 |
| `Port` | `7777` | `0` = 系统分配（测试并行用） |
| `TickIntervalMs` | `10` | 泵周期，同时是收包处理与回包发出的延迟上限 |
| `SessionIdleTimeoutSeconds` | `30` | 无任何数据报超过该时长即回收；`<=0` 关闭 |
| `MaxSessions` | `4096` | 超出后新端点数据报直接丢弃（防扫描） |
| `RequireHandshake` | `false` | `true` 时未通过握手的会话除握手包外一律丢弃 |
| `ReceiveQueueCapacity` | `8192` | 内核 → 泵的缓冲，满则丢新包并计数 |
| `LogLevel` | `Info` | `Debug` 会打印每会话/每包细节 |

KCP 调参走 `KcpSettings`，**默认值与客户端 `NetworkSettings` 完全一致**
（`Mtu=512`、`WndSize=256/256`、`NoDelay(1,10,2,1)`、`DeadLink=20`）。两侧 MTU/窗口必须一致，
否则会出现"大消息卡死"或"带宽跑不满"。

## 线程模型

所有 KCP 状态与业务处理器都跑在**唯一一条消息泵线程**上，因此会话与业务状态无需加锁。代价是必须遵守以下三条：

| 调用点 | 允许的线程 | 说明 |
|---|---|---|
| `MessageHandler.Handle` / 所有处理器 | 泵线程 | 跑在泵里，**禁止阻塞**（同步 IO、`Thread.Sleep`、`lock` 长等待） |
| `NetSession.Send` | **任意线程** | 非泵线程会先入队，下一节拍由泵代发，额外延迟 ≤ 一个 `TickIntervalMs` |
| `NetServer.Send` / `Broadcast` / `CloseSession` | 仅泵线程 | 需要访问会话路由；跨线程调用会抛 `InvalidOperationException` |
| `NetServer.Post(work)` | 任意线程 | 把动作投递到泵线程执行（定时器、线程池回调、信号处理都用它） |

处理器里做慢活的标准写法（回包走 `NetSession.Send`，自动回泵线程）：

```csharp
public override void Handle(LoginReq req, MessageContext context)
{
    NetSession session = context.Session;   // 只带会话与业务数据出去，上下文本身不跨线程用
    int rpcId = context.RpcId;

    ThreadPool.QueueUserWorkItem(_ =>
    {
        var result = QueryDatabase(req.Account);              // 慢活
        session.Send(LoginResOpcode, rpcId, Serialize(result)); // 任意线程安全
    });
}
```

从外部（定时器/管理接口）操作服务端：

```csharp
server.Post(s => s.Broadcast(ServerNoticeOpcode, payload));
```

## 延迟与调优

单程延迟 ≈ 客户端 KCP 聚合间隔(10ms) + 服务端泵周期(`TickIntervalMs`) + 网络 RTT/2。
服务端的回包要等下一个节拍的 `Kcp.Update()` 才冲刷，所以：

- 弱延迟敏感场景（战斗同步）：`TickIntervalMs = 5`，CPU 占用约翻倍。
- 常规 MMO / 房间服：保持 `10`。
- 纯挂机/聊天服：`20~33` 即可，显著省 CPU。

排查工具：`NetSession.DebugInfo`（KCP 内部水位）、`NetSession.WaitSnd`（持续增长 = 对端不回 ACK）、
`ServerDispatcher.HandledCount / FailedCount`、`NetServer.RejectedByLimit`。

## 扩展点

| 接口 | 替换场景 |
|---|---|
| `IServerTransport` | 换 TCP / WebSocket / KCP over QUIC；会话层与分发层零改动（实现时需按约定实现 `Release` 归还数据报缓冲） |
| `IMessageSerializer` | Protobuf / MessagePack / MemoryPack（与客户端同步替换） |
| `IHandshakeValidator` | Token 验签、有效期、账号绑定、IP 白名单 |
| `IServerLogger` | Serilog / NLog / 云日志（记得实现 `IsEnabled`，热路径靠它避免无谓字符串分配） |

## 已知约束（上生产前必做）

1. **默认无鉴权**：`AcceptAllHandshakeValidator` 放行一切。公网部署必须实现 `IHandshakeValidator`，
   并置 `RequireHandshake = true`。
2. **conv 非随机**：客户端 conv 从 1 递增且明文，可被伪造。当前以端点隔离，风险可控；
   若要防会话劫持，建议改成"服务端分配 conv"或客户端随机 conv（需改客户端）。
3. **单线程泵**：处理器必须快速返回，禁止同步 IO / `Thread.Sleep`。慢逻辑投递线程池后用
   `NetSession.Send` 回包（任意线程安全），详见「线程模型」。
4. **实例不可重启**：`NetServer` 停机后传输通道关闭，重启请新建实例。
5. **无加密**：UDP 明文。需要防篡改时在帧层加签名或走 DTLS 类方案。
6. **重连是新端点**：客户端重连会新建 socket（新本地端口），旧会话靠空闲超时回收，属预期行为。
7. **多网卡要注意出口 IP**：客户端 `UdpSocket` 用 `Connect()` 绑定了目标地址，只接受**同一 ip:port** 回来的包。
   若服务端从别的网卡出口（源 IP 与客户端所连地址不一致），包会被客户端内核直接丢弃。
   多网卡机器请用 `BindAddress` 指定对外 IP，或用防火墙/host 绑定保证对称。

## 协议文件同步

`src/EFramework.Net/Protocol/` 下 4 个文件是从 Unity 包逐字节复制的，**不要就地修改**（文件头有 `SyncedFrom` 标注）。
客户端升级后重新同步：

```bash
python tools/sync-protocol.py                     # 默认 Unity 包路径
python tools/sync-protocol.py --package <包目录>   # 换机器/换 clone 时指定 cn.efefef.net 包目录
python tools/sync-protocol.py --check             # 只校验不写入；漂移时退出码 1（可挂 CI）

export EF_NET_PACKAGE=<包目录>                     # 或用环境变量省掉参数
```

脚本幂等（重复执行不产生差异），目标目录按脚本自身位置推导（`<repo>/src/EFramework.Net/Protocol`），
因此不依赖 checkout 到哪个盘符。`KcpSettings.cs` 与 `NetOpcodeAttribute.cs` 是服务端侧新增文件，不受同步影响。
