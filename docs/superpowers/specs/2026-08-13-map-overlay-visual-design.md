# 地图 Overlay 视觉设计 v2 —— 铁巢作战意图可视化

- 日期：2026-08-13（视觉定稿）／2026-08-16（实现机制重定稿 v2）
- 状态：**视觉设计定稿 + 实现方案定稿**；v1 实现（TextMeshPro/CreatePrimitive 路线）实测不可用已废弃，v2 全部渲染改用游戏自带 `Il2CppShapes.Line`
- 范围：只做地图上叠加的 FCS 过程可视化，不改作战地图本体、不动 HUD 面板

## 目标

让玩家/观众看懂铁巢（FCS）**打算干什么**和**正在干什么**：

- **打算**：目标落点 + 弹种 + 预计飞行时间 + 毁伤覆盖范围
- **正在**：实时落地倒计时（击发后）

地图现在"太干净"，左上角面板信息越来越多——把**有空间意义**的过程信息画到地图上，面板留给人看总结/控制。

## 范围

**只画"打击中"的目标**（已进入某一门炮的流程）：

| 状态 | 画不画 |
|---|---|
| 任务派到炮管（装填/瞄准/击发）`LeftTask`/`RightTask` | ✅ |
| 在飞（已击发，落地前）`InFlight` | ✅（实时倒计时） |
| 排队未派发 | ❌ |
| 落地 / 击杀 / 取消 | ❌（淡出） |

数据源 = `fcs.LeftTask ∪ fcs.RightTask ∪ fcs.InFlight`（去重）。与现有任务状态机天然对齐，不新增状态。

## 视觉元素

### 1. 毁伤圈（落点）
- 半径 = `task.BlastRadiusKm`（与注册表 `IsHandledNear` 同源数据——**所见即所算**）
- 样式：**描边环**（v2 决定：先只画描边；填充盘待装机验证 `Il2CppShapes.Disc` 后再加）
- 颜色：深红偏亮（火力线家族，比游戏深红亮一档，保证浅棕/黑地图 + 黑白航拍上可见）
- 位置：落点（任务 `position`）

### 2. 圈标签（弹种 + 时间）
- 内容：**单行** `AP 33s`（弹种 + 预计飞行时间）→ 击发后 `AP 9s`（实时落地倒计时，`{remaining:F0}` **整数秒，与面板 Impact 一致**，复用 `EstimatedToF`）
- 颜色：**白 + 深描边**（黑白航拍上也读得出）
- 位置：**落点右上固定偏移**（不压 T1、不依赖圈大小——AP/LE 小圈也放得下）
- **引线**：深红细线 标签→落点（1920s 手绘地图标注惯例，多目标区分归属）
- **层级**：引线 + 文字 renderQueue 高于火力线 → 压线也读得清（无需避让偏移逻辑）

### 3. 火力线（玩家 → 落点）
- 颜色：深红（借原版"火力线"语义）
- 位置：炮塔（玩家）→ 落点连线，clip 在地图桌内
- 语义：读作"这发弹从我这打到那"

### 4. 火力线标签（距离 + 方位角）
- 内容：`12.3km 047°`
- 颜色：**深红（与线同色）**——线和它的标注是一个打击单元
- 位置：**顺线书写**——从玩家侧向目标侧，横过来与线平行，自动翻转保证从观看角度永远正读（仿原版尺规/量测标注沿线的写法）
- **地图静态**（不旋转不拖动）→ 翻转 map-relative 即 screen-relative，稳定，无需 counter-rotate

### 5. 移动目标前进路线
- 样式：**白虚线 + 端头箭头**（借原版"尺规"语义）
- 内容：沿目标速度方向画**固定可见长度**（约 1.5-2km 或目标距离的 ~15%）——**方向指示**用途，不声称精确预测（精确预测由落点圈/提前点承担）
- 位置：移动目标当前位置 → 前方，clip 地图内
- **箭头根部速度标签**：目标当前速度（`29km/h`，内部 `AimVel×3.8164` km/s 换算），**白 + 深描边**；速度变化才更新（0.25s tick 检测，慢目标几乎不动）

## 配色与层级

**色板全部取自游戏已有语义**，读起来像"游戏自己的工具"，不突兀：

| 语义 | 颜色 | 对应游戏元素 |
|---|---|---|
| 打击单元（圈+线+线标签） | 深红偏亮 | 火力线 = 深红 |
| 弹种/时间标签 | 白 + 深描边 | 标记笔 = 白 |
| 预测路径 | 白虚线 | 尺规 = 白虚线 |

**不碰明黄**——那是游戏的测绘/计算语义，留给游戏自己的工具。

**层级（弱 → 强）**：
1. 毁伤圈填充（最弱，只示意覆盖，不盖图）—— v2 暂缺，验证 Disc 后补
2. 毁伤圈描边 / 移动路径（边界 / 方向）
3. 火力线（弹道）
4. 标签文字（最醒目但克制）

## 状态流

```
任务派炮管 → 出现：毁伤圈 + 圈标签(HE 22s) + 火力线 + 线标签(12.3km 047°)
                + 移动目标附加前进路线
击发        → 圈标签变实时倒计时(HE 9s)，（未来）填充加深
落地/击杀/取消 → 淡出消失
```

## 约束

- 线/圈 **clip 在地图桌**（Draggable Surface）内，不出桌
- 只画打击中目标（≤2-4 个），不铺满
- 填充极淡，不盖住作战地图关键信息
- 颜色只用游戏家族色（深红/白/白虚线），与 T1-T4 深红标记、敌军粉红菱形、友军亮蓝方块不冲突（形状/尺度/层级区分）

