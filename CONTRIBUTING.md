# 协作约定

## 工作安排

- 在 GitHub Issues 中为每项任务记录目标、负责成员、验收条件、依赖和目标日期，并在仓库 Projects 看板跟踪状态。
- 任务模板适用于新功能或常规工作；缺陷使用 Bug 模板。Pull Request 应关联对应 Issue。
- 涉及跨目录开发、接口契约或集成时，在 Issue 中标明双方需要确认的事项。

## 职责边界

- 成员 A：在 unity_client/ 内开发 Unity 前端。
- 成员 B：在 planner_service/ 内开发 Python 后端；规划服务不得读取 eval.json。
- contracts/：双方共同维护。接口契约或 Mock 变更须先协商，并保持向后兼容。
- submission/：仅存放最终集成和交付材料。

职责边界是团队约定；共享仓库本身不提供按目录限制成员写权限的能力。

## 分支与合并

- 从最新的 main 创建功能分支，使用 unity/<任务>、planner/<任务> 或 shared/<任务> 命名。
- 日常改动通过 Pull Request 合并到 main，不直接推送或强制推送。
- Pull Request 应关联任务 Issue（例如 Closes #12），说明改动内容、影响目录和验证情况，并请求搭档审查。
- 涉及 contracts/ 或跨职责目录时，合并前须双方确认。
- 仓库管理员可在 GitHub 的 Settings → Branches 中为 main 配置 PR 审核和状态检查规则；在规则启用前仍按上述约定协作。

## 提交内容

- 不提交 Unity 缓存/构建目录、Python 虚拟环境/缓存、凭据或本地真实 eval.json。
- ONNX 模型和其他交付所需资源是否入库，按项目交付需要决定；不要把临时数据集或可再生成的大型文件混入源码提交。
- 不要改写或删除 codex.md 中冻结的规则；确需调整时先由双方在 Issue 中协商。
- 仓库为公开仓库，不要在代码、Issue 或提交记录中放入密钥、令牌、密码或个人隐私。