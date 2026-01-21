using System;
using System.Reflection;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// プロパティの値を変更するコマンド（ジェネリック型）
    /// </summary>
    /// <typeparam name="T">プロパティの型</typeparam>
    public class ChangePropertyCommand<T> : IEditorCommand
    {
        private readonly object _target;
        private readonly string _propertyName;
        private readonly T _oldValue;
        private readonly T _newValue;
        private readonly PropertyInfo _propertyInfo;
        private readonly string _description;

        /// <summary>
        /// コマンドの説明
        /// </summary>
        public string Description => _description;

        /// <summary>
        /// アンドゥ可能かどうか
        /// </summary>
        public bool CanUndo => true;

        /// <summary>
        /// プロパティ変更コマンドを作成
        /// </summary>
        /// <param name="target">プロパティを持つオブジェクト</param>
        /// <param name="propertyName">プロパティ名</param>
        /// <param name="oldValue">変更前の値</param>
        /// <param name="newValue">変更後の値</param>
        /// <param name="description">コマンドの説明（省略時は自動生成）</param>
        public ChangePropertyCommand(object target, string propertyName, T oldValue, T newValue, string? description = null)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
            _oldValue = oldValue;
            _newValue = newValue;

            // プロパティ情報を取得
            _propertyInfo = _target.GetType().GetProperty(_propertyName)
                ?? throw new ArgumentException($"プロパティ '{_propertyName}' が見つかりません", nameof(propertyName));

            // 説明を設定
            _description = description ?? $"{_target.GetType().Name}.{_propertyName} を変更";
        }

        /// <summary>
        /// 新しい値を設定
        /// </summary>
        public void Execute()
        {
            _propertyInfo.SetValue(_target, _newValue);
        }

        /// <summary>
        /// 元の値に戻す
        /// </summary>
        public void Undo()
        {
            _propertyInfo.SetValue(_target, _oldValue);
        }
    }

    /// <summary>
    /// プロパティ変更コマンドを作成するヘルパークラス
    /// </summary>
    public static class PropertyCommand
    {
        /// <summary>
        /// プロパティ変更コマンドを作成（型推論を利用）
        /// </summary>
        public static ChangePropertyCommand<T> Create<T>(object target, string propertyName, T oldValue, T newValue, string? description = null)
        {
            return new ChangePropertyCommand<T>(target, propertyName, oldValue, newValue, description);
        }
    }
}
