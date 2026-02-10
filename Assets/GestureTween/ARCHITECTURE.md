# GestureTween Path Workbench 架构说明

## 目标

围绕 `DOTweenTimeline` 构建一个专注路径手绘的工作台：

- 一键创建工作区（驱动器空对象）
- Path-only 轨道专精（移除 Scale / Rotation 独立模块）
- Scene 手绘 + 路径后处理 + Timeline 预览
- 与 DOTween 原生能力分工清晰：旋转缩放交还 Timeline

## 结构总览

```text
Root
└── <RootName>__PerfWorkspace
    ├── DOTweenTimeline
    ├── GestureWorkspace
    └── GesturePathTrack
```

## 组件职责

### `GestureWorkspace`（Runtime）

- 维护 root、timeline、pathTrack 的绑定关系
- 作为工作区统一入口

### `GesturePathTrack`（Runtime）

- 实现 `IDOTweenAnimation`
- 保存路径偏移点（local offsets）和 ease 曲线
- 创建 `DOPath / DOLocalPath` tween

### `GestureCurveWindow`（Editor）

- Path-only 工作台主界面
- Scene 路径采样、路径生成、可视化叠绘
- Shift 绘制时半透明虚影跟随（仅视觉表现）
- 路径后处理：
  - 端点锁定平滑（多轮）
  - RDP + 贝塞尔简化重采样
  - 平滑与简化组合
- 缓动优化：
  - 基于手绘速度序列的意图读取
  - 中值滤波 + 指数平滑抑制抖动
  - 单调约束 + 关键点压缩生成稳定 ease
- 后处理恢复机制：
  - 撤销一步后处理
  - 还原到本次手绘原始路径

### `GestureWorkspaceFactory`（Editor）

- 在根节点下手动触发创建/修复工作区
- 保证 `DOTweenTimeline + GesturePathTrack + GestureWorkspace` 完整

## Timeline 预览体系

依赖 `DottEditorPreview`：

- 预览倍速可调（含慢速）
- 支持播放、暂停、停回起点、定位时间
- `GestureCurveWindow` 与 Timeline 共用同一预览速度设置

## 数据写入策略

- 所有路径改值通过 `Undo.RecordObject` + `SetDirty`
- 后处理前自动快照，支持局部撤销
- 调试完成后一键 `保存当前调试状态`
- 保持 Prefab 实例属性可追踪
