using System.Collections.Generic;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Japanese UI strings. The compiler emits stable diagnostic codes (English detail text);
    /// they are explained here so the window can show a Japanese summary plus the original detail.
    /// </summary>
    public static class L
    {
        public static string Status(string status)
        {
            switch (status)
            {
                case "pass": return "合格";
                case "needs-review": return "要確認";
                case "fail": return "不可";
                default: return status;
            }
        }

        public static string Target(string target) => target == "quest" ? "Quest" : "PC";

        public static string Stage(string stage)
        {
            if (string.IsNullOrEmpty(stage)) return "準備中";
            if (stage == "starting") return "起動中";
            if (stage == "frontend") return "PDF を解析中";
            if (stage == "reference") return "基準画像を作成中";
            if (stage == "render") return "画像を作成中";
            if (stage == "write") return "データを書き出し中";
            if (stage == "done") return "完了";
            if (stage.StartsWith("lower:")) return $"{Target(stage.Substring(6))} 用に変換中";
            return stage;
        }

        static readonly Dictionary<string, string> Reasons = new Dictionary<string, string>
        {
            ["font-license-review"] = "埋め込みフォントの利用許諾の確認",
            ["substitute-fonts"] = "代替フォントを使用",
            ["icc-fallback"] = "ICC プロファイルを使えず代替色空間で変換",
            ["cmyk-approximation"] = "CMYK を近似式で変換",
            ["device-test-required"] = "Quest 実機での確認が必要",
        };

        public static string Reason(string code) => Reasons.TryGetValue(code, out var s) ? s : code;

        static readonly Dictionary<string, string> Fallbacks = new Dictionary<string, string>
        {
            ["precomposite"] = "該当領域を画像化",
            ["region-raster"] = "領域を画像化",
            ["page-raster"] = "ページ全体を画像化",
        };

        public static string Fallback(string code) => code != null && Fallbacks.TryGetValue(code, out var s) ? s : code;

        static readonly Dictionary<string, string> Codes = new Dictionary<string, string>
        {
            // input / options / limits
            ["input.missing"] = "PDF ファイルが見つかりません。",
            ["input.not-pdf"] = "PDF ファイルではありません。",
            ["input.corrupt"] = "PDF が壊れているため読み込めません。",
            ["input.encrypted"] = "暗号化・権限制限された PDF は変換しません（保護は解除しません）。",
            ["input.too-large"] = "PDF のファイルサイズが上限を超えています。",
            ["input.pages"] = "ページ数が上限を超えています。",
            ["options.pages"] = "ページ範囲の指定が正しくありません。",
            ["options.profile"] = "品質プロファイルが見つかりません。",
            ["options.substitutes"] = "代替フォントの指定が見つかりません。",
            ["output.not-empty"] = "出力先に別のファイルがあるため書き込めません。",
            ["strict.unsupported"] = "厳密モード: 直接再現できない描画があるため中止しました。",
            ["limit.timeout"] = "制限時間を超えました。",
            ["limit.stream"] = "展開後のデータが大きすぎます。",
            ["limit.decoded"] = "展開後のデータ量の合計が上限を超えました。",
            ["limit.ops"] = "描画命令の数が上限を超えました。",
            ["limit.glyphs"] = "文字数が上限を超えました。",
            ["internal"] = "変換中に内部エラーが発生しました。",
            ["input.repaired"] = "PDF の相互参照表が壊れていたため、修復して読み込みました。",
            ["render.load"] = "PDFium が文書を読み込めませんでした。",
            ["render.memory"] = "画像用のメモリを確保できませんでした。",
            ["geometry.failed"] = "図形を三角形に分割できなかった部分を画像化しました。",
            ["image.direct"] = "直接埋め込まれた画像ストリームを画像化しました。",
            ["color.icc-approx"] = "ICC プロファイル付きのベクター色は、同じ系統のデバイス色空間で近似しています。",
            ["content.filter"] = "未対応の圧縮形式の描画命令を読み飛ばしました。",
            // fonts / text
            ["font.not-embedded"] = "フォントが埋め込まれておらず代替フォントもないため、文字を画像化しました。",
            ["font.substituted"] = "非埋め込みフォントを代替フォントで表示しました（字形が異なります）。",
            ["font.license-substituted"] = "利用制限のある埋め込みフォントを代替フォントに置き換えました。",
            ["font.license-review"] = "埋め込みフォントの利用許諾を確認してください。",
            ["font.license-restricted"] = "埋め込みフォントの利用が制限されているため中止しました。権利を宣言するか代替フォントを指定してください。",
            ["font.metrics-missing"] = "フォントの文字幅情報がなく、代替フォントの幅を使用しました（行の長さが変わる場合があります）。",
            ["font.glyph"] = "一部のグリフを読み取れませんでした。",
            ["font.parse"] = "フォントを解析できず、文字を画像化しました。",
            ["font.cmap"] = "未対応の文字コード表（CMap）のため、文字を画像化しました。",
            ["font.program"] = "未対応のフォント形式のため、文字を画像化しました。",
            ["font.matrix"] = "未対応のフォント変換行列のため、文字を画像化しました。",
            ["font.missing"] = "参照されたフォントが見つかりません。",
            ["font.type3-recursion"] = "Type 3 フォントの再帰を検出しました。",
            ["text.pattern"] = "パターンで塗られた文字を画像化しました。",
            ["text.type3-clip"] = "クリップに使われた Type 3 文字は無視しました。",
            ["text.knockout"] = "テキストノックアウトの無効化は再現していません。",
            // colour
            ["color.cmyk-naive"] = "CMYK を近似式で変換しました（ICC プロファイル指定で改善できます）。",
            ["color.cmyk-reference"] = "CMYK は基準レンダラ相当の変換表で変換しました。",
            ["color.cmyk-lut"] = "CMYK 変換表を作成できませんでした。",
            ["color.spot"] = "特色は代替色空間で表示しています。",
            ["color.calrgb"] = "CalRGB を sRGB として扱いました。",
            ["color.icc-fallback"] = "ICC プロファイルを使えず、代替色空間で変換しました。",
            ["color.overprint"] = "オーバープリントは再現していません（画面表示と同じ扱い）。",
            ["color.space"] = "未対応の色空間のため画像化しました。",
            ["color.image"] = "色数の多い特殊色空間の画像を画像化しました。",
            // images
            ["image.codec"] = "画像の形式を展開できず、該当領域を画像化しました。",
            ["image.too-large"] = "画像が大きすぎるため、該当領域を画像化しました。",
            ["image.size"] = "サイズ 0 の画像を無視しました。",
            ["image.bpc"] = "未対応のビット深度の画像を画像化しました。",
            ["image.colorspace"] = "色空間のない画像を画像化しました。",
            ["image.stencil-pattern"] = "パターンで塗られたマスク画像を画像化しました。",
            ["image.size-mismatch"] = "画像の実寸法が PDF の記述と異なります。",
            ["image.resampled"] = "画像を表示サイズに合わせて縮小しました。",
            // patterns / shadings / transparency
            ["pattern.tiling"] = "タイリングパターンを画像化しました。",
            ["pattern.missing"] = "参照されたパターンが見つかりません。",
            ["pattern.extgstate"] = "パターン内のグラフィックス状態は無視しました。",
            ["pattern.shading-stroke"] = "グラデーションで描かれた線を画像化しました。",
            ["shading.type"] = "未対応のグラデーション形式を画像化しました。",
            ["shading.missing"] = "参照されたグラデーションが見つかりません。",
            ["shading.coords"] = "グラデーションの座標が不正です。",
            ["shading.function"] = "グラデーションの関数がありません。",
            ["transparency.blend"] = "描画モード（乗算など）を使った部分を画像化しました。",
            ["transparency.softmask"] = "ソフトマスクを使った部分を画像化しました。",
            ["transparency.ais"] = "特殊な透明設定を使った部分を画像化しました。",
            ["transparency.group"] = "透明グループを画像化しました。",
            ["transparency.linear-blend"] = "半透明の重なりは Linear 色空間では PDF とわずかに異なります（「半透明を画像化」で一致させられます）。",
            // structure
            ["form.recursion"] = "再帰または深すぎる入れ子のフォームを画像化しました。",
            ["xobject.missing"] = "参照された XObject が見つかりません。",
            ["xobject.type"] = "未対応の XObject を画像化しました。",
            ["xobject.ps"] = "PostScript XObject は無視しました。",
            ["gs.missing"] = "参照されたグラフィックス状態が見つかりません。",
            ["content.parse"] = "描画命令を読み取れなかった部分を画像化しました。",
            ["content.operator"] = "一部の描画命令を処理できませんでした。",
            ["content.unknown-op"] = "未知の描画命令を無視しました。",
            ["oc.usage"] = "レイヤーは既定の表示状態で変換しました。",
            ["scope.forms"] = "フォーム（入力欄）は変換対象外です。",
            ["scope.annotations"] = "注釈は変換対象外です。",
            ["page.box"] = "ページ範囲の指定が不正なため MediaBox を使用しました。",
            ["page.rotate"] = "不正なページ回転を無視しました。",
            ["page.size-mismatch"] = "基準レンダラとページサイズが一致しません。",
            ["fallback.regions"] = "直接描けない効果を、その効果が変化させた部分に限って画像化しました。",
            ["fallback.page"] = "ページ全体を画像化しました（ページ画像化モード）。",
            ["fallback.resolution"] = "画像化した領域の解像度をテクスチャ上限に合わせて下げました。",
            ["target.budget"] = "ターゲットの負荷上限を超えています。",
        };

        /// <summary>Japanese explanation of a diagnostic / error code, or null if unknown.</summary>
        public static string Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (Codes.TryGetValue(code, out var s)) return s;
            if (code.StartsWith("function.")) return "未対応の関数を含む部分を画像化しました。";
            if (code.StartsWith("options.")) return "変換オプションが正しくありません。";
            if (code.StartsWith("exit-")) return "コンパイラが結果を返さずに終了しました。";
            return null;
        }

        public static string Severity(string s) => s == "error" ? "エラー" : s == "warning" ? "警告" : "情報";
    }
}
