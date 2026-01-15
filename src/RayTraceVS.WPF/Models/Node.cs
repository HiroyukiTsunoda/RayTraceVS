using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace RayTraceVS.WPF.Models
{
    public enum NodeCategory
    {
        Object,
        Math,
        Camera,
        Light,
        Scene
    }

    public abstract partial class Node : ObservableObject
    {
        [ObservableProperty]
        private Guid id;

        [ObservableProperty]
        private string title = string.Empty;

        [ObservableProperty]
        private Point position;

        [ObservableProperty]
        private ObservableCollection<NodeSocket> inputSockets;

        [ObservableProperty]
        private ObservableCollection<NodeSocket> outputSockets;

        [ObservableProperty]
        private NodeCategory category;

        [ObservableProperty]
        private bool isSelected;

        public Brush CategoryColor => category switch
        {
            NodeCategory.Object => new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)),
            NodeCategory.Math => new SolidColorBrush(Color.FromRgb(0xE2, 0x7A, 0x3F)),
            NodeCategory.Camera => new SolidColorBrush(Color.FromRgb(0x9B, 0x59, 0xB6)),
            NodeCategory.Light => new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F)),
            NodeCategory.Scene => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
            _ => new SolidColorBrush(Colors.Gray)
        };

        protected Node(string title, NodeCategory category)
        {
            id = Guid.NewGuid();
            this.title = title;
            this.category = category;
            inputSockets = new ObservableCollection<NodeSocket>();
            outputSockets = new ObservableCollection<NodeSocket>();
        }

        protected void AddInputSocket(string name, SocketType type)
        {
            var socket = new NodeSocket(name, type, true) { ParentNode = this };
            inputSockets.Add(socket);
        }

        protected void AddOutputSocket(string name, SocketType type)
        {
            var socket = new NodeSocket(name, type, false) { ParentNode = this };
            outputSockets.Add(socket);
        }

        public abstract object? Evaluate(Dictionary<Guid, object?> inputValues);

        protected T? GetInputValue<T>(string socketName, Dictionary<Guid, object?> inputValues)
        {
            foreach (var socket in inputSockets)
            {
                if (socket.Name == socketName && inputValues.TryGetValue(socket.Id, out var value))
                {
                    if (value is T typedValue)
                        return typedValue;
                }
            }
            return default;
        }
    }
}
