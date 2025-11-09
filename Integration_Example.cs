// 正确的集成方式：背景缓冲区由游戏层管理

public partial class ManiaGameMode : Game
{
    private IFrameBuffer? globalBackgroundBuffer;
    private EzColumnBackground columnBackground = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        // 创建轨道背景
        Add(columnBackground = new EzColumnBackground());
    }

    protected override void UpdateAfterChildren()
    {
        base.UpdateAfterChildren();

        // 在游戏层创建和管理背景缓冲区
        globalBackgroundBuffer ??= Host.Renderer.CreateFrameBuffer(null, TextureFilteringMode.Linear);

        // 捕获当前屏幕内容
        if (Host.Renderer is Renderer concreteRenderer)
        {
            concreteRenderer.CaptureScreenToFrameBuffer(globalBackgroundBuffer);
        }

        // 设置给轨道背景
        columnBackground.SetBackgroundBuffer(globalBackgroundBuffer);
    }

    protected override void Update()
    {
        base.Update();

        // 根据游戏状态控制虚化效果
        // 例如：根据谱面难度、播放状态等调整
        columnBackground.SetDimLevel(0.3f, 5f);
    }
}
