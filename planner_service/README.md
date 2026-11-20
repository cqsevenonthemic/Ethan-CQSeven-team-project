# Python 规划服务（成员 B）

P0（服务0.2.0）：FastAPI 无状态服务、严格快照校验、带朝向 A*、实际踩访覆盖、
双人物单容量调度、拒收纠偏、扫描停滞等待，以及真实执行 JSONL 回放和批量测试。
服务不读取 Unity 的 `eval.json`，不持有跨请求机器人或人物状态。
接口基线见 `../codex.md` 第六节和 [api_v1.md](../contracts/api_v1.md)。
共享契约中的 501 说明记录 P0 状态，P2 的实际行为见下文和运行服务的 `/openapi.json`。

## 环境与安装

下列 PowerShell 命令均在仓库根目录运行。使用 CPython **3.12**；系统默认 3.14 不作为本项目环境。
首次创建虚拟环境时，使用本机 Python 3.12 解释器：

```powershell
py -3.12 -m venv .venv
```

如 `py -3.12` 未注册，直接用已有 3.12 `python.exe` 的完整路径创建。
每个 worktree 使用独立的 `.venv`。VS Code 选择当前 worktree 的 `.venv/Scripts/python.exe`。

```powershell
.\.venv\Scripts\python.exe -m pip install -r planner_service/requirements-dev.lock
.\.venv\Scripts\python.exe -m pip install --no-deps -e ./planner_service
.\.venv\Scripts\python.exe -m pip check
```

`pyproject.toml` 声明兼容范围，`requirements-runtime.lock` / `requirements-dev.lock`
记录本次 Windows/Python 3.12 实测的精确运行/开发依赖（开发锁已包含运行依赖）。
它们不是带哈希的跨平台锁；其他平台须重新验证。源码安装的 setuptools 构建环境由 pip 隔离创建。
仅运行时可把上述 dev 锁换为 runtime 锁。服务不需要 PyTorch、ONNX Runtime 或 ROS；
真实识别接入时再添加推理依赖，离线训练使用另一个环境。

## 启动

```powershell
.\.venv\Scripts\python.exe -m uvicorn planner_service.app:app --host 127.0.0.1 --port 8000
```

- 健康检查：`http://127.0.0.1:8000/health`
- Swagger：`http://127.0.0.1:8000/docs`
- OpenAPI：`http://127.0.0.1:8000/openapi.json`

在另一个终端，从仓库根目录把契约请求样例发给实时服务。

```powershell
$sampleBody = Get-Content -Raw -Encoding UTF8 contracts/sample_observe.json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8000/next_action -ContentType 'application/json' -Body $sampleBody
```

样例是完整 16×16 开放迷宫：机器鼠在 `(7,6)` 朝北，携带昭君，屈原已送达。
真实算法返回 `action:"forward"`、`status:"planning"`、`remaining_tasks:1`。
执行后到 `(7,7)`，Unity 更新 pose 和 carried 人物坐标：
服务返回 `awaiting_delivery/action:null`；Unity 确认身份后写入 delivered、verified=true、
carry_slot=null，再请求得到 `completed/done:true/action:null`。
不允许直接执行整条 trajectory，也不把 null 计为动作。

## 文件职责

```text
app.py                         HTTP、OpenAPI、错误处理、健康检查
schemas.py                     请求/响应、人物枚举、地图及快照一致性校验
service.py                     纯决策编排、拾取、完成/不可达/交付等待
planner/grid.py                墙掩码、边界、动作转移与安全通行
planner/oriented_astar.py      单位代价 (x,z,theta) A*
planner/frontier_explorer.py   已知可达子图上的最短观测动作
planner/progress.py            调用方持有的扫描进度、重复请求和停滞等待
planner/task_dijkstra.py       已知子图的完整保持/换人假设代价（包含朝向）
simulation/world.py            评估器持有的真实迷宫与人物，不传给决策器
simulation/sensor.py           前向180°、121束、0.15..4.5m、有遮挡的二维雷达
simulation/mock_recognizer.py  明确标记的测试识别替身，支持误识别注入
simulation/runner.py           模拟Unity的真实执行、踩格发现、身份校验及计步
simulation/replay.py           根据观测增量独立验证保存的执行轨迹
tests/test_planner.py          独立 BFS 最短路对照及动作验证
tests/test_api.py              API、契约、错误和无状态重试
tests/test_closed_loop.py      模拟 Unity 观测、动作、发现与交付闭环
tests/make_samples.py          可复现合成 Mock 生成器
tests/sim_benchmark_100.py      固定种子批测、错误原因、JSONL与JSON统计报告
requirements-*.lock            本次验证过的依赖版本
```

