using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FusePlayer;

public partial class App : Application
{
    public static bool ToolTipsEnabled { get; set; } = true;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement),
            ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(ToolTip_OnOpening), true);
        base.OnStartup(e);
    }

    private static void ToolTip_OnOpening(object sender, ToolTipEventArgs e)
    {
        if (!ToolTipsEnabled)
            e.Handled = true;
    }

    private void ToolTip_OnOpened(object sender, RoutedEventArgs e)
    {
        if (!ToolTipsEnabled || sender is not ToolTip toolTip)
        {
            if (sender is ToolTip disabledToolTip)
                disabledToolTip.IsOpen = false;
            return;
        }

        // Les contrôles de lecture vivent dans une fenêtre overlay au-dessus
        // du HWND vidéo. Le popup WPF d'une info-bulle doit être replacé dans
        // le même ordre Z pour ne pas passer derrière l'image.
        foreach (var window in Windows.OfType<MainWindow>())
            window.SetToolTipVisibilityState(true);
        RaiseToolTipWindow(toolTip, 0);
    }

    private void RaiseToolTipWindow(ToolTip toolTip, int attempt)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (!toolTip.IsOpen)
                return;

            if (PresentationSource.FromVisual(toolTip) is HwndSource source)
            {
                SetWindowPos(source.Handle, HwndTopmost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
                foreach (var window in Windows.OfType<MainWindow>())
                    window.SetActiveToolTipHandle(source.Handle);
                return;
            }

            if (attempt < 3)
                RaiseToolTipWindow(toolTip, attempt + 1);
        });
    }

    private void ToolTip_OnClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip toolTip)
            return;

        var source = PresentationSource.FromVisual(toolTip) as HwndSource;
        foreach (var window in Windows.OfType<MainWindow>())
            window.SetToolTipVisibilityState(false);
        if (source is not null)
        {
            SetWindowPos(source.Handle, HwndNotTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
            foreach (var window in Windows.OfType<MainWindow>())
                window.ClearActiveToolTipHandle(source.Handle);
        }

    }
}
