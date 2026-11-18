# 共享接口契约

冻结基线见 [`../codex.md`](../codex.md) 第六节；第一阶段增量约定见 [api_v1.md](api_v1.md)。

2026-11-18 成员 B 获授权统一决定接口，并明确批准终止/等待响应使用 `action:null`。
原有三种动作、坐标和字段含义保留；新增状态、剩余任务、结构化轨迹和错误约定。

- `sample_observe.json` / `sample_action.json`：完整请求与真实 A* 响应。
- `sample_completed_*` / `sample_unreachable_*` / `sample_awaiting_*`：完成、不可达与等待交付。
- `sample_exploring_*`：未知地图安全转向观察。
- `sample_keep_*` / `sample_switch_*`：已知子图中的保持/换人比较。
- `sample_rejected_*`：拒收纠偏及另一未验证预测修正。
- `sample_stalled_*`：4次无进展扫描后的可恢复等待。
- `sample_legacy_*`：未提供访问记录的旧客户端安全等待，不误报搜索结束。
- `sample_recognize_request.json` / `sample_summary_request.json`：尚未实现功能的合法输入。
- `sample_error.json`：统一错误示例；不提供虚假的 RL 或识别成功样例。

样例可通过 `python -m planner_service.tests.make_samples` 重建，并由测试验证。

P0 新增 visited_cells、observation_seq、progress 与 awaiting_observation。
Unity 必须实际更新访问记录和观测序号并回传进度，详见 api_v1.md；未修改其业务代码。

契约和 Mock 由双方共同维护。修改前先协商；新增字段应向后兼容，并在 Pull Request 中说明对 Unity 与 Python 两端的影响。
