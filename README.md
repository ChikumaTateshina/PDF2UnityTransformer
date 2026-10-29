# PDF2UnityTransformer

PDF を Unity Editor 上で事前に変換し、VRChat ワールドに静的なメッシュ・テクスチャ・マテリアル・Prefab として配置するツールです。
ワールドに含まれるのは生成アセットと Unlit シェーダーだけで、PDF ファイル・スクリプト・Udon は含まれません。

## 導入（VRChat Creator Companion）

1. [導入ページ](https://chikumatateshina.github.io/PDF2UnityTransformer/) の **Add to VCC** を押す
   または VCC の Settings > Packages > Add Repository に次の URL を追加
   `https://chikumatateshina.github.io/PDF2UnityTransformer/index.json`
2. プロジェクトの Manage Project で **PDF2UnityTransformer** を追加

要件: Unity 2022.3、Windows 版 Unity Editor（追加のソフトウェアは不要です）

## 使い方

1. `Tools > PDF2UnityTransformer > PDF 変換` を開く
2. PDF を選ぶ（参照ボタン、またはドラッグ＆ドロップ）
3. 出力（PC / Quest）と **出力先フォルダ**（Assets 内）を選ぶ
4. **変換して Prefab を作成** を押す → `<出力先>/<文書名>/<PC|Quest>/Document.prefab` が作られます
5. **シーンに配置**（または「作成後にシーンへ配置する」をオン）
6. Quest 向けにビルドする前に `Tools > PDF2UnityTransformer > 配置済み文書をビルドターゲットに切り替え`

| モード | 動作 |
|---|---|
| ハイブリッド（既定） | 文字・図形・画像はそのまま変換し、直接描けない効果（描画モード、ソフトマスク、透明グループなど）は、その効果が変化させた部分だけを画像化。後から描かれる文字はベクターのまま |
| 厳密 | 直接描けない描画があれば中止 |
| ページ画像化 | 必要なページ全体を画像化（明示指定時のみ） |

| 画像 | 動作 |
|---|---|
| 軽量（既定） | 見た目が変わらない範囲で画像を縮小（PC 200dpi / Quest 150dpi 相当まで、元画像が粗いものはさらに）し、写真は JPEG（PC では DXT1 圧縮）、小さな画像は 1 枚のテクスチャ（アトラス）にまとめて描画回数を減らします。同じ画像の重複も 1 枚にまとめます。画像はすべて辺が 2 のべき乗のアトラスにまとめ、GPU 圧縮（DXT1 / BC7、Quest は ASTC）が確実にかかるようにします |
| 最小（容量優先） | ワールドのダウンロード容量を優先します。軽量の処理に加えて画像の密度を 130dpi 相当まで下げ、Crunch 圧縮をかけます。近くで見ると写真がやや粗く見えることがあります（例: A0 ポスター 1 枚で約 4.8 MB） |
| 忠実 | 画像を元の解像度（上限まで）のまま PNG で出力します |

**曲線の細かさ**（細かい / 標準 / やや粗い / 粗い / とても粗い）で、文字や図形の曲線を近似する三角形の数を調整できます。粗くすると三角面数（描画負荷）が減ります。「プレビュー…」を押すと、変換する前に選んだ細かさでの見た目（三角形の表示も可）と、ページ・文書全体の三角面数を Quest の上限と並べて確認でき、そのまま設定に反映できます。

作成後、結果欄の **容量を測る** で、その Prefab がワールドのダウンロード容量に加える大きさを実際にビルドして確認できます。

再変換しても GUID は維持され、手で編集した生成物は上書きされません。生成物に Batching Static を付けないでください（描画順が崩れる場合があります）。

## 注意

- 変換しても、PDF の文章・画像・フォントを複製・公開する権利は得られません。権利の確認は利用者の責任です。埋め込みフォントの利用許諾（fsType）が「制限」の場合は変換を停止します。
- Quest 出力は自動の負荷チェックを通っても、実機で確認するまで「要確認」と表示されます。
- Linear 色空間では半透明の重なりの色が PDF とわずかに異なります（「半透明を画像化する」で一致させられます）。

## 同梱しているもの

- [PDFium](https://pdfium.googlesource.com/pdfium/)（`Editor/Plugins/x86_64/pdfium.dll`、[pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) chromium/8076）: 画像のデコードと、直接描けない効果の画像化に使用します。Editor 専用で、ワールドのビルドには含まれません。PDFium と同梱部品のライセンスは `Editor/Plugins/x86_64/pdfium-licenses/` にあります。Portions of this software are copyright © The FreeType Project (www.freetype.org). This software is based in part on the work of the Independent JPEG Group.
- [LibTessDotNet](https://github.com/speps/LibTessDotNet) 1.1.15（SGI Free Software License B）: 三角形分割とブーリアン演算。

## ライセンス

[MIT License](LICENSE)（同梱物はそれぞれのライセンスに従います）
