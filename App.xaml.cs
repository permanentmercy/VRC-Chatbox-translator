using System;
using Microsoft.UI.Xaml;
using VrcChatboxDemo.Services;
using VrcChatboxDemo.Views;

namespace VrcChatboxDemo;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private ImmersiveWindow? _immersiveWindow;
    public static Microsoft.UI.Dispatching.DispatcherQueue? DispatcherQueueInstance { get; private set; }

    private static System.Threading.Mutex? _appMutex;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const int HWND_BROADCAST = 0xffff;
    public static readonly uint WM_WAKE_UP_APP = RegisterWindowMessage("VrcChatboxDemo_WakeUp_Message_8e29b6f1");

    public App()
    {
        bool createdNew = false;
        try
        {
            _appMutex = new System.Threading.Mutex(true, @"Local\VrcChatboxDemo_SingleInstance_Mutex_8e29b6f1", out createdNew);
        }
        catch (System.Threading.AbandonedMutexException)
        {
            // 上一个进程异常退出未释放互斥量，当前进程接管
            createdNew = true;
        }

        if (!createdNew)
        {
            try
            {
                var currentPid = Environment.ProcessId;
                var otherProcesses = System.Diagnostics.Process.GetProcessesByName("VrcChatboxDemo")
                    .Where(p => p.Id != currentPid)
                    .ToList();

                if (otherProcesses.Count > 0)
                {
                    bool signaledExisting = false;
                    foreach (var proc in otherProcesses)
                    {
                        try
                        {
                            if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                            {
                                PostMessage(proc.MainWindowHandle, WM_WAKE_UP_APP, IntPtr.Zero, IntPtr.Zero);
                                ShowWindow(proc.MainWindowHandle, 9 /* SW_RESTORE */);
                                ShowWindow(proc.MainWindowHandle, 5 /* SW_SHOW */);
                                SetForegroundWindow(proc.MainWindowHandle);
                                signaledExisting = true;
                            }
                        }
                        catch { }
                    }

                    // 广播唤醒已有实例（若已有实例处于托盘或沉浸模式）
                    PostMessage((IntPtr)HWND_BROADCAST, WM_WAKE_UP_APP, IntPtr.Zero, IntPtr.Zero);

                    if (!signaledExisting)
                    {
                        // 发现无活动窗口的孤儿进程或后台残留，强制终止清理以便新实例正常启动
                        foreach (var proc in otherProcesses)
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(500);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // 成功唤醒已有实例，退出当前重复启动的进程
                        Environment.Exit(0);
                        return;
                    }
                }
            }
            catch { }
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => ReleaseSingleInstanceMutex();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), "AppDomain: " + e.ExceptionObject?.ToString());
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), "TaskScheduler: " + e.Exception?.ToString());
            }
            catch { }
            e.SetObserved();
        };

        UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), "UnhandledException: " + (e.Exception?.ToString() ?? e.Message));
            }
            catch { }
            e.Handled = true;
        };
        InitializeComponent();
    }

    public static void ReleaseSingleInstanceMutex()
    {
        if (_appMutex != null)
        {
            try
            {
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
            }
            catch { }
            _appMutex = null;
        }
    }

    public static void ShowMainWindow()
    {
        DispatcherQueueInstance?.TryEnqueue(() =>
        {
            if (Current is App app)
            {
                try
                {
                    if (SettingsService.Instance.IsImmersiveMode)
                    {
                        SettingsService.Instance.ToggleImmersiveMode();
                    }
                    else
                    {
                        app._mainWindow?.AppWindow.Show();
                        app._mainWindow?.Activate();
                        if (app._mainWindow != null)
                        {
                            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(app._mainWindow);
                            SettingsService.Instance.HotkeyService.ActivateWindow(mainHwnd);
                        }
                    }
                }
                catch { }
            }
        });
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            DispatcherQueueInstance = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            _mainWindow = new MainWindow();
            _immersiveWindow = new ImmersiveWindow();

            var immersiveHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_immersiveWindow);
            SettingsService.Instance.HotkeyService.SetImmersiveWindow(immersiveHwnd);

            SettingsService.Instance.ImmersiveModeToggled += (isImmersive) =>
            {
                DispatcherQueueInstance.TryEnqueue(() =>
                {
                    try
                    {
                        SettingsService.Instance.HotkeyService.SwitchActiveWindow(isImmersive);

                        if (isImmersive)
                        {
                            _mainWindow?.AppWindow.Hide();
                            _immersiveWindow?.AppWindow.Show();
                            _immersiveWindow?.Activate();
                            _immersiveWindow?.FocusInput();
                            _immersiveWindow?.UpdateImmersiveLayout();
                        }
                        else
                        {
                            _immersiveWindow?.AppWindow.Hide();
                            _mainWindow?.AppWindow.Show();
                            _mainWindow?.Activate();
                            if (_mainWindow != null)
                            {
                                var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                                SettingsService.Instance.HotkeyService.ActivateWindow(mainHwnd);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            System.IO.File.AppendAllText(
                                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkey.log"),
                                $"[ImmersiveModeToggled Exception] {ex}\r\n");
                        }
                        catch { }
                    }
                });
            };

            SettingsService.Instance.HotkeyService.HotkeyPressed += () =>
            {
                DispatcherQueueInstance.TryEnqueue(() =>
                {
                    if (SettingsService.Instance.IsImmersiveMode)
                    {
                        _immersiveWindow?.FocusInput();
                    }
                });
            };

            _mainWindow.Activate();

            // 启动初始化：按配置自动启动语音识别等后台服务
            _ = SettingsService.Instance.InitializeStartupServicesAsync();
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), "OnLaunched: " + ex.ToString());
            }
            catch { }
            throw;
        }
    }
}
