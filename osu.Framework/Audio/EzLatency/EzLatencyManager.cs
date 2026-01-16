// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzLatency延迟测试管理器，提供与程序设置关联的公共接口
    /// </summary>
    public class EzLatencyManager
    {
        /// <summary>
        /// 延迟测试启用状态的绑定值，可与程序设置关联
        /// </summary>
        public readonly BindableBool Enabled = new BindableBool(false);

        private readonly EzLatencyAnalyzer analyzer;

        public EzLatencyManager()
        {
            analyzer = new EzLatencyAnalyzer();

            // 将启用状态与分析器同步
            Enabled.BindValueChanged(v =>
            {
                analyzer.Enabled = v.NewValue;
            }, true);

            // 将分析器的记录事件转发给外部
            analyzer.OnNewRecord += record => OnNewRecord?.Invoke(record);
        }

        /// <summary>
        /// 当有新的延迟记录时触发
        /// </summary>
        public event Action<EzLatencyRecord>? OnNewRecord;

        /// <summary>
        /// 在gameplay期间记录输入事件
        /// </summary>
        /// <param name="keyValue">按键值或输入标识</param>
        public void RecordInputEvent(object? keyValue = null)
        {
            if (!analyzer.Enabled) return;

            double inputTime = analyzer.GetCurrentTimestamp();
            analyzer.RecordInputData(inputTime, keyValue);
        }

        /// <summary>
        /// 在gameplay期间记录判定事件
        /// </summary>
        public void RecordJudgeEvent()
        {
            if (!analyzer.Enabled) return;

            double judgeTime = analyzer.GetCurrentTimestamp();
            analyzer.RecordJudgeData(judgeTime);
        }

        /// <summary>
        /// 在gameplay期间记录播放事件
        /// </summary>
        public void RecordPlaybackEvent()
        {
            if (!analyzer.Enabled) return;

            double playbackTime = analyzer.GetCurrentTimestamp();
            analyzer.RecordPlaybackData(playbackTime);
        }

        /// <summary>
        /// 在gameplay期间记录硬件数据
        /// </summary>
        /// <param name="driverTime">驱动时间</param>
        /// <param name="outputHardwareTime">输出硬件时间</param>
        /// <param name="inputHardwareTime">输入硬件时间</param>
        /// <param name="latencyDifference">延迟差异</param>
        public void RecordHardwareData(double driverTime, double outputHardwareTime, double inputHardwareTime, double latencyDifference)
        {
            if (!analyzer.Enabled) return;

            analyzer.RecordHardwareData(driverTime, outputHardwareTime, inputHardwareTime, latencyDifference);
        }

        /// <summary>
        /// 获取当前时间戳
        /// </summary>
        /// <returns>当前时间戳（毫秒）</returns>
        public double GetCurrentTimestamp() => analyzer.GetCurrentTimestamp();

        /// <summary>
        /// 在gameplay开始时启用延迟测试
        /// </summary>
        public void StartGameplayTest()
        {
            Enabled.Value = true;
        }

        /// <summary>
        /// 在gameplay结束时禁用延迟测试
        /// </summary>
        public void StopGameplayTest()
        {
            Enabled.Value = false;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Enabled.UnbindAll();
            analyzer.OnNewRecord -= OnNewRecord;
        }
    }
}
