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
# 1. 启动服务端（默认 UDP 0.0.0.0:7777 + HTTP 0.0.0.0:8080）
dotnet run --project src/EFramework.App

# 2. 换端口 / 打开 Debug 日志
dotnet run --project src/EFramework.App -- --port 7801 --http-port 8081 --verbose

# 3. 手工探针（另一个终端）：Ping/Pong + 业务回声
dotnet run --project tests/EFramework.Tests -- probe 127.0.0.1 7777

# 4. 管理面（HTTP）
curl http://127.0.0.1:8080/health
curl http://127.0.0.1:8080/sessions
curl -X POST "http://127.0.0.1:8080/admin/broadcast?opcode=1003" --data-binary "hello"

# 5. 跑全部验证用例
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
│  ├─ EFramework.Http/                # HTTP 宿主（net8.0 + Kestrel，仅管理面）
│  │  ├─ GameServerBridge.cs          # HTTP ↔ 游戏服唯一桥（Post / PostAsync）
│  │  ├─ GameNetHostedService.cs      # 把 UDP 游戏服挂到通用主机生命周期
│  │  ├─ HttpEndpoints.cs             # /health /stats /sessions /admin/*
│  │  └─ HttpServerOptions.cs         # 绑定地址与端口
│  └─ EFramework.App/                 # 可执行入口 + Echo 示例业务（两个宿主的组合根）
├─ tests/EFramework.Tests/            # 零依赖验证工程（同时是协议探针）
└─ tools/sync-protocol.py             # 从 Unity 包重新同步协议文件的脚本
```

依赖方向单向：`App → Http → Server → Net`，`Net` 不引用任何服务端类型，`Server` 不引用 ASP.NET。

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
| 版本 | `package.json` 的 `0.0.2` | `0.0.2`（`Directory.Build.props`） | 服务端版本跟着包走，不另立版本线 |

> **帧解码有两个重载**：`TryDecodePacket(sessionId, frame, out packet)` 把整块缓冲当作一条消息（旧行为，
> 保留兼容）；`TryDecodePacket(sessionId, frame, offset, length, out packet)` 只认指定消息切片、按<b>消息长度</b>
> 校验载荷。**复用收包缓冲的会话必须用后者** —— 否则对端伪造一个超长 `payloadLength`，解码方会把上一条消息
> 残留在缓冲里的字节当成载荷读出去。客户端 `NetworkSession` 与服务端 `NetSession` 都已改用切片重载。

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

`KcpSettings` 的两条约束：

- **构造期校验**：非法值（`Mtu` 越界、窗口 &lt;1 或 &gt;65535、`Interval`/`DeadLink` 为 0）会在
  `new NetServer(...)` 时直接抛 `ArgumentOutOfRangeException`，而不是等首个数据报到达才在会话构造里
  每包抛一次异常。窗口在段头里是 uint16，超过 65535 会被静默截断成错误的窗口通告——这类值必须拦。
- **`KcpSettings.Default` 是模板，不是全局配置**：宿主在构造时会 `Clone()` 一份自用，之后改
  `Default.Mtu` 不会影响已建服务端；要自定义就 `KcpSettings.Default.Clone()` 后改副本再传入。

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

## HTTP 与游戏服的关系

同一个进程里跑两个宿主，**不共享线程**，只通过 `Post` / `PostAsync` 单向协作：

| | 游戏服（`EFramework.Server`） | HTTP 管理面（`EFramework.Http`） |
|---|---|---|
| 协议 | UDP + KCP（12 字节帧） | HTTP/1.1（Kestrel） |
| 线程 | 定频单线程泵（默认 10ms） | 线程池异步 |
| 状态归属 | **权威**：会话、KCP、业务 | 只读快照 + 下发命令 |
| 典型用途 | 实时玩法、位置同步、房间 | 健康检查、后台运维、配置/登录等短请求 |

数据流向只有两条，且都经过 `Post`：

```
HTTP 线程 ──Post(命令)──▶ 泵线程（广播 / 踢人 / 关服）
HTTP 线程 ──PostAsync(查询)─▶ 泵线程（统计 / 会话列表）── 返回快照
```

### 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/` | 服务名、版本与端点索引 |
| GET | `/health` | `{status, version}`，给探针/负载均衡用 |
| GET | `/stats` | 版本、运行时长、会话数、已处理/失败数、tick |
| GET | `/sessions` | 会话列表（Id、端点、conv、状态、收发计数、WaitSnd） |
| POST | `/admin/broadcast?opcode=<n>` | 请求体即原始载荷（`curl --data-binary`），广播给全部会话 |
| POST | `/admin/close/{id}?reason=<text>` | 关闭指定会话 |

