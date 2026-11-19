# HTTP/JSON 接口 v1：P0 增量约定（服务 0.2.0）

基线：`../codex.md` 第六节。本文补齐其未定义行为，不删除或重写冻结方案。
2026-11-18 成员 B 获授权统一决定技术与接口；同轮另行明确批准终止/等待响应的
`action: null`。Unity 业务代码未随本次变更修改。

## 兼容性与范围

- 保留路径、JSON 字段、坐标、墙位、朝向、三种动作和每动作 1 step 的含义。
- 新增响应 `status`、`remaining_tasks`、`trajectory`；输入无需新增必填字段。
- `action` 在可执行状态仍为原三种字符串；仅终止或等待状态为 null（已获确认）。
- 状态枚举是对原先未穷举值的明确化；原示例的 waiting/carried 仍合法。
- `slots` 只包含已发现人物；未发现的 s1/s2 必须省略，不能提前发送坐标。
- 原文缩略矩阵和跳跃路径是说明性片段，不是有效 Mock。此目录样例全部使用完整 16×16 矩阵。
- P0 已实现访问覆盖、保持/换人比较、拒收纠偏和扫描停滞等待；仅 ONNX、Q-Learning 仍返回 501。

## 坐标与地图

沿用 `[x][z]`、x/z=0..15、格中心 `(x+0.5,z+0.5)` 米；
theta=0/90/180/270 分别北/东/南/西。N/E/S/W 墙位=1/2/4/8。
seen_walls 和 known_walls 均严格 16×16，整数 0..15，known 必须是 seen 的位子集。
邻格共享边的 seen/known 位均须对称；已观测的外边界必须有墙。
允许初始边界未知，但规划器永不越界。拒绝布尔值、字符串和小数冒充坐标/墙掩码。

保留原文“未知边乐观评估”的算法能力；本阶段实际下发动作采用**已确认可通行**的子图。
未知边不能直接 forward；通过前沿转向/行走后重新扫描扩大已知地图。它是执行安全门槛，
不把未知墙改为已知墙，也不声称获得未知地图的全局最优。

## POST /next_action

地址 `http://127.0.0.1:8000/next_action`。Content-Type: application/json。
完整输入见 `sample_observe.json`，输出见 `sample_action.json`。

请求字段：

| 字段 | 定义 |
|---|---|
| pose | 当前已执行完动作后的 x/z/theta |
| seen_walls / known_walls | 当前观测累积的完整双掩码 |
| step_count | 已执行动作数，非负整数；由 Unity 计数 |
| carry_slot | s1/s2/null，默认 null；必须匹配唯一 carried 人物 |
| slots | s1/s2 到人物快照的对象，默认 {}；未知人物省略 |
| event | null（默认）或原有 trigger_discovered/delivery_rejected 事件 |
| visited_cells | 可选 `[[x,z],...]`，实际踩入过的格子集合，最多256项；去重排序，必须含当前格；不是雷达可见格 |
| observation_seq | 非负观测序号，默认0；每次完成一次传感器采样递增，重复发送同一快照保持不变 |
| progress | 可选调用方持有的上次响应进度，完整回传；详见下文 |

人物字段保留 x/z/pred_role/verified/state。pred_role 为 quyuan/zhaojun。
state 枚举：waiting（当前坐标等待）、carried（坐标必须等于 pose）、delivered。
delivered 只能由 Unity 真实身份校验成功后写入，verified=true，坐标为正确终点。
屈原终点 (8,8)，昭君终点 (7,7)；verified 默认 false。
verified 表示由 Unity 成功校验或二分类拒收约束确定的身份；两个 verified 人物不能为同一角色。
探索期未验证预测允许重复。拒收不能否定已有 verified 身份，也不能与另一 verified 人物矛盾。
人物可增量携带 recognition_id（非空字符串≤128）和 recognition_source（mock/onnx/null）；
发现事件也可含 recognition_id，若提供则必须匹配 slot。它关联当前图像预测和发现位置，
不是认证凭据；服务本身尚不提供真实 ONNX 推理，也不会从ID猜测角色。
remaining_tasks 是响应派生字段：2 减去 delivered 数，包含尚未发现的人物；
前端不得用“当前已发现人物全部送达”替代全部完成。

discovery 事件必须与当前 pose、slots 内同一人物的位置/类别一致。
rejection 必须指向当前 carried 人物，rejected_at 与 pose 和预测终点一致。
事件是当前快照的附加说明，不是跨请求队列。合法拒收会在副本中反转该人物预测并置 verified=true，
必要时纠正另一未验证冲突预测，然后重新规划；保持输入不变。
收到响应后 Unity 应清除已处理事件；同一原始请求重试确定性一致。
已应用修正的新快照不得再次携带旧拒收事件（矛盾输入返回422，防止再次反转）。

