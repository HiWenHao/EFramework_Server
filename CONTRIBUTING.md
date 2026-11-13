# 贡献指南

本仓库是 EF.Net 的服务端实现，与 Unity 客户端包 `cn.efefef.net` **共享同一套线上协议**。
长期维护的核心风险不是功能演进，而是「协议悄悄漂移」与「构建门槛悄悄退化」，本文的规则都围绕这两点。

## 1. 提交前必过的三道门

```bash
dotnet build EFramework_Server.sln -c Release    # 必须 0 error / 0 warning
dotnet run --project tests/EFramework.Tests      # 必须 ALL PASSED
python tools/sync-protocol.py --check            # 改过协议层或其上游时才需要
```

- **0 warning 是硬要求**，不是建议。当前基线就是 0，任何新增告警都会掩盖后面真正的问题。
- 新增/修改能力必须补用例。现有 17 个用例是回归基线，只增不减。
- 涉及 `EFramework.Net/Protocol/` 的改动，必须同时证明协议仍与客户端一致（见 §4）。

## 2. 提交信息

仓库已配置提交模板（`.gitmessage`）。若换机器后模板失效，执行一次：

```bash
git config commit.template .gitmessage
```

格式：`<type>(<scope>): <简述>`，采用 Conventional Commits 精简版。

| type | 用途 | scope | 对应范围 |
|---|---|---|---|
| `feat` | 新增能力，对外行为变化 | `net` | 协议契约层（KCP / 帧格式 / Opcode） |
| `fix` | 修复缺陷 | `server` | 服务端运行时（会话、分发、宿主） |
| `refactor` | 重构，行为不变 | `http` | HTTP 管理面（桥接、端点、宿主托管） |
| `perf` | 性能优化 | `app` | 可执行入口与示例业务 |
| `test` | 测试相关 | `tests` | 验证工程 |
| `docs` | 文档 | `tools` | 脚手架与同步脚本 |
| `build` / `chore` / `revert` | 工程配置 / 杂务 / 回滚 | `build` | props / sln / CI |

```
feat(server): 支持服务端主动向单会话推送
fix(net): 修复 Opcode 高位为 1 时解码成负数
chore(tools): sync-protocol 增加 --check 供 CI 校验漂移
```

提交粒度：**一次提交只做一件事**。禁止把格式化、重命名、功能改动混在一起——回滚时你会感谢这条。

## 3. 分支与发布

- **trunk-based**：`master` 始终可构建、可用例。功能开发走短分支 `feat/xxx`、`fix/xxx`，合完即删。
- **版本号 = `cn.efefef.net` 的 `package.json` version**。本服务端是该包的协议对端，不另立版本线，
  所以版本号只有一个来源：`Directory.Build.props` 的 `<Version>`，它必须与包版本逐字相同。
  升级 EF 时改这一处，`NetServer.Version`（启动日志与横幅会用）随之变化。
- tag 用 `v<包版本>`（当前 `v0.0.1`），把「与哪个客户端包版本配对」固化在标签里。
- **协议层改动即 breaking**：只要动了帧格式、Opcode 语义、KCP 参数默认值，就必须先升客户端包版本，
  再同步服务端 `<Version>`，并在提交正文里写明「客户端需同步到哪个版本」——新旧客户端不兼容。
- 忘了同步会有兜底：`tools/sync-protocol.py` 的校验模式会比对两个版本号，不一致直接失败。

## 4. 协议层纪律（最容易出事的地方）

`src/EFramework.Net/Protocol/` 下这 4 个文件是**从 Unity 包逐字节复制**的，文件头有 `SyncedFrom` 标注：

```
Kcp.cs  MessagePipe.cs  NetOpcode.cs  NetProtocolException.cs
```

规则：

1. **绝不手改**。要改就先改客户端包 `cn.efefef.net`，再跑同步脚本把改动带过来。
2. 同步只有一个方向：客户端 → 服务端。
3. `KcpSettings.cs`（服务端 KCP 调参默认值）与 `NetOpcodeAttribute.cs`（服务端取码辅助）是本仓库自有文件，
   不受同步影响，但前者**必须与客户端 `NetworkSettings` 默认值一致**，否则会出现「大消息卡死」。
4. 换行由 `.gitattributes` 归一（仓库内 LF，工作区按平台检出）。**不要**给协议文件加 `-text`，
   那会让工作区换行跟随 checkout 平台变化，`sync-protocol.py` 会误报漂移。

### Opcode 分配

`1~999` 是框架保留区（`1 Ping` / `2 Pong` / `3 Disconnect` / `4 Handshake` / `5 HandshakeResult`），
业务从 `1000` 起。建议按模块每 1000 一段，避免多人协作撞号：

| 区间 | 用途 |
|---|---|
| 1000–1999 | 示例与调试（当前 Echo 占 1001–1003） |
| 2000–2999 | 账号 / 登录 / 角色 |
| 3000–3999 | 房间 / 匹配 / 战斗 |
| 4000 以上 | 按新模块顺延 |

新增业务消息时，先在 README 或本表登记区间，再动手写代码。

## 5. 代码风格

与 Unity 侧 EFramework 保持一致，便于两侧对照阅读：

- 文件头 banner（Describe / CreationTime / ScriptVersion）。
- 类内按 `#region Fields / Properties / Methods` 分组，字段 → 属性 → 方法，私有在前。
- 类型声明与非私有成员写**中英双语 XML 注释**（中文一行 + `<para>` 英文一行）；
  私有成员用 `//` 单行中文；重写接口/基类成员时若签名已自解释可省略。
- 不引入魔法数：可读常量或配置项。
- 处理器（`MessageHandler<T>`）里不做阻塞操作；跨线程回包用 `NetSession.Send`，外部线程操作服务端用 `NetServer.Post`，
  详见 README「线程模型」。

## 6. 新增一条业务消息

1. 定 Opcode（查上表，登记区间）。
2. 定义消息类 + `[NetOpcode(n)]`。
3. 写 `MessageHandler<T>`，用 `context.Reply(...)` 回包。
4. `dispatcher.Register<TReq, THandler>()` 注册。
5. 补一条端到端用例（发送 → 等待 → 断言），跑 `tests`。

## 7. 文档同步

改动下列内容时，同一次提交里更新 README：

- 目录结构、工程划分
- 配置项（`ServerOptions` / `KcpSettings`）
- 对外用法、客户端对接方式
- 协议相关事实（会话建立时机、握手格式、RPC 约定）

## 8. 持续集成

`.github/workflows/ci.yml` 在 push 到 `master` 与 PR 时执行 Release 编译 + 全量用例。
协议同步校验（`sync-protocol.py --check`）**不在 CI 里跑**——CI 拿不到 Unity 包，这一步只能本地做。