动作仅 forward/turn_left/turn_right，均代价 1；前进必须通过两侧都已确认无墙的边。
A* 的 optimistic 参数仅用于算法评估，HTTP 决策不启用它。
visited_cells 是实际踩访集合，由 Unity 更新；墙面全部可见并不意味着人物已搜索。
观测序号 observation_seq 每次采样递增；progress 完整回传，不保存服务器会话。
已知目标任务优先；无安全任务路径时先踩访已知可达未访问格，再转向观测未知边。
最多4次无新增信息的扫描旋转后 awaiting_observation/action:null；不会无限左右转。
旧客户端未提供访问记录时，已知目标仍可规划，但探索阶段安全等待协议适配。
未知目标位置省略；remaining_tasks=2-delivered 数，不会泄露隐藏人物坐标。
角色误识别可以暂时重复，但 delivered 必须由 Unity 验证；前端始终是实际环境状态的所有者。

## 测试与验收

```powershell
.\.venv\Scripts\python.exe -m pytest -c planner_service/pyproject.toml -q
.\.venv\Scripts\python.exe -m pip check
git diff --check
```

覆盖独立 BFS 与 A* 的 40 个固定随机迷宫、旋转计步、墙阻挡与绕行、未知边不可执行、
边界限制、有效 Mock、完成/等待/不可达、非法输入、未实现功能、内部异常、
交错请求隔离、两格走廊漏访回归、访问和观测进度、双目标身份约束、保持/换人比较、
拒收纠偏、前向传感器视角/量程/遮挡，以及两人物均未知的端到端闭环。
完整批测与回放命令：

```powershell
.\.venv\Scripts\python.exe -m planner_service.tests.sim_benchmark_100 --count 100 --start-seed 0 --fault-every 3 --output planner_service/artifacts/p0_seed_0_99
.\.venv\Scripts\python.exe -m planner_service.simulation.replay planner_service/artifacts/p0_seed_0_99
```

种子0..99生成连通16×16 DFS树迷宫/带环迷宫，随机安排不同位置的一名屈原、一名昭君；
默认起点(0,0)朝北，人物也允许生成在起点或目的地。每第3个场景故意把首次识别反转。
这是明确标记的 `P0_TEST_MOCK_RECOGNITION` 模式，不调用 /recognize，不能用于深度学习验收。
传感器按射线产生观测，真实地图与尚未触发的人物位置只存在评估器中。
精确格点角落的射线保守停止，不宣称三维点云或含噪声传感器仿真。
单例4096动作是固定的结构性失败预算，不靠延长超时通过测试。

`report.json` 保存模式、生成器版本、种子、错误注入配置、成功率、步数、耗时、失败原因及各场景结果；
`seed_XXXX.jsonl` 保存实际动作、前后位姿、累计步数、观测增量、人物状态、事件和决策。
executed=true 才计为真实动作；回放验证观测通路、容量、连续位姿和总步数。
runner 的 record_sensors=True 可在单例记录每帧射线；默认批测记录可重建地图的观测增量。
artifacts/ 已忽略，不会混入提交。生成算法和依赖变化后必须重新验收，不能把旧报告当新结果。
该批测不代表所有可能迷宫、Unity真机或真实图像识别均已通过。

依赖中的 Starlette 目前会对 TestClient 的 HTTPX 适配发出弃用警告；不影响本次测试通过，
升级测试传输适配前保持精确锁定版本，不屏蔽该警告。

