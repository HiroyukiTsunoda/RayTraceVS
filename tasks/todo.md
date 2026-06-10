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

## Phase 3: TextBox編集ハンドラの共通化（約740行削減）← ✅完了
- [x] Float/Vector3/Vector4/Color の重複ハンドラを `TextBoxInputHandler` に集約
- [x] `ISocketValueNode` インターフェースで Vector3/Vector4/Color のソケット値アクセスを抽象化
- [x] `supportsUndo` フラグで型ごとのUndo挙動差（Color=Undo無）を保持。FloatNodeはBinding方式で別系統維持
- [x] XAMLイベント名は不変（NodeEditorView.xaml 無変更）
- [x] 検証: ビルドOK + **レンダリングpクセル完全一致**（コードレビューでロジック保持を確認）

### Phase 3 成果
- NodeEditorView.xaml.cs を **624行削減**（2630→2006行）。純削減 約341行。
- ⚠️ TextBox手入力のUI動作はヘッドレス検証不可。ロジック1対1保持＋出力一致で技術担保。最終的なUI操作確認はユーザー手動で推奨。

## Phase A（C++エンジン効率化）: 加速構造の再構築最適化 ← ✅完了
ユーザー選択により、C#のPhase 4/5に先行してC++エンジンの効率化を実施。
- [x] 毎フレーム完全再構築していたBLAS/TLASを、ジオメトリのチェックサム変化時のみ再構築に変更（`DXRPipeline.cpp:2846`）
- [x] チェックサムを全ジオメトリ要素対応に拡張（従来: 位置のみ → plane normal, box size/axis, mesh rotation/scale を追加）。見逃し（出力バグ）を防止
- [x] チェックサム計算をラムダ（hashFloat/hashVec3）で簡潔化
- [x] 検証: **レンダリングpクセル完全一致**。scene単一インスタンス＋checksum不変により2パス目以降スキップ（論理確定）
- 教訓: C++ソースに「構」等の漢字コメントを入れるとShift-JIS解釈で2バイト目0x5Cがビルドを壊す → 英語コメントに（[[lessons]]）

### Phase A 成果
- 静的シーンで毎フレームのBLAS/TLAS構築（GPUで重い）を削減。リアルタイム時のFPS向上が期待。出力は不変。

## Phase A-2（C++追加効率化）: dynamic_cast削減＋デバッグログ条件化 ← ✅完了
- [x] UpdateSceneData(815-)とchecksum計算の dynamic_cast/dynamic_pointer_cast を `GetType()`+static_cast に置換（毎フレーム×オブジェクト数のRTTIオーバーヘッド削減）
- [x] boxのデバッグログ（毎フレームsprintf_s×3を無条件実行していた）を `GetLogEnabled()` で条件化（ログ無効時はスキップ）
- [x] 検証: レンダリングpクセル完全一致
- P-2（ディスクリプタ毎フレーム再作成スキップ）/ P-4（バッファアップロードスキップ）は依存関係が複雑でリスク中のため見送り（必要なら別途慎重に）。

---

## Phase 4: 神クラスの分割（保守性向上）← 保留（ユーザー選択でC++効率化を優先）
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

## レビュー（全体総括）

### 達成
- リファクタリング6コミット（76639f3〜dc01aa8）。全体で41ファイル変更、+2,364/-2,424行。
- **重複コード約1,700行超を削除**（シリアライズ三重重複 約1,000行、TextBoxハンドラ約740行、SceneParams詰め替え重複など）。
- 同時に**ヘッドレス検証基盤（約900行の新機能：HeadlessRenderer/ImageComparer/SceneResaver/verify_render.ps1）を追加**したため見かけの純減は小さいが、本体コードは大幅にスリム化＋保守性向上。
- C++エンジン効率化：加速構造の再構築を必要時のみに（最大の効果）、RTTI削減、デバッグログ条件化。

### 検証
- レンダリングは**決定的**（同一入力→ピクセル完全一致）と判明。
- 全6コミットで `verify_render.ps1`（出力ピクセル一致）を独立確認。Phase 1は `--resave`（保存形式バイト不変）も確認。
- C++最適化は scene単一インスタンス＋チェックサム網羅性により論理的にも安全と確認。

### 保留（リスク/コストが効果を上回ると判断）
- Phase 4（神クラス分割 NodeEditorView/MainWindow）：UI動作の手動検証が必要。
- Phase 5（C#計算効率化 NodeGraph O(n×m)→O(1)、Regexキャッシュ等）。
- C++ P-2（ディスクリプタスキップ）/ P-4（バッファアップロードスキップ）：依存関係が複雑でリスク中。

### 注意事項
- 作業ツリーに残る `ScreenShot.png` / `sample_scene.rtvs` / `shader_cache.json` は**リファクタリング開始前からの変更**（本作業と無関係・未コミット）。
- Phase 3のTextBox手入力UI動作は、GUIでの最終確認を推奨（ヘッドレスでは検証不可）。

