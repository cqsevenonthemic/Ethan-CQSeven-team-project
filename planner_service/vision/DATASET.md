# P1 真实图像数据集

正式采集和训练前签核采用 [高区分度采集与审计执行版](COLLECTION.md)。本页保留数据路径、素材来源和训练命令。

初始收集目标为屈原和王昭君各至少 100 张候选图，实际纳入数量按授权与质量筛选。按来源和画风检查两类的覆盖情况。优先选用人物身份有出处、版权状态明确、人物在图中易于辨认的图像。同一书册、拍摄批次、原画及其翻拍、裁剪、镜像和重压缩版本共用一个来源组。

## 目录和候选清单

数据和训练产物放在仓库外。`images/` 下按类存放原文件，工具引用文件路径并生成清单。

```text
P1-real-data/
  candidates.csv
  images/
    quyuan/
    zhaojun/
  manifest.json
  runs/
    mobilenet-v3-small/
    baseline-cnn/
```

`candidates.csv` 使用 UTF-8，支持 BOM。每张候选图占一行，表头如下。

```csv
path,label,source,license,group,original_filename,kind,split
```

| 字段 | 填法 |
| --- | --- |
| `path` | 相对 `images/` 的路径，推荐用 `/` 分隔。支持单帧 JPEG、PNG、WebP。 |
| `label` | 人工依据来源核对后填写 `quyuan` 或 `zhaojun`。 |
| `source` | 作品或原始文件页、可追溯的拍摄记录、素材编号，或生成批次记录。 |
| `license` | 许可名称和条款链接，或明确包含项目训练用途的书面许可编号。保留署名信息。 |
| `group` | 原始作品或系列、拍摄批次、Unity 素材包或生成批次的稳定编号。不同来源页面上的同一原图及衍生图共用编号。 |
| `original_filename` | 下载或收到的原文件名，包含扩展名。 |
| `kind` | `real` 表示已获授权的真实来源材料，含作品数字化图像及团队获授权的实拍或实机截图。`synthetic` 表示生成图，只进入 `train`。 |
| `split` | 可选，留空自动分配。为独立的 Unity 采集组填写 `test` 可固定整个关联组；也支持 `train` 和 `val`。 |
| `generation_batch`、`generation_prompt`、`generation_tool` | 只为生成图填写，分别记录批次编号、提示词及工具和模型版本。导入时三项必填，同一批次归入同一关联组。 |

对 AI 生成素材，保留生成服务、模型版本、生成批次、人工标签确认和适用条款。有参考图时同时记录参考图的使用许可，并用组编号关联同源衍生图。每批先审核身份、服装与图像质量，再进入训练候选。历史插画用于扩展来源时，两类都应覆盖相近画风；正式测试应包含授权的 Unity 实机素材。

## 导入和审计

从仓库根目录执行以下 PowerShell 命令。`--inspect` 可在候选图片尚不足以划分时先检查。每次正式导入应使用一个新的 manifest 路径，已有文件会保留。

```powershell
$visionPython = '.\planner_service\.venv-vision\Scripts\python.exe'
$dataset = (Resolve-Path '..\P1-real-data').Path
$images = Join-Path $dataset 'images'
$metadata = Join-Path $dataset 'candidates.csv'
$manifest = Join-Path $dataset 'manifest.json'

& $visionPython -m planner_service.vision.import_dataset --metadata $metadata --data-root $images --inspect
& $visionPython -m planner_service.vision.import_dataset --metadata $metadata --data-root $images --manifest $manifest --standard
if ($LASTEXITCODE -ne 0) { throw 'Dataset import failed' }
& $visionPython -m planner_service.vision.audit_dataset --manifest $manifest --data-root $images
if ($LASTEXITCODE -ne 0) { throw 'Dataset audit failed' }
```