## 刷新频率

| 层 | 频率 | 说明 |
|---|---|---|
| 几何（圈/线/路径位置） | **1s tick（1Hz）** | 慢移动目标 1s 走 ~8m，10km 地图上不可见，1Hz 跳变无感 |
| 文字（弹种/时间/距离/速度标签） | **1s 同 tick，只在值变时设** | 倒计时改**整数秒**（1Hz 下小数无意义）；速度/距离标签变化极稀 |
| 出现/消失 | 事件驱动 | 任务进炮管/落地/取消时增删，不轮询 |

每帧只剩一个节流检查，几乎零成本。若未来出现高速单位导致 1Hz 跳变可见，再单独把几何提到 4Hz（多一行的事）。

---

## 实现架构 v2（2026-08-16 重定稿）

### 为什么弃用 v1 实现（当时效果完全不可用）

v1（`feature/map-overlay` 上的 `MapOverlay.cs`）用 `AddComponent<TextMeshPro>()` 画标签、`CreatePrimitive(Quad)` + `Shader.Find("URP/Unlit")` 画填充、`LineRenderer` 画线。renchonghan 在 `enhanced-merge` 分支的 [GameInternals.md](https://github.com/renchonghan/IronNestFCS/blob/enhanced-merge/GameInternals.md) 实测确认了这些路线在本游戏（IL2CPP 裁剪）下必挂：

1. **运行时 `AddComponent<TextMeshPro>()` 不渲染**（ForceMeshUpdate 也没救）→ v1 全部文字标签不可见
2. **`CreatePrimitive` 默认材质着色器被裁剪（渲染紫块）**；`Shader.Find` 可能返回 null → v1 填充盘/线材质失效
3. 动态字体 API 被 IL2CPP 裁剪；枚举 stub 不可见（`DiscType`/`LineColorMode` 等）

### v2 渲染机制（照搬 renchonghan enhanced-merge d18b459）

- **一切几何用游戏自带 `Il2CppShapes.Line`**（`Il2CppShapesRuntime.dll`，csproj 必须加引用）：`AddComponent<Il2CppShapes.Line>()`，设 `Start/End/Thickness/Color/ColorStart/ColorEnd/Dashed`
- **文字用线段字符**（他的七段数码路线）：数字 0-9 已有；**本设计扩展字符集**到 14 段式：`A P S L R E H K M T ° . /`（覆盖 `AP 33s` / `12.3km 047°` / `29km/h` 全部文案）。字符 = 若干 Line 段，平贴板面
- **虚线原生支持**：`Line.Dashed = true`，不需要自定义虚线贴图
- **落点计时器**：`EstimatedToF − (Time.time − FiredAt)` 取整秒（等价他 `impactTime − (Time.time − fireTime)`；本仓库任务字段不用动）
- **锚点**：master 已有 `TurretLocation`（PR #7，MapRoot 下固定子物体，玩家拖动不移）→ `GetTurretLocal()`；网格空间 `turretLocation.localPosition` 即铁巢网格坐标
- **坐标换算**：`MapTable.WorldToMapLocal(w) = mapSurface.InverseTransformPoint(w)`（v1 分支加过、被 revert，重加）；圈半径 km→map-local = `BlastRadiusKm / 3.8164`
- **方向角约定**：`SignedAngle(target, Vector3.up, Vector3.forward)`，0=北(+Y)，**必须在板面局部系算**（网格画布空间朝向不同会打飞）
- **热重载安全**：运行时物件 F9 不销毁 → 槽对象登记 `tracked`（Shutdown 销毁）+ 生成时按名清旧实例，双保险

### 槽结构（每任务）

| 对象 | 元素 |
|---|---|
| 圈 | 48 段 Line 多边形（描边，深红偏亮） |
| 圈标签 | 线段字符根节点（落点右上偏移）+ 深红引线 1 条 Line |
| 火力线 | 1 条 Line（玩家→落点，深红实线） |
| 火力线标签 | 线段字符根节点（线中点，顺线旋转，翻转=转 180°） |
| 移动路径 | 1 条 Line（白，`Dashed=true`）+ 端头箭头 2 条短 Line + 根部速度标签 |

### 明确不做（本仓库范围外）

- 实体菱形框（敌对红/友军蓝全图标记）——那是 renchonghan 的沙盘交互，与本仓库"只画打击中目标"的设计冲突
- `ClickRaycaster` 右键入队/出队
- 铁巢棋子吸附（本仓库另有 `TurretLocation` 锚点方案）

### 待装机验证项

- `Il2CppShapes.Disc` 能否做毁伤圈**填充盘**（静止 0.1 / 在飞 0.2）——验证通过再补，失败维持描边圈
- 线段字符字号/描边观感（`LabelSegW` 调参，他用的 0.045 起步）
- 地图桌 clip 的实现（圈/线不出桌）

## 实现参考

- `renchonghan/IronNestFCS` `enhanced-merge` 分支 `d18b459`：`IronNestFCS.Logic/FCS/MapTable.cs`（沙盘交互 +483 行，线段字符/瞄准标记/目标虚线全部可照搬）
- `GameInternals.md`：IL2CPP/MelonLoader 踩坑清单 + 沙盘校准常数（`MapBottomLeft=(-2.6238,-1.3741)`、`MapCellSize=1/3.8164`、km 比例 3.8164）
- `ClickRaycaster.cs`：右键机制（本仓库不采用，仅备查）
