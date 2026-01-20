namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// エディタコマンドのインターフェース
    /// アンドゥ/リドゥをサポートするためのコマンドパターン
    /// </summary>
    public interface IEditorCommand
    {
        /// <summary>
        /// コマンドの説明（アンドゥ/リドゥメニュー表示用）
        /// </summary>
        string Description { get; }

        /// <summary>
        /// コマンドを実行する
        /// </summary>
        void Execute();

        /// <summary>
        /// コマンドを元に戻す
        /// </summary>
        void Undo();

        /// <summary>
        /// このコマンドがアンドゥ可能かどうか
        /// </summary>
        bool CanUndo { get; }
    }
}
