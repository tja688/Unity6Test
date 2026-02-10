# GestureTween Workbench 架构说明

## 目标

围绕 `DOTweenTimeline` 构建一个职责分离、可实时调试的动效工作台：

- 自动创建工作区（驱动器空对象）
- Path / Scale / Rotation 三通道分离
- Scene 手柄实时改值
- Timeline 慢速预览与动态调参

## 结构总览

```text
Root
└── <RootName>__PerfWorkspace
    ├── DOTweenTimeline
    ├── GestureWorkspace
    ├── GesturePathTrack
    ├── GestureScaleTrack
    └── GestureRotationTrack
```

## 组件职责

### `GestureWorkspace`（Runtime）

- 维护 root 与各 Track 的绑定关系
- 作为工作区统一入口

### `GesturePathTrack`（Runtime）

- 实现 `IDOTweenAnimation`
- 保存路径偏移点（local offsets）和 ease 曲线
- 创建 `DOPath / DOLocalPath` tween

### `GestureScaleTrack`（Runtime）

- 实现 `IDOTweenAnimation`
- 维护 `X/Y/Z` 三条缩放曲线
- 通过 `DOVirtual.Float` 实时计算并应用缩放

### `GestureRotationTrack`（Runtime）

- 实现 `IDOTweenAnimation`
- 维护 `X/Y/Z` 三条旋转曲线（角度增量）
- 支持 Local/World 旋转空间

### `GestureCurveWindow`（Editor）

- 工作台主界面（全局/路径/缩放/旋转）
- Scene 路径采样、路径生成、曲线写入
- Scene 手柄对 Scale/Rotation 曲线写 key
- 驱动 Timeline 预览与调试保存

### `GestureWorkspaceFactory`（Editor）

- 在根节点下自动创建/修复工作区
- 保证 DOTweenTimeline + 三通道组件完整

## Timeline 预览体系

依赖 `DottEditorPreview`：

- 新增 `PlaybackSpeed`，支持慢速（如 `0.1x`）
- `DOTweenTimelineEditor` 与 `GestureCurveWindow` 共用同一倍速设置
- `DottController.Pause()` 改为真正停留在当前帧

## 数据写入策略

- 所有通道改值都通过 `Undo.RecordObject` + `SetDirty`
- 调试完成后可一键 `保存当前调试状态`
- 保持 Prefab 实例属性可追踪