修改共享样例须获得接口维护授权。授权范围内可重建样例并复测：

```powershell
.\.venv\Scripts\python.exe -m planner_service.tests.make_samples
```

## 当前边界及后续阶段

- 正常双目标在线流程不再返回501；已知地图下比较完整保持/换人路线，无信息证明改善时保持当前人物。
- 在线比较代价是调用时观测子图上的假设成本；执行成本由第一幕逐动作记录。
- delivery_rejected 会反转该未验证预测并验证，必要时修正另一未验证预测；重复原始快照确定性一致。
- 同时已验证为相同身份、矛盾拒收、非法访问或未来进度返回422；预测未验证时允许重复。
- Unity 分支已有访问记录、扫描进度和执行反馈字段，实际场景运行按下方清单验收。
- 后续开展更多地图与噪声测试、性能验收、PyInstaller -D 打包和 D7/D14/D18 联调。

P2 在独立的 `feature/rl-plan-summary` worktree 开发；Unity 业务代码由 A 维护。
Commit、Push、PR 和合并均分别等待用户批准。

## P2 离线训练与三幕接口

第一幕继续使用前沿 BFS、带朝向 A* 和任务 Dijkstra。两人确认交付后，Unity 可把累计墙掩码、
第一幕的初始位姿和按最终核验角色归档的两个人物原始发现坐标发送给 `POST /plan_summary`。
接口在该快照的已确认通路上重放派送任务。Q 表由此快照限定，状态键含 `(x,z,theta)`、
携带角色和两人的等待坐标；每个人处于等待、携带或已交付状态。

模拟器在对应格子拾取或派送。与另一等待人物同格时，可以在物理动作前零成本换人。
`forward`、`turn_left`、`turn_right` 每次计 1 步，奖励为 −1；两人交付完毕加 25 并终止。
超出动作预算的回合记录为失败，截断的最后一个 TD 更新沿下一状态估值。
固定种子 20261120，默认 160 回合。首回合执行一次 A*/Dijkstra 示范，其余训练回合
用衰减 ε 在随机动作、Q 表贪婪动作和部分回合的 A* 引导之间采样。
每回合从实际执行的转移反向做一次离策略 Q 更新，首次访问使用完整 TD 目标，
后续学习率 0.65，折扣 1。冻结策略评估只读取保存的 Q 表与合法动作。

第二幕按索引读取 40 条 `explored_trajectories` 和对应的 `episode_steps`。
默认按回合顺序从已完成记录中取样，数量不足时使用实际截断的回合补齐。
`episode_records` 提供真实回合号、奖励、完成状态、旋转数、带朝向的动作轨迹
和从 1 开始计数的 `switch_steps`。所有路径保留旋转时的重复坐标。
第三幕绘制 `optimal_path`，从请求的 `start_pose` 逐步执行 `optimal_actions`，
并显示 `optimal_steps`。完整任务复盘可读取 `optimal_episode` 对应记录的换人时序。

`optimal_*` 取训练中完成任务的最少步数回合。`exact_steps` 是同一任务状态图的独立精确 BFS 成本。
`optimality_proven` 表示两者相等，证明范围由 `map_scope` 指定为 `complete_map` 或
`confirmed_subgraph`。缺少已确认的可行通路返回 422 `INVALID_INPUT`。服务每进程同一时刻
处理一个冷训练请求，并缓存最近 4 个成功响应；并发冷请求返回 503 `SUMMARY_BUSY`。

在 P2 worktree 根目录运行以下命令。实验保存每场景的 `request.json`、`training.jsonl`、
`policy.json`、`comparison.json` 和总报告，并重新加载策略、逐步核验全部回合。

