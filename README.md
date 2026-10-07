# Dipping Monitor（通道录像）

独立程序，**不并入 NCM Monitor**。主界面与窗口标题为 **「Dipping Monitor」**。左右两台与 NCM 同型号的海康 SCMVS 智能相机，Live 下录一段 dipping 视频，供人眼检查 10 个通道。二期再自动判断堵塞。

**工程目录：** `E:\yb2\软件开发\Dipping`  
**最近 commit：** `cd3b470` — Dipping Monitor 标题与绿底 YB 图标、发布配置、README/Git 说明。  
**不要和正在跑的 NCM、官方 SCMVS 同时登录同一台相机。** 默认 IP：左 `192.168.2.50`、右 `192.168.2.51`（NCM 产线相机是 `.100` / `.101`）。

**应用图标：** `Dipping.WPF/Assets/app.ico`（绿底白字 YB，多尺寸 ICO；`ApplicationIcon` + 各窗口 `Icon="/Assets/app.ico"`）。

一期录像与 TCP 触发已按当前约定实现。关程序须用窗口关闭（不要用 VS「停止调试」），以便写回相机触发并停跑。

---

## 一期（已实现）

- 登录不改相机已有触发/运行模式。Live 临时改为内部触发连续取流；**停预览 / 登出 / 关闭程序**时写回登录时记下的 TriggerMode、TriggerSource，并 `StopRun`（不再 StartRun，避免机身灯一直亮）
- 一次录像默认 **3 秒**，配置可改；时长上限默认 60 秒
- **手动录像** + **停止录像**；左右各自勾选 **自动录像** 后，该相机才执行上位机开录指令。不勾选时 TCP 仍监听、仍回 OK，只是不录像，用手按「录像」
- **拍照 / 连拍**：Live 中抓当前 JPEG；未 Live 则软件触发。目录、张数、间隔、「最快」在配置里
- 主界面：**配置**、**相机监控**（`Show`，不锁主窗）。监控默认约 980×644；关监控窗不登出
- TCP 始终监听（不依赖是否勾选自动录像）。左 `REC_START_DIPPING_L`，右 `REC_START_DIPPING_R`
- 片子先写本地目录，再复制共享 UNC（空则不复制）
- 通道框/分析接口已留；监控右侧通道表已去掉。主界面 10 通道表一期显示「—」
- Live 超过约 5 秒无新图：红字「相机未更新图像，请检查相机及其接线」。不自动登出、不自动重连
- 监控按钮：可点蓝、不可点浅灰、登录/Live 绿、录像中停止键红、连拍橙

## 二期预留

| 位置 | 作用 |
|------|------|
| `IChannelAnalyzer` / `NullChannelAnalyzer` | 换成读 AVI + 10 个 ROI，不必改录像流程 |
| `ChannelRoi`（config `channelRois`） | 每相机 10 框，像素坐标 |
| 主界面 10 通道表 | 通/堵/待分析 |
| 相机监控「显示通道框 / 显示检测结果」 | 叠在画面上 |
| `IRecordTrigger` / `PlcRecordTrigger` | PLC 接线后发 `StartRequested` |
| `DippingJob` | 一轮路径、帧数、通道结论、共享复制结果 |
| 历史查询 | 主界面按钮已禁用 |

---

## 运行

```powershell
dotnet build Dipping.sln
```

输出：`Dipping.WPF\bin\Debug\net8.0-windows\Dipping.exe`  
发布配置：`Dipping.WPF/Properties/PublishProfiles/FolderProfile.pubxml`（自包含单文件 x64；`PublishDir` 可按本机修改）

**VS2022：** 打开 `Dipping.sln`，启动项目设为 **Dipping.WPF**，再 F5。不要对 `Dipping.Core` 按 F5。

