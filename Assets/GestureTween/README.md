# GestureTween Workbench

`GestureTween` 现在是一个以 **DOTweenTimeline 工作区** 为核心的动效制作台。

## 核心变化

- 自动工作区：不再直接改根节点，工具会在根节点下创建 `__PerfWorkspace`
- 通道职责分离：路径、缩放、旋转分别由独立 Track 组件驱动
- 3 轴编辑：Scale / Rotation 全部改为 `X/Y/Z` 独立曲线
- Scene 手柄调参：在 Scene 里拖拽轴向手柄，直接写入曲线
- Timeline 实时预览：支持预览倍速（含 `0.1x` 慢速调试）

## 目录结构

```text
Assets/GestureTween/
├── Editor/
│   ├── GestureCurveWindow.cs        # 工作台窗口（全局/路径/缩放/旋转）
│   └── GestureWorkspaceFactory.cs   # 自动创建/修复工作区
└── Runtime/
    ├── GestureWorkspace.cs          # 工作区组件（绑定 root + timeline + tracks）
    ├── GesturePathTrack.cs          # 路径通道（IDOTweenAnimation）
    ├── GestureScaleTrack.cs         # 缩放通道（XYZ）
    ├── GestureRotationTrack.cs      # 旋转通道（XYZ）
    ├── GestureCurvePreset.cs        # 旧版预设（兼容保留）
    └── GestureTweenPlayer.cs        # 旧版播放器（兼容保留）
```

## 使用流程

1. 打开 `Window > GestureTween > Scene Motion Painter`
2. 选择根节点（Root）
3. 点击 `创建/修复工作区`（或保持自动创建开启）
4. 在模式栏切换：
   - `路径`：Scene 里绘制路径并生成 Path Track
   - `缩放`：拖拽 XYZ 手柄写入 Scale 曲线
   - `旋转`：拖拽 XYZ 手柄写入 Rotation 曲线
   - `全局`：统一调整工作区参数（该模式下 Scene 不显示小面板）
5. 用 `Timeline 预览` 区域播放、暂停、改倍速、慢速调试
6. 点击 `保存当前调试状态`

## 工作区命名

默认工作区命名为：

```text
<RootName>__PerfWorkspace
```

## 注意事项

- 需要 DOTween Pro 与 `Assets/Plugins/DOTweenTimeline`
- Path Track 采用相对偏移点集，便于复用
- Scale / Rotation Track 是 `IDOTweenAnimation`，可直接参与 Timeline 预览
