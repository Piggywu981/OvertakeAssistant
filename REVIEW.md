# Overtake Assistant 插件审查报告

- 审查对象：`ThirdPartyPlugins/OvertakeAssistant`（版本 0.2.0，`OvertakeAssistantPlugin.cs` 971 行）
- 审查日期：2026-09-28
- 方法：源码静态分析 + `dotnet build` 验证 + 对 `Pathfinding.dll` / `LaneAssist.dll` 的反射与 IL 扫描 + 与核心代码（`ETS2LA.Game.Output`、`FreeSpotParking`、`ETS2LA.ML`）的约定交叉核对 + SCS 官方坐标约定查证

## 总体结论

插件编译干净（0 警告 0 错误），依赖声明全部有效，核心机制成立：经 IL 级验证，Pathfinding 的 `Tick` 确实读取 `blinkerLeftActive` / `blinkerRightActive`（转向灯遥测）并结合 `sinceLastLaneChange` 做变道决策，"打灯 → pathfinding 执行变道"的前提是真实的。

**但插件的状态机是开环的、缺少执行结果验证**：变道请求发出后从不检查卡车是否真的换了道，失败路径会永久卡死且转向灯失控；邻车道安全检查只看瞬时位置、不做相对速度外推；弯道会整体击穿车道归属判定。当前应视为封闭道路原型，不应在真实交通环境中启用。

---

## 1. 已验证成立的部分

| 项目 | 结论 |
|---|---|
| 编译 | `dotnet build -c Release` 通过，0 警告 0 错误 |
| 依赖 | `tumppi066.pathlib` 安装在 `current/Libraries/tumppi066.pathlib/`（库插件目录，不在 Plugins 下），其余三项均为已安装插件；`Handler.cs:367` 的依赖检查全部可通过 |
| 核心前提 | `PathfindingPlugin.Tick` 引用 `blinkerLeftActive`、`blinkerRightActive`、`sinceLastLaneChange`（IL 扫描确认）；另有 `IndicateLeft/IndicateRight/ResetIndicators`、设置项 `EnableLaneChanges`/`EnableIndicators` |
| 转向灯输出 | `PublishIndicator` 用 `Direct` 模式写 `lblinker`，与核心 `GameOutput.ProcessChannel` 的布尔处理兼容（`Output.cs:193`） |
| Toggle 控件 | Boolean 控件按下/松开都会触发事件（`ETS2LA.Controls/Shared.cs` `UpdateState`），插件在松开时切换（`OvertakeAssistantPlugin.cs:256`），逻辑成立 |
| 设置页 | 路由 `/plugins/adjustments/local.overtakeassistant` 与 `Manager.razor:208` 的发现机制一致 |
| 通知 | 重复 ID 的通知是"更新"而非忽略（`Notifications.cs:47`），固定 ID 复用无碍 |

## 2. 关键问题（按严重程度）

### 2.1 状态机开环：失败时永久卡死 + 转向灯失控（最严重）

- `RequestingLeft` 固定 7 秒后**无条件**转入 `Passing`（`:195`，`LaneChangeSettleSeconds` 见 `:45`）；`RequestingRight` 固定 7 秒后算完成（`:204`）。全程不验证卡车是否真的换了道。
- 若 pathfinding 未响应（它自身安全检查不通过、或其 `EnableLaneChanges` 设置关闭），卡车留在原车道，而 `TryReturnToOriginalLane` 要求目标车落到车后 26 m（`:689`）——目标一直在前方，**状态机永远卡在 `Passing`**，无任何超时，只能靠手动刹车/转向或暂停触发 `Abort`。
- 连带问题：转向灯脉冲只"按下 0.35 s 再松开"（`:834`），松开按键不会熄灭游戏转向灯（开关式）。变道未发生时灯会一直闪；且下一次超车请求发出的左灯脉冲实际会把它**关掉**——插件会反复打灯/灭灯振荡。
- 修复所需的数据已具备：变道时卡车相对自身前进方向的横向位移约等于一个车道宽，用与 `ProjectVehicle` 相同的投影逻辑即可确认变道是否发生。

### 2.2 邻车道检查无相对速度外推

