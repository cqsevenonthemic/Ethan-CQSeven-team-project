# P1 高区分度古装人物采集与审计执行版

## 开始收集

1. 在仓库外的 `P1-real-data/images/quyuan/` 和 `images/zhaojun/` 放置原图及待选裁剪。复制 [CSV 表头](candidates.template.csv) 到新数据目录的 `candidates.csv`。已有清单继续追加。表格使用 UTF-8，每张文件一行，逗号出现在内容中时按 CSV 规则加引号。
2. 按原作、参考图、拍摄或渲染批次先分组，再录入。录入者填写可核查的身份与授权；审核者逐图检查人物、许可、裁剪和视觉属性，再把 `review_status` 从 `pending` 改为 `approved` 或 `rejected`。把损坏、完全重复或未获授权文件移出采集池，保留拒收记录。
3. 两类使用相同的采集矩阵。每类先收传统古画 10、国风插画 40、AI 古风写实 35、简底古装人物 35 张。每张原图计入一个渠道。简底是采集渠道，也要如实填 `style`。优先补同一构图、背景复杂度或道具条件下数量较少的一类。
4. 运行下方只读审计，查看字段错误、各类候选原图和批准图数量、渠道缺口、视觉覆盖及独立组数量。发布正式清单前处理所有阻塞，再完成人工签核。

## `candidates.csv` 字段

| 字段 | 填写要求 |
| --- | --- |
| `path`、`label` | 相对 `images/` 的实际文件路径。标签固定为 `quyuan` 或 `zhaojun`。使用单帧 JPEG、PNG 或 WebP。Unity 只使用 PNG／JPEG。 |
| `source`、`download_url`、`original_filename` | `source` 填作品页、素材或拍摄记录编号。可选 `download_url` 填原图下载地址。保留接收或下载时的文件名。 |
| `license`、`attribution`、`identity_evidence` | 填许可名称及条款链接或书面授权编号、对应署名、支持该人物标签的证据。主体为多人时补充位置和确认依据。获授权的 Unity 图同时核对上游角色资产。 |
| `original_id` | 每张独立原图的稳定编号。翻拍、压缩、裁剪、镜像、调色和增强版本沿用此编号。正式池每个原图只保留一个获批准代表。 |
| `group`、`reference_group` | `group` 为原作系列、拍摄或素材组编号。不同发布页的同源文件沿用组号。有共享参考图时，把其 `group` 填为 `reference_group`。多个关联来源先统一组号。 |
| `kind` | `real` 或 `synthetic`，同时表示是否 AI 生成。实际 Unity 请求图填 `real`，并保留其资产出处。 |
| `generation_batch`、`generation_prompt`、`generation_tool` | `synthetic` 三项必填。工具字段包含模型版本；提示词记录实际使用内容；同一批次沿用编号。真实图留空。 |
| `channel`、`style` | 渠道为 `historical`、`guofeng`、`ai_realistic` 或 `clean_portrait`。视觉风格为 `historical` 古画／版画、`guofeng` 国风插画、`realistic` 写实。AI 写实渠道须填写 `synthetic` 和 `realistic`。 |
| `composition`、`background` | 构图为 `closeup` 近景、`half` 半身、`full` 全身。背景为 `simple` 简底、`moderate` 有少量环境、`complex` 多层环境或多人。简底渠道填 `simple`。 |
| `props` | 没有可见道具填 `none`。否则以 `\|` 分隔，例如 `book\|brush`。可用项为 `book`、`bamboo_slip`、`brush`、`pipa`、`other_instrument`、`horse`、`sword`、`fan`、`other`。填 `other` 时在备注中写明。 |
| `prop_cue`、`scene_cue` | 分别记录道具和场景的角色提示强度，取 `none`、`weak`、`strong`。例如背景中普通家具可为 `scene_cue=none`，明确出塞叙事场景可为 `strong`。`props=none` 要求 `prop_cue=none`。 |
| `gender_cue` | `weak`、`medium`、`strong`，按画面中性别线索的显著性填写。侧身、远景或服装遮挡可降低显著性，人物标签仍需有出处。 |
| `unity_set` | `none`、`train`、`dev`、`final`。后三项同时标记为 Unity 样本，`split` 分别固定为 `train`、`val`、`test`。 |
| `unity_asset_group`、`unity_batch`、`unity_encoding` | Unity 图必填。记录角色原型或资产家族、渲染批次、实际编码和分辨率，JPEG 有质量参数时一并记录。非 Unity 图留空。相同资产或批次编号全库一致。 |
| `review_status`、`reviewer`、`review_notes` | 状态为 `pending`、`approved`、`rejected`。批准时填审核者标识。多人、裁剪、题签去除、署名细节、识别依据不明确或拒收原因写入备注。 |
| `split` | `train`、`val`、`test`，固定组时填写。普通候选可留空，生成图只进入 `train`。正式划分前建议先固定验证和测试组，由工具验收。 |