```powershell
.\.venv\Scripts\python.exe -m planner_service.tests.sim_benchmark_100 --count 100 --output planner_service/artifacts/p2/p0
.\.venv\Scripts\python.exe -m planner_service.simulation.replay planner_service/artifacts/p2/p0
.\.venv\Scripts\python.exe -m planner_service.simulation.rl_experiment --observed-dir planner_service/artifacts/p2/p0
.\.venv\Scripts\python.exe -m planner_service.simulation.rl_experiment --evaluate-dir planner_service/artifacts/p2/rl/sample
.\.venv\Scripts\python.exe -m uvicorn planner_service.app:app --host 127.0.0.1 --port 8000
```

```powershell
$sample = (Resolve-Path contracts/sample_summary_request.json).Path
$reply = Invoke-WebRequest -UseBasicParsing -Uri http://127.0.0.1:8000/plan_summary -Method Post -ContentType 'application/json' -InFile $sample -TimeoutSec 5
$plan = $reply.Content | ConvertFrom-Json
$plan.optimal_steps
```

## 人物识别验收

P2 复用 P1 的输入预处理、角色标签和 ONNX 推理。没有配置训练权重时，
`/recognize` 按现有样例返回 501 `VISION_NOT_IMPLEMENTED`。工程冒烟使用合成图和
图像相关的 ONNX 测试图验证会话、解码和 JSON；真实人物分类验收等待正式训练模型。
届时安装可选依赖 `pip install -e './planner_service[vision]'`，设置 `PLANNER_ONNX_MODEL`
为正式权重路径，再设置 `PLANNER_VISION_ACCEPTANCE_MANIFEST` 为留出集 JSON 清单路径。
清单是含 `path`、`label` 的对象数组，图像路径相对清单目录，覆盖屈原和昭君。

## Unity Live 联调

以 Ethan 的 `unity/dashboard-lidar-ui` 为客户端基线，使用 Unity `6000.3.28f1` 打开
`unity_client/Assets/Scenes/Main.unity`。当前 DTO 可读取 v1 的五个汇总字段。
后端坐标顺序为 `[x,z]`，墙矩阵按 `[x][z]` 发送，C# 的 `Vector2Int.y` 对应 z。
`theta` 的 0、90、180、270 分别表示北、东、南、西，旋转在路径中保留重复坐标。

1. 先在 P2 worktree 启动服务，并用上面的 PowerShell 请求确认 `/plan_summary` 返回 200。
   Unity 默认连接 `http://127.0.0.1:8000`，通过 `/openapi.json` 进入 Live。
   确认控制台或仪表盘显示 `LIVE API`，并观察后端的实际请求日志。
2. 使用正式 ONNX 权重并配置 `PLANNER_ONNX_MODEL`。在 Unity 工程中将
   `Assets/StreamingAssets/eval.example.json` 复制为本地 `eval.json`，
   设置 `_template_only=false`，填入两个不同格子、各自真实角色和图像路径。
   若场景指定识别相机，客户端发送相机截图；用能拍到对应人物的素材验证。
   缺少正式权重时可执行 HTTP 预检和验证 Live 的 501 提示，三幕连续验收需等模型。
3. 第一幕从未知地图开始。检查两个人物分别在踩格后触发 `/recognize`，
   `/next_action` 在发现前收到的 `slots` 省略对应人物。
   两次交付核验后须实际收到 `status=completed`，再发送一次 `/plan_summary`。
4. 发送汇总前，客户端需要以保存的 `RecognitionCell` 为原始发现坐标，并使用最终核验角色
   组成 `initial_targets`。当前分支使用会随携带和交付变化的 `station.Cell`。
   第二幕核对显示的 40 条轨迹与对应计数，若补齐了截断回合，界面可据 `episode_records.completed` 区分。
5. 第三幕需要从请求的 `start_pose` 启动，当前分支固定从 `(0,0)` 朝北开始。
   对照最佳回合的 `trajectory.poses`、`optimal_path` 和 `optimal_steps` 检查每一步，
   将阻墙或提前中断计作复盘失败。当前回放只遍历移动动作，完整搬运演示还需消费
   `optimal_episode` 对应的 `switch_steps` 并呈现拾取和交付。
   最优性文案根据 `optimality_proven` 和 `map_scope` 显示，当前 DTO 忽略这些增量字段。
