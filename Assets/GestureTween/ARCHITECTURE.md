# GestureTween 架构设计文档

## 概述

GestureTween 是一个基于手势绘制的动效生成工具，允许开发者在 Unity Scene 视图中直接绘制运动曲线，并将其转换为 DOTween 兼容的动画数据。

## 核心架构

```
┌─────────────────────────────────────────────────────────────┐
│                      Editor Layer                           │
│  ┌─────────────────────────────────────────────────────┐   │
│  │              GestureCurveWindow                      │   │
│  │  ┌───────────┐ ┌───────────┐ ┌───────────────────┐  │   │
│  │  │  Input    │ │  Curve    │ │     Export        │  │   │
│  │  │  Handler  │→│ Generator │→│     Manager       │  │   │
│  │  └───────────┘ └───────────┘ └───────────────────┘  │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                     Runtime Layer                           │
│  ┌─────────────────────┐    ┌─────────────────────────┐    │
│  │  GestureCurvePreset │    │   GestureTweenPlayer    │    │
│  │  (ScriptableObject) │ ←→ │     (MonoBehaviour)     │    │
│  └─────────────────────┘    └─────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                    DOTween Integration                      │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────────┐    │
│  │ DOTweenPath  │ │DOTweenAnim   │ │    Sequence      │    │
│  └──────────────┘ └──────────────┘ └──────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

## 组件详解

### 1. GestureCurveWindow (Editor)

**职责**：提供编辑器界面，处理手势输入，生成曲线数据

#### 核心数据结构

```csharp
private struct StrokeSample
{
    public Vector2 sceneGuiPos;  // GUI 坐标（用于 Scale/Rotation 计算）
    public Vector3 worldPos;     // 世界坐标（用于 Position Path）
    public float timestamp;       // 采样时间戳（用于速度曲线计算）
}
```

#### 通道类型

| 通道 | 输入映射 | 输出 |
|------|----------|------|
| PositionPath | 世界坐标轨迹 | Vector3[] 路径点 + AnimationCurve 速度曲线 |
| Scale | GUI Y 轴偏移 | AnimationCurve (值域: 0.05 ~ 2+) |
| Rotation | GUI X 轴偏移 | AnimationCurve (值域: 角度) |

#### 关键算法

##### 速度曲线生成 (`GenerateEaseCurve`)

```
输入: StrokeSample[] samples
输出: AnimationCurve (时间 → 进度)

1. 计算每段的瞬时速度: speed[i] = distance[i] / dt[i]
2. 归一化速度值到 [0, 1]
3. 根据平滑参数混合权重:
   weight = lerp(1, normalizedSpeed, 1 - smoothing)
4. 累积加权进度生成曲线关键帧
5. 应用 ClampedAuto 切线平滑
```

##### 路径简化 (`SimplifyPath`)

使用 **Douglas-Peucker 算法** 减少路径点数量：

```
输入: Vector3[] points, float tolerance
输出: Vector3[] simplifiedPoints

1. 标记首尾点为保留
2. 递归查找距离直线最远的点
3. 若最远距离 > tolerance，标记该点并递归两侧
4. 返回所有标记点
```

##### 坐标空间转换

```
World → Local Offset:
  offset = parent.InverseTransformPoint(worldPoint) - startLocalPosition

Local Offset → World:
  worldPoint = parent.TransformPoint(startLocalPosition + offset)
```

#### Scene GUI 事件处理流程

```
OnSceneGUI(SceneView)
    ├── DrawStrokeInScene()        // 绘制当前笔画（橙色线条）
    ├── DrawGeneratedPathInScene() // 绘制已生成路径（青色线条）
    └── HandleSceneInput()
            ├── MouseDown + Shift → StartStroke()
            ├── MouseDrag        → AddSample()
            ├── MouseUp          → FinishStroke() → GenerateXxxChannel()
            └── Escape           → Cancel stroke
```

### 2. GestureCurvePreset (Runtime)

**职责**：作为 ScriptableObject 存储手绘动效数据

#### 数据模型

```csharp
public class GestureCurvePreset : ScriptableObject
{
    // 核心数据
    public AnimationCurve easeCurve;        // 缓动曲线
    public float recommendedDuration;        // 推荐时长
    
    // 位置通道
    public bool hasPositionPath;
    public List<Vector3> localPathPoints;   // 相对偏移点集
    
    // 缩放通道
    public bool hasScaleChannel;
    public AnimationCurve scaleCurve;
    