1. 关闭官方 SCMVS，也关闭占用同一相机的 NCM  
2. 主界面点 **相机监控**（启动已搜相机，窗内可再点 **查找相机**）  
3. 左右各自登录。关监控窗不登出；关 Dipping 主程序才登出并恢复相机  
4. 未勾选 **自动录像** 时点「录像」只录这一台；到时或「停止录像」后回到 Live  
5. **拍照 / 连拍** 按配置保存  
6. 录像路径：`本地根目录\yyyyMMdd_HHmmss\左相机.avi`（及右相机）  
7. 配置了共享目录则收尾整夹复制  

### PC 程序开录（TCP，默认端口 10100）

连接后发**一行**（须带行尾）：

```
REC_START_DIPPING_L
REC_START_DIPPING_R
```

- 左右完全独立，各算各的时段次数  
- 认出指令后**立刻**回 `OK`（表示收下，不是录完）。未登录、未勾自动录像、不在时段、次数用尽时仍可能已回 OK，但不开录  
- `OK` 的行尾与指令相同：对方 `\r` 则 `OK\r`，`\n` 则 `OK\n`，`\r\n` 则 `OK\r\n`  
- 编码跟指令：UTF-8/ASCII 或 UTF-16 LE/BE  
- 旧指令 `REC_START_DIPPING`（无 `_L`/`_R`）不开录、不回 OK  
- 连接保持，可反复发。上位机不必心跳  
- 关 Dipping 后须在调试助手里**断开再连接**；界面有时仍显示已连接，旧套接字到不了新进程  

### 按时段和次数限制

配置里「自动录像时按时段和次数限制」，**默认不勾选**（不限制）。勾选后：

- 每天 12 个两小时时段（从 07:00 起），每行：序号、启用、时间段、次数  
- 默认表：只启用 07:00–09:00、次数 1；其余行可改、可勾  
- 到点**不会**自动开录，必须收到 `_L`/`_R`  
- 不在启用时段或该相机该时段次数用尽：忽略指令  
- 次数按相机分开，记在 `Data/record-schedule-usage.json`（不是 `config.json`）。按 07:00 到次日 07:00 算一天。删该文件即清当天次数，程序会在下次成功开录时再建  
- 配置表必须点 **保存** 才对正在跑的程序生效；不保存关窗即丢草稿  
- 手动录像不受此表限制  

---

## 配置（config.json / 配置窗口）

配置窗打开高度约 576，输入框较矮；内容超出可滚动。保存才写入 `config.json`。

| 项 | 含义 |
|----|------|
| 左/右 IP | dipping 工位相机 |
| 账号密码 | 监控里左右各自登录，默认 `Admin` / `Abc1234` |
| 录像时长(秒) | 一次 dipping，默认 3 |
| 录像保存目录 | 空 = `Data\Videos` |
| 共享目录 | UNC，空 = 不复制 |
| 存图目录 | 拍照/连拍，空 = `Data\Images` |
| 连拍张数 / 上限 / 间隔 / 最快 | 与 NCM 连拍类似 |
| 自动录像 | 监控里左右各自勾选 |
| 自动信号源 | `PcProgram` 或 `Plc`（PLC 一期不真开录） |
| TCP 端口 | 默认 10100 |
| 自动录像时按时段和次数限制 | 总开关，默认关 |
| 时段表 | 12 行，不勾选总开关时仍可编辑 |

帧率等于 Live 实际出图。智能相机 `GetResultData` 没有工业相机裸流。

---

## 解决方案

```
Dipping/
├── Dipping.sln
├── Dipping.Core/     # 配置、TCP 触发、时段闸门、共享复制、二期分析接口
└── Dipping.WPF/      # Live、录像 AVI、界面
```

与 NCM 无项目引用。SDK DLL 从 NCM 工程复制了一份，改 Dipping 不会动 NCM 源码。

---

## Git 说明

- **commit** = 给当前代码拍快照；改文件保存 ≠ commit，需 `git add` + `git commit`
- 重要里程碑请 commit；推远程需 `git push`（若配置了 remote）
- **最近 commit：** `cd3b470` — Dipping Monitor 标题与绿底 YB 图标、发布配置、README/Git 说明