1. 检查 CSV 必填项与文件是否匹配。工具生成 SHA-256，检查缺失文件、损坏文件和字节完全重复，报告未登记的图像。
2. 审查各类数量、来源、组、尺寸和格式，复核小于 224 像素短边的候选图。检查原图是否符合 Unity 的发送条件。许可字段仍需人工核对来源页面或书面授权。
3. 审查感知哈希标出的近重复。工具检查原图和镜像的相似关系。同标签近重复自动绑定到同一集合，异标签近重复阻止导入。裁剪或大幅修改可能漏检，已知衍生图必须填写同一组。对独立性较差或重复过多的同组图片，在 CSV 中筛选代表样本。
4. 工具把来源、来源组和近重复的关联关系合并后划分，尽量按类别接近 70%／15%／15%。每个集合须有两类。固定划分发生冲突或独立组不足时，报告会说明原因。划分通过后冻结 manifest，保留实际比例与测试组清单。
5. `manifest.json` 兼容原有六个字段，额外保留来源组、原始文件名、素材类型和生成记录。训练入口会重新审计整份清单。训练集在线启用随机翻转和旋转，验证和测试仅执行确定性缩放和归一化。

## 可核查的素材线索

- 台北故宫数位典藏有屈原半身像和《明妃出塞图》。页面标注低阶图档为 CC0，中阶图档为 CC BY 4.0，后者保留馆方要求的品名及署名。画卷含多人时先人工确认目标人物，选用或裁剪能看清主角的画面，派生图沿用原卷组。 [屈原半身像](https://digitalarchive.npm.gov.tw/Collection/Detail/17841?dep=P) · [明妃出塞图](https://digitalarchive.npm.gov.tw/Collection/Detail/806?dep=P)
- 屈原可以从 Wikimedia Commons 的《三才图会》楚屈原像开始，文件页标注为公有领域。The Met 的《九歌图》也有开放馆藏，选取馆方标注开放、画面主体确为屈原的页面，同一卷册共用组编号。 [楚屈原像](https://commons.wikimedia.org/wiki/File:QuYuan.jpg) · [The Met《九歌图》](https://www.metmuseum.org/art/collection/search/40511) · [The Met 开放使用条款](https://www.metmuseum.org/about-the-met/policies-and-documents/open-access)
- 王昭君可以参考《百美新咏图传》的馆藏版画文件。Commons 文件页标有 `CC BY-SA 4.0`，保留署名、文件页和许可链接，分享改编图时遵循相同许可条件。 [王昭君版画文件](https://commons.wikimedia.org/wiki/File:%E7%8E%8B%E6%98%AD%E5%90%9B.png) · [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)
- 优先请 Ethan 提供 Unity 将实际发送的授权 PNG／JPEG 样本，以及拍摄或渲染批次、资产包来源和许可。以截图的宽高、JPEG 压缩、背景、光照、镜头距离、人物占比和呈现风格来选择和检查样本。测试组按素材包、人物资产和渲染批次与训练组隔离。字段沿用原始标准 Base64 的 `image_base64`，无 data URI 前缀，解码后最大 4 MiB。 [现有 API 契约](../../contracts/api_v1.md#post-recognize)

## 开训与独立测试

- 核对各集合的每类图片数、独立组数和生成图占比。人工逐组复核标签和许可，冻结 `test` 后只用 `val` 选 checkpoint 和调参。
- 确认 `.venv-vision` 的 `pip check` 与 CUDA 正常，默认 ImageNet 权重已缓存。RTX 4060 上用 `224×224` 输入完成过 batch size 8、16、32 的 AMP 前向、反向和 AdamW 更新。训练可从 32 开始，若运行时显存紧张，改用 16 或 8。
- 用同一份 `development-manifest.json` 拟合主模型与基线，并使用独立输出目录。数据负责人保管完整清单和最终图片。真实数据未经审计时，保留候选盘点结果。

```powershell
$primary = Join-Path $dataset 'runs\mobilenet-v3-small'
$baseline = Join-Path $dataset 'runs\baseline-cnn'
$development = Join-Path $dataset 'development-manifest.json'
& $visionPython -m planner_service.vision.train --manifest $development --data-root $images --defer-test `
  --output-dir $primary --architecture mobilenet_v3_small --batch-size 32 --epochs 30 --freeze-epochs 2 --seed 20261119
if ($LASTEXITCODE -ne 0) { throw 'MobileNetV3-Small training failed' }
& $visionPython -m planner_service.vision.train --manifest $development --data-root $images --defer-test `
  --output-dir $baseline --architecture baseline_cnn --batch-size 32 --epochs 30 --seed 20261119
if ($LASTEXITCODE -ne 0) { throw 'CNN baseline training failed' }

# 冻结模型与阈值后，由数据负责人在持有完整清单和图片的环境运行。
foreach ($run in @($primary, $baseline)) {
  & $visionPython -m planner_service.vision.evaluate_onnx --manifest $manifest --data-root $images `
    --model (Join-Path $run 'classifier.onnx') | Tee-Object -FilePath (Join-Path $run 'onnx-test.json')
  if ($LASTEXITCODE -ne 0) { throw "ONNX evaluation failed for $run" }
}
```

独立评测读取 `test` 样本，输出 accuracy、每类 precision／recall／F1、混淆矩阵及 CPU 单张推理耗时的均值、P50 和 P95。混淆矩阵行是真实标签，列是预测标签，顺序均为 `quyuan`、`zhaojun`。计时窗口从 Base64 解码开始，覆盖图像预处理、ONNX Runtime 和后处理。真实 HTTP 来回耗时可用 Unity 原始请求另外测量。

## 国风插画首轮试验

屈原按成熟楚地文士设计，带胡须、束发或文士冠、宽袖交领袍。王昭君按端庄汉代仕女设计，
无胡须，盘发配簪，着汉式交领衣裙。两类采用相同的哑光国风数字水粉质感、构图条件和颜色范围。
正面、侧面和四分之三侧脸交错出现，半身和全身混合，主体约占画面高度 60%–80%。
背景以简洁为主，少量画面使用低细节庭院，并覆盖柔光、侧光与傍晚光。
两类都有空手和道具图，屈原使用书卷或竹简，王昭君可持琵琶，也可使用两类共享的素扇。
入选图逐张检查人物特征、构图和可见文字，画面不放姓名、题签、印章、标记或水印。

保存实际提示词及结果文件名，以 `<label>_g<组号>_<组内序号>.png` 命名。
训练、验证和测试按预先固定的原型组划分。相同参考图、原图或衍生系列沿用关联组。
候选表记录实际观察到的 `view_angle`、构图与道具。首轮 `--pilot` 允许已审核的生成插画存在于
三个隔离集合，仍按文件哈希、感知哈希和来源关联拦截跨集合泄漏。正式采集继续按上文
`--standard` 规则执行。插画独立组评估用于该插画域，正式泛化验收还需要独立来源或 Unity 输入。

```powershell
$vision = '.\planner_service\.venv-vision\Scripts\python.exe'
$pilotRoot = (Resolve-Path '..\P1-real-data\pilot-illustrations-v1').Path
$images = Join-Path $pilotRoot 'images'
$metadata = Join-Path $pilotRoot 'candidates.csv'
$manifest = Join-Path $pilotRoot 'manifest.json'
$development = Join-Path $pilotRoot 'development-manifest.json'
$run = Join-Path $pilotRoot 'runs\mobilenet-v3-small-v1'
& $vision -m planner_service.vision.import_dataset --pilot --metadata $metadata --data-root $images --manifest $manifest
if ($LASTEXITCODE -ne 0) { throw 'Pilot manifest failed' }
& $vision -m planner_service.vision.import_dataset --pilot --metadata $metadata --data-root $images --development-manifest $development
if ($LASTEXITCODE -ne 0) { throw 'Pilot development manifest failed' }
& $vision -m planner_service.vision.train --pilot --defer-test --manifest $development --data-root $images --output-dir $run --epochs 24 --freeze-epochs 3 --batch-size 12 --seed 20261120 --patience 6
if ($LASTEXITCODE -ne 0) { throw 'Pilot training failed' }
& $vision -m planner_service.vision.evaluate_onnx --pilot --manifest $manifest --data-root $images --model (Join-Path $run 'classifier.onnx')
```

训练日志保存按验证损失选定的 checkpoint 和对应验证指标。冻结 checkpoint 后才跑独立测试。
ONNX 评估同时报告全部测试图、无道具图和各视角的计数及分类指标。未知古装人物的 softmax
分数只能作为误判风险观测，当前二分类输出没有经过拒识校准。
