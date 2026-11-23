# Unity 前端（成员 A）

Unity 项目位于本目录。迷宫始终使用固定的 16×16 布局；两个状态机位置从本地评测配置读取，彼此不能重合。

## 场景组件

默认可运行场景是 `Assets/Scenes/Main.unity`，已加入 Build Settings。用 Unity 6000.3.28f1 打开项目后，在 Project 窗口双击该场景并按 Play。场景包含固定迷宫、机器人和朝向标记、雷达、Planner/状态机/任务驱动、俯视相机和灯光；组件引用已连接，机器人模型没有碰撞体，不会遮挡雷达。

如果创建其他场景，添加并连接 `MazeBuilder`、`RobotController`、`LidarSimulator`、`PlannerHttpClient`、`StateMachineTrigger` 和 `PlannerTaskDriver`。`StateMachineTrigger.recognitionCamera` 可选：设置后会在发现人物时截取摄像头帧；未设置时读取配置中的 `image_path`。

把 `Assets/StreamingAssets/eval.example.json` 复制为本地忽略文件 `eval.json`，填写两个状态机的格子坐标、真实身份和图像路径。运行时只把已发现且识别过的人物位置发给规划器。不要提交 `eval.json` 或真实图像。

## 后端接口

`PlannerHttpClient` 先探测本机 `http://127.0.0.1:8000/openapi.json`。服务不可连接时使用 `StreamingAssets/Mock`；服务有响应时使用实时 API。实时接口的 4xx/5xx、501 和超时都会停止自动动作并显示错误，不会改用 Mock 伪造成功。

`PlannerTaskDriver` 每次只保留一个 `/next_action` 请求。它把实际踩访格、已观测墙、观测序号、完整扫描进度和人物状态组成快照；旧快照的迟到响应会被丢弃。收到响应后先更新人物状态，再执行 `planning` 或 `exploring` 返回的单个 `action`。`trajectory` 只发送给预览事件；真实执行路径由 `RobotController` 按实际完成的动作记录。

`awaiting_observation`、`awaiting_delivery`、`unreachable` 和 `completed` 都不会执行动作。等待扫描时只有观测到新墙或新踩访格才自动恢复；传感器修复后可调用 `ResumeAfterSensorRecovery()`。超时后不会自动重试，可在确认当前环境后调用 `RetryAfterError()`。

发现人物时 Unity 创建 `recognition_id`，将它与截取图像、发现格子、识别结果、`slots` 和发现事件一起保存；请求 `/recognize` 本身仍按契约只发送 `image_base64`。离线 Mock 来源标记为 `mock`，实时识别来源标记为 `onnx`。

P0 后端尚未实现真实 `/recognize` 和 `/plan_summary` 推理；实时 `/recognize` 返回 501 时 Unity 会按契约停止并报告，不会把模拟识别冒充为模型结果。离线 Mock 仅用于演示。