`IsAdjacentLaneClear`（`:732`）只按瞬时位置判断：左后方 50 m 处一辆快 30 km/h 的小车会被判"安全"，但几秒内就会追上。返回右侧车道时的后向 35 m 检查（`ReturnRearClearance`，`:41`）同样如此。下坡+高速差会显著放大此缺陷（见第 4 节）。

### 2.3 目标跟踪脆弱

- `TrafficVehicle.id` 是共享内存 39 个槽位的编号（`ETS2LA.Game/SDK/Traffic.cs:196`、`:226`），槽位会随车辆生成/消失被复用；`Passing` 阶段较长时旧 id 可能匹配到无关车辆。
- `FindTargetVehicle` 返回 null 时（目标掉出槽位列表），"已超越目标"的门禁被整体跳过（`:685`），会立即开始返回流程。

### 2.4 依赖假设未校验

- 整个机制依赖 pathfinding 的 `EnableLaneChanges` 设置为开，插件从不检查，关了就静默失效。
- `Dependencies` 里的 `tumppi066.adaptivecruisecontrol`（`:128`）在代码中完全未使用——不必要的硬依赖，未装 ACC 会导致插件无法启用。

### 2.5 触发条件与 pathfinding 存在结构性张力

超车要求自车比目标快至少 5 km/h（`MinimumClosingSpeed`，`:35`、`:717`）。但插件的用法是 FullSelfDriving 模式配合 pathfinding——pathfinding 的速度控制会把卡车减速到前车速度，收敛后速度差趋近 0，门槛再也过不去。插件可能只在速度差收拢前的短暂窗口内有触发机会。同时 pathfinding 自身有 `EnableLaneChanges`、`sinceLastLaneChange`、`IndicateLeft/IndicateRight` 等自主变道与打灯逻辑，灯脉冲可能与其决策互相干扰。

### 2.6 次要问题

- `_estimatedForward` 在遥测线程写、Tick 和 AR 渲染线程读，无同步（`:275`）；变道期间它会把横向移动平均进"前进方向"，恰好在最需要精确投影的返回扫描阶段造成横向失真。
- 手动输入检测只在非 `Idle` 阶段生效，`userBrake > 0.08` 阈值较低，踏板抖动可能误触发中止。
- `Abort` 在 `Cooldown` 阶段被触发时会再续 10 秒冷却（`:890`），无意义。
- AR 包围盒基于 10 Hz 快照渲染，60 fps 下明显滞后于车辆（视觉问题）。
- `Shutdown`/`OnDisable` 未调用 `UnregisterControl` 清理 `Init` 注册的控件。

---

## 3. 弯道场景分析

### 3.1 机制：车道归属判定整体错位，误差随距离平方增长

投影基于**卡车当前切线方向**，再按固定偏移 ±4.5 m ±2.4 m 容差判断车道（`:715`、`:774`）。弯道中弧长 d 处的路面相对切线横向偏移：

```
e(d) = d² / (2R)    （朝弯道内侧；前方与后方的点都向内侧偏——切线只与道路相切于卡车脚下）
```

横向误差（米）：

| 弯道半径 | 44 m | 60 m | 70 m（前向安全检查） | 85 m（最大搜索距离） |
|---|---|---|---|---|
| R=1600 m（很缓） | 0.6 | 1.1 | 1.5 | 2.3 |
| R=800 m | 1.2 | 2.3 | 4.5 → 见下 | 4.5 |
| R=400 m（普通高速弯） | **2.4** | **4.5（一整个车道）** | 6.1 | 9.0 |
| R=200 m（匝道/山路） | 4.8 | 9.0 | 12.3 | 18.1 |

判定窗总宽仅 4.8 m（±2.4 m），故**有效检测距离 ≈ 2.2×√R**：R=400 时约 44 m，R=800 时约 62 m。要撑住 85 m 全检测范围需 R ≥ 1500 m。

### 3.2 三种症状（R=400 m 为例）

**左弯（内侧在左）**
- 同车道前车被推向左侧：44 m 外滑出本车道窗口——真目标在搜索范围内的大半段消失；
- 41–74 m 区间恰好落入左车道窗口，而 `IsAdjacentLaneClear(Left)` 前向检查覆盖 70 m（`:747`）——空旷的左车道被同车道卡车"幻影占用"，超车被压制（"弯道里它从来不动作"）；
- 左车道真正的车 44 m 外横向 >6.9 m，完全不可见——包括 44–70 m 这段本该被安全检查覆盖的距离。插件报"左车道空闲"并请求变道。

