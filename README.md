# 16×16 机器鼠迷宫仿真项目

本仓库用于《机器人与人工智能》课程机器鼠项目，是团队的代码、接口文档和协作安排入口。项目规则与接口基线以 codex.md 为准。

## 项目分工

- unity_client/：成员 A 负责 Unity 迷宫、传感器模拟、交互和可视化。
- planner_service/：成员 B 负责 Python 规划、识别和强化学习服务。
- contracts/：双方共同维护接口契约与 Mock 样例；修改前先协商。
- submission/：存放最终集成与交付材料。

当前仓库包含项目方案和协作骨架，尚未包含前后端业务代码。接口基线见 codex.md 第六节。

## 任务安排

使用本仓库的 GitHub Issues 记录任务、负责人、验收条件、依赖和目标日期；仓库内的任务模板可从 Issues 页面新建。需要看板时，在仓库的 Projects 中创建或关联项目，并用状态列跟踪待办、进行中和完成项。

跨目录工作、接口变更和集成事项应在 Issue 中写明双方需要确认的内容。不要在公开 Issue 或提交记录中放入密码、令牌、真实评测配置或其他敏感信息。

## Git 协作

团队 GitHub 仓库是唯一日常协作入口：https://github.com/cqsevenonthemic/Ethan-CQSeven-team-project。从这里克隆：

    git clone git@github.com:cqsevenonthemic/Ethan-CQSeven-team-project.git

- 从最新的 main 为每项工作创建分支，例如 unity/maze-builder、planner/oriented-astar。
- 完成后创建 Pull Request，关联任务 Issue（例如 Closes #12），说明影响目录和验证情况。
- main 用于稳定集成；按团队约定通过 Pull Request 合并，不直接推送或强制推送。
- main 的分支保护由仓库管理员在 GitHub 设置中启用；设置生效前也遵守 PR 流程。
- 原个人仓库和 Gitee 远程不再作为日常提交入口，也不会自动同步。

只在自己的职责目录内提交代码。Unity/Python 生成文件、凭据和本地真实 eval.json 不应入库；这些内容已列入 .gitignore。

## 本地评测配置

真实的 eval.json 仅用于本地评测并已从 Git 忽略。Unity 配置结构尚未冻结；unity_client/Assets/StreamingAssets/eval.example.json 是占位示例，开始实现读取逻辑前需与实际配置格式对齐。