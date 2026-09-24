using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services;
using VrcChatboxDemo.Views;

namespace VrcChatboxDemo;

public sealed partial class MainWindow : Window
{
    private bool _isAlwaysOnTop = false;
    private const int NormalWidth = 820;
    private const int NormalHeight = 560;
    private readonly IntPtr _hWnd;
    private readonly TrayIconService _trayIconService = new();

    // 导航项映射表与拖动重排状态
    private readonly Dictionary<string, NavigationViewItem> _allNavItems = new();
    private NavigationViewItem? _pressedItem;
    private NavigationViewItem? _draggedItem;
    private Windows.Foundation.Point _pointerStartPos;
    private DispatcherTimer? _longPressTimer;
    private bool _isDragging = false;
    private int _currentDropSlot = -1;
    private bool _isReorderingNav = false;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        var cfg = SettingsService.Instance.Config;

        // 1. 恢复窗口尺寸
        int width = cfg.WindowWidth >= 400 ? cfg.WindowWidth : NormalWidth;
        int height = cfg.WindowHeight >= 300 ? cfg.WindowHeight : NormalHeight;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        // 2. 恢复窗口屏幕位置 (校验是否存在于当前活动显示器内)
        if (cfg.WindowX.HasValue && cfg.WindowY.HasValue)
        {
            var targetPoint = new Windows.Graphics.PointInt32(cfg.WindowX.Value, cfg.WindowY.Value);
            var displayArea = DisplayArea.GetFromPoint(targetPoint, DisplayAreaFallback.None);
            if (displayArea != null)
            {
                AppWindow.Move(targetPoint);
            }
        }

        // 3. 恢复置顶与最大化状态
        _isAlwaysOnTop = cfg.IsAlwaysOnTop;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _isAlwaysOnTop;
            UpdateTopMostButtonVisual();

