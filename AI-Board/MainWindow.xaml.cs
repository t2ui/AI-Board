using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Animation;
namespace AI_Board
{
    public partial class MainWindow : System.Windows.Window
    {
        private InferenceSession _session;
        private string _modelPath = "Models/2.onnx";

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        // 参数配置
        private const int ImgSize = 640;
        private const int MaskSize = 160;
        private const float ConfThreshold = 0.5f;
        private const float NmsThreshold = 0.45f;
        private const int NumClasses = 1;

        // 颜色定义...
        private readonly Scalar ColorTop = Scalar.Red;
        private readonly Scalar ColorBtm = Scalar.Green;
        private readonly Scalar ColorLeft = Scalar.Blue;
        private readonly Scalar ColorRight = Scalar.Yellow;
        private readonly Scalar ColorLine = Scalar.White;
        private readonly Scalar ColorCorner = Scalar.Cyan;

        struct ParametricLine { public Point2f P; public Point2f V; }

        private VideoCapture _videoCapture;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isCameraRunning = false;
        private object _processLock = new object();

        // 性能监控
        private DispatcherTimer _monitorTimer;
        private PerformanceCounter _cpuCounter;
        private Dictionary<string, PerformanceCounter> _gpuCounters = new Dictionary<string, PerformanceCounter>();
        private int _currentPid;
        private int _frameCount = 0;
        private Stopwatch _fpsStopwatch = new Stopwatch();

        // 节能与回滚变量
        private Mat _lastFrameGray = null;
        private DateTime _lastProcessTime = DateTime.MinValue;
        private readonly object _historyLock = new object();


        private double _fixedTop = double.NaN; // 用于锁定最大化时禁止上下拖拽
        private bool _wasRunningBeforeMinimize = false; // 用于记录最小化前的状态
                                                        // 新增：用于自定义拖拽的变量
        private bool _isDragging = false;
        private System.Windows.Point _dragStartPoint;
        // 缓存宽高比，默认为 NaN (不强制)
        private double _cachedTargetRatio = double.NaN;
        // 防止 SizeChanged 递归触发的标志位
        private bool _isResizing = false;

        // --- 云服务相关变量 ---
        private static readonly HttpClient _httpClient = new HttpClient();
        private DateTime _lastCloudUploadTime = DateTime.MinValue; // 上次成功上传的时间
        private double _continuousStaticSeconds = 0; // 当前画面已持续静止的秒数
        private string _lastScheduledUploadMinute = ""; // 防止同一分钟内重复触发定时上传
        // 用于存储上一次触发静止上传时的画面（灰度图），用于对比去重
        private Mat _lastUploadedStaticFrame = null;
        // 历史记录结构体
        struct HistoryFrame
        {
            public DateTime Timestamp;
            public ImageSource DisplayImage;
            public ImageSource RectifiedImage;
        }
        private LinkedList<HistoryFrame> _historyBuffer = new LinkedList<HistoryFrame>();

        // 标记是否正在初始化摄像头，防止 UI 触发死循环
        private bool _isInitializingCamera = false;

        public MainWindow()
        {
            InitializeComponent();
            ConfigManager.Load();
            InitHardwareMonitors();
        }
        // ================== 云服务上传逻辑 ==================
        // ================== 云服务上传逻辑 (修正为表单文件上传) ==================
        private async Task TriggerCloudUpload(Mat imageToUpload, string triggerReason)
        {
            // 基础校验
            string baseUrl = ConfigManager.Config.CloudServerUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return;

            // 注意：静止检测的冷却逻辑在调用此方法前已经处理，此处直接执行上传

            try
            {
                // 1. 准备图片二进制数据 (JPEG 格式)
                byte[] imageBytes;
                using (var ms = imageToUpload.ToMemoryStream(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 80)))
                {
                    imageBytes = ms.ToArray();
                }

                // 2. 准备 API 地址 (自动替换 {date})
                string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                string apiUrl = baseUrl.TrimEnd('/');
                if (!apiUrl.Contains("/api/camera/submit"))
                {
                    apiUrl += $"/api/camera/submit/{dateStr}";
                }
                else
                {
                    apiUrl = apiUrl.Replace("{date}", dateStr);
                }

                // 3. 构造 Multipart 表单数据
                // 必须使用 MultipartFormDataContent 来模拟浏览器文件上传行为
                var formData = new MultipartFormDataContent();

                // 添加普通文本字段
                formData.Add(new StringContent($"{triggerReason} Upload"), "title");
                formData.Add(new StringContent($"Triggered at {DateTime.Now:HH:mm:ss}"), "text");

                // 添加图片文件字段
                // 参数1: 内容, 参数2: 表单字段名(name="image"), 参数3: 文件名(filename="capture.jpg")
                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                formData.Add(imageContent, "image", $"capture_{DateTime.Now:HHmmss}.jpg");

                // 4. 构造 HTTP 请求
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, apiUrl);

                // 添加 Token 头
                if (!string.IsNullOrWhiteSpace(ConfigManager.Config.CloudToken))
                {
                    requestMessage.Headers.Add("X-Camera-Token", ConfigManager.Config.CloudToken);
                }

                // 设置 Body
                requestMessage.Content = formData;

                // 5. 发送请求
                _lastCloudUploadTime = DateTime.Now; // 更新上传时间
                                                     // 仅在手动上传时显示“正在上传”的 Toast，自动上传时不打扰
                if (triggerReason == "Manual")
                {
                    ShowToast("正在上传到云端...", ToastType.Info);
                }
                UpdateStatusUI("正在上传", "#D28E00");