    // 旋转通道
    public bool hasRotationChannel;
    public AnimationCurve rotationCurve;
}
```

#### Sequence 构建逻辑

```csharp
public Sequence CreateTransformSequence(Transform target, ...)
{
    var sequence = DOTween.Sequence();
    
    // 1. Position Path (使用 DOPath/DOLocalPath)
    if (hasPositionPath)
        sequence.Join(target.DOLocalPath(waypoints, duration)
                           .SetEase(easeCurve));
    
    // 2. Scale Channel (使用 DOVirtual.Float)
    if (hasScaleChannel)
        sequence.Join(DOVirtual.Float(0, 1, duration, t => 
            target.localScale = baseScale * scaleCurve.Evaluate(t)));
    
    // 3. Rotation Channel (2D: Z 轴)
    if (hasRotationChannel)
        sequence.Join(DOVirtual.Float(0, 1, duration, t =>
            target.localEulerAngles = new Vector3(x, y, baseZ + rotationCurve.Evaluate(t))));
    
    return sequence.Pause();
}
```

### 3. GestureTweenPlayer (Runtime)

**职责**：运行时播放 GestureCurvePreset

#### 生命周期

```
OnEnable()
    └── playOnEnable ? Play() : -

Play(restart)
    ├── restart ? Stop() : -
    ├── preset.CreateTransformSequence(transform, ...)
    ├── linkToGameObject ? SetLink() : -
    └── sequence.Play()

OnDisable()
    └── Stop()

Stop()
    └── sequence?.Kill()
```

## 扩展指南

### 添加新通道

1. 在 `GestureChannel` 枚举中添加新类型
2. 在 `GestureCurvePreset` 中添加对应数据字段
3. 在 `GestureCurveWindow` 中：
   - `DrawChannelConfig()` 添加 UI
   - 添加 `GenerateXxxChannel()` 方法
   - `FinishStroke()` 中添加分支
   - `PopulatePreset()` 中添加数据拷贝
4. 在 `GestureCurvePreset.CreateTransformSequence()` 中添加 Tween 构建逻辑

### 自定义导出目标

参考 `ApplyToDotweenPath()` 实现：

```csharp
private void ApplyToCustomComponent()
{
    Transform target = GetTarget();
    
    // 1. 获取或创建组件
    var component = target.GetComponent<CustomType>() 
                 ?? Undo.AddComponent<CustomType>(target.gameObject);
    
    // 2. 使用 SerializedObject 写入数据
    Undo.RecordObject(component, "Apply Gesture Data");
    var so = new SerializedObject(component);
    SetFloat(so, "duration", _recommendedDuration);
    SetCurve(so, "easeCurve", CloneCurve(_generatedEaseCurve));
    // ... 其他属性
    so.ApplyModifiedPropertiesWithoutUndo();
    
    // 3. 标记脏数据
    EditorUtility.SetDirty(component);
    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
}
```

## DOTween 集成细节

### DOTweenPath 集成

通过反射访问 DOTweenPath（避免硬依赖编译问题）：

```csharp
private static readonly Type DotweenPathType = 
    Type.GetType("DG.Tweening.DOTweenPath, DOTweenPro");

// 使用 SerializedObject 写入：
SetFloat(so, "duration", duration);
SetInt(so, "easeType", (int)Ease.INTERNAL_Custom);
SetCurve(so, "easeCurve", curve);
SetBool(so, "isLocal", true);
SetInt(so, "pathType", 1);  // CatmullRom
WriteVector3Array(so, "wps", pathPoints, skipFirst: true);
```

### 路径点格式说明

DOTween 的 `DOPath` / `DOLocalPath` API：
- **第一个路径点 = 目标当前位置**（隐式）
- `wps` 数组从第二个点开始

因此写入时需要 `skipFirst: true`：
```csharp
WriteVector3Array(so, "wps", pathPoints, skipFirst: true);
```

## 性能考量

| 场景 | 建议 |
|------|------|
| 大量采样点 | 使用较大的 `minSampleDistance` 和 `minSampleInterval` |
| 复杂路径 | 增加 `pathSimplifyTolerance` 减少点数 |
| 运行时创建 | 预先加载 Preset，复用 Sequence |
| 多个 Player | 使用 `SetLink()` 自动管理生命周期 |

## 文件编码

所有 `.cs` 文件使用 **UTF-8 with BOM** 编码，以确保中文注释正确显示。

## 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| 1.0.0 | 2026-02 | 初始版本：Position/Scale/Rotation 通道，DOTween 集成 |