SHA-256、图像实际宽高、格式和文件大小由工具读取。人工录入字段中，暂缺值可留在 `pending`，正式清单只提取 `approved`。渠道配额在报告中提示缺口，独立性及开训的数量门槛由工具执行。

## Unity 开发验证与正式测试隔离

- **开发验证 `dev` → `val`。** Ethan 按实际请求路径交付样本，用于联调、复盘误判、确定编码和阈值。两类都覆盖相近的镜头距离、场景明暗、背景复杂度和压缩条件。
- **最终独立测试 `final` → `test`。** 由数据负责人提前锁定独立的角色原型、资产包和渲染批次。与训练或开发验证共享原型、素材包、源帧、参考图或近重复关联的文件，整组放在同一个集合。换场景、改分辨率、重新压缩或从同一段截帧时仍沿用原组。
- 数据负责人保管最终图片、标签及完整审计报告。开发期间只共享采集规格和汇总数量。冻结模型、预处理和阈值后才解封评测。模型选型与调参只用 `val`，正式测试结果用于报告。若只有同一套角色资产，先保留现有组，待补到独立资产再开放正式测试。
- 数据负责人通过完整审计后，使用 `--development-manifest` 另存仅含 `train`、`val` 的清单。开发侧只接收该清单及对应图片。拟合时加 `--defer-test`，该模式会拒绝含 `test` 的清单。锁定模型后，负责人持完整清单运行 `evaluate_onnx`。
- 原始 API 输入为标准 Base64 的 `image_base64`，无前缀及空白，解码后 PNG／JPEG 最大 4 MiB。采集时保留实际输出；尚未确定尺寸时可从长边 512–1024 像素试起，目标人物约占画面高度 50%–80%。 [接口契约](../../contracts/api_v1.md#post-recognize)

## 训练前通过清单

- [ ] 两类各至少 120 个已标识的候选原图、80 张获批准图片。每类 `train`、`val`、`test` 分别至少 45／15／20 张，且至少 5／3／4 个独立关联组。两类各有 Unity `dev` 和 `final` 样本。
- [ ] 每类至少 35% 获批准图的 `prop_cue=none`，至少 25% 的 `scene_cue=none`。每类 `test` 至少 8 张无明显角色道具图，来自至少 2 组。另留弱性别线索、简底及实际 Unity 样本用于分项检查。
- [ ] 审核者逐图核对标签、许可、署名和原图关联。解决损坏、完全重复、近重复和跨集合冲突，检查多人主体和低分辨率警告。
- [ ] 人工确认两类在画风、构图、背景复杂度、主色、道具和性别线索强弱上有重叠。检查题字、色卡、水印、固定角色配色及 HUD 等捷径。工具能报告分布，身份和授权真实性由人工确认。
- [ ] 记录数据负责人、审核日期、CSV／manifest 的 SHA-256、最终测试组编号和授权台账。冻结清单后再交付训练入口，后续修改写为新版本。

```powershell
$visionPython = '.\planner_service\.venv-vision\Scripts\python.exe'
$dataset = (Resolve-Path '..\P1-real-data').Path
$metadata = Join-Path $dataset 'candidates.csv'
$images = Join-Path $dataset 'images'

# 只读审计，失败时退出码为 1，查看 JSON errors。
& $visionPython -m planner_service.vision.import_dataset --metadata $metadata --data-root $images --inspect --standard

# 人工签核及审计通过后，生成一个新的正式清单。
$manifest = Join-Path $dataset 'manifest.json'
& $visionPython -m planner_service.vision.import_dataset --metadata $metadata --data-root $images --manifest $manifest --standard
if ($LASTEXITCODE -ne 0) { throw 'P1 collection audit failed' }
& $visionPython -m planner_service.vision.audit_dataset --manifest $manifest --data-root $images
if ($LASTEXITCODE -ne 0) { throw 'P1 manifest audit failed' }
Get-FileHash -Algorithm SHA256 -LiteralPath $metadata,$manifest

# 数据负责人另行导出开发清单，只交付其中列出的 train/val 图片。
$development = Join-Path $dataset 'development-manifest.json'
& $visionPython -m planner_service.vision.import_dataset --metadata $metadata --data-root $images --development-manifest $development
if ($LASTEXITCODE -ne 0) { throw 'P1 development export failed' }
```
