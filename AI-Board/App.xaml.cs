using System;
using System.Threading;
using System.Windows;

namespace AI_Board
{
    public partial class App : Application
    {
        // 声明为静态变量，防止被垃圾回收器(GC)意外回收导致锁失效
        private static Mutex _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "AI_Board_Unique_Instance_Lock";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // 发现多开，弹出提示并直接强制退出
                MessageBox.Show("AI Board 已经在运行中！\n请检查桌面右下角悬浮窗或系统进程。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}