```bash
curl -X POST "http://127.0.0.1:8080/admin/broadcast?opcode=1003" --data-binary "server maintenance in 5 min"
curl -X POST "http://127.0.0.1:8080/admin/close/7?reason=kicked"
```

### 为什么不做成"统一传输层"

`IServerTransport` 是**报文语义**（一条数据报一次出队、非阻塞、无连接），HTTP 是**流语义**（连接复用、
请求边界、背压、超时）。把它们抽象成一个接口，得到的只会是"两边都不好用"的最小公倍数——
比如为了迁就 UDP 而放弃流式响应，或为了迁就 HTTP 而在泵里引入异步边界。

正确的复用层次是**领域层**（会话、广播、统计），而不是传输层。

### 什么时候该拆成两个进程

同进程的收益是零成本的服务调用与单次部署；代价是一起扩缩容、一起崩溃。出现下列任一情况就拆：

- HTTP 侧要公网暴露，而游戏服只走内网（安全域不同）；
- 两侧扩缩容节奏不同（HTTP 扛静态资源/登录洪峰，游戏服按房间数扩）；
- HTTP 侧的依赖（数据库驱动、第三方 SDK）不想让游戏服承担。

拆的时候只需把 `EFramework.Http` 单独起一个进程，把 `Post` 换成 RPC/消息队列 —— 因为桥接层只有
`GameServerBridge` 一个文件，改这一处即可。

## 延迟与调优

单程延迟 ≈ 客户端 KCP 聚合间隔(10ms) + 服务端泵周期(`TickIntervalMs`) + 网络 RTT/2。
服务端的回包要等下一个节拍的 `Kcp.Update()` 才冲刷，所以：

- 弱延迟敏感场景（战斗同步）：`TickIntervalMs = 5`，CPU 占用约翻倍。
- 常规 MMO / 房间服：保持 `10`。
- 纯挂机/聊天服：`20~33` 即可，显著省 CPU。

排查工具：`NetSession.DebugInfo`（KCP 内部水位）、`NetSession.WaitSnd`（持续增长 = 对端不回 ACK）、
`ServerDispatcher.HandledCount / FailedCount`、`NetServer.RejectedByLimit`。

### 收发路径的分配

| 环节 | 分配 | 说明 |
|---|---|---|
| 入站 UDP 缓冲 | **0** | 传输层用 `ArrayPool` 按包长取，路由器用完在 `finally` 里归还 |
| 入站帧解码 | 1 个载荷数组 | `NetPacket.Payload` 是独立拷贝，保证 handler 可以安全持有（不改这条语义） |
| 出站帧编码 | **0** | 复用会话级帧缓冲：`MessagePipe.Encode(..., destination, out length)` |

也就是说稳态下每条消息只剩「入站载荷拷贝」这一次小分配。要连它一起消掉，就得把 `NetPacket.Payload`
改成「借用会话缓冲、下一条消息到达即失效」，属于语义破坏性变更 —— 等压测数据显示它占比可观时再做。

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
8. **HTTP 管理面没有任何鉴权**：`/admin/*` 能广播与踢人。只可内网/本机使用；公网暴露前必须加
   认证（如 `MapGroup("/admin").RequireAuthorization()` + JWT/Basic）与限流。
9. **业务消息不要走 HTTP**：那会丢掉 KCP 的可靠性与顺序性，还得重新实现一遍。HTTP 只服务运维与
   "一次请求一次响应"的短交互（登录、配置、公告拉取）。

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