## 学び
- C++ソース(.cpp/.h)の新規コメントは英語で書く（漢字のShift-JIS 0x5C問題、`tasks/lessons.md` 参照）。
- リファクタリングは検証基盤を最初に作ると、各変更を安全かつ高速に検証できる。

---

# 第2弾: パフォーマンス最適化（2026-06-11開始）

方針（ユーザー確認済み）: 安全な最適化すべて＋render/readbackコマンドリスト統合（WaitForGPU 3→2回）。ダブルバッファ化・Phase 4（神クラス分割）はスコープ外。出力ピクセル完全一致・保存形式不変を維持。
計画詳細: `C:\Users\HT\.claude\plans\fizzy-watching-brook.md`

- [x] Step 0: ベースライン計測（条件A/B×3回）
- [x] Step 1 [C#]: デバッグノイズ除去＋小修正 → 76313e3
- [x] Step 2 [C#]: NodeGraphアルゴリズム改善（接続探索O(1)索引 / HasCycle二重計算除去 / DirtyTracker .ToList()除去）→ 5c18a69
- [x] Step 3 [C#]: UIホットパス改善（FindNearestSocket粗カリング / 重複呼び出し除去 / UpdatePath入力不変スキップ）→ f662902
- [x] Step 4 [C++]: Interop/NativeBridgeデバッグログ条件化（OPT-6）→ 73e4547
- [x] Step 5 [C++]: dynamic_cast置換＋ゼロスキャン条件化（OPT-5/11）→ 59e491d
- [x] Step 6 [C++]: ステージングvectorメンバ化（OPT-3。チェックサム前倒しOPT-4は不要と判明：P-4はバッファ単位チェックサムで独立実装）→ 35c0b40
- [x] Step 7 [C++]: バリアFlushバッチ化 8回→2回（OPT-12）→ 5d0c9f5
- [x] Step 8 [C++]: 内容不変バッファのアップロードスキップ（P-4、FNV-1a 64bit）→ ded8d7f
- [x] Step 9 [C++]: メッシュキャッシュ永続化（OPT-7、2重コピー解消）→ 982be95
- [x] Step 10 [C++]: ディスクリプタ再作成スキップ（P-2、参照リソーススナップショット比較方式）→ dcb2f70
- [x] Step 11 [C++]: render+readbackコマンドリスト統合（WaitForGPU 3→2）→ d78c4b2
- [x] Step 12: 最終計測・レビュー・メモリ更新

## 最終計測結果（第2弾）

### Releaseビルド A/B交互計測（最も信頼できる数値。新旧バイナリを交互実行し環境ノイズを平均化、同一シェーダー使用）
- 4パス(1280x720): 旧939ms → 新858ms（**8.6%改善**、5ペア全勝）
- 16パス(1280x720): 旧3341ms → 新2790ms（**16.5%改善**、3ペア全勝・分散±1%以下）
- **パスあたり定常コスト: 旧200ms → 新161ms（約20%改善）** ← 連続レンダリング（ドラッグ操作中等）の体感に直結

### Debugビルド（GBV有効、参考値）
- verify_render 4パスの推移: Step7まで33〜45秒 → Step8(P-4)以降 22〜26秒帯に低下
- ベースライン比較（環境ノイズ大）: 条件A中央値 69.2→62.5秒、条件B中央値 177.9→169.6秒
- 所見: DebugはGPU-Based Validationのコストが支配的でCPU側改善が相対的に薄まる。実利用（Release）では上記の通り明確な改善。

## レビュー（第2弾総括）

### 達成
- 11コミット（76313e3〜d78c4b2）で速度最適化を完遂。**全コミットでレンダリング出力ピクセル完全一致＋保存形式不変**を検証。
- 最終的な無害性証明: 同一シェーダーバイナリ下で旧コード（7ed2297）と新コード（develop）の出力が**ピクセル単位で完全一致（差分0）**。
- C++エンジン: 不要なGPUアップロード・ディスクリプタ再作成・GPU同期・ログ・メモリコピーを「変更がなければやらない」構造に。C#層: O(n×m)探索のO(1)化とUI重複処理の排除。

### 検証で得た知見（lessons.mdに記録）
- ベースライン画像はシーンファイル変更とシェーダーキャッシュ世代の両方に紐づく。検証NG時は「コードを疑う前に」タイムスタンプと差分パターンを確認し、新旧コードの出力直接比較で切り分ける。

### 注意事項
- Phase 3のUI操作（ノードドラッグ・接続ドラッグ・ソケットスナップ）はヘッドレス検証不可のため、GUI起動での最終手動確認を推奨。
- ScreenShot.png / sample_scene.rtvs / shader_cache.json の未コミット変更はユーザーのもの（本作業では触っていない。shader_cache.jsonはビルドにより更新が混ざる）。
- 保留継続: Phase 4（神クラス分割）、GetInputValueソケット参照キャッシュ、ダブルバッファ化。

## ベースライン計測結果
Debugビルド（D3D12 Debug Layer + GPU-Based Validation有効）、ヘッドレス `--render sample_scene.rtvs`：
- 条件A (1280x720, 16パス): 72729 / 69175 / 64256 ms → **中央値 69175 ms**（約4.3秒/パス）
- 条件B (640x360, 64パス): 186161 / 177896 / 169927 ms → **中央値 177896 ms**（約2.8秒/パス）
- 参考 (2560x1440, 32パス): 123031 ms（約3.8秒/パス）

**所見**: 解像度を16倍変えてもパスあたり時間がほぼ不変 → GPUレイトレ時間ではなく**パスあたり固定コスト（CPU処理・GPU同期・GBV検証）が支配的**。今回の最適化（アップロードスキップ・同期削減・ログ除去）のターゲットと一致。
Releaseビルドの前後比較は全コミット後に git checkout で実施（Step 12）。

---

# 第3弾: 拡張性向上リファクタリング（2026-06-11開始）

方針（ユーザー確認済み）: 新ノード追加・レンダリング機能強化・UI拡張のすべてを見据え、C#+Interop+C++/HLSL全層を対象。神クラス分割（旧Phase 4）も含む。UI手動検証は最後にまとめて実施。
計画詳細: `C:\Users\HT\.claude\plans\snuggly-nibbling-clover.md`

検証（全Step共通）: `.\build.ps1 -NoPackage` → `.\tools\verify_render.ps1`（ピクセル一致）＋ resave（保存形式一致）→ コミット。C++新規コメントは英語。

## マイルストーン1: ノード拡張性（C#層）
- [x] Step 0: ベースライン再生成（.cso世代差で旧ベースラインNG→現状コードで再生成、決定性確認OK、resaveベースラインは有効のまま）
- [x] Step 1 [C#]: NodeRegistryメタデータ化＋パレット自動生成（XAML静的ボタン15個＋個別ハンドラ→動的生成＋共通ハンドラ1個）→ 2a85609
- [x] Step 2 [C#]: SceneEvaluatorのisチェーン解消（SceneCollector＋ディスパッチテーブル、2経路統一）。フォールバック経路の手動構築（Evaluateの劣化コピー: スケール無視・Direction非正規化等）を廃止しEvaluate結果に統一。SceneNodeなしテストシーンでも旧新ピクセル完全一致を確認
- [x] Step 3 [C#]: データフロー簡素化（SceneParams廃止、RenderService.UpdateScene 1引数化、SceneEvaluationResultに既定値＋PhotonDebug設定を集約）

## マイルストーン2: Interop境界とC++エンジン
- [x] Step 4 [Interop/C++]: RenderSettings構造体化（UpdateScene 24→8パラメータ、DXEngine/RenderSettings.hをScene/Bridge/Interopで共有し詰め替えなし）＋SanitizeMaterial共通化（30行×4回コピペ解消）
- [x] Step 5 [C++/HLSL]: SharedTypes.h共有ヘッダ（src/Shader/に配置、9構造体をC++/HLSLで一元化、C++はSharedGpu名前空間＋GPU*エイリアス、static_assertサイズ検証）。検証: ①dxc -Pプリプロセス比較で全13シェーダー意味的同一 ②再コンパイル後ピクセル完全一致。**知見: .csoは-Zi埋め込みのためソース無変更でも再コンパイルでバイナリが変わるが、同一セッション内なら出力は決定的**（事前実験で確認→検証戦略に利用）
- [~] Step 6 [C++]: PhotonMappingPass抽出 → **見送り**。実装調査で判明: ①フォトンは causticsEnabled=false で無効化された実験機能（リソース作成すらされず検証パスで実行されない＝抽出の正しさをピクセル検証で担保できない）②UpdatePhotonDescriptorsがシーンバッファ4種+TLAS+定数+ルートシグネチャ等12個の共有リソースに依存し、抽出すると注入コードで全体複雑度がむしろ増す。テスト不能なコードのリファクタリングは原則に反するため中止
- [~] Step 7 [C++]: CompositePass抽出 → **見送り**。CompositeDescriptorSnapshot 13リソース+UIパラメータ群+denoiser連携の依存があり同様の判断。DXRPipelineの行数削減は可読性向上のみで拡張性への寄与が薄い
- [~] Step 8 [C++/オプション]: SceneBufferManager抽出 → 見送り（計画時からリスク中〜高と評価、Step 6/7の判断に準ずる）

## マイルストーン3: UI/MVVM神クラス分割（最後にまとめてUI手動検証）
- [x] Step 9 [C#]: 空振りインターフェース3つ削除（IRenderService/ISceneFileService/ISettingsService、実装ゼロ・シグネチャ乖離のため）＋IMeshCacheProvider導入でFBXMeshNode→App層の直接依存を解消（FBXメッシュ2個入りシーンでピクセル一致確認）
- [ ] Step 10 [C#]: 型互換チェック統合（2箇所不整合の解消）＋CreateConnectionのConnectionHandler移動
- [ ] Step 11 [C#]: コピー/ペーストのEditCommandHandler移動
- [ ] Step 12 [C#]: MainViewModelへのファイルI/O・ICommand移動（DIコンテナは導入しない）
- [ ] Step 13 [C#]: SceneNodeソケット管理の集約
- [ ] 手動検証チェックリストをユーザーに提示
