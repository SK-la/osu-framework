// 全局背景缓冲区管理器 - 自动管理背景捕获

using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Graphics.Containers
{
    public class BackgroundBufferManager
    {
        private static BackgroundBufferManager? instance;
        private static readonly object lockObject = new object();

        public static BackgroundBufferManager Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (lockObject)
                    {
                        instance ??= new BackgroundBufferManager();
                    }
                }

                return instance;
            }
        }

        private IFrameBuffer? backgroundBuffer;
        private IRenderer? renderer;
        private bool initialized;

        // 初始化管理器（在游戏启动时调用一次）
        public void Initialize(IRenderer renderer)
        {
            this.renderer = renderer;
            initialized = false;
        }

        // 获取背景缓冲区 - 如果不存在则创建
        public IFrameBuffer? GetBackgroundBuffer()
        {
            if (backgroundBuffer == null && renderer != null)
            {
                backgroundBuffer = renderer.CreateFrameBuffer(null, TextureFilteringMode.Linear);
                initialized = true;
            }
            return backgroundBuffer;
        }

        // 获取或创建背景缓冲区（在绘制线程调用 - 延迟创建模式）
        public IFrameBuffer GetOrCreateBackgroundBuffer(IRenderer renderer)
        {
            if (backgroundBuffer == null)
            {
                this.renderer = renderer;
                backgroundBuffer = renderer.CreateFrameBuffer(null, TextureFilteringMode.Linear);
                initialized = true;
            }

            return backgroundBuffer;
        }

        // 确保背景缓冲区已创建（在绘制线程安全的地方调用）
        public void EnsureBackgroundBuffer()
        {
            if (!initialized && renderer != null)
            {
                backgroundBuffer ??= renderer.CreateFrameBuffer(null, TextureFilteringMode.Linear);
                initialized = true;
            }
        }

        // 更新背景缓冲区（在UpdateAfterChildren中调用）
        public void UpdateBackgroundBuffer()
        {
            if (renderer is Renderer concreteRenderer && backgroundBuffer != null)
            {
                concreteRenderer.CaptureScreenToFrameBuffer(backgroundBuffer);
            }
        }

        // 清理资源
        public void Dispose()
        {
            backgroundBuffer?.Dispose();
            backgroundBuffer = null;
            renderer = null;
        }
    }
}
