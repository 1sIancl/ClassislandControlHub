# 课表编辑器独立化：依赖清单与拆分计划

> 现状：`#/timetables/{档案}` 是独立**路由**，但内部复用档案编辑器内核，
> 只是把其它页签隐藏了。本文是把它真正拆成独立模块前的准备。

## 一、事实：`renderClassPlans` 的依赖

函数体 **2040–2145 行（106 行）**，位于 `js/views/profileEditor.js`。

### 用到的 state 字段（5 个）

| 字段 | 用途 | 拆分后由谁提供 |
|---|---|---|
| `content` | 课表/时间表/科目数据 | 由新模块自己加载 `/admin/profiles/{id}` |
| `classPlanLayoutId` | 当前选中的时间表 | 新模块持有 |
| `scheduleSelection` | 当前选中的格子 `{day, index}` | 新模块持有 |
| `activeTab` | 只用于判断「是否在课表页」 | 新模块恒为 `classPlans` |
| `rotationWeek` | 多周轮换的当前周次 | 新模块持有 |

### 调用的函数（11 个，按体量排序）

| 函数 | 说明 | 拆分策略 |
|---|---|---|
| `schedCell` | 单个格子：渲染 + 点击/双击/拖拽/右键 | **最重**，先搬 |
| `renderPalette` | 右侧科目调色板（含过滤框） | 次重 |
| `openBulkOps` | 批量操作弹窗 | 中 |
| `classPlanLayout` | 取当前时间表对象 | 可内联 |
| `profileRotation` / `currentWeek` / `weekDayCount` | 多周轮换计算 | 这 3 个被别处共用，**建议保留在 profileEditor 并导出** |
| `classCount` | 课表节数 | 可内联 |
| `repaintTab` | 重绘整个页签 | **关键阻塞点**，见下 |
| `segmented` / `emptyState` | UI 组件 | 从 `core/ui.js` 引入 |

### 关键阻塞点：`repaintTab`

`schedCell` 里凡是改完数据都调 `repaintTab()`，它的实现是**重绘整个档案编辑器页面**（页签栏 + 档案头 + 所有区块）。

在独立模块里没有"档案编辑器页面"可重绘，直接调用会抛错或渲染出不该有的东西。

这不是搬函数的问题，是**控制流要换**：新模块需要一个 `repaint()` 只重绘课表区域。

## 二、拆分步骤（每步可独立验证）

### 步骤 1：抽出课表区域的重绘入口

在 `profileEditor.js` 里引入一个间接层：

```js
// 现在的写法（直接重绘整页）
repaintTab();

// 改成一个可替换的钩子
repaintClassPlans();      // 默认实现 = repaintTab()
```

这一步**不改任何行为**，只是让"重绘什么"变成可注入的。先跑一遍现有 E2E（`verify-timetable-drag` 等）确认无变化。

> 为什么先做这一步：后面搬 `schedCell` 时，只要把钩子换成"只重绘课表"即可，
> 不用在每个改数据的地方逐个判断。

### 步骤 2：搬纯渲染部分

新建 `js/views/timetableEditor.js`，先只搬：

- `render`（加载档案 + 调色板 + 网格）
- `schedCell` 的**渲染分支**（不含任何事件监听）

此时页面能"看"，但不能"点"。用一次人工截图确认行列、短表头、右栏布局与原页面一致。

### 步骤 3：搬交互

按这个顺序，每搬完一块跑一次 E2E：

1. 点格子选中 / 双击清空
2. 点调色板排课（`armedSubjectId` 流程）
3. 拖拽调课（`dragPayload` 流程）
4. 右键菜单
5. 批量操作弹窗

> 顺序的理由：从"无状态"到"有跨区域状态"。拖拽和批量依赖前面的选中态，
> 倒过来搬会同时引入多个未知量。

### 步骤 4：删除旧实现

确认 `profileEditor.js` 里不再有课表页签相关的引用（`TABS` 里已移除、`renderClassPlans` 已无调用方），再删。

## 三、风险与对策

| 风险 | 后果 | 对策 |
|---|---|---|
| 抽漏依赖 | 课表页白屏 | 每步跑一次 `verify-classboard` / `verify-timetable-drag`，白屏立刻暴露 |
| 状态引用错位 | **排课静默失效**（点了没反应但不报错） | 每个交互搬完都写一条断言：点格子 → 断言 `scheduleSelection` 变化 |
| `repaintTab` 未替换干净 | 独立页里出现档案头的残留 | 步骤 1 的钩子必须全局替换，用 grep 确认 0 处直接调用 |
| 两个模块各留一份 `schedCell` | 以后改一处忘一处 | 步骤 4 必须真的删除，不能"先留着" |

## 四、为什么不在这一轮硬做

课表比时间表复杂得多：`schedCell` 一个函数里同时有渲染、点击、双击、拖拽、右键五套逻辑，`renderPalette` 依赖 `armedSubjectId` 这个跨区域状态，`openBulkOps` 是独立弹窗。

当初抽 `layoutEditor.js` 时，我因为括号层数数错重写过一次。课表这边如果一次搬完，出问题时的表现会是**排课点了没反应**——比白屏更难查，因为不报错。

所以按上面 4 步走，每步都能验证，每步都能回退。

## 五、完成标准

- [ ] `renderClassPlans` 及其依赖全部迁出 `profileEditor.js`
- [ ] `profileEditor.js` 行数显著下降（当前 2000+ 行）
- [ ] `#/timetables/{档案}` 加载时**不**出现档案头、页签栏、使用的时间表区块
- [ ] 现有 E2E 全绿：`verify-timetable-drag`（拖拽）、`verify-classboard`、`verify-layout-split`
- [ ] 新增断言：独立页里点格子 → 选中态生效 → 点调色板 → 课表被写入