            if (cfg.IsMaximized)
            {
                presenter.Maximize();
            }
        }

        // 监听窗口尺寸与位置变化并自动持久化
        AppWindow.Changed += OnAppWindowChanged;

        // 4. 初始化左侧导航功能栏项映射与持久化顺序
        InitializeNavItems(cfg.NavOrder);

        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SettingsService.Instance.HotkeyService.Initialize(_hWnd);
        SettingsService.Instance.ApplyHotkey();
        SettingsService.Instance.HotkeyService.HotkeyPressed += OnHotkeyPressed;

        // 初始化系统托盘图标
        _trayIconService.Initialize(
            _hWnd,
            onOpenMainWindow: () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppWindow.Show();
                    SettingsService.Instance.HotkeyService.ActivateWindow(_hWnd);
                });
            },
            onToggleImmersive: () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SettingsService.Instance.ToggleImmersiveMode();
                });
            },
            onQuitApp: () =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    _trayIconService.RemoveTrayIcon();
                    await SettingsService.Instance.QuitApplicationAsync();
                });
            });

        // 窗口关闭拦截：点击右上角关闭按钮时隐藏到托盘，后台持续运行并保存配置
        AppWindow.Closing += (sender, args) =>
        {
            args.Cancel = true;
            SettingsService.Instance.SaveConfigImmediately();
            AppWindow.Hide();
        };

        Closed += MainWindow_Closed;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                var cfg = SettingsService.Instance.Config;
                if (p.State == OverlappedPresenterState.Restored)
                {
                    cfg.WindowWidth = AppWindow.Size.Width;
                    cfg.WindowHeight = AppWindow.Size.Height;
                    cfg.WindowX = AppWindow.Position.X;
                    cfg.WindowY = AppWindow.Position.Y;
                    cfg.IsMaximized = false;
                    SettingsService.Instance.SaveConfigDebounced();
                }
                else if (p.State == OverlappedPresenterState.Maximized)
                {
                    cfg.IsMaximized = true;
                    SettingsService.Instance.SaveConfigDebounced();
                }
            }
        }
    }

    #region 导航项管理与长按拖拽重排

    private void InitializeNavItems(List<string>? savedOrder)
    {
        _allNavItems["chat"] = ChatNavItem;
        _allNavItems["speech"] = SpeechNavItem;
        _allNavItems["translation"] = TranslationNavItem;
        _allNavItems["hotkey"] = HotkeyNavItem;
        _allNavItems["interaction"] = InteractionNavItem;
        _allNavItems["network"] = NetworkLogNavItem;

        // 为每个项绑定长按拖拽事件与右键上下文菜单
        foreach (var item in _allNavItems.Values)
        {
            item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnNavItemPointerPressed), handledEventsToo: true);
            item.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnNavItemPointerMoved), handledEventsToo: true);
            item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnNavItemPointerReleased), handledEventsToo: true);
            item.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnNavItemPointerReleased), handledEventsToo: true);
            item.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnNavItemPointerReleased), handledEventsToo: true);

            SetupNavItemContextMenu(item);
        }

        // 整体 NavView 同时也监听指针移动与释放，防止鼠标稍微移出 item 时拖拽断掉
        NavView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnNavItemPointerMoved), handledEventsToo: true);
        NavView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnNavItemPointerReleased), handledEventsToo: true);
        NavView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnNavItemPointerReleased), handledEventsToo: true);
        NavView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnNavItemPointerReleased), handledEventsToo: true);

        ApplyNavOrder(savedOrder);

        // 默认激活第一项
        var firstItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault() ?? ChatNavItem;
        NavView.SelectedItem = firstItem;
        ContentFrame.Navigate(typeof(ChatPage));
    }

    private void ApplyNavOrder(List<string>? order)
    {
        _isReorderingNav = true;
        try
        {
            NavView.MenuItems.Clear();
            var added = new HashSet<string>();

            if (order != null)
            {
                foreach (var tag in order)
                {
                    if (_allNavItems.TryGetValue(tag, out var item) && added.Add(tag))
                    {
                        NavView.MenuItems.Add(item);
                    }
                }
            }

            // 补充缺省的项（保证所有功能入口均存在）
            foreach (var kvp in _allNavItems)
            {
                if (added.Add(kvp.Key))
                {
                    NavView.MenuItems.Add(kvp.Value);
                }
            }
        }
        finally
        {
            _isReorderingNav = false;
        }
    }

    private void SetupNavItemContextMenu(NavigationViewItem item)
    {
        var flyout = new MenuFlyout();

        var moveUpItem = new MenuFlyoutItem
        {
            Text = "向上移动",
            Icon = new FontIcon { Glyph = "\uE74A" }
        };
        moveUpItem.Click += (s, e) => MoveNavItem(item, -1);

        var moveDownItem = new MenuFlyoutItem
        {
            Text = "向下移动",
            Icon = new FontIcon { Glyph = "\uE74B" }
        };
        moveDownItem.Click += (s, e) => MoveNavItem(item, 1);

        var resetItem = new MenuFlyoutItem
        {
            Text = "恢复默认排序",
            Icon = new FontIcon { Glyph = "\uE777" }
        };
        resetItem.Click += (s, e) => ResetNavOrder();

        flyout.Items.Add(moveUpItem);
        flyout.Items.Add(moveDownItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(resetItem);

        item.ContextFlyout = flyout;
    }

    private void MoveNavItem(NavigationViewItem item, int delta)
    {
        int index = NavView.MenuItems.IndexOf(item);
        if (index < 0) return;
        int target = index + delta;
        if (target < 0 || target >= NavView.MenuItems.Count) return;

        _isReorderingNav = true;
        try
        {
            NavView.MenuItems.RemoveAt(index);
            NavView.MenuItems.Insert(target, item);
            NavView.SelectedItem = item;
        }
        finally
        {
            _isReorderingNav = false;
        }

        SaveCurrentNavOrder();
    }

    private void ResetNavOrder()
    {
        var defaultOrder = new List<string> { "chat", "speech", "translation", "hotkey", "interaction", "network" };
        ApplyNavOrder(defaultOrder);
        SaveCurrentNavOrder();
    }

    private void OnNavItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item) return;
        var props = e.GetCurrentPoint(NavView).Properties;
        if (!props.IsLeftButtonPressed) return;

        _pressedItem = item;
        _pointerStartPos = e.GetCurrentPoint(NavView).Position;

        _longPressTimer?.Stop();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _longPressTimer.Tick += (s, args) =>
        {
            _longPressTimer.Stop();
            if (_pressedItem != null && !_isDragging)
            {
                StartNavDrag(_pressedItem);
            }
        };
        _longPressTimer.Start();
    }

    private void StartNavDrag(NavigationViewItem item)
    {
        _isDragging = true;
        _draggedItem = item;
        _draggedItem.Opacity = 0.55;

        // 更新悬浮幽灵图标
        if (_draggedItem.Icon is FontIcon fi)
        {
            DragGhostIcon.Glyph = fi.Glyph;
        }
        DragGhostBadge.Visibility = Visibility.Visible;
        DragInsertIndicator.Visibility = Visibility.Visible;

        Canvas.SetLeft(DragGhostBadge, 4);
        Canvas.SetTop(DragGhostBadge, Math.Max(0, _pointerStartPos.Y - 20));

        UpdateDragTargetPosition(_pointerStartPos.Y);
    }

    private void OnNavItemPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var currentPos = e.GetCurrentPoint(NavView).Position;

        if (!_isDragging)
        {
            // 长按触发前如果移动超过 8px，视为正常划动或轻点，取消长按
            if (_longPressTimer != null && _longPressTimer.IsEnabled)
            {
                if (Math.Abs(currentPos.X - _pointerStartPos.X) > 8 ||
                    Math.Abs(currentPos.Y - _pointerStartPos.Y) > 8)
                {
                    _longPressTimer.Stop();
                }
            }
            return;
        }

        e.Handled = true;

        // 移动幽灵卡片跟随鼠标
        Canvas.SetLeft(DragGhostBadge, 4);
        Canvas.SetTop(DragGhostBadge, Math.Max(0, currentPos.Y - 20));

        // 更新插入指示线
        UpdateDragTargetPosition(currentPos.Y);
    }

    private void UpdateDragTargetPosition(double currentY)
    {
        var items = NavView.MenuItems.OfType<NavigationViewItem>().ToList();
        if (items.Count == 0) return;

        int targetSlot = items.Count;
        double indicatorY = 0;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var transform = it.TransformToVisual(NavView);
            var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double top = pt.Y;
            double height = it.ActualHeight > 0 ? it.ActualHeight : 40;
            double mid = top + height / 2.0;

            if (currentY < mid)
            {
                targetSlot = i;
                indicatorY = top - 1;
                break;
            }
            else if (i == items.Count - 1)
            {
                targetSlot = items.Count;
                indicatorY = top + height + 1;
            }
        }

        if (targetSlot < items.Count)
        {
            var targetItem = items[targetSlot];
            var transform = targetItem.TransformToVisual(NavView);
            var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            indicatorY = pt.Y - 1;
        }

        _currentDropSlot = targetSlot;

        double indicatorWidth = NavView.IsPaneOpen ? 180 : 42;
        DragInsertIndicator.Width = indicatorWidth;
        Canvas.SetLeft(DragInsertIndicator, 3);
        Canvas.SetTop(DragInsertIndicator, Math.Max(0, indicatorY));
    }

    private void OnNavItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _longPressTimer?.Stop();

        if (_isDragging)
        {
            e.Handled = true;
            EndNavDrag();
        }
        else
        {
            _pressedItem = null;
            _draggedItem = null;
        }
    }

    private void EndNavDrag()
    {
        if (!_isDragging || _draggedItem == null) return;

        _isDragging = false;
        DragGhostBadge.Visibility = Visibility.Collapsed;
        DragInsertIndicator.Visibility = Visibility.Collapsed;
        _draggedItem.Opacity = 1.0;

        int oldIndex = NavView.MenuItems.IndexOf(_draggedItem);
        int targetSlot = _currentDropSlot;

        if (oldIndex >= 0 && targetSlot >= 0)
        {
            int newIndex = targetSlot;
            if (oldIndex < targetSlot)
            {
                newIndex = targetSlot - 1;
            }

            if (newIndex >= 0 && newIndex < NavView.MenuItems.Count && newIndex != oldIndex)
            {
                _isReorderingNav = true;
                try
                {
                    NavView.MenuItems.RemoveAt(oldIndex);
                    NavView.MenuItems.Insert(newIndex, _draggedItem);
                    NavView.SelectedItem = _draggedItem;
                }
                finally
                {
                    _isReorderingNav = false;
                }

                SaveCurrentNavOrder();
            }
        }

        _pressedItem = null;
        _draggedItem = null;
    }

    private void SaveCurrentNavOrder()
    {
        var list = new List<string>();
        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem nvi && nvi.Tag is string tag)
            {
                list.Add(tag);
            }
        }
        SettingsService.Instance.Config.NavOrder = list;
        SettingsService.Instance.SaveConfigDebounced();
    }

    #endregion

    private void OnHotkeyPressed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsService.Instance.IsImmersiveMode) return;

            var hk = SettingsService.Instance.HotkeyService;
            if (hk.IsWindowForeground(_hWnd))
            {
                // 当前主窗口已在前台并有焦点：按一次把焦点切回刚刚的程序
                hk.RestorePreviousWindowFocus();
                return;
            }

            // 当前在其他程序：按一次唤出主窗口并聚焦输入框
            hk.ActivateWindow(_hWnd);

            if (ContentFrame.CurrentSourcePageType != typeof(ChatPage))
            {
                NavView.SelectedItem = ChatNavItem;
                ContentFrame.Navigate(typeof(ChatPage));
            }

            if (ContentFrame.Content is ChatPage chatPage)
            {
                chatPage.FocusInput();
            }
        });
    }

    private void ImmersiveButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.ToggleImmersiveMode();
    }

    private void TopMostButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            _isAlwaysOnTop = !_isAlwaysOnTop;
            presenter.IsAlwaysOnTop = _isAlwaysOnTop;
            UpdateTopMostButtonVisual();

            SettingsService.Instance.Config.IsAlwaysOnTop = _isAlwaysOnTop;
            SettingsService.Instance.SaveConfigDebounced();
        }
    }

    private void UpdateTopMostButtonVisual()
    {
        TopMostIcon.Glyph = _isAlwaysOnTop ? "\uE718" : "\uE840";
        TopMostIcon.Foreground = _isAlwaysOnTop
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SettingsService.Instance.SaveConfigImmediately();
        _trayIconService.RemoveTrayIcon();
        SettingsService.Instance.HotkeyService.HotkeyPressed -= OnHotkeyPressed;
        SettingsService.Instance.HotkeyService.Dispose();
        App.ReleaseSingleInstanceMutex();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isReorderingNav) return;

        // 切换页面时，立即抑制并清除可能残留的聊天打字动画与心跳
        SettingsService.Instance.SuppressTypingAnimation();

        if (args.SelectedItem is NavigationViewItem item)
        {
            string tag = item.Tag?.ToString() ?? string.Empty;
            Type? targetPage = tag switch
            {
                "chat" => typeof(ChatPage),
                "speech" => typeof(SpeechSettingsPage),
                "translation" => typeof(TranslationSettingsPage),
                "hotkey" => typeof(HotkeySettingsPage),
                "interaction" => typeof(InteractionSettingsPage),
                "network" => typeof(NetworkLogSettingsPage),
                "settings" => typeof(SpeechSettingsPage),
                _ => null
            };

            if (targetPage != null && ContentFrame.CurrentSourcePageType != targetPage)
            {
                ContentFrame.Navigate(targetPage);
            }
        }
    }
}