**右弯（内侧在右），镜像反向**
- 左车道 60 m 处的车横向投影 ≈ 0，被当成"本车道慢车"选为目标（假目标），AR 框与 "Target" 框住实际在左车道的车；
- 同车道前车被推向右车道窗口，污染 `Passing` 阶段的返回检查；
- 同样存在 44–72 m 检测盲区。

**两方向共通**：后方点也向内侧偏，被扫描一侧的后方来车同样漏检——返回原车道的后向 35 m 检查在弯道中基本失效。

### 3.3 连锁影响

- AR 扫描区矩形沿估计前进方向画直框（`DrawLaneClearanceZone`，`:485`），R=400 时 70 m 深的框远端偏离路面 6 m，肉眼可见地画到路外——用户可直接观察到数据不可信。
- 结合 2.1 的状态机缺陷，弯道触发的失败更容易进入卡死路径。
- 实际执行变道的是 pathfinding（有真实地图数据），弯道中的**转向本身**不会因此走错；出问题的是"何时打灯、是否安全"这层判断。
- `_estimatedForward` 按位移差更新，跟随弯道切线无明显滞后（25 m/s、R=300 时 yaw rate ≈ 4.8°/s，一帧滞后 <1°）——误差纯粹来自几何项，不是估计器的问题。

### 3.4 ⚠️ 坐标系陷阱（不要"修复"它）

分析中曾怀疑 `GetPrimaryLaneLateral` 左右写反（Left 返回 +4.5，`:771`），深查后确认**行为正确、变量命名是陷阱**：

- SCS heading 约定为逆时针（0=北，π/2=西），而 ETS2LA 的 `ForwardFromScsEuler` 给出 forward = (sin h, 0, cos h)（`FreeSpotParking/Geometry/Frames.cs:84`）→ 推出游戏世界是**左手系**（+X=西，+Y=上，+Z=北）；
- 因此 `Cross(UnitY, forward)` 算出的 `right` 变量实际指向**左侧**，正 lateral = 左边；
- `Left → +4.5` 恰好正确，AR 绘制（`truckPosition + right * lateral`）自洽。

任何人把 `Left` "纠正"为 −4.5 都会真正弄反左右。建议将变量改名（如 `left`）并加注释。顺带：FreeSpotParking 中 `sinAlpha // + = target to the right`（`ParkingController.cs:416`）的注释也是同样原因下错的（实际是左侧），只因 SCS 转向"正=左"的约定才未出事；核心 `ETS2LA.ML/Vision/Meshes/Road.cs:224` 对该向量命名 "right" 亦为误称（道路网格对称所以从未暴露）。

---

## 4. 坡道场景分析

### 4.1 坡道几何基本无害——与弯道的本质区别

弯道的问题在水平面内几何，上下坡是垂直面内弯曲。插件在两处投影前都执行 `delta.Y = 0f`（`:280` 前进方向估计、`:797` 车辆投影），所有横向/纵向计算在水平面内完成：

- 坡道上车道在俯视图中仍是 4.5 m 间隔，横向窗口不错位；
- 纵向距离取水平投影，8% 坡度下与坡面实际距离仅差 0.3%；
- 凸/凹竖曲线（坡顶、坡底）只弯垂直方向，对平面判定零影响；
- AR 车辆包围盒使用车辆真实坐标（含高度），上坡时前车的框正确地画在更高处。

### 4.2 "压平"的真实代价：垂直结构混入扫描

Y 归零意味着高度差完全不参与过滤，`IsUsableVehicle`（`:806`）也没有任何高度检查：

- **跨线桥/天桥上的车**：横向短暂扫过 ±2.4 m 判定走廊（每次约 0.2–2 秒），速度满足"慢 5 km/h"即成假目标——插件会为一辆在 8 米高桥上的车打出左转向灯；
- **更系统性的是立交区**：平行匝道、高架集散道若横向落在 2.1–6.9 m 且高度不同（ETS2 立交常见），其车流被**持续**当成邻车道交通，互通附近幻影阻挡/假目标密集；
- 修复极便宜：扫描时忽略 `|车辆Y − 卡车Y| > 3 m` 的车（只过滤扫描，不过滤已锁定目标）。

