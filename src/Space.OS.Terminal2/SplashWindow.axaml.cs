using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Space.OS.Terminal2;

public partial class SplashWindow : Window
{
    private readonly ScaleTransform _splashScale = new(0.96, 0.96);
    private readonly TaskCompletionSource _introAnimationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SplashWindow()
    {
        InitializeComponent();
        SplashRootGrid.RenderTransform = _splashScale;
        Loaded += SplashWindow_Loaded;
    }

    /// <summary>Завершается после fade+zoom интро (без искусственного Delay в App).</summary>
    public Task WaitForIntroAnimationAsync() => _introAnimationCompleted.Task;

    private async void SplashWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Fade: RunAsync(Window) — окно это Visual, ОК.
            // Zoom: НЕ RunAsync(ScaleTransform) — внутри аниматора цель приводится к Visual → InvalidCastException.
            const int fadeMs = 1500;
            var fadeIn = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(fadeMs),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(OpacityProperty, 0.0) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(OpacityProperty, 1.0) }
                    }
                }
            };

            await Task.WhenAll(
                fadeIn.RunAsync(this),
                RunSplashZoomAsync());
        }
        finally
        {
            _introAnimationCompleted.TrySetResult();
        }
    }

    /// <summary>0.96→1, QuadraticEase EaseOut (медленнее, чем оригинальный WPF 700 ms).</summary>
    private async Task RunSplashZoomAsync()
    {
        const int durationMs = 2200;
        var start = Environment.TickCount64;

        while (true)
        {
            var elapsed = Environment.TickCount64 - start;
            if (elapsed >= durationMs)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _splashScale.ScaleX = _splashScale.ScaleY = 1.0;
                });
                return;
            }

            var t = elapsed / (double)durationMs;
            var eased = 1.0 - (1.0 - t) * (1.0 - t);
            var s = 0.96 + 0.04 * eased;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _splashScale.ScaleX = _splashScale.ScaleY = s;
            });

            await Task.Delay(16);
        }
    }
}
