---
title: ".accdb/.mdbをブラウザだけで読むためにMDB ToolsをWASM化した話"
emoji: "🧩"
type: "tech"
topics: ["blazor", "webassembly", "emscripten", "access", "wasm"]
published: false
---

## はじめに

Accessファイルの構造確認アプリをBlazor WebAssemblyで作った。

アプリ自体の紹介は別記事に譲り、この記事では技術的に難しかった部分を書く。

テーマは、`.accdb` / `.mdb` をサーバーに送らず、ブラウザ上のBlazor WebAssemblyだけでどこまで読めるか。

結論から言うと、Microsoft Access本体なし、ACE OLE DBなし、サーバーアップロードなしでも、構造確認用途ならかなり実用的なところまでできた。

ただし、最初から「これは簡単にできる」と思っていたわけではない。むしろ最初は難しいと思っていた。

## なぜ難しそうだったのか

Excelファイルなら、`.xlsx` はOpen XML形式なのでZIPを展開してXMLを読めばある程度解析できる。

一方で、Accessの `.mdb` / `.accdb` はそうではない。

Jet / ACE系のデータベースファイルであり、ブラウザ標準APIだけで素直に読める形式ではない。

普通に考えると、次のどれかが必要になる。

- Microsoft Access本体
- ACE OLE DB / ODBCドライバ
- サーバー側でAccessファイルを読む処理
- ネイティブ実行可能な解析ツール

しかし今回は「純WASMでできるところまで」が条件だった。

つまり、Windowsローカルに依存するACEドライバは使えない。サーバーにアップロードして解析する方式も避けたい。Blazor WebAssemblyからexeやDLLを直接実行することもできない。

この時点では、かなり厳しいと思っていた。

## 方針: MDB ToolsをWASM化する

