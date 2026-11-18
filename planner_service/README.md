# Python 规划服务（成员 B）

P0（服务0.2.0）：FastAPI 无状态服务、严格快照校验、带朝向 A*、实际踩访覆盖、
双人物单容量调度、拒收纠偏、扫描停滞等待，以及真实执行 JSONL 回放和批量测试。
服务不读取 Unity 的 `eval.json`，不持有跨请求机器人或人物状态。
接口基线见 `../codex.md` 第六节，补齐约定见 [api_v1.md](../contracts/api_v1.md)。

## 环境与安装

下列 PowerShell 命令均在仓库根目录运行。使用 CPython **3.12**；系统默认 3.14 不作为本项目环境。
首次创建虚拟环境时，使用本机 Python 3.12 解释器：

```powershell
py -3.12 -m venv planner_service/.venv
```

如 `py -3.12` 未注册，直接用已有 3.12 `python.exe` 的完整路径创建。
当前工作站已创建 `.venv`，无需重建。VS Code 选择 `planner_service/.venv/Scripts/python.exe`。

```powershell
.\planner_service\.venv\Scripts\python.exe -m pip install -r planner_service/requirements-dev.lock
.\planner_service\.venv\Scripts\python.exe -m pip install --no-deps -e ./planner_service
.\planner_service\.venv\Scripts\python.exe -m pip check
```

`pyproject.toml` 声明兼容范围，`requirements-runtime.lock` / `requirements-dev.lock`
记录本次 Windows/Python 3.12 实测的精确运行/开发依赖（开发锁已包含运行依赖）。
它们不是带哈希的跨平台锁；其他平台须重新验证。源码安装的 setuptools 构建环境由 pip 隔离创建。
仅运行时可把上述 dev 锁换为 runtime 锁。服务不需要 PyTorch、ONNX Runtime 或 ROS；
真实识别接入时再添加推理依赖，离线训练使用另一个环境。

## 启动

```powershell
.\planner_service\.venv\Scripts\python.exe -m uvicorn planner_service.app:app --host 127.0.0.1 --port 8000
```

- 健康检查：`http://127.0.0.1:8000/health`
- Swagger：`http://127.0.0.1:8000/docs`
- OpenAPI：`http://127.0.0.1:8000/openapi.json`

在另一个终端，从仓库根目录发送真实 Mock：

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
.\planner_service\.venv\Scripts\python.exe -m pytest -c planner_service/pyproject.toml -q
.\planner_service\.venv\Scripts\python.exe -m pip check
git diff --check
```

覆盖独立 BFS 与 A* 的 40 个固定随机迷宫、旋转计步、墙阻挡与绕行、未知边不可执行、
边界限制、有效 Mock、完成/等待/不可达、非法输入、未实现功能、内部异常、
交错请求隔离、两格走廊漏访回归、访问和观测进度、双目标身份约束、保持/换人比较、
拒收纠偏、前向传感器视角/量程/遮挡，以及两人物均未知的端到端闭环。
完整批测与回放命令：

```powershell
.\planner_service\.venv\Scripts\python.exe -m planner_service.tests.sim_benchmark_100 --count 100 --start-seed 0 --fault-every 3 --output planner_service/artifacts/p0_seed_0_99
.\planner_service\.venv\Scripts\python.exe -m planner_service.simulation.replay planner_service/artifacts/p0_seed_0_99
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
.\planner_service\.venv\Scripts\python.exe -m planner_service.tests.make_samples
```

## 当前边界及后续阶段

- `/recognize`：校验 Base64、4 MiB 限制、PNG/JPEG 签名后返回 501；尚无完整图片解码或 ONNX 模型。
- `/plan_summary`：校验输入后返回 501；没有伪造探索轨迹或最优性声明。
- 正常双目标在线流程不再返回501；已知地图下比较完整保持/换人路线，无信息证明改善时保持当前人物。
- 比较代价不等于已执行成本，也不是未知全图最优；不成熟的全局任务优化留待后续实验。
- delivery_rejected 会反转该未验证预测并验证，必要时修正另一未验证预测；重复原始快照确定性一致。
- 同时已验证为相同身份、矛盾拒收、非法访问或未来进度返回422；预测未验证时允许重复。
- 现已具备进入真实视觉开发的传统任务基线；还需Unity实现访问记录、扫描进度和执行反馈后联调。
- Q-Learning 接入前复用同一任务转移规则，使用精确搜索核对最优性；已知子图与真实全图的最优性分开表述。
- 后续开展更多地图与噪声测试、性能验收、PyInstaller -D 打包和 D7/D14/D18 联调。

每次开发前检查远程，保持在 feature/planner-service；Unity 业务代码由 A 维护。
Commit、Push、PR 和合并均分别等待用户批准。