### 4.3 坡度改变的是门限与物理裕度

- **上坡**：`CanAssistRun` 要求 ≥55 km/h（`:540`），重载卡车在 6–8% 长上坡掉到 55 以下是常态——最需要超车的地方插件整体停用；起伏路段速度在门限附近震荡，超车进行到一半上坡吃掉速度即触发 `CanAssistRun` 失败 → `Abort`（`:880`）→ 10 秒冷却，转向灯/变道处于半途。
- **下坡**：闭速度偏大，5 km/h 触发门槛轻松通过、触发更频繁；而所有间隙检查是静态位置快照、不做相对运动外推（2.2 的缺陷），下坡+高速差下余量消耗快得多。坡度不创造新缺陷，但放大已有缺陷。
- **变道时序**：7 秒固定等待对陡上坡重载卡车偏短（换道 5–8 秒），提前转入 `Passing`，状态机与现实的偏差又多一分。
- **AR 显示**：扫描区矩形画在卡车海拔 +0.12 m 的固定水平面（`:489`）——上坡远端沉入路面下，下坡悬浮路上方。仅显示问题。

### 4.4 综合判断

坡道单独出现（高速长直坡）问题有限；**山区 = 坡 + 弯叠加**时弯道破坏车道判定、上坡门限叠加停用，插件基本不可用。根治方向同弯道：用 PathLib 地图数据。

---

## 5. 修复路线

> **实施记录（0.3.0，2026-09-28）**：第一步（1 高度过滤、2 曲率门控、3 相对速度外推）与第二步（4 横向位移验证、5 Passing 30 秒硬超时、6 已验证失败时的熄灯脉冲）均已实现并编译通过，DLL 已部署到 `ETS2LA-win-release-Portable/current/Plugins/`。顺手项同步完成：进行中超车最低速度放宽至 45 km/h、移除未使用的 ACC 依赖、`right` 变量改名 `left` 并加左手系注释（`ProjectVehicle` / `DrawLaneClearanceZone` / `DrawLaneClearanceLabel` / `GetPrimaryLaneLateral`）。未实施：第三步 PathLib 接入、`_estimatedForward` 跨线程同步、`UnregisterControl` 清理、目标投影连续性跟踪。已知残留：曲率门控用 heading 差分 + EMA（alpha 0.2/帧）估计；横向位移验证在弯道上会被路面矢高（sagitta）干扰——弯道中可能提前误确认（由 Passing 超时兜底）、几乎不会误触发熄灯分支；`IsAdjacentLaneClear` 的外推区间包含当前位置，严格覆盖原静态检查语义，无行为回退。

### 第一步：安全兜底（纯插件内小改，合计约半天）

1. **高度过滤**：`FindLeadingSlowVehicle` 与 `IsAdjacentLaneClear` 的扫描循环忽略 `|ΔY| > 3 m` 的车。只过滤扫描，不过滤 `FindTargetVehicle`（已锁定目标不中断）。
2. **曲率门控**：κ = 相邻两帧 `_estimatedForward` 夹角 ÷（速度×时间），1 秒平滑；`R = 1/κ < 800 m` 时 `TryStartOvertake` 不发起新超车（进行中的照常走既有流程）。
3. **邻车道相对速度外推**：扫描范围放宽到纵向 [−60, +70] m，对每辆邻车道车预测 T=4 s 后纵向位置 `pred = long + (vehicleSpeed − truckSpeed)·t`，[0, T] 内任一时刻 pred 落入 [−(rear+buf), front+buf] 即判不安全。一个检查同时覆盖后方快车与下坡放大场景。

顺手项：进行中超车时最低速度放宽到 45 km/h（避免起伏路面反复 Abort）；删除未使用的 ACC 依赖；`right` 变量改名/加注释（见 3.4）。

### 第二步：状态机闭环（核心，约 2–3 小时）

