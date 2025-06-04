
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
namespace DefragManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {

            ConfigureHardwareAcceleration();


            ConfigureRenderingQuality();


            ConfigureExceptionHandling();

            base.OnStartup(e);
        }
        private void ConfigureHardwareAcceleration()
        {

            RenderOptions.ProcessRenderMode = RenderMode.Default;


            int renderingTier = (RenderCapability.Tier >> 16);


            if (renderingTier >= 2)
            {

                RenderOptions.ProcessRenderMode = RenderMode.Default;
            }
            else
            {

                RenderOptions.ProcessRenderMode = RenderMode.Default;
            }
        }
        private void ConfigureRenderingQuality()
        {

            this.Activated += (sender, args) =>
            {
                if (MainWindow != null)
                {
                    ApplyRenderingSettings(MainWindow);
                }
            };
        }
        private void ApplyRenderingSettings(Window window)
        {
            if (window == null) return;


            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);


            TextOptions.SetTextHintingMode(window, TextHintingMode.Fixed);


            RenderOptions.SetBitmapScalingMode(window, BitmapScalingMode.Fant);
            RenderOptions.SetEdgeMode(window, EdgeMode.Aliased);


            RenderOptions.SetCachingHint(window, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(window, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(window, 2.0);
        }
        private void ConfigureExceptionHandling()
        {

            DispatcherUnhandledException += (sender, args) =>
            {
                MessageBox.Show($"Ошибка отображения:\n{args.Exception.Message}",
                    "Ошибка рендеринга", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var exception = args.ExceptionObject as Exception;
                Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Критическая ошибка:\n{exception?.Message ?? "Неизвестная ошибка"}",
                        "Фатальная ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            };
        }
        protected override void OnExit(ExitEventArgs e)
        {

            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            base.OnExit(e);
        }
    }
}