响应保留 action/carry_slot/slots/plan_path/decision/done。
decision.switched 说明是否建议原地换人。
新增 cost_scope=not_evaluated/confirmed_map。cost_keep/cost_switch 为非负动作数或 null；
null 表示未比较，或对应完整路线在已知子图中不可达，绝不是零成本。
携带期间同格遇到第二人时，在 `(阶段,x,z,theta)` 状态图上比较完整三段路线：
当前人终点→返回当前格接第二人→第二人终点，或反向接送顺序。
到站任务转移零成本，前进/旋转均1；包含跨路段朝向成本，只走已确认无墙边。
已知换人路线存在且成本严格更低时建议换人；平局或缺少可证明改善时保持当前人物。
这是当前已知地图、当前预测下的假设代价，不是未知全图最优证明；实际逐步重规划，
未来观测/识别变化或每段路线的重选可改变最终实绩，不能把比较值当成已执行总步数。
空载同格存在多个等待人物时按 slot_id 确定性选择后比较，不再返回未实现错误。
reason 为说明文本，不应作为机器逻辑条件。

| status | action | done | Unity 行为 |
|---|---|---|---|
| planning | 原三种字符串之一 | false | 执行一个动作 |
| exploring | 原三种字符串之一 | false | 执行一个动作后扫描 |
| awaiting_delivery | null | false | 当前携带人物已到预测终点，执行真实身份校验 |
| awaiting_observation | null | false | 补齐访问/进度快照，等待新采样，或恢复停滞的传感器；不执行、不计步 |
| completed | null | true | 两人已由 Unity 确认交付，结束在线任务 |
| unreachable | null | false | 有访问记录，当前已知可达分量已踩访、无目标路径且无观测前沿，停止自动执行并提示 |

unreachable 仅是当前快照的结论，不等同 completed；环境变化或地图修复后可重新请求。
错误响应永不包含可执行动作。

### 状态应用时序和重试

1. Unity 完成上一步动画和扫描；更新 pose、携带人物坐标、墙掩码、步数及事件。
2. 请求完整快照。服务只计算，不执行移动，不递增 step_count，不缓存任务。
3. 成功响应后先应用 slots/carry_slot（位于脚下的 waiting 人物可被建议拾取，不额外计步）。
4. 只有 planning/exploring 且 action 非 null 时，执行**第一个**动作，启动时计一步，0.2s 动画后重新请求。
   plan_path/trajectory 是当前快照下的预览，不能一次性执行整条路径。
5. 携带人物抵达预测终点后，Unity 先核验真实身份：成功写 delivered/verified=true、carry_slot=null；
   失败保留 carried 并发 delivery_rejected。Python 不自动假定送达。
6. 同一快照重试得到同一决策。每个机器人最多一个在途请求；超时不执行动作，
   Unity 通过本地快照代次丢弃迟到响应。应用响应后不要再次应用旧快照的响应。

### 访问覆盖、扫描进度与兼容性

先规划已发现人物任务；没有安全任务路径时，优先通过已确认通路进入未踩访格，
再寻找未知墙面的观测前沿。不能用 seen_walls=15 代替踩格检测人物。
未发现人物的位置仍不得提供给后端。访问记录及进度由 Unity 真实执行结果更新，服务不追加虚构访问。

progress 响应字段必须完整回传：
- fingerprint：已观测双掩码与访问集合的 SHA-256，仅用于变化检测，不含真实地图。
- last_observation_seq / last_step_count：本次决策基于的观测序号与已执行步数。
- scan_attempts：未增加地图或访问信息的扫描旋转数，0..4。
- pending_scan：本次建议动作是否属于原地观测旋转；只有动作实际执行、步数变化后才计入尝试。

同一步重复请求不增加尝试。动作后观测序号未增加时先等待新观测；序号倒退或进度引用未来步数返回422。
最多4次无信息增益的扫描旋转后返回 awaiting_observation；反复轮询不会清除该等待，
真正新增墙面/访问观测会恢复。仍有可安全踩访格时不会因扫描预算耗尽而误报 unreachable。
该机制不替代传感器诊断，且依赖客户端诚实回传快照，不是服务端隐藏会话或防篡改协议。

旧字段和动作均保留。未发送 visited_cells 的旧客户端仍可使用已知目标路线、交付和完成逻辑，
但需要探索时会安全返回 awaiting_observation，不能声称已搜索完成。
已有动作数>0且要继续原地扫描时，必须回传 progress，否则等待协议适配；
要获得完整 P0 能力，Unity 必须实现三个新增请求字段并处理新增等待状态。

