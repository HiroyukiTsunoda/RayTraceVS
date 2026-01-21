using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace RayTraceVS.WPF.Utils
{
    /// <summary>
    /// Sliderのドラッグ完了時のみバインディングを更新する添付ビヘイビア
    /// </summary>
    public static class SliderDragBehavior
    {
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
                
                // Thumbを取得してDragCompletedイベントを登録
                var track = slider.Template.FindName("PART_Track", slider) as Track;
                if (track?.Thumb != null)
                {
                    track.Thumb.DragCompleted += (s, args) =>
                    {
                        // Valueプロパティのバインディングを更新
                        var binding = BindingOperations.GetBindingExpression(slider, Slider.ValueProperty);
                        binding?.UpdateSource();
                    };
                }
            }
        }
    }
}
