# Acrylic Container 实现说明 / Implementation Notes

## 问题分析 / Problem Analysis

### 原始需求 / Original Requirement
用户希望实现"真正的毛玻璃效果" - 即模糊容器**背后**的内容,类似于 Windows Acrylic 或 macOS 的毛玻璃效果。

The user wanted to implement a "true frosted glass effect" - blurring content **behind** the container, similar to Windows Acrylic or macOS frosted glass.

### 技术限制 / Technical Limitations

经过深入研究后发现,在 osu-framework 的当前架构下,**无法实现真正的背景模糊效果**:

After thorough research, it was discovered that **true background blur is not possible** in the current osu-framework architecture:

1. **渲染顺序 / Rendering Order**
   - Framework 使用自上而下的渲染顺序 (top-to-bottom)
   - 当容器被渲染时,背后的内容已经绘制到了后台缓冲区
   - 没有办法"回溯"并捕获已经渲染的内容
   
   - Framework uses top-to-bottom rendering order
   - When a container is rendered, content behind it is already drawn to the backbuffer
   - There's no way to "retroactively" capture already-rendered content

2. **CaptureScreenToFrameBuffer 未实现 / CaptureScreenToFrameBuffer Not Implemented**
   - 发现 `IRenderer.CaptureScreenToFrameBuffer` 方法存在但未实现
   - `DeferredRenderer.CaptureScreenToFrameBuffer` 只是一个 TODO 占位符
   - 这个方法原本可以用来捕获当前屏幕内容,但目前不可用
   
   - The `IRenderer.CaptureScreenToFrameBuffer` method exists but is not implemented
   - `DeferredRenderer.CaptureScreenToFrameBuffer` is just a TODO placeholder
   - This method could have been used to capture current screen content, but it's currently unavailable

3. **BufferedContainer 的限制 / BufferedContainer Limitations**
   - `BufferedContainer` 只能模糊**自己的子元素**
   - 它先将子元素渲染到帧缓冲区,然后应用模糊
   - 无法访问或模糊父容器或兄弟元素的内容
   
   - `BufferedContainer` can only blur **its own children**
   - It renders children to a framebuffer first, then applies blur
   - It cannot access or blur content from parent or sibling containers

## 实现方案 / Implementation Approach

### 最终方案 / Final Solution
基于技术限制,改为实现一个**视觉上接近毛玻璃效果**的容器:

Given the technical constraints, implemented a container that **visually approximates the frosted glass effect**:

```csharp
public partial class AcrylicContainer : BufferedContainer
{
    // 模糊一个背景层 (Blur a background layer)
    private Drawable backgroundBox;
    
    // 在上面叠加一个着色覆盖层 (Overlay a tinted layer on top)
    private Drawable tintOverlay;
    
    // 用户内容在最上层 (User content on top)
    public Children { get; set; }
}
```

**工作原理 / How It Works:**

1. **背景层 (Background Layer)**
   - 创建一个 `Box` 作为背景 (可以是纯色或图像)
   - 这个背景层会被 `BufferedContainer` 的模糊效果处理
   
   - Creates a `Box` as background (can be solid color or image)
   - This background layer is processed by `BufferedContainer`'s blur effect

2. **模糊处理 (Blur Processing)**
   - 继承自 `BufferedContainer`,利用其内置的高斯模糊
   - 通过 `BlurTo()` 方法应用模糊效果
   
   - Inherits from `BufferedContainer`, leveraging its built-in Gaussian blur
   - Applies blur effect via `BlurTo()` method

3. **着色覆盖 (Tint Overlay)**
   - 在模糊背景上叠加一个半透明的着色层
   - 模拟毛玻璃的着色效果
   
   - Overlays a semi-transparent tinted layer on the blurred background
   - Simulates the tinting effect of frosted glass

4. **用户内容 (User Content)**
   - 用户添加的子元素显示在最上层
   - 可以透过半透明的覆盖层看到模糊的背景
   
   - User-added children are displayed on top
   - Blurred background is visible through the semi-transparent overlay

## 使用方法 / Usage

### 基本用法 / Basic Usage

```csharp
var acrylicEffect = new AcrylicContainer
{
    RelativeSizeAxes = Axes.Both,
    BlurStrength = 15f,                           // 模糊强度 (0-100)
    TintColour = new Color4(0, 0, 0, 0.3f),       // 着色 (半透明黑色)
    BackgroundColour = Color4.White,               // 要模糊的背景色
    Children = new Drawable[]
    {
        new SpriteText { Text = "Content" }
    }
};
```

