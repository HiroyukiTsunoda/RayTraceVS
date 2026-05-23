# RayTraceVS リファクタリング計画

## 前提・方針（ユーザー確認済み）
- **スコープ**: C#層（`src/RayTraceVS.WPF/`）を優先。後から C++/HLSL に拡張可能。
- **出力基準**: 視覚的に同等ならOK。ただしC#層はエンジンに同じ値を渡す限り**完全一致**を目指す。
- **検証**: ヘッドレスレンダリング検証モードを新規実装し、出力をピクセル比較する。

## 基本ルール
- 各Phaseは独立してビルド可能・コミット可能な単位にする。
- 各Phase完了時にヘッドレス検証（ベースライン画像との比較）を行う。
- 出力（ピクセル）に影響しうる変更は最小限・慎重に。C#層は本来エンジンへ渡す値が変わらなければ出力は不変。

---

## Phase 0: 検証基盤（ヘッドレスレンダリング） ← ✅完了
これが無いと「出力が変わっていないこと」を客観確認できないため最優先。

- [x] `App.OnStartup` にコマンドライン引数処理を追加（`--render` / `--compare`）
- [x] ヘッドレスレンダラー実装（`Services/HeadlessRenderer.cs`）
      - 非表示ウィンドウで HWND を確保（`WindowInteropHelper.EnsureHandle()`、Showしない）
      - `SceneFileService` でシーン読込 → `MainViewModel` 経由（MainWindowと同一経路）
      - `SceneEvaluator.EvaluateScene` → `RenderService` をNパス → `GetPixelData` → PNG保存
- [x] 画像比較機能（`Services/ImageComparer.cs`、`--compare`）— WPFのBitmapDecoder使用
- [x] CLIビルド確認（`build.ps1 -NoPackage`）→ 成功
- [x] ベースライン画像を生成（`baseline/baseline_1280x720_p4.png`、.gitignore済み）
- [x] 検証スクリプト `tools/verify_render.ps1`（-UpdateBaseline / 比較）
- [x] 決定性確認: 同一入力で2回実行 → **ピクセル単位で完全一致（最大差分0）**

### Phase 0 成果
- レンダリングは**決定的**と判明 → リファクタリング後も**ピクセル一致**で検証可能（視覚的同等以上）。
- 検証ワークフロー: `.\build.ps1 -NoPackage` → `.\tools\verify_render.ps1`（[検証OK]/[検証NG]）。
- 追加ファイル: `HeadlessRenderer.cs`, `ImageComparer.cs`, `tools/verify_render.ps1`。変更: `App.xaml.cs`, `.gitignore`。
- 既存のレンダリングロジックには未介入（出力同一性を確認済み）。

## Phase 1: シリアライズ/デシリアライズの三重重複を統一 ← ✅完了
- [x] `ISerializableNode` を Dictionary ベースに変更（現状の保存形式=PascalCaseキー+値直接代入を維持）
- [x] 全22ノードに `SerializeProperties`/`DeserializeProperties` を実装（switchから1対1移植）
- [x] `SceneFileService` の巨大switch2つ（プロパティ約400行）を ISerializableNode 経由の数行に置換
- [x] ノード生成の型switchを `NodeRegistry.CreateNodeByClassName` に統一（クラス名対応を追加、"LightNode"互換維持）
- [x] `NodeEditorView.xaml.cs` のクリップボード重複（約500行）を削除し `SceneFileService` の公開メソッドに統一
- [x] 検証ツール追加: `--resave`（保存形式の往復検証）
- [x] 検証: **RESAVE完全一致（保存形式不変）＋レンダリングpiクセル完全一致**（独立再検証済み）

### Phase 1 成果
- 重複コード **約1,008行を削除**。シリアライズロジックが各ノードに1箇所集約。
- 新ノード追加時は `NodeRegistry.Register` + `ISerializableNode` 実装だけで保存/読込/コピペ対応。
- 出力・保存形式とも1ビットも変えずに達成（render一致 + resave一致で二重検証）。

## Phase 2: SceneEvaluator の戻り値クラス化 ＋ 詰め替え重複の解消 ← ✅完了
- [x] `SceneEvaluator.EvaluateScene` の23要素タプルを `SceneEvaluationResult` クラス（init専用プロパティ）に置換
- [x] `RenderWindow` の `SceneParams` 詰め替え3重複を `BuildSceneParams()` 1メソッドに集約
- [x] `HeadlessRenderer` のタプルアクセス（Item1-7）も名前付きプロパティに更新
- [x] 検証: **レンダリングpクセル完全一致**（計算ロジック・エンジンへ渡す値とも不変）

### Phase 2 成果
- 23要素の無名タプル（可読性最悪）を名前付き結果クラスに。呼び出し側が `ev.Spheres` 等で明快に。
- 詰め替え重複（3箇所×約12行）を解消。新パラメータ追加時の修正箇所が1→大幅減。

## Phase 3: TextBox編集ハンドラの共通化（約740行削減）
- [ ] Float/Vector3/Vector4/Color の `PreviewTextInput`/`KeyDown`/`LostFocus`/`GotFocus`/`Apply*` を汎用ハンドラに統合
- [ ] 検証: 各ノードの数値入力・確定・キャンセル動作が従来通り。ヘッドレス出力一致。

## Phase 4: 神クラスの分割（保守性向上）
- [ ] `NodeEditorView.xaml.cs`(3,162行) からソケットドラッグ等の重複ロジックをハンドラ/ヘルパーへ抽出
- [ ] `MainWindow.xaml.cs` のシーンI/O・レンダリング管理を ViewModel/Service へ移動
- [ ] 検証: 全UI機能の動作確認。ヘッドレス出力一致。

## Phase 5: 計算効率化（出力不変）
- [ ] `NodeGraph.EvaluateNode` の接続線形探索 O(n×m) → 入力ソケットID索引で O(1)
- [ ] `NodeGraph.HasCycle` のトポロジカルソート重複計算を除去
- [ ] `IsValidFloatInput` の `new Regex()` を `static readonly` 化
- [ ] `FindNearestSocket` の全ソケット線形探索を事前フィルタで軽減
- [ ] ハードコードパス（`c:\git\RayTraceVS\sample_scene.rtvs`）を相対/設定経由に
- [ ] 検証: 出力一致 + 各操作の動作確認。

---

## レビュー（各Phase完了後に追記）

## 学び（修正を受けたら lessons.md に転記）
