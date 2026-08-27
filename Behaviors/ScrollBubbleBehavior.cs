using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskCleaner.Behaviors
{
    /// <summary>
    /// 当控件内部的 ScrollViewer 无法继续滚动（内容未溢出或已到边界）时，
    /// 将滚轮事件冒泡给最近的祖先 ScrollViewer，避免「鼠标移到内部控件后整页滚轮失效」。
    /// 用法：在样式或元素上设置 b:ScrollBubbleBehavior.Enabled="True"。
    /// </summary>
    public static class ScrollBubbleBehavior
    {
        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled", typeof(bool), typeof(ScrollBubbleBehavior),
                new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element && (bool)e.NewValue)
            {
                // handledEventsToo=true：即使内部 ScrollViewer 已把事件标记为已处理也要拦截
                element.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), true);
            }
        }

        private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 内部未处理：事件会正常冒泡，交给祖先；无需干预。
            if (!e.Handled) return;

            var element = (UIElement)sender;
            var inner = FindVisualChild<ScrollViewer>(element);

            if (inner != null && inner.ScrollableHeight > 0)
            {
                bool atTop = inner.VerticalOffset <= 0.5;
                bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
                // 内部还能朝该方向滚动 → 保留 Handled，让内部 ScrollViewer 处理。
                if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom))
                    return;
            }

            // 内部滚不动（无溢出 / 已到边界）：取消 Handled，让事件继续冒泡给祖先 ScrollViewer。
            e.Handled = false;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var r = FindVisualChild<T>(child);
                if (r != null) return r;
            }
            return null;
        }
    }
}