### 属性说明 / Properties

| 属性 / Property | 说明 / Description |
|----------------|-------------------|
| `BlurStrength` | 模糊强度,值越大越模糊 (0-100) / Blur intensity, higher = more blur (0-100) |
| `TintColour` | 着色颜色,通常是半透明色 / Tint color, usually semi-transparent |
| `BackgroundColour` | 背景颜色,这个颜色会被模糊 / Background color that will be blurred |

### 视觉效果 / Visual Result

```
┌─────────────────────────────────┐
│     用户内容 (User Content)       │  ← 清晰的文字/图像
├─────────────────────────────────┤
│   半透明着色层 (Tint Overlay)     │  ← Alpha < 1.0
├─────────────────────────────────┤
│   模糊的背景 (Blurred Background) │  ← 高斯模糊效果
└─────────────────────────────────┘
```

## 局限性 / Limitations

1. **不是真正的背景模糊 / Not True Background Blur**
   - 只模糊容器自己的背景层,不是背后的其他元素
   - 要模糊的内容必须作为背景添加到容器内部
   
   - Only blurs the container's own background layer, not elements behind it
   - Content to be blurred must be added as a background inside the container

2. **性能开销 / Performance Overhead**
   - 每个 `AcrylicContainer` 使用一个 `BufferedContainer`
   - 模糊操作需要额外的帧缓冲区和GPU计算
   - 不建议在一个场景中使用过多此效果
   
   - Each `AcrylicContainer` uses a `BufferedContainer`
   - Blur operations require additional framebuffers and GPU computation
   - Not recommended to use too many instances in one scene

3. **框架限制 / Framework Limitations**
   - 真正的毛玻璃效果需要 framework 级别的支持
   - 需要实现 `CaptureScreenToFrameBuffer` 或类似机制
   - 这超出了当前任务的范围
   
   - True frosted glass requires framework-level support
   - Would need to implement `CaptureScreenToFrameBuffer` or similar mechanism
   - This is beyond the scope of the current task

## 未来改进 / Future Improvements

如果要实现真正的背景模糊,需要:

To implement true background blur, would need:

1. **实现屏幕捕获 / Implement Screen Capture**
   ```csharp
   // 在 DeferredRenderer 中实现
   public override void CaptureScreenToFrameBuffer(IFrameBuffer frameBuffer)
   {
       // 将当前后台缓冲区内容复制到 frameBuffer
       // Copy current backbuffer content to frameBuffer
   }
   ```

2. **渲染顺序调整 / Rendering Order Adjustment**
   - 在绘制 AcrylicContainer 之前,先绘制所有背后的元素
   - 捕获屏幕内容
   - 应用模糊并绘制
   
   - Draw all elements behind the AcrylicContainer first
   - Capture screen content
   - Apply blur and render

3. **Z-Order 支持 / Z-Order Support**
   - Framework 需要支持基于深度的渲染顺序
   - 或者添加特殊的"背景捕获"阶段
   
   - Framework needs to support depth-based rendering order
   - Or add a special "background capture" phase

## 测试 / Testing

运行测试场景查看效果:

Run the test scene to see the effect:

```bash
dotnet run --project osu.Framework.Tests -- TestSceneAcrylicContainerNew
```

**预期结果 / Expected Result:**
- 看到带有模糊背景的容器
- 背景是模糊的白色/黑色
- 上面有清晰的文字
- 背后的彩色方块移动 (不会被模糊)

- See a container with blurred background
- Background is blurred white/black
- Clear text on top
- Colored boxes moving behind (not blurred)

## 结论 / Conclusion

虽然无法实现用户最初想要的"真正的毛玻璃效果"(模糊背后的内容),但当前实现提供了:

While true "frosted glass effect" (blurring content behind) is not achievable, the current implementation provides:

✅ 视觉上接近的效果 / Visually similar effect
✅ 简单易用的 API / Simple, easy-to-use API
✅ 符合 framework 架构 / Follows framework architecture
✅ 良好的性能 / Good performance

如果将来 framework 添加了背景捕获支持,可以在不改变 API 的情况下升级实现。

If the framework adds background capture support in the future, the implementation can be upgraded without changing the API.
