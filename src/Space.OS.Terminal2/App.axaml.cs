using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Diagnostics;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Space.OS.Terminal2;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ConsoleLogCapture.Install();

            // Match WPF: no accidental shutdown while only splash is open (MainWindow not yet the real shell).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _ = ShowSplashThenMainAsync(desktop);
        }

#if DEBUG
        this.AttachDevTools();
#endif
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowSplashThenMainAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        SplashWindow? splash = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                splash = new SplashWindow();
                desktop.MainWindow = splash;
                splash.Show();
            });

            // Ровно по окончании интро-анимации сплэша — без лишнего Task.Delay.
            if (splash != null)
                await splash.WaitForIntroAnimationAsync().ConfigureAwait(false);

            // Continuation может быть с пула; вся работа с окнами — на UI thread.
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                MainWindow main;
                try
                {
                    main = new MainWindow();
                }
                catch
                {
                    splash?.Close();
                    throw;
                }

                // Не вешать MainWindow на main сразу: сплэш исчезает, а main ещё Opacity=0 → «пустая» пауза.
                main.Opacity = 0;
                main.Show();

                var showAnim = new Animation
                {
                    Duration = TimeSpan.FromMilliseconds(320),
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0),
                            Setters = { new Setter(Window.OpacityProperty, 0.0) }
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1),
                            Setters = { new Setter(Window.OpacityProperty, 1.0) }
                        }
                    }
                };

                var closeAnim = new Animation
                {
                    Duration = TimeSpan.FromMilliseconds(320),
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0),
                            Setters = { new Setter(Window.OpacityProperty, 1.0) }
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1),
                            Setters = { new Setter(Window.OpacityProperty, 0.0) }
                        }
                    }
                };

                await Task.WhenAll(showAnim.RunAsync(main), closeAnim.RunAsync(splash!));
                splash!.Close();

                main.Opacity = 1.0;
                desktop.MainWindow = main;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                splash?.Close();
                var dialog = new Window
                {
                    Title = "Space.OS.Terminal2",
                    Width = 400,
                    Height = 200,
                    Content = new TextBlock
                    {
                        Text = $"Startup failed: {ex.Message}",
                        Margin = new Thickness(20)
                    }
                };
                desktop.MainWindow = dialog;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                dialog.Show();
            });
        }
    }
}