                // 异步发送，不阻塞主线程
                await _httpClient.SendAsync(requestMessage).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Debug.WriteLine($"上传异常: {t.Exception?.InnerException?.Message}");
                        UpdateStatusUI("上传异常", "#B33A3A");
                        ShowToast($"上传异常: {t.Exception?.InnerException?.Message}", ToastType.Error);
                    }
                    else if (t.Result.IsSuccessStatusCode)
                    {
                        Debug.WriteLine("上传成功");
                        UpdateStatusUI("上传成功", "#4A7545");
                        ShowToast("云服务同步完成", ToastType.Success);
                    }
                    else
                    {
                        Debug.WriteLine($"上传失败: {t.Result.StatusCode}");
                        UpdateStatusUI($"错误 {t.Result.StatusCode}", "#B33A3A");
                        ShowToast($"上传失败: {t.Result.ReasonPhrase}", ToastType.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"上传逻辑错误: {ex.Message}");
            }
        }
        // ================== 窗口与基础交互事件 ==================
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _isInitializingCamera = true;
            ApplyWindowSettings(); // 附加：应用布局设置
            RefreshCameraList();

            // 1. 自动开启摄像头
            string lastCamName = ConfigManager.Config.LastCameraName;
            if (!string.IsNullOrEmpty(lastCamName))
            {
                var targetCam = CmbCameras.Items.Cast<CameraInfo>().FirstOrDefault(c => c.Name == lastCamName);
                if (targetCam != null)
                {
                    CmbCameras.SelectedItem = targetCam;
                    StartCamera();
                }
            }
            _isInitializingCamera = false;

            // 2. 异步加载模型
            await InitializeModelAsync();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // 1. 弹出退出确认框
            var result = MessageBox.Show(
                "确认要完全退出 AI Board 吗？\n\n提示：若只需挂起，请【左键】点击最小化转为桌面小黑板。",
                "退出确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            // 如果用户点击了“否”，取消关闭流程
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            // --- 下面是你原有的清理逻辑，保持不变 ---
            StopCamera();
            foreach (var counter in _gpuCounters.Values)
            {
                counter.Dispose();
            }
            _gpuCounters.Clear();

            // 如果悬浮窗开着，确保一起关掉，否则进程不会退出
            if (_miniWidget != null)
            {
                _miniWidget.Close();
                _miniWidget = null;
            }
        }

        // 支持无边框窗口拖动
        // 1. 修改鼠标按下事件
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                // 如果开启了强制高度模式，使用自定义拖拽（只改 X 轴）
                if (ConfigManager.Config.MaximizeWindowHeight)
                {
                    _isDragging = true;
                    _dragStartPoint = e.GetPosition(this); // 记录点击时相对于窗口的位置
                    this.CaptureMouse(); // 捕获鼠标，防止移出窗口后失效
                }
                else
                {
                    // 普通模式：使用系统原生的全向拖拽
                    this.DragMove();
                }
            }
        }
        // 2. 新增鼠标移动事件（实现横向锁定）
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && ConfigManager.Config.MaximizeWindowHeight)
            {
                // 计算鼠标移动的偏差量
                var currentPoint = e.GetPosition(this);
                double deltaX = currentPoint.X - _dragStartPoint.X;

                // 只有当偏差明显时才移动，减少微小抖动
                if (Math.Abs(deltaX) > 0)
                {
                    this.Left += deltaX;
                }
            }
        }

        // 3. 新增鼠标抬起事件（释放捕获）
        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                this.ReleaseMouseCapture();
            }
        }
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
        // ================== 窗口大小调整逻辑 ==================
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 如果开启了“最大化锁定高度”，则不由这里控制
            if (ConfigManager.Config.MaximizeWindowHeight) return;

            // 如果没有设置强制比例，或者是最小化状态，直接返回
            if (double.IsNaN(_cachedTargetRatio) || this.WindowState == WindowState.Minimized) return;

            // 防止递归调用 (因为我们在下面修改了 Width/Height，会再次触发 SizeChanged)
            if (_isResizing) return;

            _isResizing = true;

            try
            {
                // 侧边栏宽度 (根据 XAML 定义，这里假设是 60，如果是 80 请改为 80)
                const double SidebarWidth = 60;

                // 计算视频区域的实际高度
                double videoHeight = e.NewSize.Height;

                // 根据比例计算 理论上的 视频区域宽度
                double expectedVideoWidth = videoHeight * _cachedTargetRatio;

                // 理论上的 窗口总宽度 = 视频宽 + 侧边栏宽
                double expectedTotalWidth = expectedVideoWidth + SidebarWidth;

                // 如果当前宽度与理论宽度差异较大 (防止浮点数微小抖动)
                if (Math.Abs(e.NewSize.Width - expectedTotalWidth) > 2)
                {
                    // 强制调整宽度以匹配高度
                    this.Width = expectedTotalWidth;
                }
            }
            finally
            {
                _isResizing = false;
            }
        }
        // ================== 摄像头控制与 UI ==================
        private void BtnCam_Click(object sender, RoutedEventArgs e)
        {
            PopupCamera.IsOpen = !PopupCamera.IsOpen; // 弹出气泡
        }

        private void BtnRefreshCam_Click(object sender, RoutedEventArgs e)
        {
            RefreshCameraList();
        }

        private void RefreshCameraList()
        {
            var tempSelection = CmbCameras.SelectedIndex;
            CmbCameras.Items.Clear();
            try
            {
                var cameras = DirectShowCameraEnumerator.GetCameras();
                for (int i = 0; i < cameras.Count; i++)
                {
                    CmbCameras.Items.Add(new CameraInfo { Index = i, Name = cameras[i] });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取摄像头列表失败: " + ex.Message);
            }

            if (CmbCameras.Items.Count > 0 && tempSelection < CmbCameras.Items.Count)
                CmbCameras.SelectedIndex = tempSelection >= 0 ? tempSelection : 0;
            else if (CmbCameras.Items.Count == 0)
                CmbCameras.Items.Add(new CameraInfo { Index = 0, Name = "默认摄像头 (未检测到)" });
        }

        private void CmbCameras_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingCamera) return; // 防止初始化时重复触发

            if (CmbCameras.SelectedItem != null)
            {
                if (_isCameraRunning) StopCamera();
                StartCamera();
            }
        }

        private async void StartCamera()
        {
            if (_isCameraRunning) return;

            int camIndex = (CmbCameras.SelectedItem as CameraInfo)?.Index ?? 0;
            string camName = (CmbCameras.SelectedItem as CameraInfo)?.Name ?? "默认摄像头";

            UpdateStatusUI("启动中", "#D28E00"); // 橙色

            bool openSuccess = await Task.Run(() =>
            {
                try
                {
                    var capture = new VideoCapture(camIndex, VideoCaptureAPIs.DSHOW);
                    capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                    capture.Set(VideoCaptureProperties.FrameHeight, 720);

                    if (capture.IsOpened())
                    {
                        _videoCapture = capture;
                        return true;
                    }
                    else
                    {
                        capture.Dispose();
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"摄像头启动异常: {ex.Message}");
                    return false;
                }
            });

            if (!openSuccess)
            {
                UpdateStatusUI("打开失败", "#B33A3A"); // 红色
                ShowToast($"无法打开选定的摄像头！可能是被其他软件占用。", ToastType.Error);
                return;
            }

            if (ConfigManager.Config.LastCameraName != camName)
            {
                ConfigManager.Config.LastCameraName = camName;
                ConfigManager.Save();
            }

            _isCameraRunning = true;
            UpdateStatusUI("正常运行", "#333333"); // 深灰

            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(() => CameraLoop(_cancellationTokenSource.Token));
        }

        private void StopCamera()
        {
            _isCameraRunning = false;
            _cancellationTokenSource?.Cancel();

            if (_videoCapture != null)
            {
                try
                {
                    if (!_videoCapture.IsDisposed)
                    {
                        if (_videoCapture.IsOpened()) _videoCapture.Release();
                        _videoCapture.Dispose();
                    }
                }
                catch { }
                finally { _videoCapture = null; }
            }

            UpdateStatusUI("已停止", "#555555"); // 灰色
            TxtFpsVal.Text = "0.0";
            ImgMain.Source = null;
        }

        // ================== 设置与硬件监控 ==================
        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // 使用独立的现代化设置窗口
            SettingsWindow sw = new SettingsWindow();
            sw.Owner = this;
            sw.ShowDialog();
            // 每次关闭设置界面后刷新布局设置
            ApplyWindowSettings();
            // 如果修改了导致需要重启引擎的参数
            if (sw.NeedsRestartModel)
            {
                await InitializeModelAsync();
            }
        }

        private void InitHardwareMonitors()
        {
            _currentPid = Process.GetCurrentProcess().Id;
            string processName = Process.GetCurrentProcess().ProcessName;

            try { _cpuCounter = new PerformanceCounter("Process", "% Processor Time", processName); } catch { }

            _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _monitorTimer.Tick += (s, e) =>
            {
                double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
                TxtFpsVal.Text = Math.Round(fps, 1).ToString();
                _frameCount = 0;
                _fpsStopwatch.Restart();

                try
                {
                    if (_cpuCounter != null)
                    {
                        float cpuUsage = _cpuCounter.NextValue() / Environment.ProcessorCount;
                        TxtCpuVal.Text = Math.Round(cpuUsage, 1) + "%";
                        TxtCpuVal.Foreground = GetUsageColor(cpuUsage); // 应用红黄绿颜色
                    }
                }
                catch { }

                UpdateGpuUsage();
            };
            _monitorTimer.Start();
            _fpsStopwatch.Start();
        }
        private SolidColorBrush GetUsageColor(float usage)
        {
            if (usage < 50) return new SolidColorBrush(Color.FromRgb(144, 238, 144)); // 绿色 (LightGreen)
            if (usage < 80) return new SolidColorBrush(Color.FromRgb(255, 215, 0));   // 黄色 (Gold)
            return new SolidColorBrush(Color.FromRgb(179, 58, 58));                  // 红色 (自定红)
        }
        private void UpdateGpuUsage()
        {
            try
            {
                float totalGpuUsage = 0f;
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();
                string pidPrefix = $"pid_{_currentPid}_";

                var currentGpuInstances = instanceNames
                    .Where(x => x.StartsWith(pidPrefix) && (x.Contains("engtype_3D") || x.Contains("engtype_Compute")))
                    .ToList();

                foreach (var instance in currentGpuInstances)
                {
                    if (!_gpuCounters.ContainsKey(instance))
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        _gpuCounters.Add(instance, counter);
                    }
                }

                var keysToRemove = _gpuCounters.Keys.Except(currentGpuInstances).ToList();
                foreach (var key in keysToRemove)
                {
                    _gpuCounters[key].Dispose();
                    _gpuCounters.Remove(key);
                }

                foreach (var counter in _gpuCounters.Values)
                {
                    totalGpuUsage += counter.NextValue();
                }

                TxtGpuVal.Text = Math.Round(totalGpuUsage, 1) + "%";
                TxtGpuVal.Foreground = GetUsageColor(totalGpuUsage); // 应用红黄绿颜色
            }
            catch
            {
                TxtGpuVal.Text = "--%";
                TxtGpuVal.Foreground = new SolidColorBrush(Colors.Gray);
            }
        }

        private async Task InitializeModelAsync()
        {
            UpdateStatusUI("加载引擎", "#D28E00");
            BtnSettings.IsEnabled = false;

            await Task.Run(() =>
            {
                try
                {
                    string localDmlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DirectML.dll");
                    LoadLibrary(localDmlPath);

                    SessionOptions options = new SessionOptions();
                    options.InterOpNumThreads = ConfigManager.Config.MaxCpuThreads;
                    options.IntraOpNumThreads = ConfigManager.Config.MaxCpuThreads;

                    if (ConfigManager.Config.UseGPU) options.AppendExecutionProvider_DML(0);

                    var newSession = new InferenceSession(_modelPath, options);

                    lock (_processLock)
                    {
                        _session?.Dispose();
                        _session = newSession;
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"模型加载失败: {ex.Message}"));
                }
            });

            BtnSettings.IsEnabled = true;
            UpdateStatusUI(_isCameraRunning ? "正常运行" : "已停止", _isCameraRunning ? "#333333" : "#555555");
        }
        // ================== 新增：布局与拉伸设置 ==================
        private void ApplyWindowSettings()
        {
            // 1. 应用窗口置顶
            this.Topmost = ConfigManager.Config.WindowTopmost;

            // 2. 解析目标宽高比 (用于后续逻辑)
            double targetRatio = double.NaN;
            if (!string.IsNullOrWhiteSpace(ConfigManager.Config.ForceAspectRatio))
            {
                var parts = ConfigManager.Config.ForceAspectRatio.Replace("：", ":").Split(':');
                if (parts.Length == 2 && double.TryParse(parts[0], out double rw) && double.TryParse(parts[1], out double rh) && rh > 0)
                {
                    targetRatio = rw / rh;
                }
            }
            _cachedTargetRatio = targetRatio; // 将比例存入类成员变量

            // 3. 处理窗口模式：最大化锁定 vs 自由调整
            if (ConfigManager.Config.MaximizeWindowHeight)
            {
                // === 模式 A: 锁定高度，仅横向移动 ===
                this.ResizeMode = ResizeMode.NoResize; // 禁止边缘拖动
                this.Height = SystemParameters.WorkArea.Height;
                this.Top = SystemParameters.WorkArea.Top;

                // 强制应用比例（如果设置了）
                if (!double.IsNaN(targetRatio))
                {
                    ImgMain.Stretch = Stretch.Fill;
                    this.Width = this.Height * targetRatio + 60; // 60是侧边栏宽度
                }
                else
                {
                    ImgMain.Stretch = Stretch.Uniform;
                }
            }
            else
            {
                // === 模式 B: 自由窗口，允许边缘调整大小 ===
                this.ResizeMode = ResizeMode.CanResize; // 允许边缘拖动

                // 如果设置了比例，且当前窗口不符合比例，则强制调整一次
                if (!double.IsNaN(targetRatio))
                {
                    ImgMain.Stretch = Stretch.Fill;
                    // 以当前高度为基准调整宽度
                    double correctWidth = this.Height * targetRatio + 60;
                    if (Math.Abs(this.Width - correctWidth) > 2)
                    {
                        this.Width = correctWidth;
                    }
                }
                else
                {
                    ImgMain.Stretch = Stretch.Uniform;
                }
            }
        }



        // 窗口最小化/恢复时休眠/唤醒相机
        private MiniWidgetWindow _miniWidget; // 引用悬浮窗实例

        // 修改原有的 Window_StateChanged 方法
        private void Window_StateChanged(object sender, EventArgs e)
        {
            // 1. 最小化逻辑
            if (this.WindowState == WindowState.Minimized)
            {
                // 记录之前的运行状态
                _wasRunningBeforeMinimize = _isCameraRunning;
                if (_isCameraRunning) StopCamera();

                // 如果开启了悬浮窗功能
                if (ConfigManager.Config.EnableMiniWidget)
                {
                    // 隐藏任务栏图标
                    //this.ShowInTaskbar = false;

                    // 显示悬浮窗
                    if (_miniWidget == null)
                    {
                        _miniWidget = new MiniWidgetWindow(this);
                        _miniWidget.Show();
                    }
                }
            }
            // 2. 恢复正常/最大化逻辑
            else if (this.WindowState == WindowState.Normal || this.WindowState == WindowState.Maximized)
            {
                // 确保悬浮窗关闭
                if (_miniWidget != null)
                {
                    _miniWidget.Close();
                    _miniWidget = null;
                }

                // 恢复任务栏图标
                //this.ShowInTaskbar = true;

                // 恢复摄像头
                if (_wasRunningBeforeMinimize && !_isCameraRunning)
                {
                    StartCamera();
                }
            }
        }
        // 捕获最小化按钮的右键点击
        private void BtnMinimize_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // 阻止事件继续向上传递
            this.Close();     // 调用窗体关闭，这会自动触发下面的 Window_Closing 事件
        }
        // [新增] 公开给 MiniWidget 调用的恢复方法
        public void RestoreFromMiniWidget()
        {
            this.Show(); // 确保窗口可见
            this.WindowState = WindowState.Normal; // 恢复窗口状态
            this.Activate(); // 激活窗口焦点
        }
        // ================== AI 处理与主循环 ==================
        private async Task CameraLoop(CancellationToken token)
        {
            int gcCounter = 0;
            Mat currentGray = new Mat();

            while (!token.IsCancellationRequested && _isCameraRunning)
            {
                int targetFps = ConfigManager.Config.MaxFPS <= 0 ? 30 : ConfigManager.Config.MaxFPS;
                int minFrameTimeMs = 1000 / targetFps;
                Stopwatch frameSw = Stopwatch.StartNew();

                using Mat frame = new Mat();
                if (_videoCapture.Read(frame) && !frame.Empty())
                {
                    EnhanceImage(frame, frame);
                    bool lockTaken = false;
                    try
                    {
                        Monitor.TryEnter(_processLock, ref lockTaken);
                        if (lockTaken)
                        {
                            bool shouldProcess = true;
                            double motionScore = 0;

                            Cv2.CvtColor(frame, currentGray, ColorConversionCodes.BGR2GRAY);
                            Cv2.GaussianBlur(currentGray, currentGray, new OpenCvSharp.Size(5, 5), 0);

                            if (_lastFrameGray != null)
                            {
                                motionScore = CalculateMotion(_lastFrameGray, currentGray);
                                // === 2. 更新静止计时器 (新增逻辑) ===
                                double elapsedSec = frameSw.Elapsed.TotalSeconds; // 或者用 1.0/FPS 估算
                                if (motionScore < ConfigManager.Config.MotionThreshold)
                                {
                                    // 如果这一帧是静止的，累加时间 (这里简单用上一帧耗时累加，或者固定步长)
                                    _continuousStaticSeconds += (minFrameTimeMs / 1000.0);
                                }
                                else
                                {
                                    // 画面动了，重置静止计时
                                    _continuousStaticSeconds = 0;
                                }
                                TimeSpan timeSinceLast = DateTime.Now - _lastProcessTime;
                                if (motionScore < ConfigManager.Config.MotionThreshold &&
                                    timeSinceLast.TotalSeconds < ConfigManager.Config.ForceUpdateIntervalSec)
                                {
                                    shouldProcess = false;
                                }
                            }

                            if (shouldProcess)
                            {
                                if (_lastFrameGray == null) _lastFrameGray = new Mat();
                                currentGray.CopyTo(_lastFrameGray);
                                _lastProcessTime = DateTime.Now;

                                ProcessFrame(frame, out Mat debugDisplay, out Mat rectifiedImage);

                                // =========================================================
                                // === 4. 检查云服务触发 (移动到此处，确保能拿到 rectifiedImage) ===
                                // =========================================================

                                // 确定要上传哪张图：如果有矫正成功的图，就传矫正图；否则传原图(frame)
                                // 注意：这里只是引用，千万不要 Dispose，Clone 后再传给异步任务
                                Mat sourceForUpload = (rectifiedImage != null && !rectifiedImage.Empty()) ? rectifiedImage : frame;

                                // A. 检查定时上传
                                string nowMinute = DateTime.Now.ToString("HH:mm");
                                if (_lastScheduledUploadMinute != nowMinute)
                                {
                                    var schedules = ConfigManager.Config.CloudScheduleTimes.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (schedules.Contains(nowMinute))
                                    {
                                        _lastScheduledUploadMinute = nowMinute;
                                        // 克隆成品图进行上传
                                        using (Mat uploadMat = sourceForUpload.Clone())
                                        {
                                            _ = TriggerCloudUpload(uploadMat, "Scheduled");
                                        }
                                    }
                                }

                                // B. 检查静止上传 (带画面对比去重)
                                int staticThreshold = ConfigManager.Config.CloudStaticThresholdSec;
                                if (staticThreshold > 0 && _continuousStaticSeconds >= staticThreshold)
                                {
                                    if ((DateTime.Now - _lastCloudUploadTime).TotalMinutes >= ConfigManager.Config.CloudUploadCooldownMin)
                                    {
                                        bool contentChanged = false;

                                        // 依然使用【原始灰度图】来判断画面是否变化，这比判断成品图更稳定
                                        // 因为成品图的裁剪边缘可能会微小跳动，导致误判为“内容变了”
                                        if (_lastUploadedStaticFrame == null)
                                        {
                                            contentChanged = true;
                                        }
                                        else
                                        {
                                            double diffScore = CalculateMotion(_lastUploadedStaticFrame, currentGray);
                                            if (diffScore > ConfigManager.Config.MotionThreshold)
                                            {
                                                contentChanged = true;
                                                Debug.WriteLine($"静止上传触发：差异 {diffScore:F2}");
                                            }
                                        }

                                        if (contentChanged)
                                        {
                                            // 更新对比基准 (存原图的灰度用于下次对比)
                                            if (_lastUploadedStaticFrame != null) _lastUploadedStaticFrame.Dispose();
                                            _lastUploadedStaticFrame = currentGray.Clone();

                                            // 上传成品图
                                            using (Mat uploadMat = sourceForUpload.Clone())
                                            {
                                                _ = TriggerCloudUpload(uploadMat, "Static");
                                            }
                                        }
                                    }
                                }
                                //---5 UI
                                var bmpDisplay = debugDisplay?.ToWriteableBitmap();
                                bmpDisplay?.Freeze();
                                var bmpRectified = rectifiedImage?.ToWriteableBitmap();
                                bmpRectified?.Freeze();

                                bool isTargetFound = (rectifiedImage != null);

                                if (isTargetFound)
                                {
                                    UpdateUiImages(bmpDisplay, bmpRectified, "正常运行", "#333333");
                                    AddToHistory(bmpDisplay, bmpRectified);
                                }
                                else
                                {
                                    var restored = GetRestoredFrame(ConfigManager.Config.TargetLostRestoreMs);
                                    if (restored != null)
                                    {
                                        UpdateUiImages(restored.Value.DisplayImage, restored.Value.RectifiedImage, "目标丢失", "#B33A3A");
                                    }
                                    else
                                    {
                                        UpdateUiImages(bmpDisplay, null, "目标丢失", "#B33A3A");
                                    }
                                }

                                debugDisplay?.Dispose();
                                rectifiedImage?.Dispose();
                            }
                            else
                            {
                                // 节能跳过
                                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    UpdateStatusUI("静止节能", "#4A7545");
                                }));
                            }

                            _frameCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Loop Exception: " + ex.Message);
                    }
                    finally
                    {
                        if (lockTaken) Monitor.Exit(_processLock);
                    }
                }

                gcCounter++;
                if (gcCounter % 60 == 0)
                {
                    PruneHistory();
                    GC.Collect();
                }

                frameSw.Stop();
                int elapsed = (int)frameSw.ElapsedMilliseconds;
                if (elapsed < minFrameTimeMs) await Task.Delay(minFrameTimeMs - elapsed);
                else await Task.Delay(1);
            }

            currentGray?.Dispose();
            _lastFrameGray?.Dispose();
            _lastFrameGray = null;
            lock (_historyLock) { _historyBuffer.Clear(); }
        }

        // ================== UI 更新辅助方法 ==================
        private void UpdateStatusUI(string text, string hexColor)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtStatusVertical.Text = text;
                StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
            }));
        }

        private void UpdateUiImages(ImageSource display, ImageSource rectified, string statusText, string hexColor)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 核心：单屏幕显示切换逻辑
                if (ConfigManager.Config.ShowDebugStream)
                {
                    ImgMain.Source = display; // 显示调试原图
                }
                else
                {
                    // 显示成品图。如果识别不到或丢失且无历史，则退回显示原图以防黑屏
                    ImgMain.Source = rectified ?? display;
                }

                // 更新侧边栏胶囊状态
                TxtStatusVertical.Text = statusText;
                StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
            }));
        }
        // ================== Toast 通用通知逻辑 ==================

        public void ShowToast(string message, ToastType type = ToastType.Info)
        {
            // 确保在 UI 线程执行
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 1. 设置内容
                ToastText.Text = message;

                // 2. 根据类型设置颜色和图标
                switch (type)
                {
                    case ToastType.Success:
                        ToastContainer.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A7545")); // 绿色
                        ToastIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.CheckCircle;
                        break;
                    case ToastType.Error:
                        ToastContainer.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B33A3A")); // 红色
                        ToastIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.AlertCircle;
                        break;
                    default: // Info
                        ToastContainer.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333")); // 深灰
                        ToastIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Information;
                        break;
                }

                // 3. 动画逻辑 (淡入 -> 停留 -> 淡出)

                // 停止之前的动画（如果有）
                ToastContainer.BeginAnimation(OpacityProperty, null);

                // 创建 Storyboard
                var storyboard = new Storyboard();

                // 淡入 (0s -> 0.3s)
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
                Storyboard.SetTarget(fadeIn, ToastContainer);
                Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));

                // 淡出 (2.3s -> 2.8s)
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
                fadeOut.BeginTime = TimeSpan.FromMilliseconds(2300); // 停留 2 秒
                Storyboard.SetTarget(fadeOut, ToastContainer);
                Storyboard.SetTargetProperty(fadeOut, new PropertyPath("Opacity"));

                storyboard.Children.Add(fadeIn);
                storyboard.Children.Add(fadeOut);

                storyboard.Begin();
            });
        }
        // ================== 分享与导出功能 ==================

            // 1. 点击分享按钮：显示弹窗
        private void BtnShare_Click(object sender, RoutedEventArgs e)
        {
            // 切换打开/关闭状态
            PopupShare.IsOpen = !PopupShare.IsOpen;
        }

        // 2. 弹窗打开时：自动刷新 U 盘列表
        private void PopupShare_Opened(object sender, EventArgs e)
        {
            RefreshUsbList();
        }

        // 3. 刷新 U 盘列表逻辑
        private void BtnRefreshUsb_Click(object sender, RoutedEventArgs e)
        {
            RefreshUsbList();
        }

        private void RefreshUsbList()
        {
            CmbUsbDrives.Items.Clear();
            try
            {
                // 获取所有可移动磁盘 (Removable) 且已准备就绪 (IsReady)
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                    .ToList();

                if (drives.Count == 0)
                {
                    CmbUsbDrives.Items.Add(new UsbDriveItem { RootPath = "", DisplayName = "未检测到 U 盘" });
                    CmbUsbDrives.SelectedIndex = 0;
                    CmbUsbDrives.IsEnabled = false;
                }
                else
                {
                    foreach (var d in drives)
                    {
                        string label = string.IsNullOrEmpty(d.VolumeLabel) ? "无卷标" : d.VolumeLabel;
                        // 格式示例: E:\ (U_DISK)
                        CmbUsbDrives.Items.Add(new UsbDriveItem
                        {
                            RootPath = d.RootDirectory.FullName,
                            DisplayName = $"{d.Name} ({label})"
                        });
                    }
                    CmbUsbDrives.SelectedIndex = 0;
                    CmbUsbDrives.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取驱动器失败: {ex.Message}");
            }
        }

        // 4. 获取当前显示的图片 (从 UI 的 ImgMain 获取)
        private Mat GetCurrentDisplayedMat()
        {
            if (ImgMain.Source is BitmapSource bmpSource)
            {
                // 使用 OpenCvSharp.WpfExtensions 将 BitmapSource 转回 Mat
                return bmpSource.ToMat();
            }
            return null;
        }

        // 5. 执行：保存到 U 盘
        private void BtnSaveToUsb_Click(object sender, RoutedEventArgs e)
        {
            if (CmbUsbDrives.SelectedItem is UsbDriveItem selectedDrive && !string.IsNullOrEmpty(selectedDrive.RootPath))
            {
                // 检查是否选择了无效项
                if (!CmbUsbDrives.IsEnabled) return;

                try
                {
                    using (Mat mat = GetCurrentDisplayedMat())
                    {
                        if (mat == null || mat.Empty())
                        {
                            ShowToast($"当前没有画面可保存！", ToastType.Error);
                            return;
                        }

                        // 生成文件名: 20260226_163005.jpg
                        string fileName = DateTime.Now.ToString("AI小黑板 yyyyMMdd_HHmmss") + ".jpg";
                        string fullPath = System.IO.Path.Combine(selectedDrive.RootPath, fileName);

                        // 写入文件
                        bool success = Cv2.ImWrite(fullPath, mat);

                        if (success)
                        {
                            // 使用 Toast 替代 MessageBox
                            ShowToast($"已保存至 U 盘: {fileName}", ToastType.Success);
                            UpdateStatusUI("保存成功", "#4A7545");
                        }
                        else
                        {
                            ShowToast("保存失败，请检查 U 盘权限", ToastType.Error);
                            UpdateStatusUI("保存失败", "#B33A3A");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowToast($"写入异常: {ex.Message}", ToastType.Error);
                }
            }
            else
            {
                ShowToast("请先选择一个有效的 U 盘", ToastType.Error);
            }
        }

        // 6. 执行：手动上传到云服务
        private async void BtnManualUpload_Click(object sender, RoutedEventArgs e)
        {
            PopupShare.IsOpen = false; // 关闭弹窗

            using (Mat mat = GetCurrentDisplayedMat())
            {
                if (mat == null || mat.Empty())
                {
                    ShowToast($"当前没有画面可上传！", ToastType.Error);
                    return;
                }

                // 克隆一份用于异步上传，防止 dispose 问题
                using (Mat uploadMat = mat.Clone())
                {
                    UpdateStatusUI("正在上传", "#D28E00"); // 橙色
                    await TriggerCloudUpload(uploadMat, "Manual");
                }
            }
        }
        // ================== 历史回滚与图像处理逻辑 ==================
        private double CalculateMotion(Mat prev, Mat curr)
        {
            using (Mat diff = new Mat())
            {
                Cv2.Absdiff(prev, curr, diff);
                Scalar mean = Cv2.Mean(diff);
                return mean.Val0;
            }
        }

        private void AddToHistory(ImageSource display, ImageSource rectified)
        {
            lock (_historyLock)
            {
                _historyBuffer.AddLast(new HistoryFrame
                {
                    Timestamp = DateTime.Now,
                    DisplayImage = display,
                    RectifiedImage = rectified
                });
                if (_historyBuffer.Count > ConfigManager.Config.MaxFPS * 5)
                {
                    _historyBuffer.RemoveFirst();
                }
            }
        }

        private void PruneHistory()
        {
            lock (_historyLock)
            {
                DateTime threshold = DateTime.Now.AddMilliseconds(-(ConfigManager.Config.TargetLostRestoreMs + 2000));
                while (_historyBuffer.Count > 0 && _historyBuffer.First.Value.Timestamp < threshold)
                {
                    _historyBuffer.RemoveFirst();
                }
            }
        }

        private HistoryFrame? GetRestoredFrame(int msAgo)
        {
            lock (_historyLock)
            {
                if (_historyBuffer.Count == 0) return null;
                DateTime targetTime = DateTime.Now.AddMilliseconds(-msAgo);
                var node = _historyBuffer.Last;
                HistoryFrame? bestMatch = null;
                double minDiff = double.MaxValue;

                while (node != null)
                {
                    double diff = Math.Abs((node.Value.Timestamp - targetTime).TotalMilliseconds);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        bestMatch = node.Value;
                    }
                    if (node.Value.Timestamp < targetTime && diff > minDiff) break;
                    node = node.Previous;
                }
                return bestMatch;
            }
        }

        private void EnhanceImage(Mat src, Mat dst)
        {
            if (ConfigManager.Config.FlipHorizontal) Cv2.Flip(src, dst, FlipMode.Y);
            else if (src.CvPtr != dst.CvPtr) src.CopyTo(dst);

            double alpha = ConfigManager.Config.Contrast / 100.0;
            double beta = ConfigManager.Config.Brightness;
            if (Math.Abs(alpha - 1.0) > 0.01 || Math.Abs(beta) > 0.01) dst.ConvertTo(dst, -1, alpha, beta);

            int sharpLevel = ConfigManager.Config.Sharpness;
            if (sharpLevel > 0)
            {
                using (Mat blurred = new Mat())
                {
                    Cv2.GaussianBlur(dst, blurred, new OpenCvSharp.Size(0, 0), 3);
                    double amount = sharpLevel * 0.2;
                    Cv2.AddWeighted(dst, 1.0 + amount, blurred, -amount, 0, dst);
                }
            }
        }

        private void ProcessFrame(Mat originalImage, out Mat outDebugDisplay, out Mat outRectified)
        {
            outDebugDisplay = null;
            outRectified = null;

            if (originalImage.Empty() || _session == null)
            {
                if (!originalImage.Empty()) outDebugDisplay = originalImage.Clone();
                return;
            }

            int origW = originalImage.Width;
            int origH = originalImage.Height;

            float ratio = Math.Min((float)ImgSize / origW, (float)ImgSize / origH);
            int newW = (int)(origW * ratio);
            int newH = (int)(origH * ratio);

            using Mat canvas = new Mat(ImgSize, ImgSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
            using Mat resizedImg = new Mat();
            Cv2.Resize(originalImage, resizedImg, new OpenCvSharp.Size(newW, newH));
            using Mat roiOnCanvas = new Mat(canvas, new OpenCvSharp.Rect(0, 0, newW, newH));
            resizedImg.CopyTo(roiOnCanvas);

            using Mat blob = CvDnn.BlobFromImage(canvas, 1.0 / 255.0, new OpenCvSharp.Size(ImgSize, ImgSize), new Scalar(0, 0, 0), true, false);
            float[] tensorData = new float[blob.Total()];
            Marshal.Copy(blob.Data, tensorData, 0, tensorData.Length);
            var inputTensor = new DenseTensor<float>(tensorData, new[] { 1, 3, ImgSize, ImgSize });

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", inputTensor) };
            using var results = _session.Run(inputs);
            var output0 = results.First().AsTensor<float>();
            var output1 = results.Last().AsTensor<float>();

            int numProposals = output0.Dimensions[2];
            List<OpenCvSharp.Rect2d> boxes = new List<OpenCvSharp.Rect2d>();
            List<float> scores = new List<float>();
            List<float[]> maskWeightsList = new List<float[]>();

            for (int i = 0; i < numProposals; i++)
            {
                float maxScore = 0;
                for (int c = 0; c < NumClasses; c++)
                {
                    float score = output0[0, 4 + c, i];
                    if (score > maxScore) maxScore = score;
                }

                if (maxScore > ConfThreshold)
                {
                    float cx = output0[0, 0, i]; float cy = output0[0, 1, i];
                    float w = output0[0, 2, i]; float h = output0[0, 3, i];
                    boxes.Add(new OpenCvSharp.Rect2d(cx - w / 2, cy - h / 2, w, h));
                    scores.Add(maxScore);

                    float[] weights = new float[32];
                    for (int k = 0; k < 32; k++) weights[k] = output0[0, 4 + NumClasses + k, i];
                    maskWeightsList.Add(weights);
                }
            }

            CvDnn.NMSBoxes(boxes, scores, ConfThreshold, NmsThreshold, out int[] indices);

            Mat debugDisplay = originalImage.Clone();
            Mat bestMask = null;
            float maxArea = 0;

            float[] protoData = output1.ToArray();
            using Mat protoMat = new Mat(32, MaskSize * MaskSize, MatType.CV_32FC1);
            Marshal.Copy(protoData, 0, protoMat.Data, protoData.Length);

            foreach (int idx in indices)
            {
                var box = boxes[idx];
                var weights = maskWeightsList[idx];

                using Mat wMat = new Mat(1, 32, MatType.CV_32FC1);
                Marshal.Copy(weights, 0, wMat.Data, weights.Length);
                using Mat maskRes = wMat * protoMat;
                using Mat mask160 = maskRes.Reshape(1, MaskSize);
                using Mat maskProb = new Mat();
                Cv2.Exp(-mask160, maskProb);
                using Mat ones = Mat.Ones(maskProb.Rows, maskProb.Cols, MatType.CV_32FC1);
                using Mat tempAdd = new Mat();
                Cv2.Add(ones, maskProb, tempAdd);
                Cv2.Divide(ones, tempAdd, maskProb);

                using Mat mask640 = new Mat();
                Cv2.Resize(maskProb, mask640, new OpenCvSharp.Size(ImgSize, ImgSize));
                OpenCvSharp.Rect validRoi = new OpenCvSharp.Rect(0, 0, newW, newH);
                using Mat maskValid = new Mat(mask640, validRoi);
                using Mat maskOriginal = new Mat();
                Cv2.Resize(maskValid, maskOriginal, new OpenCvSharp.Size(origW, origH));

                using Mat binaryMask = new Mat();
                Cv2.Threshold(maskOriginal, binaryMask, 0.5, 255, ThresholdTypes.Binary);
                binaryMask.ConvertTo(binaryMask, MatType.CV_8UC1);

                using Mat colorLayer = new Mat(origH, origW, MatType.CV_8UC3, new Scalar(0, 50, 0));
                Cv2.AddWeighted(debugDisplay, 1.0, colorLayer, 0.3, 0, debugDisplay, -1);

                if (box.Width * box.Height > maxArea)
                {
                    maxArea = (float)(box.Width * box.Height);
                    if (bestMask != null) bestMask.Dispose();
                    bestMask = binaryMask.Clone();
                }
            }

            if (bestMask != null)
            {
                outRectified = RectifyDocument(originalImage, bestMask, debugDisplay);
                bestMask.Dispose();
            }

            outDebugDisplay = debugDisplay;
        }

        private Mat RectifyDocument(Mat original, Mat mask, Mat debugCanvas)
        {
            Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            if (contours.Length == 0) return null;

            var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
            if (contour.Length < 4) return null;

            Moments m = Cv2.Moments(contour);
            int cx = (int)(m.M10 / m.M00);
            int cy = (int)(m.M01 / m.M00);

            Cv2.Circle(debugCanvas, cx, cy, 5, Scalar.Magenta, -1);

            List<OpenCvSharp.Point> topPts = new List<OpenCvSharp.Point>();
            List<OpenCvSharp.Point> btmPts = new List<OpenCvSharp.Point>();
            List<OpenCvSharp.Point> leftPts = new List<OpenCvSharp.Point>();
            List<OpenCvSharp.Point> rightPts = new List<OpenCvSharp.Point>();

            int w = original.Width;
            int h = original.Height;

            foreach (var p in contour)
            {
                double angle = Math.Atan2(p.Y - cy, p.X - cx) * 180.0 / Math.PI;

                if (angle >= -45 && angle < 45) rightPts.Add(p);
                else if (angle >= 45 && angle < 135) btmPts.Add(p);
                else if (angle >= 135 || angle < -135) leftPts.Add(p);
                else topPts.Add(p);
            }

            List<OpenCvSharp.Point> cleanTop = TrimEnds(topPts, true, 0.25f);
            List<OpenCvSharp.Point> cleanBtm = TrimEnds(btmPts, true, 0.25f);
            List<OpenCvSharp.Point> cleanLeft = TrimEnds(leftPts, false, 0.25f);
            List<OpenCvSharp.Point> cleanRight = TrimEnds(rightPts, false, 0.25f);

            DrawDebugPoints(debugCanvas, cleanTop, ColorTop);
            DrawDebugPoints(debugCanvas, cleanBtm, ColorBtm);
            DrawDebugPoints(debugCanvas, cleanLeft, ColorLeft);
            DrawDebugPoints(debugCanvas, cleanRight, ColorRight);

            ParametricLine lineTop = FitLineRobust(cleanTop);
            ParametricLine lineBtm = FitLineRobust(cleanBtm);
            ParametricLine lineLeft = FitLineRobust(cleanLeft);
            ParametricLine lineRight = FitLineRobust(cleanRight);

            DrawDebugLine(debugCanvas, lineTop, w, h);
            DrawDebugLine(debugCanvas, lineBtm, w, h);
            DrawDebugLine(debugCanvas, lineLeft, w, h);
            DrawDebugLine(debugCanvas, lineRight, w, h);

            Point2f tl = ComputeIntersection(lineTop, lineLeft);
            Point2f tr = ComputeIntersection(lineTop, lineRight);
            Point2f br = ComputeIntersection(lineBtm, lineRight);
            Point2f bl = ComputeIntersection(lineBtm, lineLeft);

            Cv2.Circle(debugCanvas, (int)tl.X, (int)tl.Y, 8, ColorCorner, 2);
            Cv2.Circle(debugCanvas, (int)tr.X, (int)tr.Y, 8, ColorCorner, 2);
            Cv2.Circle(debugCanvas, (int)br.X, (int)br.Y, 8, ColorCorner, 2);
            Cv2.Circle(debugCanvas, (int)bl.X, (int)bl.Y, 8, ColorCorner, 2);

            float widthA = (float)Math.Sqrt(Math.Pow(br.X - bl.X, 2) + Math.Pow(br.Y - bl.Y, 2));
            float widthB = (float)Math.Sqrt(Math.Pow(tr.X - tl.X, 2) + Math.Pow(tr.Y - tl.Y, 2));
            int maxWidth = Math.Max((int)widthA, (int)widthB);

            float heightA = (float)Math.Sqrt(Math.Pow(tr.X - br.X, 2) + Math.Pow(tr.Y - br.Y, 2));
            float heightB = (float)Math.Sqrt(Math.Pow(tl.X - bl.X, 2) + Math.Pow(tl.Y - bl.Y, 2));
            int maxHeight = Math.Max((int)heightA, (int)heightB);

            if (maxWidth <= 0) maxWidth = 100;
            if (maxHeight <= 0) maxHeight = 100;

            Point2f[] srcPts = new[] { tl, tr, br, bl };
            Point2f[] dstPts = new[]
            {
                new Point2f(0, 0),
                new Point2f(maxWidth, 0),
                new Point2f(maxWidth, maxHeight),
                new Point2f(0, maxHeight)
            };

            using Mat matrix = Cv2.GetPerspectiveTransform(srcPts, dstPts);
            Mat rectified = new Mat();
            Cv2.WarpPerspective(original, rectified, matrix, new OpenCvSharp.Size(maxWidth, maxHeight));

            return rectified;
        }

        private List<OpenCvSharp.Point> TrimEnds(List<OpenCvSharp.Point> pts, bool sortByX, float trimRatio)
        {
            if (pts == null || pts.Count < 10) return pts;
            var sorted = sortByX ? pts.OrderBy(p => p.X).ToList() : pts.OrderBy(p => p.Y).ToList();
            int trimCount = (int)(sorted.Count * trimRatio);
            return sorted.Skip(trimCount).Take(sorted.Count - 2 * trimCount).ToList();
        }

        private ParametricLine SimpleFit(List<OpenCvSharp.Point> pts)
        {
            if (pts == null || pts.Count == 0) return new ParametricLine { V = new Point2f(1, 0), P = new Point2f(0, 0) };
            OpenCvSharp.Line2D fitResult = Cv2.FitLine(pts, DistanceTypes.L1, 0, 0.01, 0.01);
            return new ParametricLine
            {
                V = new Point2f((float)fitResult.Vx, (float)fitResult.Vy),
                P = new Point2f((float)fitResult.X1, (float)fitResult.Y1)
            };
        }

        private ParametricLine FitLineRobust(List<OpenCvSharp.Point> pts)
        {
            if (pts == null || pts.Count < 2) return new ParametricLine { P = new Point2f(0, 0), V = new Point2f(0, 0) };
            ParametricLine baseLine = SimpleFit(pts);
            if (pts.Count < 5) return baseLine;

            List<double> distances = new List<double>(pts.Count);
            double sumDist = 0;
            foreach (var p in pts)
            {
                double d = GetDistanceToLine(p, baseLine);
                distances.Add(d);
                sumDist += d;
            }

            double mean = sumDist / pts.Count;
            double sumSq = distances.Sum(d => Math.Pow(d - mean, 2));
            double stdDev = Math.Sqrt(sumSq / pts.Count);
            double threshold = Math.Max(mean + 2.0 * stdDev, 3.0);

            List<OpenCvSharp.Point> cleanPts = new List<OpenCvSharp.Point>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (distances[i] <= threshold) cleanPts.Add(pts[i]);
            }

            if (cleanPts.Count < 2) return baseLine;
            return SimpleFit(cleanPts);
        }

        private double GetDistanceToLine(OpenCvSharp.Point p, ParametricLine line)
        {
            float dx = p.X - line.P.X;
            float dy = p.Y - line.P.Y;
            return Math.Abs(dx * line.V.Y - dy * line.V.X);
        }

        private Point2f ComputeIntersection(ParametricLine l1, ParametricLine l2)
        {
            float x1 = l1.P.X, y1 = l1.P.Y;
            float x2 = l1.P.X + l1.V.X, y2 = l1.P.Y + l1.V.Y;
            float x3 = l2.P.X, y3 = l2.P.Y;
            float x4 = l2.P.X + l2.V.X, y4 = l2.P.Y + l2.V.Y;

            float d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(d) < 1e-5) return new Point2f(0, 0);

            float pre = (x1 * y2 - y1 * x2);
            float post = (x3 * y4 - y3 * x4);
            float x = (pre * (x3 - x4) - (x1 - x2) * post) / d;
            float y = (pre * (y3 - y4) - (y1 - y2) * post) / d;

            return new Point2f(x, y);
        }

        private void DrawDebugPoints(Mat canvas, List<OpenCvSharp.Point> pts, Scalar color)
        {
            for (int i = 0; i < pts.Count; i += 2) Cv2.Circle(canvas, pts[i], 2, color, -1);
        }

        private void DrawDebugLine(Mat canvas, ParametricLine line, int w, int h)
        {
            if (line.V.X == 0 && line.V.Y == 0) return;
            Point2f p1 = line.P + line.V * -2000;
            Point2f p2 = line.P + line.V * 2000;
            Cv2.Line(canvas, (int)p1.X, (int)p1.Y, (int)p2.X, (int)p2.Y, ColorLine, 2, LineTypes.AntiAlias);
        }
    }
}