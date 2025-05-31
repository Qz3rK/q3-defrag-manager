// Copyright (c) 2025 Qz3rK 
// License: MIT (https://opensource.org/licenses/MIT)

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
            // Настройка аппаратного ускорения
            ConfigureHardwareAcceleration();
            
            // Настройка качества рендеринга
            ConfigureRenderingQuality();
            
            // Обработка исключений
            ConfigureExceptionHandling();
            
            base.OnStartup(e);
        }

        private void ConfigureHardwareAcceleration()
        {
            // Принудительное использование аппаратного ускорения
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            
            // Проверка возможностей GPU
            int renderingTier = (RenderCapability.Tier >> 16);
            
            // Оптимальные настройки в зависимости от возможностей GPU
            if (renderingTier >= 2)
            {
                // Максимальное качество для мощных GPU
                RenderOptions.ProcessRenderMode = RenderMode.Default;
            }
            else
            {
                // Базовые настройки для слабых GPU
                RenderOptions.ProcessRenderMode = RenderMode.Default;
            }
        }

        private void ConfigureRenderingQuality()
        {
            // Применение настроек к главному окну
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
            
            // Настройки текста - максимальное качество
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
            
            // Настройки сглаживания текста
            TextOptions.SetTextHintingMode(window, TextHintingMode.Fixed);
            
            // Настройки изображений - максимальное качество
            RenderOptions.SetBitmapScalingMode(window, BitmapScalingMode.Fant);
            RenderOptions.SetEdgeMode(window, EdgeMode.Aliased);
            
            // Оптимизация производительности
            RenderOptions.SetCachingHint(window, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(window, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(window, 2.0);
        }

        private void ConfigureExceptionHandling()
        {
            // Обработка исключений в UI потоке
            DispatcherUnhandledException += (sender, args) =>
            {
                MessageBox.Show($"Ошибка отображения:\n{args.Exception.Message}", 
                    "Ошибка рендеринга", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Обработка исключений в других потоках
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
            // Сброс настроек перед выходом
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            base.OnExit(e);
        }
    }
}
