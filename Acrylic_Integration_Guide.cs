// 完整的自动亚克力效果集成指南

/*
## 自动亚克力效果系统

这个系统让 AcrylicContainer 自动工作，无需修改 Game 类。

### 1. 在你的游戏中添加自动背景捕获组件

在你的 Game 类的 load 方法中添加：

```csharp
[BackgroundDependencyLoader]
private void load()
{
    // ... 其他初始化代码 ...

    // 添加自动背景捕获组件（只需要添加一次）
    Add(new AutoBackgroundCapture());

    // ... 创建你的 UI 组件 ...
}
```

### 2. 在 UI 组件中使用 AcrylicContainer

```csharp
public partial class MyUIComponent : CompositeDrawable
{
    private AcrylicContainer background = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        background = new AcrylicContainer
        {
            RelativeSizeAxes = Axes.Both,
            TintColour = new Color4(0, 0, 0, 0.3f), // 黑色半透明
            BlurStrength = 5f, // 模糊强度
        };

        AddInternal(background);
    }

    // 动态控制效果
    public void SetAcrylicEffect(float alpha, float blur)
    {
        background.TintColour = new Color4(0, 0, 0, alpha);
        background.BlurStrength = blur;
    }
}
```

### 3. 系统如何工作

1. **AutoBackgroundCapture** 组件自动在 `UpdateAfterChildren` 中捕获屏幕内容
2. **BackgroundBufferManager** 单例管理全局背景缓冲区
3. **AcrylicContainer** 自动从管理器获取背景缓冲区并应用效果
4. 所有 AcrylicContainer 实例共享同一个背景缓冲区

### 4. 优势

- ✅ **无需修改 Game 类**：保持游戏逻辑清洁
- ✅ **自动工作**：UI 组件创建后自动获得背景虚化
- ✅ **高性能**：所有组件共享一个背景缓冲区
- ✅ **易于使用**：只需创建 AcrylicContainer 即可

### 5. 自定义选项

如果需要自定义背景缓冲区，可以手动设置：

```csharp
acrylicContainer.BackgroundBuffer = myCustomBuffer;
```

但在大多数情况下，自动系统就足够了。
*/

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace osu.Framework.Graphics.Containers
{
    // 这个文件只是为了展示用法，实际实现已在其他文件中
}
