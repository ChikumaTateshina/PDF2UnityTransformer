# PDF2UnityTransformer

PDF を Unity Editor 上で事前に変換し、VRChat ワールドに静的なメッシュ・テクスチャ・マテリアル・Prefab として配置するツールです。
ワールドに含まれるのは生成アセットと Unlit シェーダーだけで、PDF ファイル・スクリプト・Udon は含まれません。

## 導入（VRChat Creator Companion）

1. [導入ページ](https://chikumatateshina.github.io/PDF2UnityTransformer/) の **Add to VCC** を押す
   または VCC の Settings > Packages > Add Repository に次の URL を追加
   `https://chikumatateshina.github.io/PDF2UnityTransformer/index.json`
2. プロジェクトの Manage Project で **PDF2UnityTransformer** を追加

要件: Unity 2022.3、Windows、[Python 3.10 以上](https://www.python.org/)（変換作業を行う PC のみ）

## 使い方

1. `Tools > PDF2UnityTransformer > PDF 変換` を開く
2. 初回のみ「変換環境（Python）」の **準備する** を押す（`%LOCALAPPDATA%\PDF2UnityTransformer` に専用の Python 環境を作り、PyPI から依存ライブラリを導入します）
3. PDF（参照ボタンまたはドラッグ＆ドロップ）・ページ範囲・変換モード・出力先（PC / Quest）を選び **変換する**
4. 診断を確認して **PC 用 Prefab を生成**（Quest も同様）→ **シーンに配置**
5. Quest 向けにビルドする前に `Tools > PDF2UnityTransformer > 配置済み文書をビルドターゲットに切り替え`

| モード | 動作 |
|---|---|
| ハイブリッド（既定） | ベクター・文字・画像はネイティブ、再現できない効果だけ該当領域を画像化 |
| 厳密 | 再現できない描画命令があれば失敗 |
| ページ画像化 | 必要なページ全体を画像化（明示指定時のみ） |

生成先は `Assets/PDF2UnityTransformer/Generated/<文書名>/<PC|Quest>/` です。再変換しても GUID は維持され、手で編集した生成物は上書きされません。生成物に Batching Static を付けないでください（描画順が崩れる場合があります）。

## 注意

- 変換しても、PDF の文章・画像・フォントを複製・公開する権利は得られません。権利の確認は利用者の責任です。埋込フォントの利用許諾（fsType）が「制限」の場合は変換を停止します。
- Quest 出力は自動の負荷チェックを通っても、実機で確認するまで「要確認」と表示されます。
- 依存ライブラリ（pikepdf, pypdfium2, pyclipper, mapbox-earcut, fontTools, NumPy, Pillow）はそれぞれのライセンスに従います。

## ライセンス

[MIT License](LICENSE)
