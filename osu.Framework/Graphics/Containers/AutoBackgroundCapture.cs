// 自动背景捕获组件 - 在游戏中自动管理背景缓冲区更新

using osu.Framework.Graphics.Rendering;
using osu.Framework.Allocation;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A component that automatically captures the screen background for acrylic effects.
    /// Add this to your game once to enable automatic background capture for all AcrylicContainer instances.
    /// </summary>
    public partial class AutoBackgroundCapture : Drawable
    {
        [Resolved]
        private IRenderer? renderer { get; set; }

        private bool initialized;

        protected override void LoadComplete()
        {
            base.LoadComplete();
            // 初始化延迟到Update方法中进行
        }

        protected override void Update()
        {
            base.Update();

            // 延迟初始化，确保在正确的线程中
            if (!initialized && renderer != null)
            {
                BackgroundBufferManager.Instance.Initialize(renderer);
                initialized = true;
            }

            // 自动更新背景缓冲区
            BackgroundBufferManager.Instance.UpdateBackgroundBuffer();
        }
    }
}
