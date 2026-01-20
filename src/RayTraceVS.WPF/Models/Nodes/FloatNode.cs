using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// Floatノード（浮動小数点数）
    /// </summary>
    public partial class FloatNode : Node
    {
        private float _value = 0.0f;
        public float Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                {
                    MarkDirty();
                }
            }
        }

        public override bool HasEditableFloat => true;
        
        public override float EditableFloatValue
        {
            get => Value;
            set => Value = value;
        }

        public FloatNode() : base("Float", NodeCategory.Math)
        {
            AddOutputSocket("Value", SocketType.Float);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            return Value;
        }
    }
}
