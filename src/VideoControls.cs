using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IPhonePC {
    // Both the workbench and floating toolbar use one setting and the same menu.
    internal sealed class VideoScaleSelector {
        private readonly Button button;
        private readonly ContextMenu menu;
        private readonly Dictionary<int, MenuItem> items = new Dictionary<int, MenuItem>();
        public bool IsOpen { get { return menu.IsOpen; } }
        public event EventHandler Closed;
        public VideoScaleSelector(Button target, Action<int> selected) {
            button = target;
            menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MinWidth = 150 };
            foreach (int percent in new [] {50,75,100}) {
                int value = percent;
                var item = new MenuItem { Header = value + "%", IsCheckable = true };
                item.Click += delegate { selected(value); };
                items.Add(value, item); menu.Items.Add(item);
            }
            button.ContextMenu = menu;
            button.ToolTip = "只缩放画面，保持窗口大小；选择后立即生效。";
            button.Click += delegate { menu.IsOpen = true; };
            menu.Closed += delegate { if (Closed != null) Closed(this, EventArgs.Empty); };
            SetValue(100);
        }
        public void SetValue(int percent) {
            button.Content = "画面缩放 " + percent + "%  ▾";
            foreach (var item in items) item.Value.IsChecked = item.Key == percent;
        }
    }
}