そこで使ったのが[MDB Tools](https://github.com/mdbtools/mdbtools)。

MDB Toolsは `.mdb` / `.accdb` を読むためのOSSツール群で、次のようなCLIを持っている。

- `mdb-ver`
- `mdb-tables`
- `mdb-schema`
- `mdb-count`
- `mdb-queries`
- `mdb-json`

これらをEmscriptenでWebAssemblyにビルドし、Blazor WebAssemblyからJavaScript interop経由で呼び出す構成にした。

全体像はこう。

```text
Blazor WebAssembly
  -> JavaScript interop
    -> MDB Tools WASM module
      -> Emscripten FSにAccessファイルを書き込み
      -> callMain(...)
      -> stdout / stderrを回収
      -> C#側で整形して表示
```

きれいな意味での「ライブラリ化」ではない。

WASM化したCLIをブラウザ内で起動し、stdoutを読む方式である。

## Emscriptenビルドで詰まったところ

MDB Toolsはそのままブラウザ用ライブラリとして配布されているわけではない。

AutotoolsベースのCプロジェクトをEmscriptenでビルドする必要があった。

最終的にはDockerの `emscripten/emsdk` イメージを使い、次のようなconfigureにした。

```bash
emconfigure ./configure \
  --host=wasm32-unknown-emscripten \
  --disable-shared \
  --enable-static \
  --disable-glib \
  --disable-iconv \
  --disable-man \
  --with-bash-completion-dir=no
```

ポイントはこのあたり。

- `--disable-glib` でGLib依存を外す
- `--disable-iconv` で一旦シンプルにする
- man pageやbash completionなどブラウザ実行に不要なものを切る
- SQL関連のビルドのためにflex / bisonを入れる
- Windows clone由来のCRLFで `autoreconf` が失敗するため、事前にLFへ正規化する

WASM出力時は、CLIとして自動実行されると困る。

そのため `INVOKE_RUN=0` にして、JavaScript側から明示的に `callMain()` する形にした。

```bash
-sMODULARIZE=1
-sENVIRONMENT=web,worker,node
-sINVOKE_RUN=0
-sALLOW_MEMORY_GROWTH=1
-sFORCE_FILESYSTEM=1
-sEXPORTED_RUNTIME_METHODS=FS,callMain
```

`FS` と `callMain` をエクスポートしておくのが重要だった。

## CLIをWASMモジュールとして扱う

ブラウザではローカルファイルパスをCLIに直接渡せない。

そこで、ユーザーが選択したAccessファイルをbyte配列として受け取り、Emscriptenの仮想ファイルシステムに書き込む。

```js
module.FS.writeFile("/work/input.accdb", bytes);
module.callMain(["-1", "-t", "table", "/work/input.accdb"]);
```

stdout / stderr はモジュール生成時にフックする。

```js
const stdout = [];
const stderr = [];

const module = await createMdbTablesModule({
  print: value => stdout.push(String(value)),
  printErr: value => stderr.push(String(value)),
  locateFile: fileName => `mdbtools/${fileName}`
});
```

EmscriptenでCLIを動かすと、終了処理が例外のように見えることがある。

そのため `callMain()` は `try/catch` で包み、実際の結果はstdout / stderrから判断する形にした。

```js
try {
  module.callMain(commandArgs);
} catch {
  // Emscripten uses exceptions for process exits.
}
```

このあたりは「CLIをブラウザ内で動かしている」感が強い。

## なぜ複数コマンドに分けたか

MDB Toolsには用途ごとにCLIが分かれている。

今回使った主なコマンドは以下。

| コマンド | 用途 |
| --- | --- |
| `mdb-ver` | Access形式の判定 |
| `mdb-tables` | テーブル、クエリ、フォーム、レポート等の一覧 |
| `mdb-schema` | CREATE TABLE、インデックス、制約、リレーション |
| `mdb-count` | テーブルごとのレコード件数 |
| `mdb-queries` | クエリ一覧とSQL |
| `mdb-json` | テーブルプレビュー、CSV出力、`MSysObjects`参照 |

最初は「ひとつの巨大なWASMにまとめるべきか」とも考えた。

しかし、コマンド単位でWASMモジュールを分けた方が扱いやすかった。

- どの処理で失敗したか分かりやすい
- stdout / stderrをコマンド単位で記録できる
- 必要なコマンドだけ呼び出せる
- Blazor側の結果モデルへ変換しやすい

一方で、各コマンド実行ごとにWASMモジュールを初期化し、仮想FSへファイルを書き込むコストはある。

ここは今後の最適化余地として残っている。

## ブラウザ内ファイル処理の難しさ

Accessファイルは小さいとは限らない。

業務用の `.accdb` では数十MB、場合によっては数百MBになることもある。

ブラウザ上で何も考えずに全データを読むと、メモリやUI応答性の問題が出る。

そのため、実装では次のような制限を入れた。

- ファイルサイズ上限を設ける
- 大きいファイルでは取得するクエリSQL件数を抑える
- テーブルデータはプレビュー表示にする
- CSV出力はユーザーの明示操作にする
- WASM側は `ALLOW_MEMORY_GROWTH=1` を有効にする

特にテーブルデータは、構造確認と全件エクスポートで扱いを分けた。

画面で見るだけなら先頭数十件で十分なことが多い。全件が必要な場合だけCSV出力する。

この割り切りをしないと、ブラウザアプリとして重くなりすぎる。

## 日本語と文字化け

地味に悩ましかったのが日本語である。

Accessファイルでは、テーブル名、列名、クエリ名、SQLに日本語が入ることが珍しくない。

検証中、Nodeやブラウザでは正しくUTF-8として扱えているのに、PowerShellの `Get-Content` では文字化けして見える場面があった。

つまり、問題が次のどこにあるのかを切り分ける必要があった。

- MDB Toolsの出力
- Emscripten / JavaScriptの文字列化
- Blazorへの受け渡し
- JSON保存
- ターミナル表示

結果として、ブラウザ表示やUTF-8としての読み取りでは正しく扱えていても、PowerShellの表示だけが文字化けして見えるケースがあった。

この手の検証では「見た目が文字化けしている」だけで即座にデータ破損と判断しない方がよい。

## MSysObjectsを読むという割り切り

フォーム、レポート、マクロ、モジュールの扱いも難しい。

AccessのUI定義やVBAを完全に復元するのは、今回のスコープでは現実的ではない。

そこで、内部定義を完全解析するのではなく、`MSysObjects` から取得できる範囲に絞った。

```text
mdb-json <file> MSysObjects
```

これにより、名前、Type、Flags、作成日時、更新日時などは取得できる。

ただし、`MSysObjects` にはバイナリプロパティなどノイズの多い情報も含まれる。

そのため、画面に出す情報は以下のような最低限に絞った。

- オブジェクト名
- 作成日時
- 更新日時
- Type
- Flags

ここは「無理してAccessの内部を全部見せない」という判断をした。

構造確認アプリとしては、まず一覧と更新日時が分かるだけでも価値がある。

## mdb-schemaの結果を再利用する

`mdb-schema` はCREATE TABLE形式のテキストを返す。

この中には、列定義だけでなく、インデックス、制約、外部キー相当の情報も含まれる。

アプリ側ではこのテキストをC#でパースし、次の情報に分解している。

- テーブル名
- カラム名
- 型
- サイズ
- NOT NULL
- インデックス
- PRIMARY KEY / FOREIGN KEY / UNIQUEなどの制約
- リレーション

MDB Toolsが返すSQLをそのまま表示するだけでもよいが、画面で構造確認しやすくするには、C#側でモデル化した方が扱いやすかった。

ただし、ここも完全なSQLパーサーを作ったわけではない。

MDB Toolsが出力する形式に合わせ、構造確認に必要な範囲で正規表現によるパースに留めている。

## できたこと

純WASM構成で、次のような情報は取得できた。

- Accessバージョン
- テーブル一覧
- カラム定義
- レコード件数
- クエリ一覧
- 取得できる範囲のSQL
- フォーム、レポート、マクロ、モジュールの一覧
- 作成日時、更新日時などの一部メタ情報
- インデックス
- 制約
- リレーション
- テーブルプレビュー
- CSV出力

「Accessファイルの中身をざっと把握する」という用途なら十分使える。

## できないこと、割り切ったこと

一方で、これはAccess互換実行環境ではない。

次のようなものは対象外、または限定対応としている。

- Accessフォームの完全な再現
- レポートレイアウトの再現
- VBAコードの完全解析
- マクロの実行
- クエリの実行
- パスワード付きAccessファイル
- Access本体やACEと同等の互換性
- 全テーブルの全データを常に画面展開すること

重要なのは、スコープを「Accessを再現する」ではなく「構造を確認する」に置いたこと。

ここを間違えると、途端に難易度が跳ね上がる。

## やってみて分かったこと

純WASMでAccessファイルを読むのは、無理ではなかった。

ただし、何でもできるわけではない。

成立した理由は、スコープをかなり明確にしたからだと思う。

- Access互換環境を作らない
- クエリやマクロを実行しない
- フォームやレポートを再現しない
- 構造確認に必要な情報を取り出す
- 取れない情報は無理に取らない

MDB ToolsをWASM化してCLIとして呼ぶ方式は、設計としては少し泥臭い。

しかし、既存のCツールをブラウザ内で活かせるという意味ではかなり強い。

特にAccessファイルのように、ブラウザ標準APIだけでは扱いにくいファイル形式では有効な選択肢になる。

## まとめ

最初は、`.accdb` / `.mdb` を純WASMで読むのはかなり難しいと思っていた。

しかし、MDB ToolsをEmscriptenでWASM化し、Blazor WebAssemblyからCLIとして呼び出すことで、構造確認用途としては十分成立した。

ポイントは次の3つだった。

- MDB Toolsを無理にライブラリ化せず、WASM化したCLIとして扱う
- Emscripten FSと `callMain()` を使ってブラウザ内でコマンド実行する
- Access完全互換ではなく、構造確認にスコープを絞る

Access本体なし、サーバーアップロードなしで `.accdb` / `.mdb` の構造を読めるのは、実務上かなり便利だと感じた。

「このAccess、何が入っているんだっけ？」を解決する用途なら、ブラウザだけでもかなりのところまで行ける。
