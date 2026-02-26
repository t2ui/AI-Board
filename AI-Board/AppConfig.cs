using System;
using System.IO;
using System.Text.Json;

namespace AI_Board
{
    // --- 实体类：摄像头设备 ---
    public class CameraInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
    }
    // 用于显示 U 盘信息的简单实体
    public class UsbDriveItem
    {
        public string RootPath { get; set; }     // 例如 E:\
        public string DisplayName { get; set; }  // 例如 E:\ (KINGSTON)
    }
    // 定义 Toast 类型
    public enum ToastType
    {
        Info,
        Success,
        Error
    }

    // ==========================================
    // 1. 定义反射 UI 渲染所需的特性和枚举
    // ==========================================
    public enum SettingControlType
    {
        Switch,   // 开关 (ToggleButton)
        Slider,   // 滑块 (Slider)
        TextBox   // 文本框 (TextBox)
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class SettingAttribute : Attribute
    {
        public string Group { get; set; }           // 分组名称
        public string Title { get; set; }           // 配置项标题
        public string Description { get; set; }     // 配置项详细说明
        public SettingControlType ControlType { get; set; } // 控件类型
        public double Min { get; set; }             // 滑块最小值
        public double Max { get; set; }             // 滑块最大值
        public double Tick { get; set; } = 1;       // 滑块步长
        public bool RequiresRestart { get; set; } = false; // 是否需要重启 AI 引擎
    }

    // ==========================================
    // 2. 应用程序配置类 (文案优化版)
    // ==========================================
    public class AppConfig
    {
        // 内部配置
        public string LastCameraName { get; set; } = "";

        // --- 画面显示 ---
        [Setting(Group = "画面显示", Title = "调试图层", Description = "叠加显示识别区域掩膜(Mask)及边缘轮廓", ControlType = SettingControlType.Switch)]
        public bool ShowDebugStream { get; set; } = false;

        [Setting(Group = "画面显示", Title = "丢失保持时长", Description = "识别失败时维持上一帧有效画面的时间 (ms)", ControlType = SettingControlType.Slider, Min = 0, Max = 5000, Tick = 100)]
        public int TargetLostRestoreMs { get; set; } = 1500;

        [Setting(Group = "画面显示", Title = "纵向铺满模式", Description = "窗口高度填满屏幕，仅允许横向调整以最大化显示区域", ControlType = SettingControlType.Switch)]
        public bool MaximizeWindowHeight { get; set; } = true;

        [Setting(Group = "画面显示", Title = "强制输出比例", Description = "指定矫正后画面的宽高比 (如 4:3)，留空则自适应", ControlType = SettingControlType.TextBox)]
        public string ForceAspectRatio { get; set; } = "4:7";

        [Setting(Group = "画面显示", Title = "窗口置顶", Description = "保持窗口位于其他应用程序之上", ControlType = SettingControlType.Switch)]
        public bool WindowTopmost { get; set; } = true;

        // --- 悬浮窗 (Mini Mode) 分组 ---
        [Setting(Group = "悬浮窗", Title = "启用最小化悬浮窗", Description = "窗口最小化时在任务栏上方显示快捷入口", ControlType = SettingControlType.Switch)]
        public bool EnableMiniWidget { get; set; } = true;

        [Setting(Group = "悬浮窗", Title = "水平位置比例", Description = "悬浮窗在屏幕宽度的百分比位置 (0.1 - 0.95)", ControlType = SettingControlType.Slider, Min = 0.0, Max = 1.0, Tick = 0.05)]
        public double MiniWidgetPositionX { get; set; } = 0.80; // 默认 80%

        [Setting(Group = "悬浮窗", Title = "自动适配文字颜色", Description = "根据背景颜色自动切换黑/白文字 (关闭则强制白色)", ControlType = SettingControlType.Switch)]
        public bool MiniWidgetAutoColor { get; set; } = true;
    
        // --- AI 引擎 ---
        [Setting(Group = "AI 引擎", Title = "GPU 加速", Description = "启用显卡 DirectML 硬件加速 (需重启)", ControlType = SettingControlType.Switch, RequiresRestart = true)]
        public bool UseGPU { get; set; } = true;

        [Setting(Group = "AI 引擎", Title = "CPU 线程限制", Description = "设定 ONNX 推理引擎的最大并发核心数", ControlType = SettingControlType.Slider, Min = 1, Max = 32, Tick = 1, RequiresRestart = true)]
        public int MaxCpuThreads { get; set; } = 4;

        [Setting(Group = "AI 引擎", Title = "帧率上限", Description = "限制处理帧率以降低系统功耗 (FPS)", ControlType = SettingControlType.Slider, Min = 10, Max = 120, Tick = 5)]
        public int MaxFPS { get; set; } = 30;


        // --- 动态策略 ---
        [Setting(Group = "动态策略", Title = "静止判定阈值", Description = "帧间差异低于此值时暂停 AI 计算", ControlType = SettingControlType.Slider, Min = 0, Max = 10, Tick = 0.5)]
        public double MotionThreshold { get; set; } = 2.0;

        [Setting(Group = "动态策略", Title = "静止强制刷新", Description = "画面静止时的最低重识别间隔 (秒)", ControlType = SettingControlType.Slider, Min = 1, Max = 30, Tick = 1)]
        public int ForceUpdateIntervalSec { get; set; } = 5;


        // --- 图像源 ---
        [Setting(Group = "图像源", Title = "水平镜像", Description = "左右翻转摄像头原始输入画面", ControlType = SettingControlType.Switch)]
        public bool FlipHorizontal { get; set; } = false;

        [Setting(Group = "图像源", Title = "输入对比度", Description = "调整原始画面的对比度增益 (100为基准)", ControlType = SettingControlType.Slider, Min = 50, Max = 150, Tick = 1)]
        public int Contrast { get; set; } = 100;

        [Setting(Group = "图像源", Title = "输入亮度", Description = "调整原始画面的亮度偏移", ControlType = SettingControlType.Slider, Min = -100, Max = 100, Tick = 5)]
        public int Brightness { get; set; } = 0;

        [Setting(Group = "图像源", Title = "边缘锐化", Description = "增强边缘清晰度以辅助 AI 识别", ControlType = SettingControlType.Slider, Min = 0, Max = 5, Tick = 1)]
        public int Sharpness { get; set; } = 0;


        // --- 云端同步 ---
        [Setting(Group = "云端同步", Title = "服务器地址", Description = "接收板书图片的 API 完整路径", ControlType = SettingControlType.TextBox)]
        public string CloudServerUrl { get; set; } = "";

        [Setting(Group = "云端同步", Title = "认证令牌", Description = "API 请求头鉴权 Token", ControlType = SettingControlType.TextBox)]
        public string CloudToken { get; set; } = "";

        [Setting(Group = "云端同步", Title = "定时任务", Description = "指定自动上传的时间点 (格式: 09:00,12:30)", ControlType = SettingControlType.TextBox)]
        public string CloudScheduleTimes { get; set; } = "";

        [Setting(Group = "云端同步", Title = "静止自动存档", Description = "画面维持静止达指定时长后自动上传 (0为关闭)", ControlType = SettingControlType.Slider, Min = 0, Max = 60, Tick = 1)]
        public int CloudStaticThresholdSec { get; set; } = 10;

        [Setting(Group = "云端同步", Title = "存档冷却", Description = "静止上传触发后的最小间隔时间 (分)", ControlType = SettingControlType.Slider, Min = 1, Max = 60, Tick = 1)]
        public int CloudUploadCooldownMin { get; set; } = 5;
    }

    // ==========================================
    // 3. 配置管理器
    // ==========================================
    public static class ConfigManager
    {
        private static readonly string ConfigPath = "config.json";
        public static AppConfig Config { get; private set; } = new AppConfig();

        public static void Load()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch { Config = new AppConfig(); }
            }
        }

        public static void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}