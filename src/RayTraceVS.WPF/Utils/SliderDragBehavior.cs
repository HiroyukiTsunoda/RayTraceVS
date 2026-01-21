using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Utils
{
    /// <summary>
    /// Sliderのドラッグ完了時のみバインディングを更新する添付ビヘイビア
    /// Undo/Redo対応：ドラッグ開始時に値を記録し、完了時にコマンドを発行
    /// </summary>
    public static class SliderDragBehavior
    {
        // スライダーごとのドラッグ開始時の値を記録
        private static readonly Dictionary<Slider, object> _dragStartValues = new();

        public static readonly DependencyProperty UpdateOnDragCompletedProperty =
            DependencyProperty.RegisterAttached(
                "UpdateOnDragCompleted",
                typeof(bool),
                typeof(SliderDragBehavior),
                new PropertyMetadata(false, OnUpdateOnDragCompletedChanged));

        public static bool GetUpdateOnDragCompleted(DependencyObject obj)
        {
            return (bool)obj.GetValue(UpdateOnDragCompletedProperty);
        }

        public static void SetUpdateOnDragCompleted(DependencyObject obj, bool value)
        {
            obj.SetValue(UpdateOnDragCompletedProperty, value);
        }

        private static void OnUpdateOnDragCompletedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Slider slider)
            {
                if ((bool)e.NewValue)
                {
                    slider.Loaded += Slider_Loaded;
                }
                else
                {
                    slider.Loaded -= Slider_Loaded;
                }
            }
        }

        private static void Slider_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Slider slider)
            {
                slider.Loaded -= Slider_Loaded;
                
                // Thumbを取得してDragStarted/DragCompletedイベントを登録
                var track = slider.Template.FindName("PART_Track", slider) as Track;
                if (track?.Thumb != null)
                {
                    track.Thumb.DragStarted += (s, args) =>
                    {
                        // ドラッグ開始時の値を記録
                        var binding = BindingOperations.GetBindingExpression(slider, Slider.ValueProperty);
                        if (binding != null)
                        {
                            var source = binding.ResolvedSource;
                            var propertyName = binding.ResolvedSourcePropertyName;
                            if (source != null && !string.IsNullOrEmpty(propertyName))
                            {
                                var propertyInfo = source.GetType().GetProperty(propertyName);
                                if (propertyInfo != null)
                                {
                                    var currentValue = propertyInfo.GetValue(source);
                                    _dragStartValues[slider] = currentValue ?? 0;
                                }
                            }
                        }
                    };
                    
                    track.Thumb.DragCompleted += (s, args) =>
                    {
                        // Valueプロパティのバインディングを更新
                        var binding = BindingOperations.GetBindingExpression(slider, Slider.ValueProperty);
                        binding?.UpdateSource();
                        
                        // Undo/Redo用コマンドを発行
                        if (binding != null && _dragStartValues.TryGetValue(slider, out var oldValue))
                        {
                            _dragStartValues.Remove(slider);
                            
                            var source = binding.ResolvedSource;
                            var propertyName = binding.ResolvedSourcePropertyName;
                            if (source != null && !string.IsNullOrEmpty(propertyName))
                            {
                                var propertyInfo = source.GetType().GetProperty(propertyName);
                                if (propertyInfo != null)
                                {
                                    var newValue = propertyInfo.GetValue(source);
                                    
                                    // 値が変更された場合のみコマンドを発行
                                    if (!Equals(oldValue, newValue))
                                    {
                                        var viewModel = FindMainViewModel(slider);
                                        if (viewModel != null)
                                        {
                                            // 型に応じてコマンドを作成
                                            var command = CreatePropertyCommand(source, propertyName, oldValue, newValue, propertyInfo.PropertyType);
                                            if (command != null)
                                            {
                                                viewModel.CommandManager.RegisterExecuted(command);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    };
                }
            }
        }

        /// <summary>
        /// ビジュアルツリーを辿ってMainViewModelを取得
        /// </summary>
        private static MainViewModel? FindMainViewModel(DependencyObject element)
        {
            var window = Window.GetWindow(element);
            return window?.DataContext as MainViewModel;
        }

        /// <summary>
        /// プロパティの型に応じてChangePropertyCommandを作成
        /// </summary>
        private static IEditorCommand? CreatePropertyCommand(object target, string propertyName, object? oldValue, object? newValue, Type propertyType)
        {
            var description = $"{target.GetType().Name}.{propertyName} を変更";
            
            if (propertyType == typeof(int))
            {
                return new ChangePropertyCommand<int>(target, propertyName, (int)(oldValue ?? 0), (int)(newValue ?? 0), description);
            }
            else if (propertyType == typeof(float))
            {
                return new ChangePropertyCommand<float>(target, propertyName, (float)(oldValue ?? 0f), (float)(newValue ?? 0f), description);
            }
            else if (propertyType == typeof(double))
            {
                return new ChangePropertyCommand<double>(target, propertyName, (double)(oldValue ?? 0.0), (double)(newValue ?? 0.0), description);
            }
            
            return null;
        }
    }
}