### 结构化轨迹

保留 plan_path 为逐动作坐标列表，起点包含在内，转向会重复坐标，不包含跨格跳跃。
新增 trajectory={poses:[{x,z,theta},...],actions:[...],steps:N}。
poses 长度=N+1，actions 长度=N，steps=N；每动作代价=1。
终止/等待轨迹只有当前 pose，actions=[]，steps=0。

## POST /recognize

保留输入 image_base64，未来成功输出 pred_role/confidence。
编码限定原始标准 Base64（无 data URI 前缀、无空白），PNG/JPEG，解码后 1 byte..4 MiB。
第一阶段检查 Base64/大小/文件签名；完整图片解码、像素限制和模型推理尚未接入。
合法编码示例 `sample_recognize_request.json`；当前返回 HTTP 501 VISION_NOT_IMPLEMENTED，
不提供假类别或置信度。未来预处理、标签顺序、图像尺寸和低置信度策略随真实模型冻结。

## POST /plan_summary

保留 start_pose、双掩码、initial_targets.quyuan/zhaojun，目标均为已发现人物的原始位置，
只能在在线任务完成后由 Unity 发起。输入示例 `sample_summary_request.json`。
当前返回 HTTP 501 RL_NOT_IMPLEMENTED；没有伪造的 sample_summary 成功结果。
未来保留 explored_trajectories/episode_steps/optimal_path/optimal_actions/optimal_steps，
增量增加带朝向和动作的轨迹结构。轨迹必须来自真实训练，单条动作及统计可回放验证。
地图不完整时只能称为已知子图最优；“真实全图最优”要求完整探索和精确搜索对照验证。
不承诺固定 200..500 轮 Q-Learning 必然收敛；先完成传统搜索基准。

## 错误与超时

统一错误：`{"error":{"code":"INVALID_INPUT","message":"..."}}`。

| HTTP | code | 行为 |
|---|---|---|
| 422 | INVALID_INPUT | 修复输入再重试，不回显原始图片或请求体 |
| 501 | VISION_NOT_IMPLEMENTED / RL_NOT_IMPLEMENTED | 功能未接入，禁止伪装成功 |
| 500 | INTERNAL_ERROR | 不执行动作；详细异常仅服务日志记录 |

Unity 建议 next_action/recognize 超时 5s，未来 plan_summary 超时 60s；
超时由客户端执行，本阶段服务未实现训练作业取消。错误后停止自动动作并提示，不能无限重试。
性能目标仍为原文 next_action <10ms、推理 <15ms；先规定硬件、预热和 p95 测量范围再优化，
当前未声明达标。仅监听 loopback；本阶段不提供公网部署或跨设备访问方案。

## 十项决策落地记录

1. 人物三态、未知人物省略、remaining_tasks 派生；固定两人任务。
2. completed、unreachable、awaiting_delivery 分离；null action 已获确认。
3. Unity 持有并执行环境状态，Python 纯决策；完整快照重试，单在途请求。
4. 只执行已确认无墙边，乐观路径仅算法评估，扫描后重规划。
5. 优先已知局部子图；送达不代表全图探明，不夸大最优性范围。
6. JSON trajectory 带位姿、动作、步数；逐动作回放可验证。
7. 完整矩阵 Mock 自动由真实算法生成；未实现功能只有错误样例。
8. Base64 PNG/JPEG、统一错误、客户端超时；完整解码随模型接入。
9. P0 完成探索覆盖和双人物/纠偏，ONNX、RL 仍后续接入，不混入当前成功能力。
10. Python 3.12、venv/pip、小型 API 依赖；先测试闭环，再性能/打包。
    D1 实际日历起点未指定，后续排期用 D7/D14/D18 相对节点；
    模型网络、训练划分、RL 超参数和二进制入库策略待实际实验后记录，不是当前依赖。

## P0 测试边界

静态 Mock 是单元/协议快照，不代表线上允许注入完整真实地图。
`planner_service.simulation` 仅为测试：评估器持有真实迷宫和身份，传感器按前向180°、
121束、0.15..4.5m、第一堵墙遮挡输出观测；格点角落歧义保守停止，不做穿墙推测。
当前为二维无噪声传感器，不替代 Unity 的三维点云验证。
测试识别器明确标记 mock：仅实际踩格后产生预测，支持故意误识别注入；
角色、真实点位和地图不会在发现前传给决策器。没有真实深度学习验收结论。
只有 /recognize 和 /plan_summary 保留501，正常在线双人物流程不使用501。
