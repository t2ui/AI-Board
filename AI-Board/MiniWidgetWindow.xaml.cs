using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Drawing; // 需要引用 System.Drawing (或 NuGet System.Drawing.Common)
using Point = System.Drawing.Point; // 消除歧义

namespace AI_Board
{
    public partial class MiniWidgetWindow : Window
    {
        private MainWindow _mainWindow;
        private DispatcherTimer _topmostTimer;
        private DispatcherTimer _colorCheckTimer;

        // Win32 API 引入
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        public MiniWidgetWindow(MainWindow main)
        {
            InitializeComponent();
            _mainWindow = main;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdatePosition();

            // 1. 启动强制置顶定时器 (每 1/2 秒执行一次，防止被任务栏覆盖)
            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
            _topmostTimer.Tick += (s, args) => ForceTopmost();
            _topmostTimer.Start();


            // 2. 启动颜色检测 (首次执行，之后每 3 秒检测一次)
            if (ConfigManager.Config.MiniWidgetAutoColor)
            {
                CheckBackgroundColor();
                _colorCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _colorCheckTimer.Tick += (s, args) => CheckBackgroundColor();
                _colorCheckTimer.Start();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _topmostTimer?.Stop();
            _colorCheckTimer?.Stop();
        }

        // 点击恢复主窗口
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mainWindow.RestoreFromMiniWidget();
            this.Close();
        }

        // 计算位置：水平按比例，垂直贴底 (位于任务栏上方)
        private void UpdatePosition()
        {
            var workArea = SystemParameters.WorkArea;
            double screenHeight = SystemParameters.PrimaryScreenHeight; // [修改] 获取包含任务栏的屏幕总高度

            // X轴：保持原有逻辑 (80% 处)
            double targetX = workArea.Left + (workArea.Width * ConfigManager.Config.MiniWidgetPositionX) - (this.Width / 2);

            // Y轴：[核心修改] 改用屏幕总高度计算
            // 现在的逻辑：屏幕最底部 - 窗口高度 - 底部边距(例如15px)
            // 这样它就会落在任务栏的范围内了
            double bottomMargin = 2; // 你可以调整这个值来控制它在任务栏内部的垂直位置
            double targetY = screenHeight - this.Height - bottomMargin;

            // X轴边界检查
            if (targetX < workArea.Left) targetX = workArea.Left;
            if (targetX + this.Width > workArea.Right) targetX = workArea.Right - this.Width;

            this.Left = targetX;
            this.Top = targetY;
        }
        // 强制置顶 (Win32 方式)
        private void ForceTopmost()
        {
            var interopHelper = new System.Windows.Interop.WindowInteropHelper(this);
            SetWindowPos(interopHelper.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // 检测背景颜色并反转文字颜色
        // 检测背景颜色并反转文字颜色
        private void CheckBackgroundColor()
        {
            try
            {
                // 1. 获取窗口附近的逻辑坐标 (WPF 坐标系)
                // 【避坑1】：不要取正中心 (this.Width/2)，防止取到悬浮窗自己的图标或文字导致无限变色闪烁！
                // 解决方案：取窗口左侧外边缘 20 像素处的点，确保取到的是纯粹的任务栏背景。
                double logicalX = this.Left - 20;
                if (logicalX < 0) logicalX = this.Left + this.Width + 20; // 防止悬浮窗靠最左边时越界

                double logicalY = this.Top + this.Height / 2; // 垂直依然取中心

                // 2. 获取当前屏幕的 DPI 缩放比例
                double dpiX = 1.0;
                double dpiY = 1.0;

                // 从当前的 UI 视觉树获取真实的缩放矩阵
                PresentationSource source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11; // 125% 缩放时，这里会是 1.25
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                // 3. 【避坑2】：转换为 Win32 API 需要的真实物理像素坐标 (乘 DPI)
                int physicalX = (int)(logicalX * dpiX);
                int physicalY = (int)(logicalY * dpiY);

                // 4. 获取屏幕 DC 并取色
                IntPtr hdc = GetDC(IntPtr.Zero);
                uint pixel = GetPixel(hdc, physicalX, physicalY);
                ReleaseDC(IntPtr.Zero, hdc);

                // 5. 解析颜色 (Windows GDI 的 COLORREF 是 0x00BBGGRR，和 C# 的 RGB 是反过来的)
                System.Drawing.Color color = System.Drawing.Color.FromArgb(
                    (int)(pixel & 0x000000FF),             // R
                    (int)(pixel & 0x0000FF00) >> 8,        // G
                    (int)(pixel & 0x00FF0000) >> 16        // B
                );

                // 6. 计算亮度 (Luminance) 阈值可微调，通常 130~140 是中界线
                double brightness = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B);

                // 根据亮度判断，背景太亮则用黑字，太暗则用白字
                var newBrush = (brightness > 140) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;

                // 7. 更新 UI
                if (TxtTitle.Foreground != newBrush)
                {
                    TxtTitle.Foreground = newBrush;
                }
            }
            catch
            {
                // 取色失败时，默认回落为白色
                TxtTitle.Foreground = System.Windows.Media.Brushes.White;
            }
        }
    }
}