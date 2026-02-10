# GestureTween Path Workbench

`GestureTween` 现已收敛为 **快速路径手绘专用工具**。

旋转、缩放和其他复杂叠加效果，建议直接使用 `DOTween Animation + Timeline` 原生能力完成。

## 当前能力

- 一键创建工作区：在根节点下自动创建 `__PerfWorkspace`
- 专注 Path 通道：仅保留 `GesturePathTrack`
- Shift 手绘虚影跟随：绘制时显示半透明“路径驱动虚影”（仅视觉，不改对象状态）
- 路径后处理：
  - 一键平滑（端点锁定，支持多轮叠加）
  - 贝塞尔简化（RDP 控制点抽象 + 曲线重采样）
  - 平滑 + 贝塞尔组合处理
- 缓动优化：根据手绘速度节奏重建更稳定的 Ease 曲线
- 后处理可撤销：支持“撤销一步后处理 / 还原到本次手绘原始路径”
- Timeline 实时预览：支持播放、暂停、停回起点、倍速

## 目录结构

```text
Assets/GestureTween/
├── Editor/
│   ├── GestureCurveWindow.cs        # 路径工作台（手绘 + 后处理 + 预览）
│   └── GestureWorkspaceFactory.cs   # 手动创建/修复工作区
└── Runtime/
    ├── GestureWorkspace.cs          # 工作区组件（绑定 root + timeline + path）
    ├── GesturePathTrack.cs          # 路径通道（IDOTweenAnimation）
    ├── GestureCurvePreset.cs        # 旧版预设（兼容保留）
    └── GestureTweenPlayer.cs        # 旧版播放器（兼容保留）
```

## 使用流程

1. 打开 `Window > GestureTween > Scene Motion Painter`
2. 选择根节点（Root）
3. 点击 `创建/修复工作区`
4. 在 Scene 视图按住 `Shift + 左键` 手绘路径
5. 在窗口中按需执行后处理：
   - `一键平滑路径`
   - `贝塞尔简化路径`
   - `平滑 + 贝塞尔简化`
   - `重建节奏缓动`
6. 用 `Timeline 预览` 区域进行调试，最后点击 `保存当前调试状态`

## 工作区命名

默认工作区命名为：

```text
<RootName>__PerfWorkspace
```

## 注意事项

- 需要 DOTween Pro 与 `Assets/Plugins/DOTweenTimeline`
- `GesturePathTrack` 保存的是以起点为参考的 local offsets，便于复用
- 本工具不再维护旋转/缩放轨道；请交给 DOTween 原生轨道处理