4. **自身横向位移验证**：进入 `RequestingLeft` 时记录卡车位置与方向基向量，每 Tick 计算横向位移——3 秒后仍 <2 m → 未变道 → `Abort`；位移达 ~3 m 且稳定 1.5 s → 提前转 `Passing`（不必傻等 7 秒）；`RequestingRight` 同样验证，失败则 Abort 而非假装完成。
5. **`Passing` 硬超时**（30 秒），覆盖目标丢 tracking、ACC 跟速等一切卡死路径。
6. **失败路径主动熄灯**：游戏转向灯是开关式，**仅当确认变道未发生时**补发一次同向脉冲关灯（变道已完成、游戏自动熄灯的情况下多余脉冲会把它点亮——取消脉冲只挂在第 4 项的"未变道"分支上）。

### 第三步：PathLib 治本（单独安排）

车道归属、曲率、纵坡全部取自地图数据，替代世界坐标启发式——弯道/坡道/立交问题的共同根治。工作量主要在摸清 PathLib API（仅有编译好的 DLL，需先做接口勘察）。目标跟踪改用"投影连续性"而非裸 id 也放在此步。

### 验证清单

- [ ] 直道正常超车流（请求 → 变道 → 超越 → 返回）
- [ ] R≈400 m 弯道：完全不触发
- [ ] 跨线桥/立交路段：无假目标、无幻影阻挡
- [ ] 下坡 + 后方快车：保持不动
- [ ] 模拟变道失败（pathfinding 拒绝）：~10 秒内 Abort 且转向灯熄灭
- [ ] 重载上坡 <55 km/h：停用且不反复 Abort 已完成的操作

---

## 附录 A：关键代码位置索引

| 主题 | 位置 |
|---|---|
| 常量（速度/间隙/时长） | `OvertakeAssistantPlugin.cs:32-52` |
| 相位机 Tick | `:189-215`（RequestingLeft→Passing `:195`，RequestingRight→Complete `:204`） |
| 运行条件检查 | `CanAssistRun` `:512-561`（速度门限 `:540`） |
| 邻车道安全检查 | `IsAdjacentLaneClear` `:732-762`；车道窗口 `:764-785` |
| 目标搜索/跟踪 | `FindLeadingSlowVehicle` `:705`、`FindTargetVehicle` `:722` |
| 转向灯输出 | `PublishIndicator` `:834`、`StopIndicatorPulse` `:852` |
| 中止/冷却 | `Abort` `:880` |
| 前进方向估计 | `UpdateEstimatedForward` `:275`（Y 归零 `:280`） |
| 车辆投影 | `ProjectVehicle` `:792`（Y 归零 `:797`） |
| AR 扫描区绘制 | `DrawLaneClearanceZone` `:485` |
| 依赖声明 | `:123-129` |
| 交通槽位/id 语义 | `ETS2LA.Game/SDK/Traffic.cs:196`、`:226`（Size=宽/高/长 `:216`） |
| 依赖强制检查 | `ETS2LA.Backend/PluginHandler/Handler.cs:367` |

## 附录 B：Pathfinding 相关验证证据

对 `tumppi066.pathfinding/Pathfinding.dll` 的反射与 IL 扫描结果：

- `PathfindingPlugin.Tick` 方法体引用 `blinkerLeftActive`、`blinkerRightActive`、`sinceLastLaneChange` → 转向灯状态参与其 Tick 决策，插件前提成立；
- `PathfindingPlugin.IndicateLeft/IndicateRight` 写 `lblinker`/`rblinker`，`ResetIndicators` 读转向灯遥测并回写灯控；
- 设置项 `PathfindingSettings.EnableLaneChanges` / `EnableIndicators`（UI 与 `PathfindingUtils.CreateNextPlannedItem` 使用）；
- `tumppi066.laneassist/LaneAssist.dll` 中无任何 blinker/indicator 字符串 → LaneAssist 不响应转向灯，变道执行在 pathfinding。

## 附录 C：坐标约定参考来源

- [TruckTel/doc/api.md](https://github.com/jvanstraten/TruckTel/blob/main/doc/api.md)——SCS heading 逆时针（0=北，0.25=西）
- [ETCARS "The Data"](https://etcars.readthedocs.io/en/master/thedata.html)——heading 从上往下看逆时针；转向正值=左
